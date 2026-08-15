# Nekolla.Nekostick 实现 Agent 执行手册

## 1. 用途与权威顺序

本文是实现 agent 的执行说明。目标是将 [`technical-design.md`](./technical-design.md) 完整落地为可运行、可测试的 .NET 10 项目，而不是只搭建演示性骨架。

文档的权威顺序如下：

1. 用户在当前任务中给出的新指令。
2. [`technical-design.md`](./technical-design.md) 中明确的外部契约与用户确认的决策。
3. 本执行手册。
4. 实现 agent 的常规偏好。

发生冲突、设计中没有定义却会改变外部行为、持久化格式、安全边界、路由语义或默认值时，停止实现并向用户提问。不得以“常见做法”覆盖设计中的明确规则。

标有“计划默认值，可配置”的值必须实现为 PostgreSQL 业务配置中的默认值，而不是散落在业务逻辑里的不可覆盖常量。允许用常量作为首次初始化值和硬安全上限，但不得改变其语义。

## 2. 交付目标

完成后，项目必须具备：

- .NET 10 ASP.NET Core HTTP/1.1 宿主，根命名空间 `Nekolla.Nekostick`。
- PostgreSQL 16 持久化、EF Core migration、启动期 advisory lock migration。
- 动态 route 快照、原子热切换和多节点配置传播。
- 三类 route target：本地微服务、静态文件、可信 extension handler。
- 本地子进程 supervisor、端口租约、health/restart、HTTP/WebSocket 代理。
- 可信 in-process extension 的 manifest、依赖、私有 ALC/DI、load/unload/reload、handler、fallback、配置和事件能力。
- Dockerfile、systemd unit 模板、CLI 安全启动/诊断命令和 GitHub Actions 集成测试。

不要交付以下替代品：

- 用配置文件代替 PostgreSQL 业务配置。
- 用可变全局字典代替不可变配置快照。
- 仅代理 HTTP 而省略 WebSocket。
- 将 extension 管理做成扫描文件后自动加载/自动重载。
- 暴露核心管理 HTTP API、管理 UI 或认证系统。
- 用 Windows API、Windows service 或 shell execute 作为核心路径。

## 3. 开工前的工作方式

1. 阅读完整的 [`technical-design.md`](./technical-design.md)，先列出本次任务要影响的设计章节和验收点。
2. 在仓库中先建立 solution、项目边界和测试项目，再实现业务功能。
3. 每次改动只覆盖一个可验证的垂直切片；不要在没有测试的情况下连续堆叠所有模块。
4. 修改数据库 schema 时，同步新增 EF migration、可重复执行 SQL script 和集成测试。
5. 改变 route、代理、扩展或子进程行为时，同步添加对应集成测试。
6. 不得访问、打印或提交连接串、环境变量 secrets、Cookie、Authorization 或请求 body。

实现分支或任务说明必须至少声明：修改范围、对应设计章节、数据迁移影响、测试命令和回滚/兼容性影响。

## 4. 建议的解决方案结构

可以调整文件名，但要保持下列依赖方向。业务领域不得反向依赖 ASP.NET、EF Core、YARP 或具体扩展程序集。

```text
src/
  Nekolla.Nekostick.Host/          # Program、Kestrel、CLI、DI composition root
  Nekolla.Nekostick.Contracts/     # 稳定 Host/extension contract，只含抽象和 DTO
  Nekolla.Nekostick.Domain/        # route/service/config 领域模型、校验、排序规则
  Nekolla.Nekostick.Routing/       # 快照、hash/trie/regex matcher、fallback 调度
  Nekolla.Nekostick.Persistence/   # EF DbContext、repositories、migration、NOTIFY/LISTEN
  Nekolla.Nekostick.Proxy/         # YARP forwarder、header/path/retry、static target
  Nekolla.Nekostick.Supervision/   # 进程、端口租约、health、restart、子进程日志
  Nekolla.Nekostick.Extensions/    # manifest、ALC、DI、生命周期、handler、events
tests/
  Nekolla.Nekostick.UnitTests/     # 纯领域与 matcher 单元测试
  Nekolla.Nekostick.IntegrationTests/
  Fixtures.Microservice/           # 最小真实 HTTP/WS 子进程 fixture
  Fixtures.Extension/              # 测试 extension 与 contracts fixture
deploy/
  systemd/nekostick.service
  compose.example.yaml
.github/workflows/ci.yml
Dockerfile
```

`Contracts` 是 host 与 extension 的长期 ABI 边界：其中的 public 类型须采用明确 SemVer，避免暴露 EF 实体、`NpgsqlConnection`、内部 DI 容器或非稳定实现类型。extension 之间共享服务时，也只能通过独立 contract assembly 的显式 export/import，不得引用对方实现程序集。

## 5. 推荐实施顺序与完成条件

以下顺序是依赖顺序，不是可任选清单。每个阶段完成前，不要进入依赖它的阶段。

### 阶段 A: 基础宿主与持久化

实现：

1. 创建 .NET 10 solution、项目边界、通用错误模型和结构化日志约定。
2. 实现引导配置：`--connection-string`、`--listen-address`、`--listen-port`、`--node-id`，优先级固定为 `CLI > env > default`。
3. 配置 Kestrel 仅接收 HTTP/1.1；默认 `127.0.0.1:8080`。
4. 实现 PostgreSQL 16 `DbContext`、UUID v7 实体、`version` 并发字段和所有核心表。
5. 生成 idempotent SQL migration script，并在应用启动时使用 PostgreSQL advisory lock 执行 migration；失败必须退出。
6. 实现 `run`、`status`、`doctor`、`--skip-extensions`、`--disable-supervisor`、`--read-only`。

完成条件：migration 并发启动仅由一个节点执行；缺连接串、migration 失败和无快照数据库离线均不能对外提供动态服务。

### 阶段 B: 配置写入与不可变快照

实现：

1. 实现 `configuration_revisions`、全量读取、实体/全局乐观版本、批量事务提交和 `NOTIFY`。
2. 定义 `IHostConfigApi`，extension 只能通过它读写业务配置。禁止将原始数据库连接或可写 `DbContext` 交给 extension。
3. 实现 `LISTEN/NOTIFY`、30 秒版本轮询及断线后带抖动的重连。
4. 将校验后的全量配置构建成不可变快照，并用原子引用切换。
5. 实现节点注册、心跳、默认 `nodeId=0` 的单节点限制、已有快照离线继续服务策略。

完成条件：失败快照永不部分生效；并发写入得到明确冲突；通知丢失后仍能通过版本轮询修复；数据库离线时不得新启动服务或写配置。

### 阶段 C: Route 领域和匹配引擎

实现：

1. 实现 route 实体、target 引用完整性、enabled、host/method 条件和所有 matcher 类型。
2. 实现既定 path 规范化：保留 percent encoding 与重复 slash，仅处理字面 `/` 的 dot segments，非法 percent encoding 返回 `400`。
3. 实现 Exact/ExactCI hash、Prefix/PrefixCI trie、预编译 Regex 列表；所有索引属于快照，读取期不可变。
4. 严格实现 `Exact > ExactCI > Prefix > PrefixCI > Regex`，再实现 priority、长度、创建时间、UUID 的稳定排序。
5. 实现 `/foo` 完整匹配无结果后才以 `/foo/` 再完整匹配的回退。
6. 实现 segment/raw prefix 的 `*` 语义、Regex `CultureInvariant | NonBacktracking`、全串匹配和 timeout 跳过行为。
7. 实现唯一 fallback 的 404 reason 传递。

完成条件：matcher 的排序、尾随 slash、大小写、host、method、prefix 边界、regex timeout 和 fallback reason 均由自动化测试覆盖。不要让实际数据结构偶然决定选择顺序。

### 阶段 D: Path、header 与 HTTP/WebSocket 代理

实现：

1. 实现 `Preserve`、`Strip`、`Replace`，以及 `{path}`、`{match}`、Regex `$0..$n` 模板。query 必须原样保留。
2. 在持久化校验期拒绝无效 Strip 组合、非法模板变量、非绝对输出 path、控制字符和错误捕获组。
3. 集成 YARP forwarder 或等价流式 HTTP/1.1/WebSocket 实现；不得为普通代理默认缓冲 body。
4. 实现 hop-by-hop header 剥离、合法 WebSocket Upgrade 重建、默认保留 Host 和 request/response `remove -> set -> add` 改写。
5. 实现可信 CIDR、`Forwarded` 优先于 `X-Forwarded-For`、不可信 forwarding header 剥离和 client IP 解析。
6. 实现无 body 请求才可重试的策略、全局/route 可覆盖的 timeout，正确映射 `502`/`504`。

完成条件：HTTP body 流、WebSocket、header 多值/`Set-Cookie`、可信反代链、重试限制和超时映射通过集成测试。不得允许 header 模板引入 CR/LF 或 hop-by-hop header。

### 阶段 E: 静态文件 target

实现：

1. 将 forwarding 后的绝对 URI path 安全映射到每 route 的绝对 root。
2. 仅支持 `GET`/`HEAD`，支持每层目录 `index.html`。
3. 通过 realpath 保证结果留在 root 内；任何不存在、越界 symlink、无 index 或不安全映射均为 static miss。
4. 对 static miss 调用 fallback；fallback 不处理时返回 `404`。
5. 支持 MIME、弱 ETag、Last-Modified、条件请求和单范围 Range。
6. 禁用目录列表、预压缩协商，默认 `Cache-Control: no-cache`，允许每 route 覆盖缓存策略。

完成条件：路径穿越、symlink、index、Range、conditional request、cache、GET/HEAD 之外 method 和 fallback 均有测试。

### 阶段 F: 微服务 supervisor

实现：

1. 实现共享 service 定义，限制 `ProcessStartInfo` 到 `FileName`、`ArgumentList`、`WorkingDirectory`、`Environment`，固定 `UseShellExecute=false`。
2. 验证可执行文件和工作目录为绝对路径；继承宿主环境，再用服务环境覆盖。
3. 为每个服务设置 `$PORT`、`PORT` 和 `HOST`；只代理至可选 `127.0.0.1` 或 `::1` loopback 的 HTTP/1.1 地址。
4. 实现 `(nodeId, port)` PostgreSQL 租约、显式/自动端口、OS bind race 重试、失联回收和数据库离线限制。
5. 实现 Eager/Lazy 启动、同服务并发启动合并、配置更新的先健康后切换、禁用/删除时 `503`。
6. 实现 Process/TCP/HTTP health、默认重启策略、独立 POSIX process group、SIGTERM/SIGKILL 和行级 stdout/stderr 结构化日志及限流。

完成条件：真实 fixture 子进程可被启动、代理、停止、重启；端口冲突、health 失败、lazy 启动、日志超额、子孙进程和数据库不可用都得到验证。

### 阶段 G: Extension 宿主

实现：

1. 实现 `extensions/<dir>` 发现和 JSON/YAML 二选一 manifest。校验 schema、ID、SemVer、dependencies、`requiredHostApiVersion`、路径边界和未知字段。
2. 按依赖拓扑和 ID 加载；对重复 ID、环、缺依赖、版本不兼容和 contract 不兼容拒绝加载。
3. 为每个 extension 创建 collectible ALC、私有 DI provider、后台任务管理器和 extension JSONB config。
4. 提供稳定 handler ID、单 handler HTTP 契约和唯一 fallback；不得允许扩展注册任意 endpoint 或 health endpoint。
5. 实现显式 load/unload/reload：新 ALC 验证和 `Start(reloading: true)` 成功后，旧实例 drain/stop，再调用新 `OnPreviousStopped`，最后切换 handler。
6. 实现旧 ALC 泄漏告警、失败滚动窗口、自动 stop、每扩展有序 best-effort 事件队列。

完成条件：测试程序集覆盖 manifest、依赖顺序、handler、fallback、reload 成功/回滚/503 窗口、ALC 泄漏、失败阈值、contracts 和队列丢弃。

### 阶段 H: 限流、部署和 CI

实现：

1. 实现节点本地 global + route per-client-IP token bucket，默认不限制，WebSocket 只限流握手。
2. 实现可配置 body/header/concurrency/read timeout 限制，且 Kestrel 层保留不可超越的硬安全上限。
3. 实现基础结构化日志和对 extension 的状态/事件输出，不额外绑定监控或健康 HTTP endpoint。
4. 添加 Dockerfile、Compose 示例、systemd unit 模板和 GitHub Actions Ubuntu + PostgreSQL 16 service。
5. 建立使用 `NEKOSTICK_TEST_PG` 的随机独立数据库/schema 集成测试基础设施，并确保 `finally` 清理。

完成条件：CI 在 Ubuntu 上运行 migration、unit、integration 测试；部署工件以非 root 身份运行，并可注入引导配置。

## 6. 不可违反的行为约束

实现 agent 必须在代码审查和测试中逐项核对以下事项：

### 配置与多节点

- 所有业务配置都在 PostgreSQL 中；监听地址、端口、连接串、node ID 是引导配置，不能由数据库热改。
- 不允许 extension 直接操作核心业务表、`DbContext` 或连接串。
- `NOTIFY` 仅是提示，必须存在版本轮询恢复路径。
- 任何节点只在完整快照验证成功后切换；不能逐表、逐 route 半更新。
- 数据库离线时已有内存快照可服务；无快照不能启动；不能启动新服务或写配置。
- secrets 按当前设计明文保存，所有日志、CLI、错误和测试输出必须脱敏。

### Route

- matcher 类型优先级绝对高于数值 priority。
- Regex 只在其他四类无可用匹配时运行；必须预编译、`NonBacktracking`、全串匹配和 timeout。
- `Exact`/`ExactCI` 与 `Prefix`/`PrefixCI` 的大小写语义不得混用。
- `/foo` 到 `/foo/` 的回退是“完整匹配轮回退”，不是双键索引或只查 Exact。
- 任何 no-match、条件不符、static miss/no-index 都先成为 fallback 候选；普通 `500`、`502`、`503`、`504`、`400`、资源限制错误不能交给 fallback。
- 禁止保存无效 target 引用；运行期不可用的既存 target 返回 `503`。

### 代理与静态文件

- 不可信 peer 提供的 forwarding headers 必须在代理前移除。
- 无 request body 是重试的必要条件，不因为 method 是幂等就绕过此限制。
- 不得将 `Set-Cookie` 合并为逗号分隔的单值。
- 静态路径在真实文件系统解析后仍必须位于 root 内；字符串前缀比较不够。
- 目录列表和预压缩协商始终关闭。

### 子进程与 extension

- 永远 `UseShellExecute=false`；用户若需要 shell，必须自己将 shell 设为进程文件名。
- 端口租约以 `(nodeId, port)` 唯一；显式端口也要租约。
- 每个子进程需独立 POSIX process group；停止必须覆盖进程树。
- ALC 可卸载不代表 extension 安全隔离，切勿将不可信代码加载入进程。
- extension reload 没有 file watcher；新的 extension 完成验证和 reload-start 前，旧实例必须继续服务。
- extension 不得注册通用 HTTP endpoint 或 health endpoint，只能提供 route handler 和唯一 fallback。

## 7. 默认值登记表

下列默认值已经授权实现 agent 采用。它们必须从全局业务配置初始化，允许设计规定的位置由 service 或 route 覆盖：

| 范畴 | 默认值 |
| --- | --- |
| config version poll | 30 秒 |
| DB reconnect | 1 秒起、随机抖动指数退避、最大 30 秒 |
| node heartbeat / expiry | 10 秒 / 30 秒 |
| port lease TTL / renew | 30 秒 / 10 秒 |
| auto port range | `20000-29999` |
| port bind attempts | 5；100ms 至 1600ms 的带抖动退避 |
| regex max length / timeout | 4096 字符 / 50ms |
| startup timeout / interval | 30 秒 / 1 秒 |
| steady health interval / timeout | 10 秒 / 3 秒 |
| health failure threshold | 3 |
| process restart | OnFailure；1 秒至 30 秒退避；5 分钟最多 10 次 |
| process stop grace | 15 秒后 SIGKILL |
| child log line / rate | 16 KiB；每服务 200 行/秒且 1 MiB/秒 |
| proxy retry | 默认 0 次；200ms 至 2 秒退避 |
| proxy connect / headers / total / WS idle | 10 秒 / 30 秒 / 100 秒 / 120 秒 |
| static cache | `Cache-Control: no-cache`，弱 ETag 与 Last-Modified 开启 |
| extension drain / task stop | 30 秒 / 30 秒 |
| ALC unload check | 最多 3 轮 GC |
| extension failure threshold / window | 10 次 / 60 秒 |
| extension event queue | 每 extension 每 node 1024，满时丢 newest 并计数 |
| default rate limit | global/route 均不限制 |
| request limits | body 30 MiB；headers 32 KiB；每节点 1024 并发；read timeout 30 秒 |

## 8. 测试策略

### 8.1 测试层级

- **Unit tests**：path normalization、matcher 排序、模板展开、header 规则、配置校验、SemVer/manifest 校验、重试决策、端口选择等纯逻辑。
- **Integration tests**：真实 PostgreSQL 16、真实 Kestrel HTTP/1.1、真实子进程 fixture、真实 WebSocket、真实 collectible extension assembly。
- **不使用 in-memory provider 替代 PostgreSQL 集成覆盖**：它不能验证 advisory lock、JSONB、LISTEN/NOTIFY、并发、UUID、迁移或租约约束。

### 8.2 数据库测试约定

- 从 `NEKOSTICK_TEST_PG` 读取专用测试连接串；缺失时集成测试明确 skip 或失败，CI 中必须提供。
- 每次测试 run 创建随机 UUID 名称的独立 database 或 schema，禁止使用共享固定库。
- 清理必须位于 `finally`；清理失败视为测试失败。
- GitHub Actions 使用 Ubuntu 和 PostgreSQL 16 service；并行 run 使用不同 database/schema。

### 8.3 最低验收矩阵

每个功能进入完成状态前，至少覆盖：

| 区域 | 最低证据 |
| --- | --- |
| migration/config | advisory lock 并发、失败退出、version conflict、批处理原子性、NOTIFY 丢失轮询恢复、离线快照。 |
| route | 所有 matcher、host/method、priority、slash fallback、path 编码、regex timeout、fallback reason。 |
| proxy | HTTP stream、WebSocket、path/header rewrite、可信代理、无 body retry、502/504。 |
| static | index、range、ETag、conditional、symlink escape、missing fallback、缓存。 |
| supervisor | Eager/Lazy、并发启动合并、health/restart、port lease、固定端口冲突、进程树、日志限流。 |
| extensions | JSON/YAML、依赖、contracts、handler、fallback、reload/rollback、泄漏、失败阈值、事件队列。 |
| deploy | Docker 非 root、systemd SIGTERM、CLI 安全启动开关、Ubuntu CI。 |

## 9. 代码质量与变更纪律

- 每个 public contract、持久实体字段、配置项和状态枚举必须有 XML 文档或同等精确的 API 文档。
- 对外错误不泄露 route、文件路径、子进程命令、端口、数据库或 extension internals。
- 所有取消、超时和后台循环必须接受 `CancellationToken`；停止过程不得遗留无观察的 task 异常。
- 不吞掉 exception：预期失败应映射为状态/响应，非预期失败应记录带 correlation/route/service/extension 标识的结构化日志。
- 不把 route 或 extension 的用户输入拼接到 shell、SQL、正则构造选项或 header 中。
- migration、contracts 和 manifest schema 变更必须定义兼容策略；无法兼容时提高对应 schema/API major version，并拒绝错误版本，而非静默猜测。
- 避免“以后再处理”的空接口、TODO fallback 和默认 permissive 行为。功能未实现时，应在设计允许的范围内明确拒绝配置或返回预期错误。

## 10. 最终交付检查清单

在宣布实现完成前，agent 必须确认：

- [ ] `technical-design.md` 的所有“必须”“禁止”和默认值均可追溯到代码、配置或测试。
- [ ] 引导配置与 PostgreSQL 业务配置的边界没有混淆。
- [ ] 数据库迁移使用 advisory lock，失败退出。
- [ ] 业务配置经 Host Config API、乐观版本、事务、NOTIFY、轮询和全量原子快照生效。
- [ ] route matcher、slash fallback、prefix `*`、regex 限制和 fallback 全部符合契约。
- [ ] HTTP/1.1/WebSocket、header/path rewrite、可信代理和 retry/timeout 完整实现。
- [ ] 静态文件实现 realpath 防护、index、条件请求、Range 和安全缓存默认值。
- [ ] supervisor 满足 POSIX process group、port lease、health、restart、Eager/Lazy 和日志限制。
- [ ] extension 满足 manifest/ALC/contracts/handler/fallback/显式 reload/失败隔离与事件规则。
- [ ] 本地限流、资源限制、secret 脱敏和基础日志已实现。
- [ ] `NEKOSTICK_TEST_PG` 集成测试、Dockerfile、systemd unit、GitHub Actions Ubuntu + PostgreSQL 16 service 已交付。

若任一项未完成，最终报告必须说明缺口、风险、受影响的设计章节和下一步，而不能声明“完整实现”。
