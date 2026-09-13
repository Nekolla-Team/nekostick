# API 1.4：完善扩展能力与全局管控

1.4.0 相对 1.3 的变化：完善扩展自身能力的可用性与可观测性，并加强全局管控面的信息暴露。具体追加：`ConfigurationErrorCode.NoSettings` 错误码、`ExtensionHostReadinessState.Publishing` 状态、`ExtensionManagementEntry` 上的扩展自定义上报状态字段。全部是追加式演进，不改变既有桥契约；要求 Host API 1.3 的既有扩展 manifest 仍然有效。

当前 Contracts 包版本为 **1.4.0-preview.1**（`HostApiVersion.Current` / `ExtensionAbi.Version` 均为 `1.4.0`）。探测方式：

```csharp
var has14 = ExtensionAbi.IsCompatible(new HostApiVersion(1, 4, 0), host.ApiVersion);
```

在 1.3.x 及更低的 Host 上：设置文档缺失仍返回 `NotFound`；`Readiness` 不会出现 `Publishing`（发布窗口内表现为 `Unready`）；`ReportedStatusKind` / `ReportedStatusCode` 恒为 `null`。

## 设置文档缺失的专用错误码（NoSettings）

刚安装的扩展还没有已持久化的设置文档，此时 `ReadSettingsAsync` 返回失败，错误码为专用的 `NoSettings`（与 `NotFound` 区分）。这是正常初始状态，用 `version: 0` 调 `WriteSettingsAsync` 创建即可：

```csharp
var current = await host.ConfigurationApi.ReadSettingsAsync(cancellationToken);
if (!current.IsSuccess && current.Errors.Any(e => e.Code == ConfigurationErrorCode.NoSettings))
{
    // 初始状态：文档不存在，用 version 0 创建
    await host.ConfigurationApi.WriteSettingsAsync(
        0, new ExtensionSettingsConfiguration("example.hello", 1, """{"greetingPrefix":"hi"}""", 0),
        cancellationToken);
}
```

| `ConfigurationErrorCode` | 含义 | 典型场景 |
| --- | --- | --- |
| `NoSettings` | 扩展还没有已持久化的设置文档。 | 读取自身 settings 而文档尚未创建；属正常初始状态，写入即创建。 |

其余错误码含义见 [api-1.3.md](api-1.3.md#结果与失败代码)。

## 发布中的 readiness（Publishing）

`ExtensionHostReadinessState` 追加 `Publishing`：一次配置发布正在进行、尚无已验证快照时 `Readiness` 为它，发布完成后推进到 `Ready` / `Degraded`。`Unready` 从此只表示「当前没有任何发布在推进」，「等一等会好」与「坏了」可以区分。

生命周期回调（如 `StartAsync`）运行在发布流程内部，此时 `Readiness` 必然是 `Publishing`（1.3.x Host 上为 `Unready`），绝不可能是 `Ready`——在那里等待 `Ready` 会死锁。生命周期回调里只把 `HostInfo` 当观测信息使用，不要用它门控扩展的主流程。

另一方面，扩展作用域的配置写（routes、services、自身 settings）从 `StartAsync` 起就是安全的：发布会为正在启动的扩展保持 staged 写通道，无需等待 Host ready。这条保证从 1.4.0 起写入契约（`IExtensionConfigurationApi` 的备注）。

## 扩展状态上报的持久观测

扩展通过 `Status.Report(new ExtensionStatus(kind, code))` 上报的自定义状态不再被丢弃：

- Host 记录最近一次上报，并暴露为 `ExtensionManagementEntry.ReportedStatusKind` / `ReportedStatusCode`（`Management.ListAsync` 返回；从未上报过为 `null`）。
- 每次非 `Healthy` 上报都会写入一条 Warning 日志；从异常恢复到 `Healthy` 时写一条 Information 日志。

上报只影响观测面，不改变扩展的加载状态、路由或失败统计。
