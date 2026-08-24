# API 1.3：遥测、路由观测与自定义日志

1.3.0 相对 1.2 的变化：**新增旁路桥 `IExtensionHostBridge13`**，在不改动 1.2 桥契约的前提下追加三组能力：

- `Supervisor`：全局服务运行遥测（只读）
- `RouteEvents`：路由观测订阅与可干预转发的动作钩子
- `LogWriter`：Host 署名的自定义文本日志

## 能力探测（必读）

`IExtensionHostBridge13` 继承 `IExtensionHostBridge`，1.3 Host 的内置桥同时实现两个接口。使用 1.3 能力前做两步检查：

```csharp
// 第一步：桥是否实现了 1.3 旁路接口
if (context.Host is not IExtensionHostBridge13 bridge13)
{
    // 外部桥实现（如测试替身）只实现了 1.2 契约
    return;
}

// 第二步：协商出的版本是否真的到 1.3
if (!ExtensionAbi.IsApi13Supported(context.Host.ApiVersion))
{
    // 桥类型存在，但 Host 版本低于 1.3：能力处于「不受支持」状态
    return;
}
```

不满足条件时调用 1.3 成员的行为是**安全的降级**，不会抛异常：

- `Supervisor.ReadAsync` / `GetAsync` 返回 `ConfigurationErrorCode.Unsupported`；
- `RouteEvents.TrySubscribe` / `TryRegisterHook` 返回 `false`；
- `LogWriter.WriteText` 静默丢弃文本。

## 服务运行遥测（Supervisor）

`IExtensionSupervisorApi` 提供全节点服务的运行时快照，按服务 ID 查询：

```csharp
ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(CancellationToken cancellationToken = default);
ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(Guid serviceId, CancellationToken cancellationToken = default);
```

`ExtensionServiceRuntimeSnapshot` 字段：

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `ServiceId` | `Guid` | 服务 ID。 |
| `ProcessId` | `int?` | 当前操作系统进程号；未知时为 `null`。 |
| `StartedAt` | `DateTimeOffset?` | 当前进程代次的启动时间（UTC）。 |
| `Uptime` | `TimeSpan?` | 当前进程代次的运行时长。 |
| `LifecycleState` | 枚举 | `Unknown` / `Disabled` / `Starting` / `Running` / `Stopping` / `Failed`。 |
| `HealthState` | 枚举 | `Unknown` / `Healthy` / `Unhealthy`。 |
| `ForwardedRequestCount` | `long` | 累计转发请求数。 |
| `ActiveForwardedRequestCount` | `long` | 正在转发的请求数。 |
| `LastUpdatedAt` | `DateTimeOffset?` | 该遥测的最后更新时间（UTC）。 |
| `LastHealthAt` | `DateTimeOffset?` | 最近一次健康检查时间（UTC）。 |

这是全局视图（不限于自己的服务），适合做监控面板、健康巡检类扩展：

```csharp
public async ValueTask ReportUnhealthyAsync(IExtensionHostBridge13 bridge, CancellationToken cancellationToken)
{
    var read = await bridge.Supervisor.ReadAsync(cancellationToken);
    if (!read.IsSuccess) return;

    foreach (var service in read.Value!)
    {
        if (service.LifecycleState == ExtensionServiceLifecycleState.Failed ||
            service.HealthState == ExtensionServiceHealthState.Unhealthy)
        {
            bridge.LogWriter.WriteText(
                ExtensionLogLevel.Warning,
                $"service {service.ServiceId} is {service.LifecycleState}/{service.HealthState}");
        }
    }
}
```

## 路由观测订阅（RouteEvents.TrySubscribe）

订阅后，每条路由在「转发前」（trigger）和「转发完成后」（return）各产生一条观测，通过扩展自己的标准事件队列投递（与 1.0 的事件总线同一条队列，有序、串行、best-effort）：

```csharp
var accepted = bridge.RouteEvents.TrySubscribe(async (evt, token) =>
{
    // evt.Type 是 "route.trigger" 或 "route.return"（ExtensionRouteEventTypes.Trigger / .Return）
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
bridge.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, token) =>
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
bridge.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Return, (context, token) =>
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
bridge.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, token) =>
{
    if (CircuitBreaker.IsOpen(context.RouteId))
    {
        // 显式取消；客户端会得到 Host 的错误响应
        return ValueTask.FromResult(new ExtensionRouteHookResult(
            ExtensionRouteHookAction.CancelForwarding)); // 本次转发被取消
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
bridge.LogWriter.WriteText(ExtensionLogLevel.Information, "cache warmed: 128 entries");
bridge.LogWriter.WriteText(ExtensionLogLevel.Warning, $"upstream latency {latencyMs}ms");
```

- 单条文本最长 4096 字符（`ExtensionLogLimits.MaximumTextLength`）。
- 级别仍然只有 `Information` / `Warning` 两档。
- 不要写入机密（设置文档、环境变量、Cookie、Authorization 等）。

## 上限汇总

| 项目 | 上限 |
| --- | --- |
| 路由观测订阅数（每扩展） | 256 |
| 动作钩子数（每扩展） | 128 |
| 动作钩子回调时限 | 250 ms |
| 观测 / 钩子快照 body | 64 KiB |
| 自定义日志单条文本 | 4096 字符 |

## 从 1.2 迁移

- 1.2 及以前的契约一字未动，`IExtensionHostBridge` 的全部成员行为不变。
- 只需要在用到新能力的地方按本文开头的两步检查做探测；老代码不需要改。
