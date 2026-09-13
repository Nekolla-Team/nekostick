# API 1.3：遥测、路由观测、自定义日志、扩展管理与流式处理

本文描述 API 1.3 的完整能力。`1.3.2` / `1.3.3` 是 API 1.3 代次内的增量补丁，不引入新的 API version，也不改变 1.2 桥契约。要求 Host API 1.3 的既有扩展 manifest 仍然有效（例如要求 1.3 major/minor 且 `<2.0.0` 的范围可以由 1.3.3 Host 满足）。后续代次的追加能力见 [api-1.4.md](api-1.4.md)。

API 1.3 通过旁路桥 `IExtensionHostBridge13` 追加七组能力：

- `Supervisor`：全局服务运行遥测与属主过滤（只读）。
- `RouteEvents`：路由观测订阅与可干预转发的动作钩子。
- `LogWriter`：Host 署名的自定义文本日志。
- `Management`：跨扩展安装记录、启用状态、reload、`ReloadSoon`、删除与目录刷新。
- `DataDirectory`：Host 配置的数据目录路径。
- 流式请求/响应处理器 `IExtensionStreamingHandler`。
- 设置内容变更事件 `ExtensionCoreEventKind.ExtensionSettingsChanged`。

`1.3.3` 在同一旁路桥上追加三项能力：`Supervisor` 的节点本地 `ResumeAsync` / `RestartAsync`、桥属性 `HostInfo`（宿主节点状态快照）、服务生命周期新状态 `Waiting`；`ExtensionManagementEntry` 追加 `ContentHash` 内容摘要字段。这些成员要求协商版本不低于 `1.3.3`（检查方式见下文「能力探测」）。

## 能力探测（必读）

`IExtensionHostBridge13` 继承 `IExtensionHostBridge`，内置桥同时实现两个接口。契约包只提供**一个**版本号（`ExtensionAbi.Version` / 桥上的 `ApiVersion`）和通用的 `ExtensionAbi.IsCompatible(required, host)` 谓词；**某个版本号对应哪些能力由调用方自己声明阈值并校验**。使用 API 1.3 能力前做两步检查：

```csharp
// 调用方自己声明所需最低版本
static readonly HostApiVersion Api13Minimum = new(1, 3, 2);

// 第一步：桥是否实现了 1.3 旁路接口
if (context.Host is not IExtensionHostBridge13 bridge13)
{
    // 外部桥实现（如测试替身）可能只实现较早的契约
    return;
}

// 第二步：协商出的版本是否达到调用方要求的门槛
if (!ExtensionAbi.IsCompatible(Api13Minimum, bridge13.ApiVersion))
{
    // 桥类型存在，但 Host 版本低于要求：能力处于「不受支持」状态
    return;
}

var management = bridge13.Management;
```

`Management` 属性本身是非空的。桥类型存在但版本门槛未满足，或 Host 没有安装该能力时，Host 返回一个 unsupported stub；调用它的所有方法都返回 `ConfigurationErrorCode.Unsupported`，不会抛出异常。其他降级行为如下：

- `Supervisor.ReadAsync` / `ReadForExtensionAsync` / `GetAsync` 返回 `ConfigurationErrorCode.Unsupported`；
- `RouteEvents.TrySubscribe` / `TryRegisterHook` 返回 `false`；
- `LogWriter.WriteText` 静默丢弃文本。

`1.3.3` 追加的能力由调用方用更高的自选门槛检查（桥类型在 1.3.2 就存在，但新成员在旧 Host 上不可用）：

```csharp
static readonly HostApiVersion Api133Minimum = new(1, 3, 3);

if (ExtensionAbi.IsCompatible(Api133Minimum, bridge13.ApiVersion))
{
    // ResumeAsync / RestartAsync 可用; HostInfo 返回真实快照
}
```

版本不足时 `HostInfo` 返回 `ExtensionHostInfoSnapshot.Unavailable`（所有字段为未知/空，不抛异常）；`ResumeAsync` / `RestartAsync` 所在的 `Supervisor` 能力与既有成员同一规则：桥存在但版本不足时整个能力为 unsupported stub。

## 结果与失败代码

管理 API 与 Supervisor 使用 `ConfigurationReadResult<T>` / `ConfigurationWriteResult`。业务失败不通过异常表示；调用方应检查 `IsSuccess`，再读取 `Value` 或 `Errors`。配置错误码的稳定含义如下：

| `ConfigurationErrorCode` | 含义 | 管理 API 中的典型场景 |
| --- | --- | --- |
| `Validation` | 输入或业务状态未通过校验。 | 非法 extension ID、错误的 load state、manifest 版本不匹配、依赖不满足、重复 ID 刷新。 |
| `ConcurrencyConflict` | 记录或全局配置版本已被其他写入更新。 | 记录版本或发布版本过期；重新读取后重试。 |
| `NotFound` | 请求的稳定配置项不存在。 | 目标 extension record 不存在。 |
| `Unsupported` | 当前桥、节点或调用上下文不支持该操作。 | API 版本不足、read-only 节点、缺少 Host 服务、生命周期回调重入。 |
| `StorageUnavailable` | 配置存储、目录扫描或发布过程不可用。 | 数据库/目录扫描失败、无法安全确认删除条件、发布回退到旧代次。 |

Cancellation 仍按 .NET 约定传播；DTO 构造参数非法仍会抛 `ArgumentException`，不属于业务失败码。
管理操作的 `extensionId` 必须非空、不超过 128 个字符且不包含 control character；不满足时返回 `Validation`。

## 服务运行遥测（Supervisor）

`IExtensionSupervisorApi` 提供全节点服务的运行时快照，按服务 ID 查询，或者按扩展属主过滤：

```csharp
ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(
    CancellationToken cancellationToken = default);

ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadForExtensionAsync(
    string extensionId,
    CancellationToken cancellationToken = default);

ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(
    Guid serviceId,
    CancellationToken cancellationToken = default);
```

`ReadAsync` 仍是全局视图（不限于调用扩展自己的服务），适合监控面板和健康巡检。`ReadForExtensionAsync` 只保留 `OwnerExtensionId` 与传入 `extensionId` 使用 `StringComparison.Ordinal` 完全相等的快照；无属主的 Host 服务不会出现在过滤结果中。空的 `extensionId` 返回 `Validation`，运行时快照不可用返回 `Unsupported`，读取运行时失败返回 `StorageUnavailable`。找不到某个 `serviceId` 时，`GetAsync` 成功返回 `null`，不是 `NotFound`。

```csharp
var read = await bridge13.Supervisor.ReadForExtensionAsync(
    "example.worker", cancellationToken);
if (!read.IsSuccess) return;

foreach (var service in read.Value!)
{
    // OwnerExtensionId 是 Host 归属信息，扩展不能伪造
    bridge13.LogWriter.WriteText(
        ExtensionLogLevel.Information,
        $"owned service {service.ServiceId}: {service.LifecycleState}");
}
```

`ExtensionServiceRuntimeSnapshot` 字段：

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `ServiceId` | `Guid` | 服务 ID。 |
| `ProcessId` | `int?` | 当前操作系统进程号；未知时为 `null`。 |
| `StartedAt` | `DateTimeOffset?` | 当前进程代次的启动时间（UTC）。 |
| `Uptime` | `TimeSpan?` | 当前进程代次的运行时长。 |
| `LifecycleState` | 枚举 | `Unknown` / `Disabled` / `Starting` / `Running` / `Stopping` / `Failed` / `Waiting`（1.3.3 追加，见下文「节点本地服务恢复与重启」）。 |
| `HealthState` | 枚举 | `Unknown` / `Healthy` / `Unhealthy`。 |
| `ForwardedRequestCount` | `long` | 累计转发请求数。 |
| `ActiveForwardedRequestCount` | `long` | 正在转发的请求数。 |
| `LastUpdatedAt` | `DateTimeOffset?` | 该遥测的最后更新时间（UTC）。 |
| `LastHealthAt` | `DateTimeOffset?` | 最近一次健康检查时间（UTC）。 |
| `OwnerExtensionId` | `string?` | 服务属主扩展 ID；Host 服务或未知属主为 `null`。 |

这是只读遥测，不授予启动、停止或修改服务的权限。

## 节点本地服务恢复与重启（1.3.3）

`IExtensionSupervisorApi` 在 1.3.3 追加两个**只影响本节点、绝不写全局配置**的生命周期操作。这是管理面操作：扩展是受信运营方，**不做属主过滤**——包括 Host 自有服务（无扩展属主）在内的任何已存在服务都可操作（Host 自有服务没有其他管理入口）；只有未知服务 ID 返回 `NotFound`：

```csharp
ValueTask<ConfigurationWriteResult> ResumeAsync(Guid serviceId, CancellationToken cancellationToken = default);
ValueTask<ConfigurationWriteResult> RestartAsync(Guid serviceId, CancellationToken cancellationToken = default);
```

### `Waiting` 状态与 `ResumeAsync`

服务启动缺少本节点前置条件（典型场景：部署方扩展尚未把微服务二进制同步到本节点）时，Host 不再反复硬失败，而是把服务置于 `Waiting` 状态并以退避自动重试；`ExtensionServiceRuntimeSnapshot.LifecycleState` 出现 `Waiting` 即表示这种「等待内容」状态。`ResumeAsync` 用于在条件就绪后主动催一次：

- 服务处于 `Waiting`：恢复本节点启动流程；**确实离开 Waiting** 才返回 `Success()`，尝试结束但前置条件仍缺失时返回 `Validation` 失败（不假报成功）。
- 服务已在运行或处于其他已知状态：忽略请求，返回 `NoOp()`。
- 未知 ID：`NotFound`。

### `RestartAsync`（严格重启）

`RestartAsync` 在本节点**严格先停后起**：

- 除「管理性禁用」外的任何状态都执行停-起；成功结果**永不**返回 `NoOp()`。
- 管理性禁用的服务必须拒绝（`Validation`），不会被启动。
- 未知 ID：`NotFound`。
- 即使本节点当前没有该服务的运行槽位，只要它处于启用状态，严格重启也会把它拉起来。

### 与 1.1 `Services.RestartAsync` 的区别（重要）

两个同名操作的语义不同，按场景选择：

| | `Services.RestartAsync`（1.1） | `Supervisor.RestartAsync`（1.3.3） |
| --- | --- | --- |
| 定位 | 属主服务的常规生命周期请求 | 节点本地的严格恢复手段 |
| 结果类型 | `ExtensionServiceOperationResult`（`Accepted` 等异步受理码） | `ConfigurationWriteResult`（`Success` / `NoOp` / 错误码） |
| 语义 | 请求式：受理后异步停起，可能与当前状态冲突（`Conflict`） | 严格式：除禁用外任何状态都执行停-起，成功即真实发生 |
| 典型用途 | 日常重启自己部署的后端 | 内容同步完成后强制本节点重跑微服务 |

二者都不写全局配置版本；1.1 `Services` 是属主作用域（只能操作自己的服务），`Supervisor` 是管理面（无属主过滤，可操作任意已存在服务）。

> 注意：1.1 `Services` 的 `StopAsync` / `RestartAsync` 在当前 Host 实现中**尚返回 `Unsupported`**（只有 `StartAsync` 与 CRUD 生效）。需要停止服务时请移除或禁用对应配置（`RemoveAsync` / 禁用属主扩展会停止其服务进程）；需要严格重启时用本节的 `Supervisor.RestartAsync`。

## Host 信息面（HostInfo，1.3.3）

`IExtensionHostBridge13.HostInfo` 返回当前 Host 节点状态的即时快照 `ExtensionHostInfoSnapshot`。每次读取都现算（不是缓存对象），适合巡检与决策：

```csharp
var info = bridge13.HostInfo;
if (info.Readiness == ExtensionHostReadinessState.Degraded)
{
    // 例如：数据库暂不可用时主动降级非关键功能
}
```

> 注意：生命周期回调（如 `StartAsync`）运行在发布流程内部，此时 `Readiness` 必然是
> `Unready`，绝不可能是 `Ready` —— 在那里等待 `Ready` 会死锁；生命周期回调里只把
> `HostInfo` 当观测信息使用。1.4 起该窗口细分为独立的 `Publishing` 状态，见
> [api-1.4.md](api-1.4.md#发布中的-readinesspublishing)。

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `NodeId` | `string?` | 稳定节点标识；不可用时为 `null`。 |
| `ReadOnly` | `bool` | 本进程是否禁用配置写入。 |
| `ExtensionsSkipped` | `bool` | 本进程是否禁用扩展加载。 |
| `SupervisorDisabled` | `bool` | 本进程是否禁用进程监督。 |
| `DatabaseAvailable` | `bool` | 最近一次运行时操作中数据库是否可达。 |
| `SnapshotAvailable` | `bool` | 当前是否已发布完整配置快照。 |
| `ConfigurationValid` | `bool` | 当前已发布快照是否有效。 |
| `PublishedConfigurationVersion` | `long?` | 当前已发布的配置版本；未知为 `null`。 |
| `LastSnapshotState` | 枚举 | 最近一次快照接受 / 拒绝状态。 |
| `LastSnapshotStateAt` | `DateTimeOffset?` | 最近一次快照状态迁移时间（UTC）。 |
| `Readiness` | 枚举 | `Unknown` / `Unready` / `Ready` / `Degraded`。1.4 起追加 `Publishing`，见 [api-1.4.md](api-1.4.md#发布中的-readinesspublishing)。 |

协商版本低于 1.3.3 时返回 `ExtensionHostInfoSnapshot.Unavailable`（所有字段未知/空）。快照绝不包含连接串、机密、环境变量值、进程句柄等宿主实现细节；扩展不应依赖它做安全判定，只用作可观测性与降级提示。

## 扩展管理（Management）

`IExtensionHostBridge13.Management` 暴露 `IExtensionManagementApi`。它是 1.3 桥上的高权限、身份绑定能力：调用方可以按稳定 ID 管理任意扩展记录，而不是只管理自己的记录。它不依赖 manifest capability flag；是否能访问由 Host API 版本门控。所有返回值仍使用安全结果类型：

```csharp
IExtensionManagementApi management = bridge13.Management;
var records = await management.ListAsync(cancellationToken);
```

`IExtensionManagementApi.ApiVersion` 返回当前协商的 `HostApiVersion`；它用于记录能力版本，不会改变七个操作的结果类型或错误码。

### 管理记录 DTO

`ListAsync` 返回不可变的 `ImmutableArray<ExtensionManagementEntry>`。每一项包括：

| 属性 | 说明 |
| --- | --- |
| `ExtensionId` | 稳定扩展 ID。 |
| `InstalledVersion` | 持久化记录中的已安装版本。 |
| `LoadState` | 持久化公开状态：`Discovered`、`Loaded`、`Stopped`、`Failed`、`Unloading` 或 `Disabled`。 |
| `CreatedAt` / `UpdatedAt` | 记录创建 / 更新时间（UTC）。 |
| `RecordVersion` | 记录的乐观并发版本。 |
| `IsRunning` | 当前运行时是否有该扩展的 Loaded 代次；它不是持久化启用意图。 |
| `ManifestVersion` | 最近一次目录扫描观察到的 manifest 版本；manifest 缺失时为 `null`。 |
| `ContentHash` | 扩展目录内容的 SHA-256 摘要（`sha256:<64 位小写十六进制>`），未记录时为 `null`。用于多节点内容一致性检测，见 [README](README.md#内容摘要与多节点部署)。1.4 起条目还包含扩展自定义上报状态（`ReportedStatusKind` / `ReportedStatusCode`），见 [api-1.4.md](api-1.4.md#扩展状态上报的持久观测)。 |

`RequestRefreshAsync` 返回 `ExtensionRefreshSummary`：

| 属性 | 说明 |
| --- | --- |
| `Added` | 本次加入持久化记录的扩展 ID。 |
| `VersionUpdated` | manifest 版本改变、因此更新 `installed_version` 的扩展 ID。 |
| `Missing` | 本次扫描未找到文件、但仍保留在持久化中的扩展 ID。 |

### 七个操作


#### `ListAsync`

```csharp
ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>> ListAsync(
    CancellationToken cancellationToken = default);
```

每次调用先读取一个数据库 snapshot，再进行当前 `extensions/` 目录扫描，并从运行时状态得到 `IsRunning`。这三个观察并非一个事务，因此结果是 **eventually consistent**；它不是对数据库、磁盘和运行时状态的原子时刻快照。数据库 snapshot 或目录根枚举不可用时返回其安全错误（目录不可用分类为 `StorageUnavailable`）；unsupported stub 返回 `Unsupported`。

#### `EnableAsync`

```csharp
ValueTask<ConfigurationWriteResult> EnableAsync(
    string extensionId,
    CancellationToken cancellationToken = default);
```

启用只接受目标记录当前处于 `Disabled`、`Stopped` 或 `Failed` 的情况。Host 必须在当前目录扫描中找到该 manifest，并且记录版本与 manifest 版本完全匹配；manifest 声明的每一个依赖都必须有记录、处于 `Loaded`，其记录版本也必须与依赖 manifest 匹配，并且依赖 manifest 版本满足声明的 range。任一条件不满足返回 `Validation`；没有目标记录返回 `NotFound`。

通过校验后，Host 将记录持久化为 `Loaded`，然后触发 write → publish pipeline；这不是绕过发布器的直接进程启动。记录版本竞争返回 `ConcurrencyConflict`，持久化或扫描失败返回 `StorageUnavailable`，read-only / 版本不足 / 生命周期回调重入返回 `Unsupported`。持久化写入成功即返回 durable-write result；即时 publish 触发是 best-effort，不会把即时发布失败另行映射为 `StorageUnavailable`，PG revision `NOTIFY` 的 refresh 会继续收敛。

#### `DisableAsync`

```csharp
ValueTask<ConfigurationWriteResult> DisableAsync(
    string extensionId,
    CancellationToken cancellationToken = default);
```

除已经是 `Disabled` 的记录外，任意记录状态都可以请求禁用；已经 `Disabled` 时返回成功的 no-op。若存在仍为 `Loaded` 且声明依赖目标扩展的 dependent，返回 `Validation`，不会改变目标记录。目标不存在返回 `NotFound`。

成功后记录持久化为 `Disabled` 并触发 publish pipeline。发布所需的 desired set 会排除该扩展拥有的服务，Host 会停止其拥有的服务进程（停止语义是 best-effort）；扩展拥有的 handler route 配置保留，但目标扩展未加载时路由 fail closed，客户端得到 `503`。并发冲突或持久化/扫描失败分别返回 `ConcurrencyConflict` 或 `StorageUnavailable`，read-only / 版本不足 / 生命周期回调重入返回 `Unsupported`。持久化写入成功即返回 durable-write result；即时 publish 失败不会改写为 `StorageUnavailable`，PG revision `NOTIFY` 的 refresh 会继续收敛。

#### `ReloadAsync`

```csharp
ValueTask<ConfigurationWriteResult> ReloadAsync(
    string extensionId,
    CancellationToken cancellationToken = default);
```

reload 只接受 `Loaded` 记录，并要求当前 manifest 存在且版本与记录完全匹配；目标记录初始不存在返回 `NotFound`，记录不是 `Loaded`、manifest 缺失或版本不匹配、以及目标在操作期间变为不可用时返回 `Validation`。操作通过 Host publication pipeline 强制替换该扩展的 generation，而不是调用旧的直接生命周期 reload 路径。它用于同版本文件热替换。

发布器无法完成强制 reload、或复用旧 generation / fallback 而没有重新加载时，操作返回 `ConfigurationErrorCode.StorageUnavailable`，不会把 fallback 伪装成成功。能力不可用、read-only 节点，或从 route / event / scheduler callback 调用 `ReloadAsync`（无论目标是否为调用扩展自身）时返回 `Unsupported`；这些回调场景应使用下面的 `ReloadSoon`。成功时 `NewVersion` 是实际发布的 committed snapshot version，可能比调用方之前读取的版本更新。
#### `ReloadSoon`

```csharp
bool ReloadSoon(string extensionId);
```

`ReloadSoon` 是完全同步、不可 await 的 reload 调度入口。返回 `true` 只表示请求已排入 deferred publication，不表示 generation 已替换或 reload 已完成；执行 deferred publication 时，Host 会针对最新 durable snapshot 重新校验目标（例如目标已不存在、已 Disabled 或 manifest 已 drift 时不再 reload）。后续 publication 失败不会回报给调用方，是 best-effort。

它允许从所有 callback context 调用，包括 `StartAsync`、`StopAsync`、`OnPreviousStoppedAsync` 生命周期回调，以及 route handler、event subscriber、scheduler callback。非法 ID、能力不支持或禁止 configuration writes 时立即返回 `false`；否则返回 `true`。

#### `DeleteRecordAsync`

```csharp
ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
    string extensionId,
    CancellationToken cancellationToken = default);
```

删除是显式、不可自动触发的记录操作。目标记录不存在返回 `NotFound`；只有在当前可靠目录扫描中完全没有该 ID 时才允许删除。目标仍存在或扫描发现 duplicate ID 时返回 `Validation`；任一扩展目录不可读时返回 `StorageUnavailable`，因为 Host 不能安全地证明“该 ID 已从磁盘消失”。

提交删除前，Host 先停止该扩展拥有的服务进程，然后在一个事务中级联删除 extension record、extension settings、拥有的 routes、services 以及 service runtime / port lease rows。若存在仍为 `Loaded` 且声明依赖目标的 dependent，删除返回 `Validation`，不会执行级联删除。删除失败后仍会重新发布以将已停止的服务状态收敛回来。记录不会因为 manifest 缺失、目录删除或 refresh 而自动删除。

非法 ID、目标仍存在、duplicate ID 或 Loaded dependent 返回 `Validation`；记录版本竞争返回 `ConcurrencyConflict`；存储、扫描或不可读目录返回 `StorageUnavailable`；read-only / 版本不足 / 生命周期回调重入返回 `Unsupported`。

#### `RequestRefreshAsync`

```csharp
ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(
    CancellationToken cancellationToken = default);
```

请求 Host 重新扫描当前扩展目录：

1. 有 manifest 但没有记录的发现会加入记录，初始状态为 `Disabled`，并计入 `Added`。
2. 已有记录的 manifest 版本改变时更新 `installed_version`，并计入 `VersionUpdated`。
3. 记录仍在数据库但本次扫描没有 manifest 时计入 `Missing`；这只是报告，不删除记录。
4. 扫描中无法读取的目录（manifest 缺失、重复、解析或 schema 校验失败、IO 异常等）不会静默忽略：会计入 `ExtensionRefreshSummary.Skipped`（1.3.4 起），每项包含目录的叶子名称（`DirectoryName`，不含完整路径）和稳定的失败类别名（`FailureCode`，如 `ManifestMissing`、`JsonInvalid`）。

refresh 完成后触发 publish pipeline。publish 的 descriptor 身份包含内容摘要：运行中 binding 的摘要与 desired 摘要不一致（含从 `null` 变为有值）即自动替换 generation，版本变化同理。因此 `Loaded` 扩展在版本更新或摘要漂移后的任何一次 publish（即时触发、revision `NOTIFY` 或轮询）都会收敛重载，refresh 自身不再携带额外的强制重载信号；两者之外的场景仍可用 `ReloadAsync` 显式重载。

重复 manifest ID 返回 `Validation`；目录根或持久化不可用返回 `StorageUnavailable`；乐观并发竞争返回 `ConcurrencyConflict`；read-only / 版本不足 / 生命周期回调重入返回 `Unsupported`。refresh 的 durable writes 成功后返回 summary；即时 publish 触发是 best-effort，不会把发布失败映射为 `StorageUnavailable`，PG revision `NOTIFY` 的 refresh 会继续收敛。初次启动的“零记录例外”见下文；它是启动 bootstrap 规则，不改变 refresh 新增记录的默认 `Disabled` 状态。
对于 `EnableAsync`、`DisableAsync`、`DeleteRecordAsync` 和 `RequestRefreshAsync`，数据库 durable write 与运行时 publish 是两个阶段：写入提交后会触发即时 publish，但即时失败由 revision `NOTIFY` refresh 重试并最终收敛，业务写结果不会伪报为发布失败。

## 记录生命周期规则

- **记录永不自动删除。** 文件消失、manifest 缺失、refresh 或启动扫描都只会保留记录并报告缺失。只有显式 `DeleteRecordAsync`，且 ID 已从可靠目录扫描中消失，才会删除记录及其级联数据。
- **扫描新增默认 Disabled。** 在已有任意持久化记录后，扫描到的新扩展先以 `Disabled` 注册，不会因为文件刚出现就加载。
- **首次启动例外。** 如果启动时数据库中完全没有扩展记录，bootstrap 扫描到的所有扩展仍按既有 out-of-box 行为加载；首次写入后的后续扫描都遵循新增即 `Disabled`。
- **启用 / 禁用是持久状态。** `EnableAsync` / `DisableAsync` 写入记录状态，并通过 write → publish pipeline 应用，而不是只改变当前进程；下一次启动会按持久状态决定是否加载。
- **版本变更与热替换不同。** refresh 更新 manifest 版本或钉死新摘要后，`Loaded` 记录会在下一次 publish 自动替换 generation（descriptor 身份含摘要，任何 publish 都会收敛）；其余需要重载的场景（如依赖的外部状态变化）仍须显式 `ReloadAsync`，它无条件强制重载目标，不比较摘要。
- **Disabled 的运行时效果。** Disabled 扩展不进入可加载 desired set；其 handler route 配置可保留但请求 fail closed，拥有的服务进程停止，配置行不因此删除。

## 路由观测订阅（RouteEvents.TrySubscribe）

订阅后，每条路由在「转发前」（trigger）和「转发完成后」（return）各产生一条观测，通过扩展自己的标准事件队列投递（与 1.0 的事件总线同一条队列，有序、串行、best-effort）：

```csharp
var accepted = bridge13.RouteEvents.TrySubscribe(async (evt, token) =>
{
    // evt.Type 是 "route.trigger" 或 "route.return"
    // evt.Version 是 1（ExtensionRouteEventTypes.Version）
    // evt.PayloadJson 是 ExtensionRouteEvent 的 JSON
    var observation = JsonSerializer.Deserialize<ExtensionRouteEvent>(evt.PayloadJson);
    if (observation is null) return;

    // observation.RouteId / CorrelationId / Stage / OccurredAt
    // observation.Request:  Method Path QueryString Host Headers Body IsHttps
    // observation.Response: return 阶段的 StatusCode Headers Body（trigger 阶段为 null）
});
```

- 每个扩展最多 256 个订阅（`ExtensionRouteHookLimits.MaximumSubscriptionRegistrations`），超过后 `TrySubscribe` 返回 `false`。
- 观测在路由执行之外异步投递，订阅回调再慢也不会拖慢请求。
- 载荷有界：请求 / 响应体最多复制 64 KiB，header 最多 128 个、单值最长 16 KiB。

## 路由动作钩子（RouteEvents.TryRegisterHook）

与只读订阅不同，动作钩子在转发路径上**同步**执行，可以改写请求、替换响应或取消转发：

```csharp
bool TryRegisterHook(
    ExtensionRouteEventStage stage,   // Trigger = 转发前；Return = 转发完成后
    Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback);
```

回调收到 `ExtensionRouteHookContext`（`RouteId`、`CorrelationId`、`Stage`、`Request`、`Response`），必须返回以下四种动作之一：

| 动作 | 合法阶段 | 效果 |
| --- | --- | --- |
| `Continue` | 任意 | 原样继续转发。 |
| `ReplaceRequest` | 仅 Trigger | 用新请求快照替换后继续转发。 |
| `ReplaceResponse` | 仅 Return | 用新响应快照替换后返回给客户端。 |
| `CancelForwarding` | 任意 | 取消本次转发 / 响应投递。 |

构造结果的形状由构造函数强校验（如 `ReplaceRequest` 只能携带请求快照）：

```csharp
new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue);
new ExtensionRouteHookResult(ExtensionRouteHookAction.ReplaceRequest, request: newSnapshot);
new ExtensionRouteHookResult(ExtensionRouteHookAction.ReplaceResponse, response: newResponse);
new ExtensionRouteHookResult(ExtensionRouteHookAction.CancelForwarding);
```

**规则与上限**：

- 每个扩展最多 128 个钩子（`ExtensionRouteHookLimits.MaximumHookRegistrations`）。
- 同一（路由, 阶段）上的多个钩子按注册顺序串行执行。
- 回调必须在 **250 毫秒**内返回并响应取消（`ExtensionRouteHookLimits.MaximumCallbackDuration`）。
- 回调抛异常、返回 `null`、超时、被取消，或返回阶段不合法的动作时，Host 按 `ExtensionRouteHookResult.FailClosed` 处理：不应用任何替换并取消本次转发。只有显式 `Continue` 才保证放行。
- 钩子在扩展 reload / 停止时随旧代次注销。

### 示例 1：Trigger 阶段改写请求

把所有 `/old-api` 前缀的请求改写到 `/v2`，并附带一个追踪 header：

```csharp
bridge13.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, token) =>
{
    if (!context.Request.Path.StartsWith("/old-api", StringComparison.Ordinal))
    {
        return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
    }

    var rewritten = new ExtensionRouteRequestSnapshot(
        method: context.Request.Method,
        path: "/v2" + context.Request.Path["/old-api".Length..],
        queryString: context.Request.QueryString,
        host: context.Request.Host,
        headers: context.Request.Headers.Select(h =>
            new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.AsEnumerable())),
        body: context.Request.Body.AsMemory(),
        isHttps: context.Request.IsHttps);

    return ValueTask.FromResult(new ExtensionRouteHookResult(
        ExtensionRouteHookAction.ReplaceRequest, request: rewritten));
});
```

### 示例 2：Return 阶段把 5xx 换成友好错误页

```csharp
bridge13.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Return, (context, token) =>
{
    if (context.Response is not { StatusCode: >= 500 })
    {
        return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
    }

    var body = Encoding.UTF8.GetBytes(
        $"{{\"error\": \"upstream failed\", \"correlationId\": \"{context.CorrelationId}\"}}");
    var replacement = new ExtensionRouteResponseSnapshot(
        502,
        new[] { new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["application/json"]) },
        body);

    return ValueTask.FromResult(new ExtensionRouteHookResult(
        ExtensionRouteHookAction.ReplaceResponse, response: replacement));
});
```

### 示例 3：熔断——取消转发

```csharp
bridge13.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, token) =>
{
    if (CircuitBreaker.IsOpen(context.RouteId))
    {
        // 显式取消；客户端会得到 Host 的错误响应
        return ValueTask.FromResult(new ExtensionRouteHookResult(
            ExtensionRouteHookAction.CancelForwarding));
    }

    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
});
```

### 请求 / 响应快照的构造约束

`ExtensionRouteRequestSnapshot` / `ExtensionRouteResponseSnapshot` 在构造时校验，超限直接抛 `ArgumentException`（钩子里抛异常 = fail closed）：

| 约束 | 值 |
| --- | --- |
| body 最大字节数 | 64 KiB |
| header 最多个数 | 128 |
| 单个 header 最多值数 | 64 |
| 单个 header 值最大长度 | 16 KiB |
| header 总文本最大长度 | 64 KiB |
| host 最大长度 | 256 |

`ExtensionRouteHookResult.IsValidFor(stage)` 可以在返回前自检动作是否合法。

## 自定义文本日志（LogWriter）

1.0 的 `Logger` 只能上报预定义类别码；1.3 的 `LogWriter` 允许自由文本，由 Host 自动署名当前扩展（扩展不能伪造或指定扩展 ID）：

```csharp
bridge13.LogWriter.WriteText(ExtensionLogLevel.Information, "cache warmed: 128 entries");
bridge13.LogWriter.WriteText(ExtensionLogLevel.Warning, $"upstream latency {latencyMs}ms");
```

- 单条文本最长 4096 字符（`ExtensionLogLimits.MaximumTextLength`）。
- 级别仍然只有 `Information` / `Warning` 两档。
- 不要写入机密（设置文档、环境变量、Cookie、Authorization 等）。

## 设置内容变更事件（ExtensionSettingsChanged）

1.3.2 新增 `ExtensionCoreEventKind.ExtensionSettingsChanged`。当 Host 配置发布流程检测到某个扩展的 `ExtensionSettingsConfiguration` 内容（`Version` 或 `SettingsJson`）发生变化时，向该扩展自身的事件总线投递一条事件。

**事件本身不携带设置内容**，载荷只有 `{ extensionId }`：

```csharp
context.Host.Events.TrySubscribe(async (@event, token) =>
{
    if (@event.Type != nameof(ExtensionCoreEventKind.ExtensionSettingsChanged))
    {
        return;
    }

    // 扩展收到事件后，通过现有 API 读取自己的设置并做 diff
    var read = await context.Host.ConfigurationApi.ReadSettingsAsync(token);
    if (!read.IsSuccess) { /* 处理读取失败 */ }

    var settings = read.Value;
    // 与本地缓存对比、热重载配置等
});
```

规则：

- **属主限定**：Host 只把该事件投递给 `extensionId` 对应的扩展；其他扩展不会收到。
- **内容无关**：载荷仅含扩展 ID，扩展需自行调用 `IExtensionConfigurationApi.ReadSettingsAsync` 读取并 diff。
- **有序投递**：与扩展内其他事件共用同一条有序事件队列，串行、best-effort。
- **版本门槛**：低于 1.3.2 的 Host 不会发布该事件；扩展可自行用 `ExtensionAbi.IsCompatible(new HostApiVersion(1, 3, 2), host.ApiVersion)` 判断。

## Host 数据目录（DataDirectory）

`IExtensionHostBridge13.DataDirectory` 暴露 Host 为扩展准备的持久化数据目录路径。

配置方式（按优先级从高到低）：

1. CLI 参数 `--data-directory <path>`。
2. 环境变量 `NEKOSTICK_DATA_DIRECTORY`。
3. 默认值：`Path.Combine(AppContext.BaseDirectory, "data")`，即与 `extensions/` 目录同级的 `data/`。

Host 保证在扩展 `StartAsync` 之前该目录已存在。扩展使用规则：

```csharp
if (host is not IExtensionHostBridge13 bridge13)
{
    // 旧版桥：不可用
    return;
}

if (string.IsNullOrEmpty(bridge13.DataDirectory))
{
    // 明确不可用；不能当作路径使用
    return;
}

var extensionDataPath = Path.Combine(bridge13.DataDirectory, "my.extension");
```

- `string.Empty` 表示不可用，扩展**不能**将其当作路径使用。
- 在 manifest 中要求 `>=1.3.2`，或在运行时用 `ExtensionAbi.IsCompatible(new HostApiVersion(1, 3, 2), host.ApiVersion)` 探测，确认版本后再读取。
- 该目录由 Host 所有；扩展只应读写自己命名的子目录/文件，不能删除或覆盖 Host 文件。

## 流式请求 / 响应处理器

1.3.2 新增 `IExtensionStreamingHandler`，让扩展以原始 `Stream` 消费请求体、生产响应体，与既有字节数组处理器 `IExtensionHandler` 并存。

注册方式：

```csharp
public sealed class StreamingHandler : IExtensionStreamingHandler
{
    public string HandlerId => "example.echo";

    public async ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await request.BodyStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        return new ExtensionStreamingResponse(
            200,
            new[] { new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["application/octet-stream"]) },
            buffer);
    }
}

context.Registration.TryRegisterStreamingHandler(new StreamingHandler());
```

### 请求流（`ExtensionStreamingRequest.BodyStream`）

- Host 所有；仅在 `HandleStreamingAsync` 回调执行期间有效。
- 回调返回、抛出或被取消后，Host 负责释放。
- 读取受路由 `MaxRequestBodyBytes` 限制；超出时 Host 仅让本次请求失败，**不会**计入扩展的失败统计（客户端超限不是扩展故障）。

### 响应流（`ExtensionStreamingResponse.BodyStream`）

- 回调执行期间由 handler 所有；**回调返回后所有权转移给 Host**。
- Host 从流的**当前位置**开始读取，复制到客户端后释放。
- 如果 handler 把数据写入 `MemoryStream`，**必须在返回前 rewind**（`Position = 0`）。
- 第一个字节复制到客户端即**提交响应**，此后无法回滚。
- `null` 会被替换为 `Stream.Null`，表示空响应体。

### 与路由钩子 / 观测的交互

- 流式路由上的 route hooks 收到的请求体快照为**空**（`Body` 长度 0）。
- 如果某条流式路由上存在 hooks，Host 会把整个流式响应**完全缓冲到内存**后再提供给 hooks 做 snapshot / rollback；扩展不需要关心这一行为，但应意识到大流会被缓冲。

### 何时使用流式处理器

- 请求体或响应体较大，不适合全部驻留在内存（但仍受 Host 请求体上限约束）。
- 需要流式转换、压缩、分块解析等场景。
- 不需要 hook 对请求体做 snapshot / rollback 的路由。

## Host 桥能力总览

`IExtensionHostBridge` 按版本累积能力；API 1.3 能力都经 `IExtensionHostBridge13` 暴露：

| 属性 | 引入版本 | 能力 |
| --- | --- | --- |
| `ApiVersion` | 1.0 | 协商出的 Host API 版本。 |
| `Configuration` | 1.0 | 只读的旧版设置视图（当前设置的 JSON 文档）。 |
| `Contracts` | 1.0 | 共享契约导出 / 导入。 |
| `Tasks` | 1.0 | 后台任务调度。 |
| `Events` | 1.0 | 扩展内有序事件总线。 |
| `Status` | 1.0 | 健康状态上报。 |
| `Logger` | 1.0 | 分类日志上报。 |
| `ConfigurationApi` | 1.1 | 属主配置快照读写（路由 + 服务 + 设置）。 |
| `Routes` | 1.1 | 属主路由 CRUD 便捷方法。 |
| `Services` | 1.1 | 属主服务 CRUD 与启动 / 停止 / 重启。 |
| `Endpoints` | 1.1 | 已发布的服务端点租约。 |
| `Lifecycle` | 1.1 | 自身状态查询、请求 reload / unload。 |
| `FullConfiguration` | 1.2 | 全量 Host 配置读写。 |
| `Supervisor`（经 `IExtensionHostBridge13`） | 1.3（当前 Contracts 1.3.3） | 全局服务运行遥测与属主过滤；1.3.3 追加节点本地 `ResumeAsync` / `RestartAsync`。 |
| `RouteEvents`（经 `IExtensionHostBridge13`） | 1.3（当前 Contracts 1.3.3） | 路由观测订阅与动作钩子。 |
| `LogWriter`（经 `IExtensionHostBridge13`） | 1.3（当前 Contracts 1.3.3） | 自定义文本日志。 |
| `Management`（经 `IExtensionHostBridge13`） | 1.3（当前 Contracts 1.3.3） | 跨扩展记录管理、刷新、启用 / 禁用、reload、`ReloadSoon` 与显式删除。 |
| `DataDirectory`（经 `IExtensionHostBridge13`） | 1.3.2 | Host 配置的数据目录路径。 |
| `IExtensionRegistration.TryRegisterStreamingHandler` | 1.3.2 | 流式请求 / 响应处理器注册。 |
| `ExtensionCoreEventKind.ExtensionSettingsChanged` | 1.3.2 | 设置内容变更事件。 |
| `HostInfo`（经 `IExtensionHostBridge13`） | 1.3.3 | 本节点宿主状态即时快照。 |
| `ExtensionServiceLifecycleState.Waiting` | 1.3.3 | 服务等待节点本地启动前置的生命周期状态。 |
| `ExtensionManagementEntry.ContentHash` | 1.3.3 | 扩展内容 SHA-256 摘要，多节点漂移检测。 |

## 操作限制与已知限制

- **多节点内容偏斜检测。** 记录存储在 PostgreSQL 中，但 manifest 和程序集文件位于各节点本地。1.3.3 起 Host 用 `ContentHash` 摘要检测偏斜：节点文件与持久化摘要不一致的扩展会被隔离（quarantine）并上报节点状态 `ContentMismatch`；摘要暂时无法计算时报 `ContentHashMissing`。偏斜是**节点本地**判定，不影响其他节点；`status` / `doctor` 可查看各节点分歧。部署仍应保持节点文件一致，检测是兜底而非同步机制（见 [README](README.md#内容摘要与多节点部署)）。
- **写能力。** `EnableAsync`、`DisableAsync`、`ReloadAsync`、`DeleteRecordAsync`、`RequestRefreshAsync` 和 `ReloadSoon` 等改变持久化或发布状态的操作要求节点具备 configuration write capability。read-only 节点上的异步写操作返回 `Unsupported`，`ReloadSoon` 返回 `false`，并跳过 bootstrap 的持久化写入；`ListAsync` 是读取操作，但仍可能受到节点 snapshot / 磁盘状态的最终一致性影响。
- **生命周期回调重入。** 在扩展的 `StartAsync`、`StopAsync` 或 `OnPreviousStoppedAsync` 生命周期回调中调用 `EnableAsync`、`DisableAsync`、`ReloadAsync`、`DeleteRecordAsync` 或 `RequestRefreshAsync` 会与 publication gate 形成死锁风险，因此返回 `Unsupported`，不会等待或部分提交；`ReloadSoon` 是允许在这些回调中调度 reload 的例外。
- **其他回调上下文。** route handler、event subscriber 或 scheduler callback 中允许调用 `EnableAsync`、`DisableAsync`、`DeleteRecordAsync` 和 `RequestRefreshAsync`；无论目标是哪一个扩展，这些写操作的 publish 都会延迟到 callback 返回之后，以避免 generation drain 自己造成死锁。上述回调上下文中的 `ReloadAsync` 对任何目标都返回 `Unsupported`，应改用可在任意上下文调用的 `ReloadSoon`；后者只确认调度成功，不确认 reload 已完成。
- **List 的最终一致性。** `ListAsync` 每次都执行数据库 snapshot + 磁盘扫描，复杂度为 `O(snapshot + disk scan)`；读取期间的文件、数据库和运行时代次变化可能只在下一次调用反映。
- **停止是 best-effort。** 禁用或删除前的 owned-service stop 不能保证外部进程在每个瞬间都已退出；后续 publish 会继续收敛 desired set。
- **发布异常窗口。** 极少数 `Ready` 之后的替换失败窗口中，abort 不会重启已经停止的旧代次，可能留下短暂的 zombie 状态；后续发布会尝试重新收敛。
- **generation / snapshot 配对竞态。** S1 generation 与 S2 snapshot 之间可能出现短暂配对竞态；下一次 publish 会自我收敛。
- **属主映射读取失败。** 1.3.3 起读取服务 / 路由属主映射失败（数据库故障等）会使**整次 publish 失败**：节点保持当前已发布快照继续服务并标记数据库不可用，恢复后按下一次 publish 收敛。属主校验不再在读取失败时静默降级——宁可发布失败也不基于未知属主做启动 / 停止决策。

## 上限汇总

| 项目 | 上限 |
| --- | --- |
| 路由观测订阅数（每扩展） | 256 |
| 动作钩子数（每扩展） | 128 |
| 动作钩子回调时限 | 250 ms |
| 观测 / 钩子快照 body | 64 KiB |
| 自定义日志单条文本 | 4096 字符 |
| 流式请求体上限 | 由路由 `MaxRequestBodyBytes` 决定 |
| 流式响应 hook 缓冲 | hook 启用时完整响应体被缓冲到内存（受 Host 内存限制） |

## 从 1.2 迁移

- 1.2 及以前的桥契约保持不变，`IExtensionHostBridge` 的全部成员行为不变；`IExtensionHostBridge13` 是旁路接口。
- Contracts 包升级到 **1.3.2** 即可获得 API 1.3.2 的 `DataDirectory`、流式处理器 `IExtensionStreamingHandler`、设置变更事件 `ExtensionCoreEventKind.ExtensionSettingsChanged`；`ExtensionCapabilitySet` 的旧构造函数仍保留，`ExtensionManagement` 是可空的可选能力。
- Contracts 包升级到 **1.3.3** 追加 `Supervisor.ResumeAsync` / `RestartAsync`、桥属性 `HostInfo`、生命周期状态 `Waiting` 与 `ExtensionManagementEntry.ContentHash`；用 `ExtensionAbi.IsCompatible(new HostApiVersion(1, 3, 3), bridge13.ApiVersion)` 探测。
- 只需要在用到新能力的地方按本文开头的两步检查做探测；老代码不需要改。
- API version 仍是 1.3，没有另一个管理 API version。要求 Host API 1.3 的 manifest 不需要改写；新发现扩展的持久化默认状态、显式 refresh 和记录永不自动删除是本版本的生命周期规则。
- 流式处理器是**追加**的新接口；已有的 `IExtensionHandler` 行为、路由配置和目标绑定均不变。
