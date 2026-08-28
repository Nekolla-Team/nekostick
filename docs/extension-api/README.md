# Nekostick 扩展 API 总述

Nekostick 扩展是运行在 Host 进程内的可信 .NET 程序集。扩展通过稳定的 `Nekolla.Nekostick.Contracts` 契约包与 Host 交互，可以提供 HTTP 路由处理器、全局 404 fallback、读写业务配置、管理自己的微服务、订阅事件、执行后台任务等。

> 扩展不是安全沙箱。它与 Host 同进程运行，只部署经过审核的可信代码。

本文是总述，涵盖所有版本共用的概念：打包、入口、版本协商、通用数据类型。各版本的新增能力与完整 API 说明见分版本文档：

| 文档 | 版本 | 新增能力 |
| --- | --- | --- |
| [api-1.0.md](api-1.0.md) | 1.0.0 | 入口点、路由处理器、fallback、注册表、设置读取、后台任务、事件、状态、日志、共享契约 |
| [api-1.1.md](api-1.1.md) | 1.1.0 | 属主配置 API、属主路由 CRUD、属主服务 CRUD 与生命周期、端点租约、自身生命周期 |
| [api-1.2.md](api-1.2.md) | 1.2.0 | 全量配置读写（`FullConfiguration`） |
| [api-1.3.md](api-1.3.md) | 1.3.1 | 服务运行遥测、路由观测与动作钩子、自定义日志文本、跨扩展管理与目录刷新 |

当前 Contracts 包版本为 **1.3.1**（`HostApiVersion.Current`）。

## 快速开始

一个最小扩展包含三个文件：项目文件、manifest、入口类型。

### 1. 项目文件

扩展只引用 Contracts 包，目标框架与 Host 一致（.NET 10）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Nekolla.Nekostick.Contracts" Version="1.3.1" />
  </ItemGroup>
</Project>
```

### 2. manifest.json

在扩展目录根部放置 `manifest.json`（也接受 `manifest.yaml` / `manifest.yml`，同一目录只能存在一种）：

```json
{
  "schemaVersion": 1,
  "id": "example.hello",
  "version": "1.0.0",
  "entryAssembly": "Example.Hello.dll",
  "entryType": "Example.Hello.HelloEntry",
  "dependencies": [],
  "requiredHostApiVersion": ">=1.0.0 <2.0.0"
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schemaVersion` | 是 | manifest 结构版本，当前为 `1`。 |
| `id` | 是 | 扩展的稳定标识。小写字母、数字、`.`、`-`，按分隔符分段且每段非空，最长 128 字符。 |
| `version` | 是 | 扩展自身的 SemVer 版本。 |
| `entryAssembly` | 是 | 入口程序集，相对扩展目录的 `.dll` 路径，不能越出目录。 |
| `entryType` | 是 | 入口类型的全限定名，须实现 `IExtensionEntrypoint` 或 `IExtensionEntry`。 |
| `dependencies` | 是 | 依赖的其他扩展，`[]` 表示无依赖。每项为 `{ "id": ..., "versionRange": ... }`。 |
| `requiredHostApiVersion` | 是 | 可接受的 Host API 版本范围，见下文「版本协商」。 |
| `exports` | 否 | 共享契约导出声明，见 [api-1.0.md](api-1.0.md#共享契约)。 |
| `imports` | 否 | 共享契约导入声明，见 [api-1.0.md](api-1.0.md#共享契约)。 |

未知字段、重复字段会被拒绝加载。版本范围支持精确版本、`*`/`x` 通配、比较集合（`>=1.0.0 <2.0.0`）、`^`、`~` 和 `||` 备选。

### 3. 入口类型与处理器

```csharp
using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Example.Hello;

public sealed class HelloEntry : IExtensionEntry
{
    public ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
    {
        // 注册一个稳定 ID 的路由处理器
        context.Registration.TryRegisterHandler(new HelloHandler());
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class HelloHandler : IExtensionHandler
{
    public string HandlerId => "example.hello.greeting";

    public ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes($"hello from {request.Path}");
        return ValueTask.FromResult(new ExtensionHandlerResponse(
            200,
            new[] { new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["text/plain"]) },
            body));
    }
}
```

### 4. 部署与加载

把构建产物放入 Host 可执行文件目录下的 `extensions/example.hello/`：

```text
extensions/
  example.hello/
    manifest.json
    Example.Hello.dll
```

Host 启动时扫描 `extensions/`。首次启动（数据库中没有任何扩展记录）会把发现的扩展全部加载；此后扫描到的新扩展会先以 `Disabled` 记录，只有数据库中状态为 `Loaded` 且记录版本与 manifest 版本一致的扩展才会加载。Host 不监视文件变化；API 1.3 扩展可通过 `IExtensionHostBridge13.Management.RequestRefreshAsync` 请求重新扫描，再使用 `EnableAsync` 或 `ReloadAsync` 通过发布管线应用变更（见 [api-1.3.md](api-1.3.md)）。记录不会因扩展文件缺失而自动删除；`DeleteRecordAsync` 仅在 ID 已从可靠目录扫描中消失时才允许显式删除。

让流量到达 handler：用配置 API 创建一条 `target` 指向该 handler ID 的路由，见 [api-1.1.md](api-1.1.md#属主路由 routes) 的完整示例。

## 入口点与生命周期

入口类型实现 `IExtensionEntrypoint`（或使用更短的别名 `IExtensionEntry`）：

```csharp
public interface IExtensionEntrypoint
{
    ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    ValueTask OnPreviousStoppedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

- `StartAsync`：扩展开始服务前调用，在这里注册 handler / fallback、导出契约、订阅事件、启动后台任务。`context.Reloading` 为 `true` 表示这是一次 reload。
- `StopAsync`：停止时调用，释放扩展持有的资源。Host 会先停止向该扩展派发新请求。
- `OnPreviousStoppedAsync`：reload 场景下，旧实例完全停止后在新实例上调用。不需要时可不实现（默认空实现）。

入口类型支持两种构造方式，二选一：

```csharp
// 方式一：无参构造函数，Host 桥在 StartAsync 里通过 context.Host 获取
public HelloEntry() { }

// 方式二：声明一个 IExtensionHostBridge 参数的构造函数，Host 会注入同一个桥
public HelloEntry(IExtensionHostBridge host) { _host = host; }
```

`IExtensionStartContext` 提供四个成员：

| 成员 | 类型 | 用途 |
| --- | --- | --- |
| `Reloading` | `bool` | 本次启动是否属于 reload。 |
| `Host` | `IExtensionHostBridge` | 访问 Host 能力的桥，扩展存活期内可持续使用。 |
| `Registration` | `IExtensionRegistration` | 注册 / 注销 handler 与 fallback。 |
| `Contracts` | `IExtensionContractRegistry` | 启动期共享契约导出 / 导入（与 `Host.Contracts` 相同）。 |

## 版本协商

Host API 使用 SemVer。规则：major 相同且 Host 版本不低于扩展要求的最低版本，即兼容（`ExtensionAbi.IsCompatible`）。

- manifest 的 `requiredHostApiVersion` 决定扩展能被哪些 Host 加载，推荐写成 `">=1.0.0 <2.0.0"`。
- `IExtensionHostBridge.ApiVersion` 是本次协商出的 Host API 版本，运行时可读。
- 高版本能力在低版本 Host 上返回 `Unsupported` 错误或表现为无操作，不会抛异常，也不会出现「半个能力」。按版本文档中的「兼容性」小节做能力探测。

```csharp
var api = context.Host.ApiVersion;          // 例如 1.2.0
var has13 = ExtensionAbi.IsApi13Supported(api); // 是否可用 1.3 能力
```

## Host 桥能力总览

`IExtensionHostBridge` 按版本累积能力：

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
| `Supervisor`（经 `IExtensionHostBridge13`） | 1.3 | 全局服务运行遥测。 |
| `RouteEvents`（经 `IExtensionHostBridge13`） | 1.3 | 路由观测订阅与动作钩子。 |
| `LogWriter`（经 `IExtensionHostBridge13`） | 1.3 | 自定义文本日志。 |
| `Management`（经 `IExtensionHostBridge13`） | 1.3（当前 Contracts 1.3.1） | 跨扩展记录管理、刷新、启用 / 禁用、reload、`ReloadSoon` 与显式删除。 |

## 通用约定

### 标识符一律使用 UUID v7

所有 `Guid` 类型的 ID（路由 ID、服务 ID 等）必须是 RFC 4122 variant 的 UUID v7，否则 DTO 构造函数直接抛 `ArgumentException`。`Guid.NewGuid()` 生成的是 v4，**不能**使用：

```csharp
var routeId = Guid.CreateVersion7(); // 正确
// var routeId = Guid.NewGuid();     // 错误：构造 DTO 时抛异常
```

### 乐观并发

所有配置写操作都携带 `expectedVersion`：先读快照拿到 `Version`，再以它为期望值提交写入。版本不匹配时返回 `ConcurrencyConflict`，重新读取后重试：

```csharp
var read = await host.ConfigurationApi.ReadAsync(cancellationToken);
if (!read.IsSuccess) { /* 处理错误 */ }

var write = await host.ConfigurationApi.ApplyAsync(
    read.Value!.Version, changes, cancellationToken);
if (!write.IsSuccess &&
    write.Errors.Any(e => e.Code == ConfigurationErrorCode.ConcurrencyConflict))
{
    // 有其他人先改了配置：重新读取、重建变更、重试
}
```

### 结果类型，不用异常表达业务失败

配置类 API 返回结果对象，业务失败（校验失败、并发冲突、不存在等）以错误码表达，不抛异常：

```csharp
ConfigurationReadResult<T>  // IsSuccess / Value / Errors
ConfigurationWriteResult    // IsSuccess / NewVersion / Errors
```

`ConfigurationError.Code` 的可能值：

| 值 | 含义 |
| --- | --- |
| `Validation` | 配置未通过语义校验。 |
| `ConcurrencyConflict` | `expectedVersion` 已过期。 |
| `NotFound` | 目标配置项不存在。 |
| `Unsupported` | 当前协商版本不支持该操作（低版本 Host）。 |
| `StorageUnavailable` | 配置存储暂不可用。 |

DTO 本身的构造参数非法（如相对路径、空 ID、超限文本）仍会抛 `ArgumentException`，这属于调用方编程错误，应尽早发现。

### 不可变性

所有契约 DTO 都是不可变的（record / 只读属性 / `ImmutableArray`）。读取到的快照可以在扩展内安全缓存与跨线程共享。

### 请求处理结果

- handler 不可用（未加载、停止中、reload 切换窗口）→ 客户端收到 `503`。
- handler 抛出未处理异常 → 客户端收到 `500`，并计入扩展失败统计。
- fallback 未注册、返回 `NotHandled`、超时或异常 → 客户端收到标准 `404`。
- 扩展在滑动窗口内失败次数达到阈值会被 Host 自动停止，其 handler 路由返回 `503`，需要显式 reload 恢复。
