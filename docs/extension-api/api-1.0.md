# API 1.0：基础扩展 ABI

1.0.0 是第一个稳定版本，提供扩展的骨架能力：

- 入口点：`IExtensionEntrypoint` / `IExtensionEntry`、`IExtensionStartContext`
- 注册表：`IExtensionRegistration`（handler / fallback 注册与注销）
- 路由处理器：`IExtensionHandler`、`ExtensionHandlerRequest`、`ExtensionHandlerResponse`
- 全局 fallback：`IExtensionFallback`、`ExtensionFallbackReason`、`ExtensionFallbackResult`
- 设置读取：`IExtensionSettingsReader`（桥上的 `Configuration` 属性）
- 后台任务：`IExtensionTaskScheduler`
- 事件总线：`IExtensionEventPublisher`、`ExtensionEvent`
- 状态与日志：`IExtensionStatusSink`、`IExtensionLogger`
- 共享契约：`IExtensionContractRegistry`

> 1.0 不包含任何配置写能力。桥上 `ConfigurationApi` / `Routes` / `Services` /
> `Endpoints` / `Lifecycle` 在 1.0 Host 上存在但调用会返回
> `ConfigurationErrorCode.Unsupported`（生命周期状态为空）。

## 入口点

入口类型在 manifest 的 `entryType` 中声明，须实现 `IExtensionEntrypoint`，或直接继承别名接口 `IExtensionEntry`（二者等价，后者只是更短的名字）：

```csharp
public sealed class MyEntry : IExtensionEntry
{
    public ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
    {
        // 注册 handler、订阅事件、启动任务……
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        // 释放扩展持有的资源（无需注销 handler，Host 会处理）
        return ValueTask.CompletedTask;
    }

    public ValueTask OnPreviousStoppedAsync(CancellationToken cancellationToken)
    {
        // 仅在 reload 且旧实例已完全停止后调用；可实现，也可省略
        return ValueTask.CompletedTask;
    }
}
```

`StartAsync` 抛异常会导致加载失败；reload 场景下新实例 `StartAsync` 失败时旧实例继续运行。`context.Reloading` 区分首次启动与 reload：

```csharp
public async ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
{
    if (!context.Reloading)
    {
        // 只做一次的初始化，例如预热缓存
    }
    context.Registration.TryRegisterHandler(new MyHandler());
}
```

## 注册表：handler 与 fallback

`IExtensionRegistration`（从 `context.Registration` 获取）有四个方法：

```csharp
bool TryRegisterHandler(IExtensionHandler handler);
bool TryRegisterFallback(IExtensionFallback fallback);
bool TryUnregisterHandler(string handlerId);
bool TryUnregisterFallback();
```

要点：

- `HandlerId` 全局唯一。两个扩展（或同一扩展两个实例）注册相同 ID 时，后到者得到 `false`。入口点应检查返回值并决定是失败还是继续。
- fallback 全系统**最多一个**，重复注册返回 `false`。
- 注销是「未来派发的墓碑」：返回 `true` 后新请求不再进入该 handler，但正在执行的调用会正常跑完。
- 注册表不仅在启动期可用——扩展可以保存引用，在任何时候注册 / 注销：

```csharp
public sealed class MyHandler : IExtensionHandler
{
    private readonly IExtensionRegistration _registration;

    public MyHandler(IExtensionRegistration registration) => _registration = registration;

    public string HandlerId => "example.my.temporary";

    public ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request, CancellationToken cancellationToken)
    {
        // 只服务一次：处理完就把自己注销
        _registration.TryUnregisterHandler(HandlerId);
        return ValueTask.FromResult(new ExtensionHandlerResponse(200, body: "done"u8.ToArray()));
    }
}
```

## 路由处理器

handler 是扩展接收 HTTP 流量的唯一方式（加上可选的唯一 fallback）。路由配置把某个 URL 模式指向 handler 的稳定 ID 后，匹配的请求会适配为框架无关的 `ExtensionHandlerRequest` 交给扩展：

```csharp
public interface IExtensionHandler
{
    string HandlerId { get; }
    ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request, CancellationToken cancellationToken);
}
```

`ExtensionHandlerRequest` 的属性：

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `Method` | `string` | HTTP 方法，如 `GET`、`POST`。 |
| `Path` | `string` | 规范化后的请求路径，不含 query string。 |
| `Headers` | `IReadOnlyDictionary<string, ImmutableArray<string>>` | 请求头，键不区分大小写，多值保留。 |
| `Body` | `ImmutableArray<byte>` | 请求体字节（已完整读入）。 |
| `IsHttps` | `bool` | 请求是否经 HTTPS 到达（由前置反代终止 TLS 时按其转发结果）。 |

`ExtensionHandlerResponse` 用构造函数创建：状态码（100–599）、可选响应头、可选响应体。约束：header 名最长 256 字符，单个值最长 16 KiB。

一个完整的 JSON API 示例：

```csharp
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;

public sealed class TimeHandler : IExtensionHandler
{
    public string HandlerId => "example.util.time";

    public ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request, CancellationToken cancellationToken)
    {
        if (request.Method != "GET")
        {
            return ValueTask.FromResult(JsonResponse(405, new { error = "method not allowed" }));
        }

        var payload = new { utc = DateTimeOffset.UtcNow, path = request.Path };
        return ValueTask.FromResult(JsonResponse(200, payload));
    }

    private static ExtensionHandlerResponse JsonResponse(int status, object payload) =>
        new(
            status,
            new[] { new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["application/json"]) },
            JsonSerializer.SerializeToUtf8Bytes(payload));
}
```

注意事项：

- 多值响应头（如 `Set-Cookie`）不会被合并——`Headers` 字典中每个键可以携带多个值。
- `HandleAsync` 中抛出的未处理异常会被 Host 捕获：客户端得到 `500`，异常计入扩展的失败统计。
- `cancellationToken` 在客户端断开或扩展停止时触发，长耗时处理应响应它。

## 全局 fallback

当没有任何路由匹配（或静态文件未命中）时，Host 会调用系统内唯一的 fallback，并附带原因：

```csharp
public enum ExtensionFallbackReason
{
    NoRoute,            // 没有任何路由匹配
    HostMismatch,       // host 条件不匹配
    MethodMismatch,     // method 条件不匹配
    StaticNotFound,     // 静态文件不存在
    StaticIndexMissing  // 静态目录缺少 index.html
}
```

实现 `IExtensionFallback` 并注册：

```csharp
public sealed class NotFoundFallback : IExtensionFallback
{
    public ValueTask<ExtensionFallbackResult> HandleAsync(
        ExtensionFallbackRequest request, CancellationToken cancellationToken)
    {
        if (request.Reason == ExtensionFallbackReason.MethodMismatch)
        {
            // 不处理 method 不匹配，交还给 Host 的标准 404
            return ValueTask.FromResult(ExtensionFallbackResult.NotHandled);
        }

        var html = $"<h1>404</h1><p>{request.Request.Path} not found ({request.Reason})</p>";
        return ValueTask.FromResult(ExtensionFallbackResult.HandledResponse(
            new ExtensionHandlerResponse(
                404,
                new[] { new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["text/html"]) },
                Encoding.UTF8.GetBytes(html))));
    }
}

// StartAsync 中：
context.Registration.TryRegisterFallback(new NotFoundFallback());
```

返回 `NotHandled`、未注册 fallback、fallback 超时或抛异常时，客户端收到 Host 的标准 `404`。

## 设置读取（Configuration）

扩展可以拥有一个持久化的 JSON 设置文档（由配置 API 写入，见[api-1.1.md](api-1.1.md#设置读写)）。桥上的 `Configuration` 属性提供当前快照中的只读视图：

```csharp
var settings = context.Host.Configuration.Settings; // ExtensionSettingsConfiguration?，没有设置时为 null
if (settings is not null)
{
    using var doc = JsonDocument.Parse(settings.SettingsJson);
    var prefix = doc.RootElement.GetProperty("greetingPrefix").GetString();
}
```

`ExtensionSettingsConfiguration` 字段：`ExtensionId`、`SchemaVersion`、`SettingsJson`（原始 JSON 文本）、`Version`（该设置文档的乐观并发版本）。`SettingsJson` 可能含有敏感数据，不要写入日志。

这是一个代次快照，在扩展本次加载时固定。设置文档变更后，Host 会以 reload 的方式用新设置启动新实例（`StartAsync` 中 `context.Reloading` 为 `true`），新实例读到的就是新值。不想依赖 reload、希望随时拿到最新值的扩展，可以用 1.1 的 `ConfigurationApi.ReadSettingsAsync` 实时读取（见 [api-1.1.md](api-1.1.md#设置读写)）。

## 后台任务（Tasks）

`Host.Tasks.StartAsync` 启动一个受管理的后台任务。任务在扩展停止时收到取消，返回值 `false` 表示任务容量已满（拒绝接收）：

```csharp
public async ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
{
    var accepted = await context.Host.Tasks.StartAsync("cache-refresh", async token =>
    {
        while (!token.IsCancellationRequested)
        {
            RefreshCache();
            await Task.Delay(TimeSpan.FromMinutes(5), token);
        }
    });

    if (!accepted)
    {
        context.Host.Logger.Report(ExtensionLogLevel.Warning, "cache-task-rejected");
    }
}
```

约定：

- `taskName` 是不含敏感信息的任务类别名，用于日志与诊断。
- 回调内未处理的异常会计入扩展失败统计；循环体应自己捕获可恢复错误。
- 停止时 Host 会取消任务的 token 并在有界时间内等待退出，回调必须响应取消，否则会被强行放弃（不阻塞 Host 停止）。

## 事件总线（Events）

每个扩展拥有独立的、有序串行投递的内存事件队列。`ExtensionEvent` 是版本化的 JSON 事件：

```csharp
var evt = new ExtensionEvent("example.order.created", 1, """{"orderId": 42}""");
```

约束：`Type` 最长 256 字符；`Version` 从 1 开始（结构变化时递增，方便订阅方区分新旧格式）；`PayloadJson` 最长 1 MiB 且必须是 JSON 文本。

发布与订阅：

```csharp
public ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
{
    // 订阅：回调按发布顺序串行执行
    context.Host.Events.TrySubscribe(async (evt, token) =>
    {
        if (evt.Type == "example.order.created" && evt.Version == 1)
        {
            var orderId = JsonDocument.Parse(evt.PayloadJson).RootElement
                .GetProperty("orderId").GetInt32();
            await ProcessOrderAsync(orderId, token);
        }
    });

    // 发布：队列满时返回 false（事件被丢弃）
    if (!context.Host.Events.TryPublish(new ExtensionEvent("example.order.created", 1, """{"orderId": 42}""")))
    {
        context.Host.Logger.Report(ExtensionLogLevel.Warning, "event-dropped");
    }

    return ValueTask.CompletedTask;
}
```

要点：

- 队列容量上限 1024。满了以后**丢弃最新事件**，返回 `false`。
- 同一时刻只执行一个订阅回调；回调慢会堵住后续事件。
- 回调抛异常不影响其他订阅者，但计入扩展失败统计。
- 事件只在单节点内存中存在：不持久化、不跨节点、不保证送达。

## 状态上报（Status）与分类日志（Logger）

`Host.Status` 让扩展汇报自己的健康度，`Host.Logger` 让扩展记录预定义类别（只能填类别码，不能填自由文本；自由文本日志是 1.3 的 `LogWriter`）：

```csharp
// 状态：Healthy 或 Degraded + 一个不超过 128 字符的状态码
context.Host.Status.Report(new ExtensionStatus(ExtensionStatusKind.Healthy, "ready"));
context.Host.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "upstream-slow"));

// 日志：Information 或 Warning + 类别码
context.Host.Logger.Report(ExtensionLogLevel.Information, "started");
context.Host.Logger.Report(ExtensionLogLevel.Warning, "queue-nearly-full");
```

状态与日志类别都会出现在 Host 的结构化日志里，供运维检索；用稳定的短码（如 `cache-miss-storm`）而不要把动态数据拼进去。

## 共享契约

扩展之间不直接引用对方的程序集。需要跨扩展调用时，双方约定一个独立的契约程序集（只含接口），提供方在 manifest 声明 `exports`，使用方声明 `imports`，启动期通过 `IExtensionContractRegistry` 交换实例。

提供方 manifest：

```json
{
  "id": "example.geo",
  "version": "2.1.0",
  "exports": [
    {
      "contractId": "example.geo.lookup",
      "version": "2.0.0",
      "assemblyIdentity": "Example.Geo.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null",
      "typeIdentity": "Example.Geo.Contracts.IGeoLookup"
    }
  ]
}
```

使用方 manifest：

```json
{
  "id": "example.shop",
  "version": "1.0.0",
  "imports": [
    {
      "contractId": "example.geo.lookup",
      "versionRange": "^2.0.0",
      "assemblyIdentity": "Example.Geo.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null",
      "typeIdentity": "Example.Geo.Contracts.IGeoLookup"
    }
  ],
  "dependencies": [
    { "id": "example.geo", "versionRange": "^2.1.0" }
  ]
}
```

注意 `dependencies` 也要声明，保证提供方先加载。启动时代码：

```csharp
public ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
{
    // 提供方：导出
    context.Contracts.TryExport<IGeoLookup>("example.geo.lookup", new GeoLookup());

    // 使用方：导入；找不到兼容提供方时返回 false
    if (context.Contracts.TryImport<IGeoLookup>("example.geo.lookup", out var geo) && geo is not null)
    {
        _geo = geo;
    }
    return ValueTask.CompletedTask;
}
```

- `TryExport` / `TryImport` 的泛型参数必须与 manifest 声明的 `assemblyIdentity` + `typeIdentity` 完全一致，否则返回 `false`。
- 交换只在启动期进行；提供方更新后，使用方在自己下次启动（reload）时才会拿到新实例。
- 契约版本不兼容（导出版本不满足导入范围）时加载直接失败。

## 从 1.0 升级

- 1.0 是唯一基线，无前置版本。
- 迁移到更高版本 Host 不需要改代码：1.x 内所有新增能力都是追加式的。使用 1.1+ 能力前按各版本文档的「兼容性」小节探测即可。
