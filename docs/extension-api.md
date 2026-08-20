# Extension API 指南

本文是可信、同进程 extension 的稳定 API 参考。稳定 ABI 位于 `Nekolla.Nekostick.Contracts`；extension 只应依赖该 Contracts 程序集和自己声明的独立 shared-contract 程序集，不应引用 Host、Persistence、ASP.NET、EF Core 或其他 extension 的实现程序集。

当前 Host API 和 ABI 版本均为 **1.1.0**（`HostApiVersion.Current`、`ExtensionAbi.Version`）。本文只记录已经实现并经最终验证的公开 surface；没有为尚未实现的 manifest event declaration、event filter 或通用 endpoint 保留接口。

## 1. 信任边界与 capability 边界

extension 是宿主进程内的**受信代码**。collectible ALC 用于生命周期和卸载，不是安全沙箱；不要把不受信任的程序集加载进进程，也不要把 ALC 当作权限隔离。Host 通过 `IExtensionStartContext.Host` 注入一个有限的 `IExtensionHostBridge`，所有能做的事都必须落在下表的显式 capability 上。

### 1.1 明确可用的能力

- 读取自身的旧版 settings view，以及在 API 1.1 中读写自身拥有的配置、route、service 和 settings。
- 注册、注销自身的稳定 handler；注册至多一个全局 fallback。
- 读取自身 service 的已发布、未过期 loopback endpoint lease。
- 请求自身的 staged reload/unload；观察自身的安全生命周期状态。
- 运行有数量上限、停止时取消的 extension-owned task。
- 发布/订阅 extension-local event，并接收 Host 发布的既有 node-local core event。
- 报告安全 status code、日志 category，并在启动期按 manifest 声明做 typed contract export/import。

### 1.2 刻意不可用的能力

以下对象、权限和数据**不属于** extension API，不能通过桥接对象间接取得：

- root `IServiceProvider`、root DI container，或直接的 `IHostConfigApi`；
- EF Core、`DbContext`、数据库连接、核心业务表和任意数据库/JSONB 直接操作；
- ASP.NET `HttpContext`、Host 的 HTTP pipeline、middleware、全局 route/health endpoint；
- raw process handle、supervisor handle、socket/network handle，任意端口绑定或任意 endpoint；
- secrets、service environment overrides、连接串以及其他 Host 内部敏感数据；
- foreign/Host-owned 配置的读写、全局配置策略、forwarding/static/health/global policy 的控制；
- 自动 file watcher、自动 load/reload；Host 管理面显式发起 load/unload/reload。

`ExtensionServiceConfiguration` 仅包含 Host 允许的进程启动子集（绝对 `FileName`、参数、工作目录、启动/重启策略和 health DTO），不包含 environment、运行时句柄或可调用的进程 API。extension 也不能注册任意 HTTP 或 health endpoint；HTTP 入口只能由 route 指向 caller-owned handler ID，或通过 `ExtensionServiceRouteTarget` 指向 caller-owned 或同一 change set 新建的 service ID；全局 fallback 仍至多一个。
handler 请求会收到 Host 按 admission 限制提供的有界 `ExtensionHandlerRequest.Body` 和复制后的请求 headers；请求提供时 headers 可包含 Cookie、Authorization。extension 必须将 body/headers 按敏感数据处理，并遵守 Host 的 body/header 大小与读取时限。

## 2. Manifest、声明和兼容性

### 2.1 目录和格式

扩展目录是可执行文件目录下的 `extensions/<extension-directory>/`。目录必须有且只能有以下一种 manifest：

- `manifest.json`；或
- `manifest.yaml` / `manifest.yml`。

JSON 和 YAML 同时存在时拒绝该目录。JSON 使用严格解析：不接受注释、尾逗号、重复字段或未知字段；根对象、依赖、export/import declaration 的字段集合必须准确。YAML 只支持基础 scalar/map/list；不处理 tags、anchor 驱动的对象构造或任意 CLR 类型。实现承诺的是**经过验证的 JSON 语义**，不承诺 minified、特定空白或特定格式化方式。

### 2.2 字段

根字段中以下 7 项必需：

| 字段 | 含义 |
| --- | --- |
| `schemaVersion` | 当前接受的 manifest schema 为 `1`。 |
| `id` | 稳定 extension identifier。 |
| `version` | 严格 SemVer 版本。 |
| `entryAssembly` | 扩展根目录内的相对程序集路径。 |
| `entryType` | 完全限定 entry type 名称。 |
| `dependencies` | 依赖数组；每项为 `{ "id", "versionRange" }`。 |
| `requiredHostApiVersion` | Host API 的 SemVer range。 |

可选根字段只有 `exports` 和 `imports`：

- `exports` 每项为 `{ "contractId", "version", "assemblyIdentity", "typeIdentity" }`；
- `imports` 每项为 `{ "contractId", "versionRange", "assemblyIdentity", "typeIdentity" }`。

不存在 event declaration、event name/type gate 或 event filter 字段。core-event 订阅不由 manifest 声明筛选（见 §8）。

### 2.3 可复制的 JSON manifest

下面是一个最小、可验证的 `manifest.json`；`entryAssembly` 必须实际存在于该目录内，`entryType` 必须实现 `IExtensionEntrypoint`（通常实现其简写接口 `IExtensionEntry`）。示例中的空 `exports`/`imports` 是可选字段，删除它们也符合 schema：

```json
{
  "schemaVersion": 1,
  "id": "example.extension",
  "version": "1.0.0",
  "entryAssembly": "Example.Extension.dll",
  "entryType": "Example.Extension.ExampleEntrypoint",
  "dependencies": [],
  "requiredHostApiVersion": ">=1.0.0 <2.0.0",
  "exports": [],
  "imports": []
}
```

`SemVersionRange` 支持比较符（如 `>=1.0.0 <2.0.0`）、`||` alternative、wildcard、`^` 和 `~` 等已实现形式；无效版本或 range 在 discovery 阶段拒绝。contract 的 assembly/type identity 必须与 Host 批准的 identity 完全相容；重复声明、缺 provider、版本不满足或 identity 不匹配都拒绝加载。

### 2.4 1.0/1.1 兼容

- 当前 manifest schema 是 `1`；“1.0 manifest”指面向 Host API 1.0 的既有 manifest/extension，不是 `schemaVersion: 1.0`。
- 1.0 manifest 仍可加载。`requiredHostApiVersion` 满足 Host 后，旧 ABI 的 extension 可继续使用旧成员。
- `ConfigurationApi`、`Routes`、`Services`、`Endpoints`、`Lifecycle` 等新增成员属于 Host API 1.1。只有 `ApiVersion` 支持 1.1 时才调用它们；旧 extension 不应假设这些成员在 1.0 Host 上可用。旧的 `Configuration` 属性保持不变。
- `HostApiVersion` 是不可变语义版本：`new HostApiVersion(major, minor, patch)` 拒绝负数；`ToString()` 为 `major.minor.patch`；`ExtensionAbi.IsCompatible(required, host)` 要求 major 相同且 Host 不低于 required。

## 3. Discovery、加载、staged reload 和卸载

### 3.1 Discovery 与加载

Host 只对显式指定的目录/manifest 执行 discovery 和 load，不扫描后自动加载。`ManifestDiscoveryResult` 暴露 `Succeeded`、`FailureCode`、`SourceFormat` 和成功时的 `ExtensionManifest`；`ExtensionGraphResult` 暴露依赖校验结果和确定性的 `OrderedManifests`。

校验顺序包括：

1. manifest 文件存在且唯一，JSON/YAML 语法和字段严格正确；
2. schema、identifier、SemVer、SemVer range、entry assembly/type 和目录边界正确；
3. 依赖存在、版本范围满足、没有重复 ID 或环；同一拓扑层按 manifest ID 字典序排列；
4. exports/imports 的 declaration、contract catalog 和 identity/version 均相容；
5. entry assembly 在扩展根内加载，entry type 存在、是非 abstract class，并实现 `IExtensionEntrypoint`。

`CollectibleExtensionLoader.Load(ExtensionManifest?)` 只返回安全 `ExtensionLoadResult`（`Succeeded`、`FailureCode`、`Handle`），不把路径或 raw exception 交给 extension。entry type 可以有 `IExtensionHostBridge` 构造函数，也可用无参构造；创建出的对象必须实现 `IExtensionEntrypoint`。每个成功实例位于 collectible ALC；extension 不能从 API 得到 ALC 或 Host 的 loader handle。

### 3.2 staged reload 顺序

Host 的 `ExtensionRuntimeManager.ReloadAsync` 采用 start-before-switch：

1. 在新的 collectible ALC 中验证 replacement manifest、依赖、contracts、entry type 和配置相容性。
2. 以 `IExtensionStartContext.Reloading == true` 调用新实例 `StartAsync`；此时旧实例仍接收请求，新实例尚未接管 route handler。
3. 新实例启动成功后，旧实例停止接收新 handler 请求并进入 drain；切换窗口内旧 handler target 不可用时返回 `503`。
4. 等待旧 handler 请求和后台 task；到有界 timeout 后取消其 task 并继续流程。计划默认 handler drain 与 task stop 均为 30 秒。
5. 调用新实例 `OnPreviousStoppedAsync`，再原子替换 handler/fallback registration，使新实例开始服务。
6. 尝试卸载旧 ALC，并以 weak reference、最多 3 轮 GC 确认收集。

候选 manifest 验证或候选 `StartAsync` 失败时，旧实例持续服务；返回的失败类别可为 `ReplacementPreserved` 或具体安全失败。旧实例停止失败或新实例 `OnPreviousStoppedAsync` 失败时，Host 尝试恢复旧实例并停止候选；恢复失败则 extension 停止，相关 route 返回 `503`。旧 ALC 卸载未确认会记录 `UnloadLeak`/`UnloadNotConfirmed` 类安全状态，但不会自动阻断新版本工作。

`UnloadAsync` 会先移除 dispatch registration、停止接收新请求、调用 `StopAsync`，再释放 task/event/ALC 资源。停止、卸载、重载均为显式 Host 管理操作；extension 自身只能通过 `Lifecycle` 请求自己的 reload/unload，不能选择任意 manifest/path 或控制其他 extension。

### 3.3 Manifest 与 Host runtime result DTO

`ExtensionManifest` 是 discovery 成功后得到的 immutable manifest（构造函数由 parser/Host 控制，extension 不应自行伪造路径）。其公开属性是 `SchemaVersion`、`Id`、`Version`（`SemVersion`）、`EntryAssembly`、`EntryType`、`RequiredHostApiVersion`（`SemVersionRange`）、`Dependencies`、`Exports` 和 `Imports`。每个 `ExtensionDependency` 有 `Id`、`VersionRange`；`ExtensionContractExport` 有 `ContractId`、`Version`、`AssemblyIdentity`、`TypeIdentity`；`ExtensionContractImport` 有 `ContractId`、`VersionRange`、`AssemblyIdentity`、`TypeIdentity`。

这些是 Host 管理面使用的安全结果 DTO，不会作为 raw loader/runtime handle 注入 extension：

| 类型 | 公开结果属性 |
| --- | --- |
| `ManifestDiscoveryResult` | `Succeeded`、`FailureCode`、`SourceFormat`、`Manifest`。 |
| `ExtensionGraphResult` | `Succeeded`、`FailureCode`、`OrderedManifests`。 |
| `ExtensionLoadResult` | `Succeeded`、`FailureCode`、成功时的 `Handle`。 |
| `ExtensionUnloadResult` | `Succeeded`、`FailureCode`、卸载后的 `State`。 |
| `ExtensionRuntimeOperationResult` | `Succeeded`、`FailureCode`、可选 `Status`。 |
| `ExtensionRuntimeStatus` | `ExtensionId`、`Version`、`State`、`HandlerCount`、`HasFallback`、`ActiveRequests`、`ActiveTasks`、`FailureCount`、`DroppedEvents`、`LastFailure`。 |
| `ExtensionInvocationResult` | `State`（`Handled`/`NotHandled`/`Unavailable`/`Failed`）以及 handled 时的 `Response`。 |

`ExtensionLoadHandle` 只在 Host load result 中出现，公开 `Manifest`、`State`，并实现 `Dispose()` 触发卸载；extension bridge 不暴露它。`ExtensionRuntimeState` 为 `Loaded`、`Unloading`、`Unloaded`、`UnloadNotConfirmed`。`ExtensionInvocationResult.Unavailable` 和 `.NotHandled` 是安全静态结果；Host 内部才构造 handled/failed 结果。

`ExtensionFailureCode` 是 Host 侧不含敏感资料的稳定分类，完整值为：

```text
None, InvalidArgument, ManifestMissing, DuplicateManifest,
YamlInvalid, JsonInvalid, UnknownManifestField, DuplicateManifestField,
ManifestSchemaInvalid, InvalidIdentifier, InvalidVersion, InvalidVersionRange,
UnsafePath, EntryAssemblyMissing, HostApiIncompatible, DuplicateExtensionId,
MissingDependency, DependencyVersionIncompatible, DependencyCycle,
ContractCatalogUnavailable, DuplicateContractDeclaration, MissingContractProvider,
ContractVersionIncompatible, ContractIdentityMismatch, ContractsIdentityMismatch,
EntryTypeMissing, EntryTypeNotCompatible, LoadFailed, AlreadyUnloaded,
UnloadInProgress, EntryConstructorFailed, LifecycleFailed, HandlerConflict,
FallbackConflict, RuntimeUnavailable, HandlerFailed, CallbackFailed,
FailureThresholdReached, ExtensionNotLoaded, HandlerUnavailable, Cancelled,
ReplacementPreserved, DrainTimeout, StopFailed, AlreadyStopped, TaskLimitReached,
EventQueueFull, UnloadLeak, UnloadNotConfirmed
```

extension 代码通常只处理 bridge 返回的 `ConfigurationErrorCode`、`ExtensionServiceOperationCode`、`ExtensionLifecycleOperationCode`；不要依赖 Host 内部 exception 文本或路径。


## 4. Start context 与 Host bridge

### 4.1 entrypoint 接口

```csharp
public interface IExtensionEntrypoint
{
    ValueTask StartAsync(
        IExtensionStartContext context,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    ValueTask OnPreviousStoppedAsync(
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public interface IExtensionEntry : IExtensionEntrypoint
{
}
```

`IExtensionStartContext`：

```csharp
public interface IExtensionStartContext
{
    bool Reloading { get; }
    IExtensionContractRegistry Contracts { get; }
    IExtensionHostBridge Host { get; }
    IExtensionRegistration Registration { get; }
}
```

`Reloading` 仅表示本次 start 是 replacement reload。`Contracts` 和 `Host.Contracts` 都是 startup-only typed registry；`Registration` 是当前 extension 私有的 handler/fallback 注册面。`StartAsync` 必须在返回前完成需要的初始注册；Host 只有在 start 成功后才将该 generation 置于 serving 状态。

### 4.2 完整 `IExtensionHostBridge`

```csharp
public interface IExtensionHostBridge
{
    HostApiVersion ApiVersion { get; }

    // 旧 ABI：只读 settings view，保持不变
    IExtensionSettingsReader Configuration { get; }

    // API 1.1 新增
    IExtensionConfigurationApi ConfigurationApi { get; }
    IExtensionRouteApi Routes { get; }
    IExtensionServiceApi Services { get; }
    IExtensionEndpointApi Endpoints { get; }
    IExtensionLifecycleApi Lifecycle { get; }

    IExtensionContractRegistry Contracts { get; }
    IExtensionTaskScheduler Tasks { get; }
    IExtensionEventPublisher Events { get; }
    IExtensionStatusSink Status { get; }
    IExtensionLogger Logger { get; }
}
```

旧的 `Configuration` 是 `IExtensionSettingsReader`，只有 `ExtensionSettingsConfiguration? Settings { get; }`；它不是新的 CRUD facade。`ExtensionSettingsConfiguration` 的字段为 `ExtensionId`、`SchemaVersion`、`SettingsJson`、`Version`。`SettingsJson` 是已验证的 JSON 文档，使用者仍应按敏感数据处理；不得写入日志。API 1.1 的 `ConfigurationApi` 才提供完整 owned snapshot/change-set/settings 操作。

## 5. 配置、route 和 service API

### 5.1 通用配置结果

配置 domain failure 不以 Host exception 形式穿越 ABI，而是 `ConfigurationReadResult<T>` / `ConfigurationWriteResult`：

```csharp
public enum ConfigurationErrorCode
{
    Validation,
    ConcurrencyConflict,
    NotFound,
    Unsupported,
    StorageUnavailable
}

public sealed record ConfigurationError
{
    public ConfigurationErrorCode Code { get; }
    public string Message { get; }
}

public sealed class ConfigurationReadResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public ImmutableArray<ConfigurationError> Errors { get; }

    public static ConfigurationReadResult<T> Success(T value);
    public static ConfigurationReadResult<T> Failure(params ConfigurationError[] errors);
}

public sealed class ConfigurationWriteResult
{
    public bool IsSuccess { get; }
    public long? NewVersion { get; }
    public ImmutableArray<ConfigurationError> Errors { get; }

    public static ConfigurationWriteResult Success(long newVersion);
    public static ConfigurationWriteResult Failure(params ConfigurationError[] errors);
}
```

`ConfigurationError.Message` 只使用安全固定消息，不含数据库、路径、secret 或内部 exception。读成功时看 `Value`；写成功时看 `NewVersion`；失败时不要把 `Value`/`NewVersion` 当作已提交结果。

### 5.2 完整 configuration facade

```csharp
public interface IExtensionConfigurationApi
{
    HostApiVersion ApiVersion { get; }

    ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationWriteResult> ApplyAsync(
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationWriteResult> WriteSettingsAsync(
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default);
}
```

snapshot 和 change set 是不可变 DTO：

```csharp
public sealed record ExtensionConfigurationSnapshot(
    long Version,
    ImmutableArray<ExtensionRouteConfiguration> Routes,
    ImmutableArray<ExtensionServiceConfiguration> Services,
    ExtensionSettingsConfiguration? Settings);

public sealed record ExtensionConfigurationChangeSet(
    ImmutableArray<ExtensionRouteConfiguration> Upserts,
    ImmutableArray<Guid> RemovedRouteIds,
    ImmutableArray<ExtensionServiceConfiguration> ServiceUpserts,
    ImmutableArray<Guid> RemovedServiceIds,
    ExtensionSettingsConfiguration? Settings);
```

上面的 positional 形式用于说明属性；实际构造函数参数顺序也是 `(version, routes, services, settings)` 和 `(upserts, removedRouteIds, serviceUpserts, removedServiceIds, settings)`。`settings: null` 表示在 `ApplyAsync` 中保持 settings 不变，不表示删除 settings。所有 route/service/id 必须符合 Host 的 UUID v7 和完整语义校验。

`ExtensionSettingsConfiguration` 的实际构造函数为：

```csharp
new ExtensionSettingsConfiguration(
    string extensionId,
    int schemaVersion,
    string settingsJson,
    long version)
```

Host 将 caller identity 绑定到 facade；传入其他 `extensionId` 不会取得其他 owner 的权限，并会在校验/写入阶段拒绝 spoof。`ReadAsync` 只返回本 extension 的 routes、services 和 settings。

### 5.3 route DTO 与 owned route convenience API

route 只允许两种 target：

```csharp
public sealed record ExtensionRouteConfiguration(
    Guid id,
    bool enabled,
    RouteMatcherConfiguration matcher,
    ExtensionRouteTargetConfiguration target,
    int priority);

public sealed record ExtensionServiceRouteTarget(Guid serviceId)
    : ExtensionRouteTargetConfiguration;

public sealed record ExtensionHandlerRouteTarget(string handlerId)
    : ExtensionRouteTargetConfiguration;
```

`RouteMatcherConfiguration` 来自 Contracts，构造函数为：

```csharp
new RouteMatcherConfiguration(
    RouteMatcherType type,
    string pattern,
    ImmutableArray<string> hostPatterns,
    ImmutableArray<string> methods)
```

`RouteMatcherType` 的实现值为 `Exact`、`ExactCaseInsensitive`、`Prefix`、`PrefixCaseInsensitive`、`Regex`。route target 必须引用 caller 自身拥有的 service 或 handler；不能指向 Host、foreign extension 或任意 HTTP URL。

```csharp
public interface IExtensionRouteApi
{
    ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionRouteConfiguration route,
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid routeId,
        CancellationToken cancellationToken = default);
}
```

这里的 `expectedVersion` 是全局配置版本，不是 extension 可自行决定的 route revision。`UpsertAsync`/`RemoveAsync` 和 `ConfigurationApi.ApplyAsync` 均使用同一 Host 事务边界。

### 5.4 service DTO 与 owned service/lifecycle API

`ExtensionServiceConfiguration` 构造函数和属性为：

```csharp
new ExtensionServiceConfiguration(
    Guid id,
    bool enabled,
    string fileName,
    ImmutableArray<string> argumentList,
    string workingDirectory,
    ServiceStartMode startMode,
    ServiceRestartPolicy restartPolicy,
    ServiceHealthCheckConfiguration healthCheck,
    DateTimeOffset createdAt,
    DateTimeOffset updatedAt,
    long version)
```

可读属性是 `Id`、`Enabled`、`FileName`、`ArgumentList`、`WorkingDirectory`、`StartMode`、`RestartPolicy`、`HealthCheck`、`CreatedAt`、`UpdatedAt` 和 `Version`。`FileName`、`WorkingDirectory` 必须是绝对路径；`ArgumentList` 是 immutable；没有 `Environment` 属性。`ServiceStartMode` 为 `Eager`/`Lazy`；`ServiceRestartPolicy` 为 `Never`/`OnFailure`/`Always`。health DTO 为：

```csharp
new ServiceHealthCheckConfiguration(
    ServiceHealthCheckType type,
    string? httpPath,
    TimeSpan timeout)
```

`ServiceHealthCheckType` 为 `Process`、`Tcp`、`Http`；HTTP check 必须提供 path，timeout 必须为正。

```csharp
public interface IExtensionServiceApi
{
    ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionServiceConfiguration service,
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid serviceId,
        CancellationToken cancellationToken = default);

    ValueTask<ExtensionServiceOperationResult> StartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    ValueTask<ExtensionServiceOperationResult> StopAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    ValueTask<ExtensionServiceOperationResult> RestartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}
```

`ExtensionServiceOperationResult` 有 `Succeeded`、`Code`、`ServiceId`；`Code` 为 `None`、`Accepted`、`NotFound`、`Conflict`、`Unsupported`、`Cancelled`、`Failed`、`AlreadyStopped` 或 `Reentrant`。API 1.1 的实际边界是：`StartAsync` 可以委托现有 readiness 行为；`StopAsync` 和 `RestartAsync` **始终返回 `Unsupported`**，因为没有安全的 extension service stop/restart host capability。不要把它们当作会杀进程或重启服务的 API；extension 只能修改受控配置并请求支持的 `StartAsync`。

## 6. Endpoint lease 与 owner visibility

```csharp
public sealed record ExtensionEndpointLease(
    Guid ServiceId,
    int Port,
    DateTimeOffset ExpiresAt);

public interface IExtensionEndpointApi
{
    ImmutableArray<ExtensionEndpointLease> Current { get; }

    ValueTask<ExtensionEndpointLease?> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}
```

`Port` 是 Host 分配的 loopback port，`ExpiresAt` 统一为 UTC。`Current` 和 `ResolveAsync` 只显示**调用 extension 自己拥有且当前 active、未过期的 lease**：

- Host-owned/null owner lease 隐藏；
- foreign extension lease 隐藏；
- 不存在、过期或不可用的 lease 不出现在 `Current`，`ResolveAsync` 返回 `null`；
- caller 不会看到被隐藏 service 的 port 或 service ID；
- `Current` 是原子发布的 immutable snapshot，同步 getter 不查询 EF、数据库或新建 DI scope。

Host 将 service ownership map 与 endpoint snapshot 一起原子发布，facade 在创建时绑定 extension identity。因此不要通过猜 service ID 或读取全局 endpoint resolver 绕过 owner 边界。

## 7. Lifecycle、状态和 registration

### 7.1 自身 lifecycle

```csharp
public interface IExtensionLifecycleApi
{
    ExtensionLifecycleStatus? Status { get; }

    ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(
        CancellationToken cancellationToken = default);
}
```

`ExtensionLifecycleStatus` 的属性为：`ExtensionId`、`Version`、`State`、`HandlerCount`、`HasFallback`、`ActiveRequests`、`ActiveTasks`、`FailureCount`、`DroppedEvents`、`LastFailure`。`State` 是 `ExtensionLoadState`：`Discovered`、`Loaded`、`Stopped`、`Failed`、`Unloading`。`LastFailure` 是安全的 `ExtensionLifecycleFailureCode`，包含 `None`、`InvalidArgument`、`Cancelled`、`AlreadyStopped`、`ExtensionNotLoaded`、`RuntimeUnavailable`、`ManifestInvalid`、`LoadFailed`、`LifecycleFailed`、`StopFailed`、`HandlerFailed`、`CallbackFailed`、`ContractConflict`、`RegistrationConflict`、`ReplacementPreserved` 和 `AlcUnloadUnconfirmed`。

`ExtensionLifecycleOperationResult` 有 `Succeeded`、`Code`、`Status`；`Code` 为 `None`、`Accepted`、`NotFound`、`Conflict`、`Unsupported`、`Cancelled`、`Failed`、`AlreadyStopped` 或 `Reentrant`。自身 lifecycle facade 没有 `StopAsync`/`RestartAsync`；extension 的 `StopAsync` 是 Host 在 unload/reload 过程中调用的 entrypoint callback，不是 extension 用来停止其他对象的公开操作。

### 7.2 handler/fallback 注册

```csharp
public interface IExtensionRegistration
{
    bool TryRegisterHandler(IExtensionHandler handler);
    bool TryRegisterFallback(IExtensionFallback fallback);
    bool TryUnregisterHandler(string handlerId);
    bool TryUnregisterFallback();
}
```

- handler 以稳定 `HandlerId` 注册；不同对象占用同一 ID 会被拒绝，重复提交同一对象可以返回成功。
- Host 只允许一个全局 fallback；其他 extension 已拥有 fallback 时注册失败。
- 注册和注销返回 `bool`，不会把冲突变成未经处理的 Host exception。
- handler/fallback registry 的读写、可用性检查和 generation snapshot 已由 gate 串行化；Host callback 不会在 registry gate 内被调用。

### 7.3 handler 与 fallback DTO

```csharp
public interface IExtensionHandler
{
    string HandlerId { get; }

    ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request,
        CancellationToken cancellationToken);
}

public interface IExtensionFallback
{
    ValueTask<ExtensionFallbackResult> HandleAsync(
        ExtensionFallbackRequest request,
        CancellationToken cancellationToken);
}
```

`ExtensionHandlerRequest` 是 framework-neutral immutable DTO：

```csharp
new ExtensionHandlerRequest(
    string method,
    string path,
    IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
    ReadOnlyMemory<byte> body = default,
    bool isHttps = false)
```

属性是 `Method`、`Path`、`Headers`、`Body` 和 `IsHttps`。`ExtensionHandlerResponse` 构造函数为：

```csharp
new ExtensionHandlerResponse(
    int statusCode,
    IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
    ReadOnlyMemory<byte> body = default)
```

状态码必须为 100–599；Contracts ABI 会为请求/响应 headers 与 body 创建 immutable copy，并对每个 header key/value 执行校验。传入请求的 body/header 大小与读取时限属于 Host admission 行为；Contracts 不承诺 aggregate header limit，也不提供 response body size bound。不要期待 `HttpContext`、stream 或 Host response object。

fallback 的 request 包含原始 `ExtensionHandlerRequest` 和 `ExtensionFallbackReason`：`NoRoute`、`HostMismatch`、`MethodMismatch`、`StaticNotFound`、`StaticIndexMissing`。fallback 结果为 `ExtensionFallbackResult.NotHandled` 或 `ExtensionFallbackResult.HandledResponse(response)`。

handler/fallback 不可用、已卸载、已注销或处于切换窗口时，route target 安全返回 `503`；handler callback 抛错或返回无效结果时，dispatch 返回安全失败（HTTP route boundary 映射为 `500`），并计入 extension failure window。fallback 在所有可交给 fallback 的 no-match/static-miss 候选前调用；普通 `500`、`502`、`503`、`504`、`400` 和资源限制错误不转交 fallback。

## 8. Tasks、events、status、logger 和 typed contracts

### 8.1 Bounded tasks

```csharp
public interface IExtensionTaskScheduler
{
    ValueTask<bool> StartAsync(
        string taskName,
        Func<CancellationToken, ValueTask> callback);
}
```

`taskName` 是非敏感 category，必须非空且不超过 128 字符。每个 extension 最多跟踪 64 个 task；达到容量、extension 正在 stop 或参数无效时返回 `false`。Host stop/reload 会 cancel task token，并在有界 stop timeout 内等待；callback exception 在 Host boundary 捕获、计入 failure window，不泄漏为未观察 task exception。

### 8.2 Events

```csharp
public interface IExtensionEventPublisher
{
    bool TryPublish(ExtensionEvent @event);

    bool TrySubscribe(
        Func<ExtensionEvent, CancellationToken, ValueTask> callback);
}

public sealed record ExtensionEvent(
    string Type,
    int Version,
    string PayloadJson);
```

`ExtensionEvent` 的 `Type` 非空且不超过 256 字符，`Version >= 1`，`PayloadJson` 最大 1 MiB；这里只承诺 payload 是有界字符串，不替 extension 做额外 JSON schema 解释。generic custom event 的 `TryPublish` 是 extension-local，不是跨 extension 的持久消息总线。

Host 已有 core event 种类为：

- `ConfigurationSnapshotApplied`
- `RouteChanged`
- `ServiceStateChanged`
- `PortLeaseChanged`
- `ExtensionStateChanged`

**必须注意：现有 core-event subscription 仍然没有 manifest declaration、name/type filter 或其他 declaration gate。** 已加载 extension 可以按既有 bridge 订阅 Host 提供的 core events；这不是只接收自身声明的事件。

事件语义是 node-local、内存内、有序、串行消费的 bounded best-effort queue：

- 每 node、每 extension 队列默认容量 1024；
- 满时丢弃 newest event，`TryPublish` 返回 `false`，并递增 dropped count/聚合告警；
- 不持久化、不跨节点传播、不 replay；
- subscriber 按队列顺序串行调用，subscriber exception 由 Host 捕获，不阻断其他 subscriber；
- stop/dispose 后继续 publish/subscribe 返回 `false`。
- `IExtensionEventPublisher` 没有单独的 unsubscribe API；subscription 随当前 extension generation 的 stop/dispose 清理。

### 8.3 Status 和 logger

```csharp
public enum ExtensionStatusKind
{
    Healthy,
    Degraded
}

public readonly record struct ExtensionStatus(
    ExtensionStatusKind Kind,
    string Code);

public interface IExtensionStatusSink
{
    void Report(ExtensionStatus status);
}

public enum ExtensionLogLevel
{
    Information,
    Warning
}

public interface IExtensionLogger
{
    void Report(ExtensionLogLevel level, string code);
}
```

status/logger 只接受安全 category code（最多 128 字符），不接受 arbitrary extension text；不要把 secret、路径、命令或请求内容放进 code。回调失败、失败阈值、队列 drop 等 runtime 观察通过 `ExtensionLifecycleStatus` 的计数和安全 failure category 暴露。

### 8.4 Startup-only typed contracts

```csharp
public interface IExtensionContractRegistry
{
    bool TryExport<TContract>(
        string contractId,
        TContract implementation)
        where TContract : class;

    bool TryImport<TContract>(
        string contractId,
        out TContract? contract)
        where TContract : class;
}
```

`contractId`、assembly identity、type identity 和 SemVer/version range 必须先出现在 manifest 的 `exports`/`imports` 声明中；registry 仅在 startup exchange 使用。重复 export、无 provider、版本不满足、类型/程序集 identity 不同都会返回 false 或使 candidate load 失败。跨 ALC 只传稳定 Contracts/approved contract 类型；不能用 registry 取得 root DI 或任意实现程序集。

## 9. Ownership、expectedVersion、原子性与 server-owned metadata

1. **Owner 绑定。** `ConfigurationApi`、`Routes`、`Services`、`Endpoints` 都在 Host 创建时绑定调用 extension identity；调用者没有 `ownerId` 选择参数。read 只返回自身记录；Host/foreign 记录被隐藏或按 safe `NotFound`/`null` 处理。
2. **乐观版本。** configuration/route/service writes 使用调用者读取到的全局 `snapshot.Version` 作为 `expectedVersion`；settings write 使用自身 settings 的 `Version`。旧版本得到 `ConfigurationErrorCode.ConcurrencyConflict`，不得覆盖后来提交。
3. **原子事务。** `ApplyAsync` 的 route upsert/remove、service upsert/remove 和可选 settings replacement 作为一次完整 candidate 校验和原子提交；任何 validation、ownership、handler/service target 或并发冲突都会使整个 change set rollback，不留下部分 route、service 或 settings。route/service convenience API 使用同一语义。
4. **通知时序。** configuration notification 只在数据库 transaction commit 后发布，且不在 EF config gate 内执行 callback。通知不是 extension 可直接操纵的消息持久化机制。
5. **Host-owned revision/timestamp。** `ExtensionRouteConfiguration` 不暴露可写 route revision/timestamp；持久化 route 的 revision/timestamps 由 Host 从当前实体/提交时刻赋值。现有 service 记录的 `CreatedAt`、`UpdatedAt` 和 revision/version metadata 由 Host 保留并维护；新 service 创建当前按实现接受并映射 DTO 提供的 creation metadata。客户端必须以 Host 返回的这些值和 `Version` 为权威，不得把它们当作 Host-global control，也不能自行推进 server revision。`ExtensionServiceConfiguration.Version` 和 `ExtensionSettingsConfiguration.Version` 仍是服务器返回的并发字段；repair 后不存在 public per-route `Version` expected gate，唯一公开写 gate 仍是全局 `expectedVersion`。
6. **Endpoint owner visibility。** endpoint snapshot 中的 owner metadata 是 Host 内部绑定信息；extension lease DTO 只露 `ServiceId`、`Port`、`ExpiresAt`，并且只对 exact caller-owned active lease 输出。Host-owned/null 和 foreign 永远不能通过 `Current` 或 `ResolveAsync` 观察。

## 10. Failure、reentrancy 和 unregister 语义

### 10.1 Callback failure isolation

handler、fallback、task、event subscriber 以及 entrypoint lifecycle callbacks 都在 Host 边界捕获异常。失败按 extension、callback category 计入默认 60 秒 rolling window；默认阈值为 10，成功不会把窗口计数清零。达到阈值后 Host 停止该 extension、取消其 tasks、注销其 handlers/fallback；其 route target 返回 `503`。被停止的 extension 只能由 Host 显式 load/reload 恢复。

`ExtensionInvocationResult` 是 Host handler dispatch 的安全结果，`State` 为 `Handled`、`NotHandled`、`Unavailable` 或 `Failed`；成功 handled 时 `Response` 才非空。`ExtensionRuntimeOperationResult` 有 `Succeeded`、`FailureCode`、可选 `Status`，不携带 raw exception/path/secret。`ExtensionRuntimeStatus` 只包含 `ExtensionId`、`Version`、`State`、`HandlerCount`、`HasFallback`、`ActiveRequests`、`ActiveTasks`、`FailureCount`、`DroppedEvents` 和 `LastFailure` 等安全观测。

### 10.2 Reentrancy

Host 对所有主动调用 extension 的 callback 建立 callback guard：handler、fallback、tracked task、event subscriber，以及 entrypoint 的 `StartAsync`、`OnPreviousStoppedAsync`、`StopAsync` 均包括在内。若这些 callback 内调用：

```csharp
await host.Lifecycle.RequestReloadAsync(cancellationToken);
await host.Lifecycle.RequestUnloadAsync(cancellationToken);
```

结果会**立即**返回 `Succeeded == false`、`Code == ExtensionLifecycleOperationCode.Reentrant`，不会等待自己的 dispatch lease、drain 或 lifecycle gate，因此不会死锁。来自 extension 自己、且不在任一 active Host-invoked callback 中的 background/outside-callback request 可以等待 staged operation；仍应处理 `Accepted`、`Conflict`、`Cancelled`、`Failed` 等 safe result。

### 10.3 注销、当前调用与重新注册

`TryUnregisterHandler`/`TryUnregisterFallback` 是 future-dispatch tombstone：

- 当前已经进入 handler/fallback 的 invocation 可以完成；注销不会强行中断它；
- tombstone 立即从未来 dispatch snapshot 移除，后续 route target 不可用并返回 `503`/`Unavailable`；
- 同一 generation 使用相同 handler ID 重新注册会被拒绝，即使旧对象已不在 active dictionary；
- 缺失、foreign 或不属于 caller 的 ID 返回 `false`；
- 新 generation 在 staged reload 成功切换后拥有自己的 registration state。

registry mutation 与 availability read 已 gate 串行化；manager callback 在释放 registry gate 后调用，避免 callback reentrancy 和数据竞争。

## 11. 完整但精简的 C# 使用示例

下面示例使用的类型和签名均来自 `Nekolla.Nekostick.Contracts`。示例先注册 handler，再提交只引用自身 service/handler 的 owned configuration；service metadata 由 Host 返回并维护：新建 service 时当前实现接受并映射 DTO 提供的 creation metadata，客户端必须以返回的 `CreatedAt`、`UpdatedAt` 和 `Version` 为准，且不得把这些字段当作 Host-global control。

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Example.Extension;

public sealed class ExampleEntrypoint : IExtensionEntry
{
    private IExtensionHostBridge? _host;

    public async ValueTask StartAsync(
        IExtensionStartContext context,
        CancellationToken cancellationToken)
    {
        _host = context.Host;

        // 旧 ABI：在所有兼容 Host 上仍可读取。
        ExtensionSettingsConfiguration? legacy =
            context.Host.Configuration.Settings;

        context.Host.Status.Report(
            new ExtensionStatus(ExtensionStatusKind.Healthy, "started"));
        context.Host.Logger.Report(ExtensionLogLevel.Information, "started");

        if (!context.Registration.TryRegisterHandler(new HelloHandler()))
        {
            return;
        }

        // fallback 是全局唯一资源，失败时必须按 false 处理。
        _ = context.Registration.TryRegisterFallback(new NoMatchFallback());

        // 新 facade 只在 Host API 1.1 可用时使用。
        if (context.Host.ApiVersion >= new HostApiVersion(1, 1, 0))
        {
            var read = await context.Host.ConfigurationApi
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (read.IsSuccess && read.Value is { } snapshot)
            {
                var serviceId = Guid.Parse(
                    "01900000-0000-7000-8000-000000000702");
                var routeId = Guid.Parse(
                    "01900000-0000-7000-8000-000000000701");

                var service = new ExtensionServiceConfiguration(
                    serviceId,
                    enabled: true,
                    fileName: "/opt/example/bin/example-service",
                    argumentList: ImmutableArray<string>.Empty,
                    workingDirectory: "/opt/example",
                    startMode: ServiceStartMode.Lazy,
                    restartPolicy: ServiceRestartPolicy.OnFailure,
                    healthCheck: new ServiceHealthCheckConfiguration(
                        ServiceHealthCheckType.Process,
                        httpPath: null,
                        timeout: TimeSpan.FromSeconds(3)),
                    createdAt: DateTimeOffset.UtcNow,
                    updatedAt: DateTimeOffset.UtcNow,
                    version: 0);

                var route = new ExtensionRouteConfiguration(
                    routeId,
                    enabled: true,
                    matcher: new RouteMatcherConfiguration(
                        RouteMatcherType.Exact,
                        "/hello",
                        ImmutableArray<string>.Empty,
                        ImmutableArray.Create("GET")),
                    target: new ExtensionServiceRouteTarget(serviceId),
                    priority: 0);

                var changes = new ExtensionConfigurationChangeSet(
                    ImmutableArray.Create(route),
                    ImmutableArray<Guid>.Empty,
                    ImmutableArray.Create(service),
                    ImmutableArray<Guid>.Empty,
                    settings: null);

                var write = await context.Host.ConfigurationApi
                    .ApplyAsync(snapshot.Version, changes, cancellationToken)
                    .ConfigureAwait(false);
                if (!write.IsSuccess)
                {
                    context.Host.Status.Report(
                        new ExtensionStatus(
                            ExtensionStatusKind.Degraded,
                            write.Errors[0].Code.ToString()));
                }
            }
        }

        // task callback 在 stop/reload 时收到 cancellationToken。
        _ = await context.Host.Tasks.StartAsync(
            "refresh",
            static token => ValueTask.CompletedTask);

        _ = context.Host.Events.TrySubscribe(
            static (_, _) => ValueTask.CompletedTask);
        _ = context.Host.Events.TryPublish(
            new ExtensionEvent("example.started", 1, "{\"ok\":true}"));
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    // 该方法必须从 extension 自己的 outside-callback background 路径调用；
    // 若从 Start/Stop/handler/task/event callback 调用，结果是 Reentrant。
    public ValueTask<ExtensionLifecycleOperationResult>
        RequestSelfReloadAsync(CancellationToken cancellationToken) =>
        _host!.Lifecycle.RequestReloadAsync(cancellationToken);
}

file sealed class HelloHandler : IExtensionHandler
{
    public string HandlerId => "example.hello";

    public ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes("hello");
        return ValueTask.FromResult(new ExtensionHandlerResponse(
            200,
            new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(
                    "Content-Type",
                    new[] { "text/plain; charset=utf-8" })
            },
            body));
    }
}

file sealed class NoMatchFallback : IExtensionFallback
{
    public ValueTask<ExtensionFallbackResult> HandleAsync(
        ExtensionFallbackRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExtensionFallbackResult.NotHandled);
}
```

若要让 route 指向 handler，把上例的 target 换成 `new ExtensionHandlerRouteTarget("example.hello")`；该 handler 必须已经由同一个 extension 的 `Registration` 成功注册。若 `WriteSettingsAsync`，应先读取自身 settings 的 `Version`，并使用自身 identity 创建 `ExtensionSettingsConfiguration`：

```csharp
static async ValueTask<ConfigurationWriteResult?> WriteSettingsAsync(
    IExtensionHostBridge host,
    CancellationToken cancellationToken)
{
    var settingsRead = await host.ConfigurationApi
        .ReadSettingsAsync(cancellationToken)
        .ConfigureAwait(false);
    if (!settingsRead.IsSuccess || settingsRead.Value is not { } current)
    {
        return null;
    }

    var next = new ExtensionSettingsConfiguration(
        current.ExtensionId,
        current.SchemaVersion,
        "{\"mode\":\"safe\"}",
        current.Version);
    return await host.ConfigurationApi
        .WriteSettingsAsync(current.Version, next, cancellationToken)
        .ConfigureAwait(false);
}
```

并发冲突时重新读取 snapshot/settings version、重新构造完整 candidate，再重试；不要用旧 `NewVersion` 或客户端自增的 route/service metadata 猜测提交结果。

## 12. 最终验证证据与实现定位

本页依据最终 Contracts/runtime 实现和 follow-up oracle gate 编写。Phase 2 remediation 的实际 focused evidence 为：

- `dotnet build src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj --configuration Release --no-restore --nologo --verbosity minimal`：通过，0 warnings/errors。
- `dotnet test tests/Nekolla.Nekostick.UnitTests/Nekolla.Nekostick.UnitTests.csproj --configuration Release --no-restore --nologo --logger "console;verbosity=minimal"`：468/468 passed，failed 0，skipped 0。
- 以 process-local `NEKOSTICK_TEST_PG` 提供的隔离测试 PostgreSQL 环境运行当前源码的 filtered integration coverage（`HostExtensionLoopbackIntegrationTests`、`PostgresMigrationArtifactContractTests`、`PersistenceMigrationTests`、`PostgresExtensionCapabilityIntegrationTests`）：11/11 passed，failed 0，且没有残留 `nekostick_it_*` schema。连接串值不在本文重复，避免把凭据写入文档。
- follow-up oracle 已确认 endpoint identity filtering/no-scope getter、Start/PreviousStopped/Stop 三类 entrypoint callback guard、gated immutable registry/tombstone、staged reload/failure isolation、additive ABI、owner transaction/migration boundary 均通过审查。

本文 lane 按任务要求没有重新运行 build/test/formatter/linter；上列是最终 remediation verification 的实际结果，不是对本次文档写入的重新执行声明。

对应实现入口：

- 稳定 ABI/DTO：`src/Nekolla.Nekostick.Contracts/ExtensionAbi.cs`、`HostApiVersion.cs`、`ExtensionContracts.cs`、`ExtensionConfigurationApi.cs`、`ExtensionCapabilityApis.cs`、`ConfigurationContracts.cs`、`ExtensionCapabilityFactory.cs`；
- manifest/discovery/ALC：`src/Nekolla.Nekostick.Extensions/ExtensionManifestContracts.cs`、`ExtensionManifestJsonParser.cs`、`ExtensionManifestYamlParser.cs`、`ManifestParserCore.cs`、`CollectibleExtensionLoader.cs`；
- bridge/registry/queue/task/failure：`ExtensionHostBridge.cs`、`ExtensionRuntimePrimitives.cs`、`ExtensionCapabilityRuntime.cs`；
- staged runtime：`ExtensionRuntimeManager.cs` 及其 staged/reload implementation；
- owner-bound Host facade 和 endpoint snapshot：`src/Nekolla.Nekostick.Host/ExtensionCapabilityFacades.cs`、`HostServiceEndpointResolver.cs`、`HostServiceEndpointPublicationService.cs`，以及 Persistence owner metadata/migration。

这些路径是实现定位，不表示 extension 可以引用其中的 Host/Persistence/runtime 实现类型。
