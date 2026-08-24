# API 1.1：属主配置与服务管理

1.1.0 相对 1.0 的变化：**新增五组属主（owner-scoped）能力**。桥上以下属性从「存在但返回 `Unsupported`」变为可用：

- `ConfigurationApi`：属主配置快照读写（路由 + 服务 + 设置）
- `Routes`：属主路由 CRUD 便捷方法
- `Services`：属主服务 CRUD 与启动 / 停止 / 重启
- `Endpoints`：已发布的服务端点租约查询
- `Lifecycle`：自身状态查询、请求 reload / unload

「属主」指 Host 自动把调用绑定到当前扩展：只能看到和修改自己的路由、服务与设置，无需也不能指定其他扩展。路由 target 也被限制为指向自己的 handler 或服务。

**兼容性**：在 1.0 Host 上，这些属性的每次调用都返回 `ConfigurationErrorCode.Unsupported`（`Lifecycle.Status` 为 `null`），不会抛异常。用 `host.ApiVersion >= new HostApiVersion(1, 1, 0)` 或 `ExtensionAbi.IsCompatible(new HostApiVersion(1, 1, 0), host.ApiVersion)` 做能力探测。

## 属主配置（ConfigurationApi）

`IExtensionConfigurationApi` 是读写自身路由、服务、设置的统一入口：

```csharp
public interface IExtensionConfigurationApi
{
    HostApiVersion ApiVersion { get; }
    ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask<ConfigurationWriteResult> ApplyAsync(long expectedVersion, ExtensionConfigurationChangeSet changes, CancellationToken cancellationToken = default);
    ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(CancellationToken cancellationToken = default);
    ValueTask<ConfigurationWriteResult> WriteSettingsAsync(long expectedVersion, ExtensionSettingsConfiguration settings, CancellationToken cancellationToken = default);
}
```

`ExtensionConfigurationSnapshot`：`Version`（全局配置版本，作为后续写入的 `expectedVersion`）、`Routes`、`Services`、`Settings`（可能为 `null`）。

`ExtensionConfigurationChangeSet` 描述一次原子变更，五个部分可以任意组合，不想改的部分传空数组 / `null`：

```csharp
var changes = new ExtensionConfigurationChangeSet(
    upserts: [myRoute],              // 新增或整体替换的路由
    removedRouteIds: [oldRouteId],   // 要删除的路由 ID
    serviceUpserts: [],              // 新增或整体替换的服务
    removedServiceIds: [],           // 要删除的服务 ID
    settings: null);                 // null = 设置保持不变
```

### 示例：读-改-写并处理并发冲突

```csharp
public async ValueTask<bool> AddRouteAsync(
    IExtensionHostBridge host,
    ExtensionRouteConfiguration route,
    CancellationToken cancellationToken)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var read = await host.ConfigurationApi.ReadAsync(cancellationToken);
        if (!read.IsSuccess)
        {
            return false; // 读取失败，按 read.Errors 里的错误码处理
        }

        var changes = new ExtensionConfigurationChangeSet(
            upserts: [route],
            removedRouteIds: [],
            serviceUpserts: [],
            removedServiceIds: [],
            settings: null);

        var write = await host.ConfigurationApi.ApplyAsync(read.Value!.Version, changes, cancellationToken);
        if (write.IsSuccess)
        {
            return true; // write.NewVersion 是提交后的全局版本
        }

        if (write.Errors.Any(e => e.Code == ConfigurationErrorCode.ConcurrencyConflict))
        {
            continue; // 有人先提交了，重新读取再试
        }

        return false; // Validation / StorageUnavailable 等，重试无意义
    }

    return false;
}
```

## 属主路由（Routes）

扩展创建的路由使用 `ExtensionRouteConfiguration`，target 只能是自己的 handler 或服务：

```csharp
public sealed record ExtensionRouteConfiguration
{
    public ExtensionRouteConfiguration(
        Guid id,                          // UUID v7
        bool enabled,
        RouteMatcherConfiguration matcher,
        ExtensionRouteTargetConfiguration target,  // ExtensionHandlerRouteTarget 或 ExtensionServiceRouteTarget
        int priority);
}
```

`RouteMatcherConfiguration` 描述匹配条件：

```csharp
var matcher = new RouteMatcherConfiguration(
    type: RouteMatcherType.Prefix,          // Exact / ExactCaseInsensitive / Prefix / PrefixCaseInsensitive / Regex
    pattern: "/api/hello",
    hostPatterns: ["example.com"],          // 空数组 = 任意 host
    methods: ["GET"]);                      // 空数组 = 任意 method
```

两种 target：

```csharp
new ExtensionHandlerRouteTarget("example.hello.greeting");  // 指向自己注册的 handler ID
new ExtensionServiceRouteTarget(myServiceId);               // 指向自己的服务 ID
```

`IExtensionRouteApi` 提供单条 CRUD 便捷方法（等价于构造单条变更的 `ConfigurationApi.ApplyAsync`）：

```csharp
ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken = default);
ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionRouteConfiguration route, CancellationToken cancellationToken = default);
ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid routeId, CancellationToken cancellationToken = default);
```

### 示例：为自己的 handler 发布一条路由

```csharp
public async ValueTask PublishRouteAsync(IExtensionHostBridge host, CancellationToken cancellationToken)
{
    var route = new ExtensionRouteConfiguration(
        id: Guid.CreateVersion7(),
        enabled: true,
        matcher: new RouteMatcherConfiguration(
            RouteMatcherType.Prefix, "/api/hello",
            hostPatterns: [],
            methods: ["GET"]),
        target: new ExtensionHandlerRouteTarget("example.hello.greeting"),
        priority: 0);

    var read = await host.ConfigurationApi.ReadAsync(cancellationToken);
    if (!read.IsSuccess)
    {
        return;
    }

    var write = await host.Routes.UpsertAsync(read.Value!.Version, route, cancellationToken);
    if (write.IsSuccess)
    {
        host.Logger.Report(ExtensionLogLevel.Information, "route-published");
    }
}
```

## 属主服务（Services）

扩展可以让 Host 托管自己的本地进程（微服务）。`ExtensionServiceConfiguration` 是进程启动的安全子集——**不包含环境变量**（环境变量属于敏感配置，扩展服务 API 不可见；需要环境变量时使用 1.2 的全量配置 API）：

```csharp
var service = new ExtensionServiceConfiguration(
    id: Guid.CreateVersion7(),
    enabled: true,
    fileName: "/opt/example/worker",                       // 必须是绝对路径
    argumentList: ["--port", "$PORT"],                     // $PORT 会被替换为分配到的端口
    workingDirectory: "/opt/example",                      // 必须是绝对路径
    startMode: ServiceStartMode.Lazy,                      // Eager = 配置生效即启动；Lazy = 首个请求触发
    restartPolicy: ServiceRestartPolicy.OnFailure,         // Never / OnFailure / Always
    healthCheck: new ServiceHealthCheckConfiguration(
        ServiceHealthCheckType.Http, "/healthz", TimeSpan.FromSeconds(3)),
    createdAt: DateTimeOffset.UtcNow,                      // 新建时由 Host 覆盖
    updatedAt: DateTimeOffset.UtcNow,                      // 新建时由 Host 覆盖
    version: 0);                                           // 新建时填 0，由 Host 分配
```

`IExtensionServiceApi`：

```csharp
ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(...);
ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionServiceConfiguration service, ...);
ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid serviceId, ...);
ValueTask<ExtensionServiceOperationResult> StartAsync(Guid serviceId, ...);
ValueTask<ExtensionServiceOperationResult> StopAsync(Guid serviceId, ...);
ValueTask<ExtensionServiceOperationResult> RestartAsync(Guid serviceId, ...);
```

`ExtensionServiceOperationResult`：`Succeeded`、`Code`、`ServiceId`。`Code` 的可能值：

| 值 | 含义 |
| --- | --- |
| `Accepted` | 已接受（启动 / 停止是异步的，不代表已完成）。 |
| `NotFound` | 服务不存在，或不属于当前扩展。 |
| `Conflict` | 与当前服务状态冲突（如重复启动）。 |
| `Unsupported` | 该服务不支持此操作。 |
| `Cancelled` | 操作在完成前被取消。 |
| `Failed` | 操作失败。 |
| `AlreadyStopped` | 服务本来就处于停止状态。 |
| `Reentrant` | 在扩展回调内重入调用（见下文生命周期一节）。 |

### 示例：部署并启动一个后端服务，再给它配一条路由

```csharp
public async ValueTask DeployBackendAsync(IExtensionHostBridge host, CancellationToken cancellationToken)
{
    var serviceId = Guid.CreateVersion7();
    var service = new ExtensionServiceConfiguration(
        serviceId, true,
        "/opt/example/backend", ["--urls", "http://127.0.0.1:$PORT"],
        "/opt/example",
        ServiceStartMode.Eager, ServiceRestartPolicy.OnFailure,
        new ServiceHealthCheckConfiguration(ServiceHealthCheckType.Tcp, null, TimeSpan.FromSeconds(3)),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0);

    var route = new ExtensionRouteConfiguration(
        Guid.CreateVersion7(), true,
        new RouteMatcherConfiguration(RouteMatcherType.Prefix, "/api/backend", [], []),
        new ExtensionServiceRouteTarget(serviceId),
        priority: 0);

    // 服务与路由在同一个原子变更里提交
    var read = await host.ConfigurationApi.ReadAsync(cancellationToken);
    if (!read.IsSuccess) return;

    var changes = new ExtensionConfigurationChangeSet(
        upserts: [route], removedRouteIds: [],
        serviceUpserts: [service], removedServiceIds: [],
        settings: null);
    var write = await host.ConfigurationApi.ApplyAsync(read.Value!.Version, changes, cancellationToken);
    if (!write.IsSuccess) return;

    // Eager 服务配置生效后会自动启动；也可以显式控制
    var start = await host.Services.StartAsync(serviceId, cancellationToken);
    host.Logger.Report(
        start.Succeeded ? ExtensionLogLevel.Information : ExtensionLogLevel.Warning,
        start.Succeeded ? "backend-started" : $"backend-start-{start.Code}".ToLowerInvariant());
}
```

## 端点租约（Endpoints）

Host 为每个运行中的服务分配 loopback 端口并以租约形式发布。`IExtensionEndpointApi` 提供只读查询：

```csharp
// 一次性拿到当前全部租约
foreach (var lease in host.Endpoints.Current)
{
    // lease.ServiceId / lease.Port / lease.ExpiresAt（UTC）
}

// 或按服务 ID 解析单个租约；未发布时为 null
ExtensionEndpointLease? lease = await host.Endpoints.ResolveAsync(serviceId, cancellationToken);
if (lease is not null)
{
    var address = $"http://127.0.0.1:{lease.Port}";
}
```

典型用途：扩展的 handler 需要绕过路由层直接调用自己的服务（例如做聚合）。注意租约会过期（`ExpiresAt`），服务重启后端口可能变化，不要长期缓存端口，每次调用前解析或使用 `Current`。

## 设置读写

`ConfigurationApi` 上的 `ReadSettingsAsync` / `WriteSettingsAsync` 读写扩展自己的 JSON 设置文档（1.0 的 `Configuration.Settings` 是它的只读快照视图）：

```csharp
var current = await host.ConfigurationApi.ReadSettingsAsync(cancellationToken);

var settings = new ExtensionSettingsConfiguration(
    extensionId: "example.hello",           // 必须是自己的 ID
    schemaVersion: 2,                        // 自己定义的 JSON 结构版本
    settingsJson: """{"greetingPrefix": "hi"}""",
    version: current.IsSuccess ? current.Value!.Version : 0);

var write = await host.ConfigurationApi.WriteSettingsAsync(settings.Version, settings, cancellationToken);
```

- `WriteSettingsAsync` 的 `expectedVersion` 是**设置文档自身**的版本（`ExtensionSettingsConfiguration.Version`），不是全局配置版本。
- `SettingsJson` 可以保存机密（如 API key），Host 不会把它写进日志；扩展自己也不要通过 `LogWriter` / 状态码输出它。

## 自身生命周期（Lifecycle）

`IExtensionLifecycleApi` 让扩展观察自己、并请求对自己 reload / unload：

```csharp
ExtensionLifecycleStatus? status = host.Lifecycle.Status;
if (status is not null)
{
    // status.State: Discovered / Loaded / Stopped / Failed / Unloading
    // status.HandlerCount / status.HasFallback
    // status.ActiveRequests / status.ActiveTasks
    // status.FailureCount / status.DroppedEvents
    // status.LastFailure: 最近一次失败的类别（ExtensionLifecycleFailureCode）
}

var reload = await host.Lifecycle.RequestReloadAsync(cancellationToken);
var unload = await host.Lifecycle.RequestUnloadAsync(cancellationToken);
```

`ExtensionLifecycleOperationResult`：`Succeeded`、`Code`（与上面服务操作类似的 `ExtensionLifecycleOperationCode`）、`Status`（操作后的状态，可能为 `null`）。

**重入限制**：在扩展自己的回调执行期间调用这两个方法会立即返回 `Code = Reentrant`，不会排队执行。被算作回调的包括：handler、fallback、事件订阅、受管后台任务（`Tasks.StartAsync`）、`StartAsync` / `StopAsync` / `OnPreviousStoppedAsync`——从这些上下文派生出的异步流同样继承该限制。这样设计是因为 reload 需要等待进行中的回调退出，回调内等待 reload 会互相死锁。

真正需要「让自己 reload」时，有一个更简单的办法：写入自己的设置文档（`WriteSettingsAsync`）。配置变更后 Host 会以新设置对新实例执行 reload 流程，效果与 `RequestReloadAsync` 相同，而且不受重入限制：

```csharp
var current = await host.ConfigurationApi.ReadSettingsAsync(cancellationToken);
var settings = new ExtensionSettingsConfiguration(
    "example.hello", 1, """{"restartNonce": "2026-08-23"}""",
    current.IsSuccess ? current.Value!.Version : 0);
await host.ConfigurationApi.WriteSettingsAsync(settings.Version, settings, cancellationToken);
// 提交成功后，Host 会 reload 当前扩展
```

reload 的行为（对扩展可见的部分）：新实例先 `StartAsync(Reloading: true)`，旧实例排干进行中的请求后 `StopAsync`，然后新实例收到 `OnPreviousStoppedAsync` 并接管 handler。切换窗口内指向该扩展 handler 的请求返回 `503`。新实例启动失败时旧实例继续运行。

## 从 1.0 迁移

- 1.0 扩展无需修改即可在 1.1 Host 上运行。
- 原来只能用 `Configuration.Settings` 读设置；1.1 起可以用 `ConfigurationApi.WriteSettingsAsync` 自己写设置。
- 需要动态路由 / 服务时，优先用本版本的属主 API 而不是 1.2 的全量 API：属主 API 自带身份绑定与 target 限制，误操作影响面小。
