# API 1.4：完善扩展能力、全局管控与微服务输出流

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
- 提供方重载会级联重启使用方：运行时按本次运行期间实际发生的契约导入关系（仅内存记录，不持久化）在提供方重启成功后，按依赖顺序级联重启所有导入过其契约的扩展；管理面（Facade）触发的重载在同一代际内完成级联，新旧代交接的可用性保证不变。
- **缓存契约引用是安全的**：`TryImport` 交出的是运行时隔离代理而非提供方实例本体。提供方重载/代际交接期间，代理调用挂起等待并在恢复后自动重绑到新实例；提供方被卸载或永久不可用时，代理抛 `InvalidOperationException`（信息含提供方 id）。详见下文「调用隔离与重载挂起」。
- 协商版本低于 1.4 时 `Dependencies` 返回的能力桩对所有查询给出 `ExtensionDependencyState.Unavailable`；旧的外部 bridge 实现不会实现 `IExtensionHostBridge14`，用 `is` 探测即可。

## 调用隔离与重载挂起

所有跨扩展调用（路由 / streaming / fallback 处理器与共享契约）都经过按扩展标识建立的运行时隔离层。它改变的是重载窗口内的调用语义：

- **挂起而非拒绝**：扩展开始重载（含级联目标与代际交接中被替换/移除的扩展）时，发往它的新请求挂起等待，直到重载提交；恢复后请求自动派发到新实例。调用方无需为重载写重试循环。
- **等待有界**：挂起等待略短于生命周期超时（约 25 秒）。超时后路由/streaming 调用得到 `Unavailable`，契约调用抛 `InvalidOperationException`；有界等待保证提供方的排空截止不被无限阻塞。
- **同步契约方法会阻塞调用线程**：返回 `Task`/`ValueTask` 的契约方法异步等待，普通同步方法在提供方重载期间阻塞当前线程（同样有约 25 秒上界）。不要在持有调用方自有锁的情况下调用同步契约方法。
- **级联重载整体预挂起**：提供方重载会先同时挂起提供方与全部传递依赖方，再按依赖顺序依次重启。依赖方的不可用窗口因此以上游各重载耗时之和为上界——链路越长，尾部依赖方等待越久，但任何一环结束后请求都会落到新实例上。
- **永久不可用快速失败**：提供方被卸载、停止或重载失败移除后，契约代理立即抛 `InvalidOperationException`（信息含提供方 id），路由调用得到 `Unavailable`；事件回调是 fire-and-forget，排空窗口内跳过的投递计入扩展的丢弃事件统计。

## 微服务输出流（ServiceOutput）

API 1.4.0 在 `IExtensionHostBridge14` 上提供 `ServiceOutput` 能力，包含原始 stdout/stderr 字节流与有序输出/生命周期日志订阅两个层次；扩展可读取当前 Host 配置中任意已配置 service 的输出。`HostApiVersion.Current` / `ExtensionAbi.Version` 均为 `1.4.0`。

### 能力探测（必读）

使用服务输出能力前，必须同时检查 bridge 类型和协商版本：

```csharp
static readonly HostApiVersion Api14Minimum = new(1, 4, 0);

if (context.Host is not IExtensionHostBridge14 bridge14 ||
    !ExtensionAbi.IsCompatible(Api14Minimum, bridge14.ApiVersion))
{
    // Host 未实现 API 1.4，或协商版本低于 1.4.0
    return;
}

var output = bridge14.ServiceOutput;
```

旧版 Host（API 1.3 及更低）不会实现 `IExtensionHostBridge14`；对于实现了该接口但协商版本低于 `1.4.0` 的 Host，原始流操作返回 `ExtensionServiceOutputCode.Unsupported`，有序日志订阅返回 `ExtensionServiceLogCode.Unsupported`，不会因业务不支持而抛异常。

### 第一层：原始输出流（OpenStreamAsync）

#### 输出流类型

`ExtensionServiceOutputStream` 选择要读取的标准流：

| 值 | 含义 |
| --- | --- |
| `Stdout` | 微服务的标准输出。 |
| `Stderr` | 微服务的标准错误输出。 |

服务输出 API 传递原始 bytes，不提供行边界或文本解码保证。Host helper 写入管道的 `NK_*` 协议 marker（例如 `NK_READY`、`NK_FAILED`、`NK_UNAVAILABLE`）由 Host 在 fan-out 前消费，不会出现在扩展收到的 stdout/stderr 数据中。

#### `IExtensionServiceOutputApi`（两层能力）

`IExtensionHostBridge14.ServiceOutput` 提供两个层次的服务输出访问：本层 `OpenStreamAsync` 按进程代次读取原始流，第二层 `SubscribeAsync` 提供有序输出与生命周期日志。

```csharp
public interface IExtensionServiceOutputApi
{
    ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken = default);

    ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
        Guid serviceId,
        IExtensionServiceLogSink sink,
        long? sinceSequence = null,
        CancellationToken cancellationToken = default);
}
```

#### `OpenStreamAsync`：读取原始输出流

成功时返回 `ExtensionServiceOutputStreamResult`，其中 `Succeeded` 为 `true`、`Code` 为 `Opened`，`Stream` 为调用方负责释放的可读流：

```csharp
var result = await bridge14.ServiceOutput.OpenStreamAsync(
    serviceId,
    ExtensionServiceOutputStream.Stdout,
    cancellationToken);

if (!result.Succeeded || result.Stream is null)
{
    // 根据 result.Code 处理 NotFound / NotRunning / Unsupported / Failed
    return;
}

await using var output = result.Stream;
var buffer = new byte[8192];
while (await output.ReadAsync(buffer, cancellationToken) is > 0)
{
    // buffer 中是任意边界的原始 stdout bytes
}
```

每次调用得到只读、不可 seek、由调用方释放的流，并绑定到打开时 service 的当前进程代次；进程退出后流结束，不会在 service restart 后自动切换到新代次。打开前已经产生的输出不会重放；若进程在绑定后立即退出，操作仍可返回 `Opened`，随后读到 EOF。Host teardown 或 fan-out 读取故障会以经过清理的通用 `IOException` 结束读取，不会泄漏 Host 内部异常。流缓冲溢出可能造成静默字节缺口；该 API 不提供缺口位置或数量的回调。

Host 在调用开始时根据当前配置检查 service 是否存在。service 从配置移除后，已打开的流仍接收其绑定代次的输出，直到该代次退出或 Host teardown。

#### 结果与失败代码

业务失败通过结果对象和 `ExtensionServiceOutputCode` 表达；取消仍按 .NET `CancellationToken` 约定传播。失败结果的 `Stream` 为 `null`。

| `ExtensionServiceOutputCode` | 含义 |
| --- | --- |
| `Opened` | 已打开 stream；`Succeeded` 为 `true`。 |
| `NotFound` | service 未配置，或 service ID 无效。 |
| `NotRunning` | service 已配置，但 executor 没有可绑定的 live / retained output pump。 |
| `Unsupported` | 协商版本、Host 能力或当前 executor 不支持服务输出。 |
| `Failed` | Host 在配置存在性检查之后执行打开时发生其他运行时失败。 |
| `None` | 枚举的默认 / 保留值，不表示成功打开。 |

这些结果码属于业务结果，不应通过异常控制正常的不存在、未运行或不支持分支。

### 第二层：订阅/有序日志（SubscribeAsync）

`IExtensionHostBridge14.ServiceOutput.SubscribeAsync` 为一个当前配置的 service 提供合并 stdout/stderr 原始字节与进程生命周期事件的有序日志订阅。它是 `ServiceOutput` 的订阅层：每个 service 的日志有单调序号，可有限度地重放，并用 `Gap` 明确报告丢失范围。

旧版 Host（API 1.3 及更低）不会实现 `IExtensionHostBridge14`。对于实现了该接口但协商版本低于 `1.4.0` 的 Host，`SubscribeAsync` 返回 `Succeeded == false`、`Code == ExtensionServiceLogCode.Unsupported` 且 `Subscription == null`，不把能力不支持作为异常。

#### 订阅 sink 与结果

```csharp
public interface IExtensionServiceLogSink
{
    void OnEntry(ExtensionServiceLogEntry entry);
    void OnCompleted();
}
```

订阅成功时，`ExtensionServiceLogSubscriptionResult` 的 `Succeeded` 为 `true`、`Code` 为 `Subscribed`，并包含调用方负责释放的 `IExtensionServiceLogSubscription`；失败结果不包含订阅句柄。同一订阅的 `OnEntry` / `OnCompleted` 回调在后台线程串行执行；sink 抛出的异常由 Host 隔离。慢 sink 可能耗尽其有界投递队列并造成 `Gap`，不会要求 sink 阻塞输出 pump。

#### 日志条目与序号

每个 service 的 stdout/stderr 输出与生命周期条目共享同一条单调递增 `Sequence`，因此可以按序跨进程代次观察。`CurrentState` 是订阅时的状态 marker，不带序号；扩展实例卸载或重载导致的本地 `Termination` 条目也不带 service-wide 序号。输出条目的 `Data` 是不可变原始 bytes，`Stream` 标识 stdout 或 stderr。

| `ExtensionServiceLogEntryKind` | 含义 |
| --- | --- |
| `Output` | 一段 stdout/stderr 原始 bytes；包含 `Stream`、`Data`、`ProcessInstanceId` 和 `AttemptNumber`。 |
| `GenerationStarted` | 一个新的进程代次已启动。 |
| `ProcessExited` | 一个进程代次已退出；`ProcessExitCode` 在可报告时提供。 |
| `StartupFailed` | 启动尝试失败；包含 `FailureStage`、`FailureCode` 和可选的有界安全 `FailureReason`。 |
| `CurrentState` | 订阅创建时观察到的 `ExtensionServiceLifecycleState`；`Sequence` 为 `null`。 |
| `Gap` | 一段日志序号缺失；`FirstMissingSequence` / `LastMissingSequence` 给出缺失范围。 |
| `Termination` | 日志 feed 因生命周期原因终止；通过 `TerminationReason` 说明原因。 |

每个条目还包含 `ServiceId` 与 UTC `Timestamp`；按条目种类可包含 `ProcessInstanceId`、`AttemptNumber`、`ProcessExitCode`、`LifecycleState`、`FailureStage`、`FailureCode`、`FailureReason`、`FirstMissingSequence`、`LastMissingSequence` 或 `TerminationReason`。

#### 游标重放与有界缓冲

未提供 `sinceSequence` 时，订阅先收到当前 `CurrentState` marker，再收到保留条目和后续实时条目。提供游标时只重放 `Sequence` 大于该游标的条目；游标超出当前最新序号返回 `InvalidCursor`，负数游标返回 `InvalidArgument`。如果游标之前的条目已被淘汰，Host 会在后续条目之前发送 `Gap`，报告缺失的序号范围。

Host 为每个 service 保留按字节计量、超限时丢弃最旧条目的 replay buffer。默认预算为 `1_048_576` bytes（1 MiB），由 Host 选项 `HostRuntimeOptions.ServiceLogBufferByteBudget` 配置。预算包括固定条目元数据与有界 failure reason；单条目超过保留预算时仍投递给当前实时订阅者，但后续订阅会通过 `Gap` 得知缺失。慢回调的有界投递队列也会丢弃最旧条目，并在后续有序条目前发送 `Gap`。

#### 终止通知与订阅释放

`ExtensionServiceLogTerminationReason` 的值为：

| 值 | 含义 |
| --- | --- |
| `ExtensionUnloaded` | 属主扩展实例已卸载或重载。 |
| `ServiceDisabled` | service 已在 Host 配置中禁用。 |
| `ServiceRemoved` | service 已从 Host 配置中移除。 |
| `HostShutdown` | Host 正在关闭。 |

feed 正常终止时，sink 先收到 `Termination` 条目，再收到一次 `OnCompleted()`。订阅句柄实现 `IExtensionServiceLogSubscription : IDisposable, IAsyncDisposable`：`Dispose()` 以 best-effort 方式解除订阅，但一个回调仍可能正在执行；`DisposeAsync()` 还会等待该订阅的 callback quiescence。不得在同一订阅的回调中同步等待自己的 `DisposeAsync()`，否则会发生 self-wait deadlock。

`ExtensionServiceLogCode` 的值为：

| 值 | 含义 |
| --- | --- |
| `None` | 默认 / 保留值，不表示订阅成功。 |
| `Subscribed` | 订阅已创建。 |
| `InvalidArgument` | service ID、sink 或订阅参数无效。 |
| `InvalidCursor` | 游标大于该 service 当前最新日志序号。 |
| `NotFound` | service 不在当前 Host 配置中。 |
| `Unsupported` | Host 未提供 service log capture，或协商版本不支持该能力。 |
| `Cancelled` | 请求在订阅创建前被取消。 |
| `Failed` | 订阅请求安全失败。 |

## 微服务运行态通知（ServiceRuntimeState）

`IExtensionHostBridge14.ServiceRuntimeState` 提供当前节点的 service 运行态 snapshots 与后续变更。调用 `SubscribeStatesAsync` 后先收到当前快照，再按序收到后续快照或移除通知；每个通知都标记是否属于初始快照回放。

旧版 Host（API 1.3 及更低）不会实现 `IExtensionHostBridge14`。对于实现了该接口但协商版本低于 `1.4.0` 的 Host，`SubscribeStatesAsync` 返回 `Succeeded == false`、`Code == ExtensionServiceRuntimeStateSubscriptionCode.Unsupported` 且 `Subscription == null`，不把能力不支持作为异常。

### `IExtensionServiceRuntimeStateApi` 与 sink

```csharp
public interface IExtensionServiceRuntimeStateApi
{
    ValueTask<ExtensionServiceRuntimeStateSubscriptionResult> SubscribeStatesAsync(
        IExtensionServiceRuntimeStateSink sink,
        CancellationToken cancellationToken = default);
}

public interface IExtensionServiceRuntimeStateSink
{
    void OnStateChanged(ExtensionServiceRuntimeStateChange change);
}
```

`ExtensionServiceRuntimeStateChange` 包含以下字段：

| 属性 | 含义 |
| --- | --- |
| `ServiceId` | 稳定的 service ID。 |
| `Sequence` | 当前节点单调递增的通知序号；通知按此序号顺序投递。 |
| `Kind` | `Snapshot` 或 `Removed`。 |
| `Snapshot` | 当前运行态快照；`Removed` 通知为 `null`。 |
| `IsInitialSnapshot` | 是否属于订阅创建时的初始快照回放。 |
| `OwnerExtensionId` | 已知时的属主扩展 ID；移除通知也会保留该属主 ID。 |

当前快照和后续运行态变更使用 `ExtensionServiceRuntimeSnapshot`。回调被阻塞期间，同一 service 的待投递 snapshots 可以合并为最新一份；`Removed` 通知不会因此被合并丢弃。

### 运行态快照

| 属性 | 含义 |
| --- | --- |
| `ServiceId` | 稳定的 service ID。 |
| `ProcessId` | 当前已知的操作系统进程 ID。 |
| `StartedAt` / `Uptime` | 当前进程代次的启动 UTC 时间与运行时长（可表示时）。 |
| `LifecycleState` / `HealthState` | 安全的生命周期状态与健康状态。 |
| `ForwardedRequestCount` / `ActiveForwardedRequestCount` | 累计转发请求数与当前活动转发请求数。 |
| `LastUpdatedAt` / `LastHealthAt` | 最近一次运行态更新与健康观测的 UTC 时间。 |
| `OwnerExtensionId` | 属主扩展 ID；Host-owned service 可为 `null`。 |
| `FailureStage` / `FailureCode` / `FailureReason` | 当前故障阶段、机器可读故障码与有界的人类可读说明。 |
| `LastProbe` | 最近一次健康探测的安全结果与诊断。 |
| `ProcessExitCode` | 已观察到进程退出时的退出码。 |
| `RestartCount` | 已记录的 service 重启尝试次数。 |
| `StateEnteredAt` | 当前生命周期状态开始时的 UTC 时间。 |
| `RetryAt` | 下次计划启动或重启的 UTC 时间。 |

时间戳均为 UTC，字段在尚无观测时可以为 `null`。`ExtensionServiceLifecycleState` 的值为 `Unknown`、`Disabled`、`Starting`、`Running`、`Stopping`、`Failed`、`Waiting`、`Stopped`；`ExtensionServiceHealthState` 的值为 `Unknown`、`Healthy`、`Unhealthy`。

`ExtensionServiceFailureStage` 的值为 `None`、`Spawn`、`HealthProbe`、`ProcessExit`。`ExtensionServiceFailureCode` 的值为 `None`、`StartRejected`、`InvalidLaunchSpecification`、`MissingHostEnvironment`、`ExecutableMissing`、`DependencyUnavailable`、`PortLeaseUnavailable`、`RuntimeUnavailable`、`HealthCheckFailed`、`HealthTimeout`、`ProcessExited`、`RestartPolicyDisabled`、`RestartLimitReached`、`Cancelled`、`Unknown`。

`LastProbe` 的类型为 `ExtensionServiceProbeSnapshot`，包含 `ObservedAt`、`Result`、`Target`、`FailureCode` 和 `ErrorMessage`。`ExtensionServiceProbeResult` 的值为 `Unknown`、`Healthy`、`Unhealthy`、`TimedOut`、`Cancelled`、`Unavailable`。Target 与错误说明是安全诊断；内部故障的原始异常载荷和进程输出不会包含在内。

### 结果与订阅释放

`ExtensionServiceRuntimeStateSubscriptionResult` 成功时 `Succeeded` 为 `true`、`Code` 为 `Subscribed` 且 `Subscription` 非空；失败时 `Subscription` 为 `null`。`ExtensionServiceRuntimeStateSubscriptionCode` 的值为 `Subscribed`、`Unsupported`、`InvalidArgument`、`Failed`。

订阅句柄实现 `IExtensionServiceRuntimeStateSubscription : IDisposable, IAsyncDisposable`：`Dispose()` 以 best-effort 方式解除订阅，一个回调仍可能正在执行；`DisposeAsync()` 还会等待 callback quiescence。不得在同一订阅的回调中同步等待自己的 `DisposeAsync()`。
