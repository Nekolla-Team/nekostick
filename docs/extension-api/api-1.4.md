# API 1.4：完善扩展能力与全局管控

1.4.0 相对 1.3 的变化：完善扩展自身能力的可用性与可观测性，并加强全局管控面的信息暴露。具体追加：`ConfigurationErrorCode.NoSettings` 错误码、`ExtensionHostReadinessState.Publishing` 状态、`ExtensionManagementEntry` 上的扩展自定义上报状态字段、`RouteConfiguration.OwnerExtensionId` 路由属主标识。全部是追加式演进，不改变既有桥契约；要求 Host API 1.3 的既有扩展 manifest 仍然有效。

当前 Contracts 包版本为 **1.4.0-preview.3**（`HostApiVersion.Current` / `ExtensionAbi.Version` 均为 `1.4.0`）。探测方式：

```csharp
var has14 = ExtensionAbi.IsCompatible(new HostApiVersion(1, 4, 0), host.ApiVersion);
```

在 1.3.x 及更低的 Host 上：设置文档缺失仍返回 `NotFound`；`Readiness` 不会出现 `Publishing`（发布窗口内表现为 `Unready`）；`ReportedStatusKind` / `ReportedStatusCode` 恒为 `null`；`RouteConfiguration.OwnerExtensionId` 恒为 `null`。

> preview.3 起追加：manifest `dependencies` / `imports` 的 `optional` 字段与 `IExtensionHostBridge14.Dependencies` 依赖上下文 API，见下文「可选依赖与依赖上下文」。在 1.3.x 及更低的 Host 上：含 `optional` 键的 manifest 字段会被按未知字段拒绝；bridge 不会实现 `IExtensionHostBridge14`。

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

## 可选依赖与依赖上下文

### 可选依赖（optional）

`dependencies` 与 `imports` 的每一项都可加 `"optional": true`（缺省 `false`，旧 manifest 行为不变）：

```json
{
  "dependencies": [
    { "id": "example.geo", "versionRange": "^2.1.0", "optional": true }
  ],
  "imports": [
    {
      "contractId": "example.geo.lookup",
      "versionRange": "^2.0.0",
      "assemblyIdentity": "Example.Geo.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null",
      "typeIdentity": "Example.Geo.Contracts.IGeoLookup",
      "optional": true
    }
  ]
}
```

可选声明在「不满足」时跳过而不是让整批加载失败：

- 可选依赖的扩展不存在，或已安装版本不满足 `versionRange` → 跳过，扩展正常加载。
- 可选导入找不到提供方，或提供方契约版本不满足 `versionRange` → 跳过；运行时 `TryImport` 返回 `false`。版本范围在绑定时强制：即使校验放行了可选导入，`TryImport` 也绝不会交出声明范围之外的契约实例。
- 可选关系在目标存在时仍提供启动顺序保证（被依赖方/契约提供方先启动）；参与成环的可选边会被丢弃以打破循环，此时不再保证该方向的启动顺序（必需边成环仍然整批失败）。
- `assemblyIdentity` / `typeIdentity` 不匹配属于「冲突」而非「缺失」，即使可选也仍然整批失败。

注意 `optional` 是 1.4 新增的清单字段：在 1.3.x 及更低 Host 上，带 `optional` 键的依赖/导入项会按未知字段被拒绝，因此使用它的扩展应把 `requiredHostApiVersion` 设为 `>=1.4.0`。管理面行为同步：启用扩展时可选依赖不参与前置检查；停用扩展时只被可选依赖引用的扩展不再被阻止停用。

### 依赖上下文（Dependencies）

1.4 的 bridge sibling `IExtensionHostBridge14` 暴露 `Dependencies`（`IExtensionDependencyApi`），用于查询自己声明过的依赖的解析状态：

```csharp
if (host is IExtensionHostBridge14 bridge14)
{
    var geo = bridge14.Dependencies.GetDependencyContext("example.geo");
    switch (geo.State)
    {
        case ExtensionDependencyState.Satisfied:
            // geo.InstalledVersion 为已安装版本；可直接快捷导入契约：
            if (geo.TryImport<IGeoLookup>("example.geo.lookup", out var lookup) && lookup is not null)
            {
                _geo = lookup;
            }
            break;
        case ExtensionDependencyState.NotInstalled:
            // 扩展不存在
            break;
        case ExtensionDependencyState.VersionMismatch:
            // 存在但版本不满足 geo.VersionRange；geo.InstalledVersion 为实际版本
            break;
        case ExtensionDependencyState.NotDeclared:
            // 传入的 id 并未在自己的 dependencies 里声明
            break;
    }
}
- `GetDependencyContext` 永不返回 `null`；空白 id 抛 `ArgumentException`，其余未在 `dependencies` 里声明的 id 得到 `NotDeclared` 上下文。

- `IExtensionDependencyContext.TryImport` 是「状态检查 + 导入」的快捷方式：仅当状态为 `Satisfied` 时才尝试导入，其余状态一律 `false`；与 `IExtensionContractRegistry.TryImport` 一样只在启动窗口内有效。
- 上下文是扩展自己启动时刻的快照：依赖后续更新/重载要等到本扩展下次启动才反映，与共享契约交换语义一致。
- 提供方重载会级联重启使用方：运行时按本次运行期间实际发生的契约导入关系（仅内存记录，不持久化）在提供方重启成功后，按依赖顺序级联重启所有导入过其契约的扩展；管理面（Facade）触发的重载在同一代际内完成级联，新旧代交接的可用性保证不变。因此扩展不应假定契约实例可长期缓存——级联重启后应尽快重新导入。
- 协商版本低于 1.4 时 `Dependencies` 返回的能力桩对所有查询给出 `ExtensionDependencyState.Unavailable`；旧的外部 bridge 实现不会实现 `IExtensionHostBridge14`，用 `is` 探测即可。
