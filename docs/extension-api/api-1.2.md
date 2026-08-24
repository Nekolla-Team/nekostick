# API 1.2：全量配置读写

1.2.0 相对 1.1 的变化：**新增 `IExtensionFullConfigurationApi`**（桥上的 `FullConfiguration` 属性），允许可信扩展读取并整体替换 Host 的全部业务配置。

**兼容性**：在 1.1 及更低的 Host 上，`FullConfiguration` 的调用返回 `ConfigurationErrorCode.Unsupported`。探测方式：

```csharp
var has12 = ExtensionAbi.IsCompatible(new HostApiVersion(1, 2, 0), host.ApiVersion);
```

## 什么时候用它

| 需求 | 推荐 API |
| --- | --- |
| 管理自己扩展的路由 / 服务 / 设置 | 1.1 属主 API（`ConfigurationApi` / `Routes` / `Services`） |
| 读取或修改其他扩展、静态文件路由、全局设置 | 1.2 `FullConfiguration` |
| 给服务配置环境变量（含机密） | 1.2 `FullConfiguration`（属主服务 DTO 不含环境变量） |

`FullConfiguration` 不做任何属主过滤，读取结果包含**所有**扩展的设置文档和**所有**服务的环境变量——这些都可能是明文机密。它是管理后台、配置迁移、灾难恢复类扩展的工具，普通业务扩展应该用 1.1 的属主 API。

## 接口

```csharp
public interface IExtensionFullConfigurationApi
{
    ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask<ConfigurationWriteResult> ReplaceAsync(long expectedVersion, ConfigurationChangeSet changes, CancellationToken cancellationToken = default);
}
```

### 读取：`HostConfigurationSnapshot`

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `Version` | `long` | 全局乐观并发版本，作为 `ReplaceAsync` 的 `expectedVersion`。 |
| `GlobalSettings` | `GlobalSettingsConfiguration` | 全局业务设置（端口范围、请求限制、代理超时与重试等）。 |
| `Routes` | `ImmutableArray<RouteConfiguration>` | 全部路由。 |
| `Services` | `ImmutableArray<ServiceConfiguration>` | 全部服务（含环境变量）。 |
| `ExtensionRecords` | `ImmutableArray<ExtensionRecordConfiguration>` | 全部扩展的安装记录。 |
| `ExtensionSettings` | `ImmutableArray<ExtensionSettingsConfiguration>` | 全部扩展的设置文档。 |

```csharp
var read = await host.FullConfiguration.ReadAsync(cancellationToken);
if (read.IsSuccess)
{
    var snapshot = read.Value!;
    foreach (var route in snapshot.Routes.Where(r => r.Enabled))
    {
        Console.WriteLine($"{route.Matcher.Pattern} -> {route.Target.Type} (priority {route.Priority})");
    }
}
```

### 替换：`ConfigurationChangeSet`

`ReplaceAsync` 是**全量替换**：变更集里没有出现的路由、服务、扩展记录、扩展设置会被删除（仍受 Host 校验约束，例如不能删除仍被路由引用的服务）。典型流程是「读快照 → 改副本 → 整体写回」：

```csharp
var changes = new ConfigurationChangeSet(
    globalSettings: snapshot.GlobalSettings,       // 不变的部分原样放回
    routes: [.. snapshot.Routes, newRoute],        // 追加一条路由
    services: snapshot.Services,
    extensionRecords: snapshot.ExtensionRecords,
    extensionSettings: snapshot.ExtensionSettings);

var write = await host.FullConfiguration.ReplaceAsync(snapshot.Version, changes, cancellationToken);
```

五个集合分别对应快照的五个部分。和 1.1 的增量 `ApplyAsync` 不同，这里没有「upsert + 删除列表」——**集合就是最终状态**。

## 完整配置 DTO

全量 API 使用与属主 API 不同的一组 DTO，字段更全。

### `RouteConfiguration`

在属主版路由的基础上增加：

| 属性 | 说明 |
| --- | --- |
| `Target` | 三种之一：`MicroserviceRouteTargetConfiguration(serviceId)`、`StaticFileRouteTargetConfiguration(rootPath)`、`ExtensionHandlerRouteTargetConfiguration(handlerId)`。 |
| `Forwarding` | `ForwardingConfiguration(mode, replaceTemplate)`；`mode` 为 `Preserve` / `Strip` / `Replace`，`Replace` 时必须给模板。 |
| `RequestHeaderRewrites` / `ResponseHeaderRewrites` | `HeaderRewriteConfiguration(operation, name, value)` 列表，`operation` 为 `Remove` / `Set` / `Add`。 |
| `MetadataJson` | 扩展自用的 JSON 元数据（Host 只校验合法性）。 |
| `CreatedAt` / `UpdatedAt` / `Version` | 时间戳与实体乐观版本。 |
| `ClientIpRatePolicy` | 可选的按客户端 IP 限流策略；`null` 继承全局。 |
| `MaxRequestBodyBytes` / `MaxRequestHeaderBytes` / `MaxConcurrentRequests` / `RequestReadTimeout` | 可选的路由级资源限制；`null` 继承全局。 |
| `ProxyRetries` | 可选的路由级代理重试策略；`null` 继承全局。 |

### `ServiceConfiguration`

比属主版多一个 `Environment`（`ImmutableDictionary<string, string>`，环境变量覆盖，**视为敏感数据**），其余字段含义相同。

### `GlobalSettingsConfiguration`

| 属性 | 默认值 | 说明 |
| --- | --- | --- |
| `AutoPortRangeStart` / `AutoPortRangeEnd` | 20000 / 29999 | 服务自动分配端口的闭区间。 |
| `MaxRequestBodyBytes` | 30 MiB | 请求体上限（硬上限也是 30 MiB）。 |
| `MaxRequestHeaderBytes` | 32 KiB | 请求头上限（硬上限也是 32 KiB）。 |
| `MaxConcurrentRequests` | 1024 | 节点并发请求上限。 |
| `RequestReadTimeout` | 30 秒 | 请求体读取超时。 |
| `ConfigurationPollInterval` | 30 秒 | 节点轮询配置版本的间隔。 |
| `TrustedProxyCidrs` | 空 | 可信代理 CIDR 列表。 |
| `ProxyTimeouts` | 见 `ProxyTimeoutConfiguration.Default` | 连接 10s / 首字节 30s / 总时长 100s / WebSocket 空闲 120s。 |
| `ClientIpRatePolicy` | `null`（不限流） | 全局按客户端 IP 限流。 |
| `ProxyRetries` | 见 `ProxyRetryConfiguration.Default` | 默认 0 次重试，退避 200ms–2s。 |

### 示例：新增一条静态文件路由

```csharp
public async ValueTask<bool> AddStaticRouteAsync(
    IExtensionHostBridge host, CancellationToken cancellationToken)
{
    var read = await host.FullConfiguration.ReadAsync(cancellationToken);
    if (!read.IsSuccess) return false;

    var snapshot = read.Value!;
    var route = new RouteConfiguration(
        id: Guid.CreateVersion7(),
        enabled: true,
        matcher: new RouteMatcherConfiguration(RouteMatcherType.Prefix, "/docs", [], []),
        target: new StaticFileRouteTargetConfiguration("/srv/www/docs"),
        priority: 0,
        forwarding: new ForwardingConfiguration(ForwardingMode.Strip, null),
        requestHeaderRewrites: [],
        responseHeaderRewrites: [
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Set, "Cache-Control", "max-age=3600")
        ],
        metadataJson: "{}",
        createdAt: DateTimeOffset.UtcNow,
        updatedAt: DateTimeOffset.UtcNow,
        version: 0);

    var changes = new ConfigurationChangeSet(
        snapshot.GlobalSettings,
        [.. snapshot.Routes, route],
        snapshot.Services,
        snapshot.ExtensionRecords,
        snapshot.ExtensionSettings);

    var write = await host.FullConfiguration.ReplaceAsync(snapshot.Version, changes, cancellationToken);
    return write.IsSuccess;
}
```

### 示例：修改全局代理超时

```csharp
var snapshot = (await host.FullConfiguration.ReadAsync(cancellationToken)).Value!;

var newSettings = new GlobalSettingsConfiguration(
    version: snapshot.GlobalSettings.Version,
    autoPortRangeStart: snapshot.GlobalSettings.AutoPortRangeStart,
    autoPortRangeEnd: snapshot.GlobalSettings.AutoPortRangeEnd,
    proxyTimeouts: new ProxyTimeoutConfiguration(
        connectTimeout: TimeSpan.FromSeconds(5),
        httpTotalTimeout: TimeSpan.FromSeconds(60)));
// 其余可选参数不传即取业务默认值——如果要保留现状，请逐个从旧对象复制

var changes = new ConfigurationChangeSet(
    newSettings, snapshot.Routes, snapshot.Services,
    snapshot.ExtensionRecords, snapshot.ExtensionSettings);
await host.FullConfiguration.ReplaceAsync(snapshot.Version, changes, cancellationToken);
```

> 注意：`GlobalSettingsConfiguration` 的构造参数都有业务默认值。只想改一两项时，
> 请把其余项从旧对象逐个复制过去，否则会无意把其他设置重置成默认值。

## 错误处理

错误码与 1.1 相同（见[总述](README.md#结果类型不用异常表达业务失败)）。全量替换特有的常见失败：

- `Validation`：变更集里有非法引用（如路由指向不存在的服务）、删除仍被引用的服务、静态根路径不是绝对路径等。
- `ConcurrencyConflict`：`expectedVersion` 过期——先重新 `ReadAsync`。

## 附：`IHostConfigApi`

Contracts 中还有一个 `IHostConfigApi`（快照读写 + 按扩展 ID 读写设置）。它是 Host 侧使用的同语义契约，**不会**通过扩展桥暴露给扩展；扩展代码请使用 `FullConfiguration`（或 1.1 属主 API）。实现自定义 Host 组件或测试替身时才需要关注它。

## 从 1.1 迁移

- 属主 API 全部保持不变，优先继续使用。
- 需要跨扩展管理配置时切换到 `FullConfiguration`；同一批变更不要混用两个 API 提交（版本都是全局的，混用容易触发 `ConcurrencyConflict`）。
