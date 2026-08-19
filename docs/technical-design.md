# Nekolla.Nekostick 技术设计

## 1. 目标与边界

Nekolla.Nekostick 是一个基于 ASP.NET Core 的动态路由宿主，根命名空间为 `Nekolla.Nekostick`。它在单一根入口接受请求，根据可运行时变更的路由定义，将请求交给本地微服务、静态文件或可信扩展处理器。

首期目标如下：

- 基于 .NET 10，支持 ASP.NET Core 支持的 POSIX 平台；不实现 Windows 专有行为。
- 支持 Docker 容器及 systemd 裸机部署。
- 核心仅监听 HTTP/1.1。TLS 由前置反向代理终止，核心不管理证书。
- 所有业务配置持久化于 PostgreSQL 16，并可在运行时由可信扩展通过宿主 API 修改。
- 首期按单机轻量基线设计，但配置同步、节点与端口租约模型必须可支持未来多实例部署。
- 不承诺 QPS、延迟、路由数量或并发连接数 SLA；性能测试只验证正确性、无明显退化及配置切换的原子性。

以下内容不属于核心职责：

- 核心不提供管理 HTTP API、状态 HTTP API、内置管理 UI 或认证授权机制。
- 核心不终止 HTTPS，不提供 HTTP/2、h2c、HTTP/3 或扩展 CONNECT WebSocket。
- 核心不自动监测 `extensions` 目录，也不自动重载扩展。
- 核心不在集群中集中托管子进程；每个节点只管理本节点的子进程。
- 核心不提供跨节点事件队列、分布式限流或 secrets 管理服务。

## 2. 关键技术选型

| 范畴 | 计划实现 | 说明 |
| --- | --- | --- |
| Web 宿主 | ASP.NET Core / Kestrel | 入站协议固定 HTTP/1.1。 |
| 数据访问 | EF Core + Npgsql | 用于 PostgreSQL 16 的持久化、并发版本和 migration 元数据。 |
| 数据库迁移 | EF Core idempotent SQL scripts | 发布物包含迁移脚本；应用启动时执行。 |
| 请求代理 | YARP `IHttpForwarder` | 路由选择、改写和配置快照由本项目实现；YARP 只承担 HTTP/1.1 与 WebSocket 转发。 |
| 路由索引 | 自研不可变快照 | 精确路由 hash、前缀 trie、预编译 regex 列表。 |
| 限流 | `System.Threading.RateLimiting` | 按配置快照建立节点本地 token bucket。 |
| 扩展加载 | Collectible `AssemblyLoadContext` | 仅隔离依赖并支持卸载，不作为安全沙箱。 |
| YAML manifest | `YamlDotNet` 的安全模式 | 禁止自定义类型标签、对象构造与未知类型反序列化。 |
| 日志 | `Microsoft.Extensions.Logging` 结构化日志 | 核心只记录基础结构化事件；扩展决定进一步输出、存储和展示。 |

若某一库在实现期不满足下文规定的协议和行为，可替换实现，但不得改变外部契约。

## 3. 进程、启动与部署

### 3.1 引导配置

以下配置属于引导配置，必须在连接 PostgreSQL 前获得，变更后重启节点才生效：

| 配置 | CLI | 环境变量 | 默认值 |
| --- | --- | --- | --- |
| PostgreSQL 连接串 | `--connection-string` | `NEKOSTICK_CONNECTION_STRING` | 无，缺失即拒绝启动。 |
| 监听地址 | `--listen-address` | `NEKOSTICK_LISTEN_ADDRESS` | `127.0.0.1`。 |
| 监听端口 | `--listen-port` | `NEKOSTICK_LISTEN_PORT` | `8080`。 |
| 节点标识 | `--node-id` | `NEKOSTICK_NODE_ID` | `0`。 |

引导配置的优先级固定为 `CLI > 环境变量 > 默认值`。业务配置只从 PostgreSQL 的有效配置快照读取，再回退到代码内默认值；CLI 与环境变量不覆盖业务配置。

`nodeId` 为不超过 128 个字符的稳定文本标识。未指定时使用 `0`，此值只允许单节点部署：数据库必须拒绝第二个活动 `nodeId = 0` 的注册。多实例部署必须通过 CLI 或环境变量提供唯一、稳定的 node ID。节点记录本身的公开主键使用 UUID v7，`nodeId` 是唯一业务键。

### 3.2 本地 CLI

CLI 仅承担启动覆盖、诊断和安全恢复，不负责 route/service/extension CRUD。最小命令与开关为：

- 默认 `run`：使用引导配置启动服务。
- `status`：读取数据库和本节点状态，输出配置版本、节点注册状态、扩展与子进程摘要。
- `doctor`：检查数据库连接、migration 状态、配置快照合法性、扩展 manifest 与本机目录可访问性。
- `--skip-extensions`：安全启动时不加载任何扩展；指向扩展处理器的 route 返回 `503`。
- `--disable-supervisor`：不启动、停止或重启微服务子进程；微服务 route 返回 `503`。
- `--read-only`：禁止本节点通过 Host Config API 提交配置写入，但仍可消费已发布的配置变更。

`status` 与 `doctor` 需要数据库连接；其余开关只影响当前启动的节点，不写入全局业务配置。

### 3.3 Docker 与 systemd

首期交付以下部署工件：

- 多阶段 `Dockerfile`：使用 .NET 10 ASP.NET runtime 基础镜像，最终镜像以非 root 用户运行，默认工作目录为应用目录。
- 示例 Compose 配置：注入连接串、node ID、监听配置，并将 `extensions` 目录以只读卷挂载至可执行文件目录下。
- systemd unit 模板：使用 `Type=exec`、`Restart=on-failure`、`KillSignal=SIGTERM`，以专用非 root 用户运行，并通过 `EnvironmentFile` 注入引导配置。

Docker 和 systemd 都必须将 SIGTERM 传递给主进程。主进程收到停止信号后停止接收新请求、停止扩展和子进程，并在宽限期结束后退出。

## 4. 持久化、迁移与配置快照

### 4.1 PostgreSQL 与 migration

最低支持 PostgreSQL 16。发布构建通过 EF Core 生成 idempotent SQL migration script，并将脚本作为发布物的一部分交付。应用启动时：

1. 以 PostgreSQL advisory lock 串行获取全局迁移锁。
2. 在单一事务内按 migration history 执行尚未应用的 idempotent script。
3. 成功后释放锁并继续节点注册、快照加载。
4. migration 锁等待、脚本执行或 schema 校验失败时，写出结构化错误并退出进程；不启动不就绪节点，也不自动回滚。

迁移执行账号须拥有目标 schema 的 DDL 权限。生产部署应使用权限最小化的独立运行账号和迁移账号；即使当前由应用执行 migration，也不得将超级用户连接串提供给扩展。

### 4.2 数据模型

所有持久实体均使用 UUID v7 主键、`created_at`、`updated_at` 和乐观并发 `version`。公开 API 返回 UUID v7，禁止依赖数据库内部序列号。

核心表至少包括：

| 表 | 关键字段与用途 |
| --- | --- |
| `configuration_revisions` | 单行全局配置版本、提交时间和提交者信息。 |
| `routes` | matcher、pattern、host/method 条件、target、priority、enabled、metadata JSONB。 |
| `services` | 子进程启动定义、端口策略、监听地址、生命周期、health、retry、日志限制与 enabled 状态。 |
| `global_settings` | 自动端口范围、可信代理 CIDR、限流、请求限制、静态缓存、代理超时、全局 health/retry/log 默认值等业务配置。 |
| `extension_records` | manifest ID、安装版本、载入状态和公开 UUID v7。manifest ID 为唯一的稳定文本键。 |
| `extension_settings` | extension record ID、schema version、JSONB 配置、version。 |
| `nodes` | UUID v7、唯一 `nodeId`、心跳、最后配置版本、运行状态。 |
| `port_leases` | node ID、port、service ID、租约到期时间、续约版本；唯一键为 `(node_id, port)`。 |

`routes.metadata` 和 extension 设置允许扩展保存其业务元数据，但核心只校验 JSON 有效性、大小上限和乐观版本。route/service/global settings 的核心字段须由 Host Config API 严格校验。

业务配置中的 secrets（例如子进程环境变量、extension JSONB 中的机密）按用户决策以明文存入 PostgreSQL。此模型要求：数据库连接、备份、日志、诊断输出和 Host Config API 的读取权限都视为高敏感权限；核心不得把明文字段写入日志、异常、`status` 或 `doctor` 输出。可信 in-process extension 具有与宿主相同的信任级别，不能视为 secrets 隔离边界。

### 4.3 配置写入、通知与快照

核心没有管理 HTTP API。extension 只能使用 Host Config API 读写核心业务配置，不能直接修改核心业务表，也不获得原始 `NpgsqlConnection`。Host Config API 使用宿主维护的共享管理员数据访问上下文完成操作，以保证下列步骤不可绕过：

1. 调用方携带期望的全局或实体 `version`。
2. Host API 执行完整引用、语义和资源校验；失败返回并发冲突或验证错误。
3. 批量变更在同一数据库事务内原子提交，递增全局配置版本。
4. 提交后发布 PostgreSQL `NOTIFY nekostick_config_changed, <version>`。

删除仍被 route 引用的 service、保存未注册的 extension handler、保存无效 matcher 或其他无效 target 引用均必须被拒绝。禁用目标可保留引用，但相关 route 在请求期返回 `503`。Host API 支持原子批量变更；不实现隐式级联删除。

每个节点以 `LISTEN/NOTIFY` 作为快速提示，并定期轮询 `configuration_revisions` 作为通知丢失、断线和重连的兜底。收到更高版本后，节点在后台加载并校验全量快照，再以单个原子引用替换当前不可变快照；正在处理的请求继续使用旧快照，新请求使用新快照。快照包括 core routes/services/global settings 和 extension persisted JSONB，不包括本机的进程句柄、端口租约、加载中的 ALC 或事件队列。

**计划默认值，可配置：** 配置版本轮询间隔为 30 秒；数据库监听重连采用带随机抖动的指数退避，初始 1 秒、最大 30 秒。

数据库暂时不可用时，已成功取得内存快照的节点可继续转发现有配置，但禁止提交配置变更、申请/续约后启动新端口租约和启动新服务。数据库恢复后先同步并校验最新快照，再恢复这些操作。从未成功加载有效快照的节点必须拒绝启动或保持不就绪，不得对外提供动态路由。

### 4.4 节点与端口租约

节点启动后注册/更新 `nodes` 记录并维持心跳。自动分配端口必须先在事务内获得 `(nodeId, port)` 租约，再启动子进程；不同节点可使用相同端口，同一节点不可重复使用。

**计划默认值，可配置：**

- 节点心跳每 10 秒写入一次，30 秒无心跳视为失联。
- port lease TTL 为 30 秒，每 10 秒续约一次。
- 自动端口范围为闭区间 `20000-29999`。
- 端口从范围起点按伪随机起点循环扫描；每次服务启动最多申请并尝试绑定 5 个端口。
- OS bind race 或子进程启动失败时释放本次租约，使用 100ms、200ms、400ms、800ms、1600ms 的带抖动退避重试；耗尽后服务标记为失败，相关 route 返回 `503`。

显式端口也要登记租约，以防同一节点中两个服务使用同一端口。范围外显式端口允许使用，但同样受 `(nodeId, port)` 租约约束。固定端口冲突时不改换端口，直接标记该服务启动失败。租约过期后可被回收；数据库不可用期间不以本地假续约启动或重启服务。

## 5. 路由模型与匹配

### 5.1 Route 实体

每条 route 至少具有下列字段：

| 字段 | 含义 |
| --- | --- |
| `id` | UUID v7。 |
| `enabled` | 是否参与匹配。 |
| `matcherType` | `Exact`、`ExactCI`、`Prefix`、`PrefixCI` 或 `Regex`。 |
| `pattern` | path matcher 的模式。 |
| `hostPatterns` | 空集合为任意 host；否则使用 exact 或 `*.example.com`。 |
| `methods` | 空集合为任意 HTTP method；否则为允许 method 集合。 |
| `targetType` | `Microservice`、`StaticFile` 或 `ExtensionHandler`。 |
| `targetId` | service/static target/extension handler 的稳定引用。 |
| `priority` | 有符号 `int`，数值越大优先级越高，默认 `0`。 |
| `forwardingMode` | `Preserve`、`Strip` 或 `Replace`，默认 `Strip`。 |
| `replaceTemplate` | `Replace` 时必须提供。 |
| `requestHeaderRewrites` / `responseHeaderRewrites` | 声明式 header 改写规则。 |
| `metadata` | 扩展使用的 JSONB 元数据。 |

静态文件 target 包含每 route 的绝对 `rootPath`；微服务 target 引用可由多条 route 共用的 service；extension handler target 引用某个已注册的稳定 handler ID。

### 5.2 path 规范化

query string 不参与 route 匹配和 path forwarding，除非未来另行增加显式 query 改写能力。当前规则如下：

- 保留原始 percent encoding，包括 `%2F`、`%5C` 和其他编码；它们不是路径分隔符。
- 保留重复 `/`。
- 仅按字面 `/` 处理 RFC dot segments，即消解 `.` 与 `..`；尝试越过根的路径保持在根而不产生文件系统上跳。
- 非法 percent encoding 直接返回 `400`，不进入 fallback。
- regex 的输入是上述规范化后的 path。

### 5.3 条件匹配

route 必须同时满足 path、host 和 method 条件。host 采用标准主机比较规则：忽略大小写和端口，IDN 转为 ASCII punycode 后比较，`*.example.com` 只匹配其子域而不匹配 `example.com` 本身；IPv6 literal 使用规范地址文本比较。无 `Host` 的 HTTP/1.0 请求无法匹配具有 host 条件的 route，但仍可匹配 host 条件为空的 route。method 以不区分大小写的 token 比较；不为 `HEAD` 自动回退至 `GET`，也不自动生成 `OPTIONS` 响应。

任何条件不符、route 禁用或没有 route 时都属于 404 候选。核心先调用已注册的唯一全局 extension fallback，并向其传入原因枚举，例如 `NoRoute`、`HostMismatch`、`MethodMismatch`、`StaticNotFound` 或 `StaticIndexMissing`。fallback 未注册、拒绝处理、超时或异常时，核心返回标准 `404`。除 404 候选外的错误不进入 fallback。

### 5.4 matcher 语义和稳定排序

matcher 类型的优先级绝对固定为：

```text
Exact > ExactCI > Prefix > PrefixCI > Regex
```

数值 priority 不得跨 matcher 类型反转上述顺序。同一 matcher 类型内按以下顺序选择：

1. `priority` 降序；
2. 非 Regex 按实际匹配文本长度降序；
3. `created_at` 升序；
4. route UUID v7 字典序升序，作为最终稳定收束。

精确路由优先以请求 path 完整执行五类 matcher。若完全未命中且 path 不以 `/` 结尾，则在 path 末尾添加 `/` 后再次完整执行五类 matcher。因此 `/foo` 和 `/foo/` 可分别保存，但只有前一次完整匹配无命中时才触发后一次；根路径 `/` 不再追加斜杠。

Prefix 规则如下：

- `/api` 为 segment prefix，匹配 `/api` 和 `/api/x`，不匹配 `/apix`。
- 末尾单个 `*` 表示 raw prefix；`/api*` 匹配以 `/api` 开头的任意 path，包括 `/apix`。
- `/api/*` 仅匹配 `/api/` 及其后内容，不匹配 `/api`。
- `*` 只允许出现在模式末尾；重复 `*`、中间 `*` 或转义 `*` 一律为无效配置。

Regex 使用 `CultureInvariant | NonBacktracking`，默认区分大小写，要求整串匹配而非子串搜索。保存时必须预编译验证。

**计划默认值，可配置：** Regex 最大模式长度为 4096 个字符，单次匹配 timeout 为 50ms。执行 timeout 时跳过该条 regex、记录带 route ID 的结构化告警并继续候选选择，不将 timeout 暴露给客户端。

### 5.5 路由索引

配置快照构建期预先创建下列只读索引，请求期不得遍历全部 route：

- `Exact` 与 `ExactCI` 分别使用 hash map。`ExactCI` 使用 ordinal case folding 的标准化键。
- `Prefix` 与 `PrefixCI` 分别使用 trie；叶节点保存已按稳定排序排列的候选集。
- `Regex` 使用已预编译、已排序的列表，只在前四类都没有可用匹配时执行。

host 和 method 条件在各 matcher 候选集中继续过滤。快照构建失败时保留旧快照，节点记录配置应用错误而不部分应用新配置。

## 6. 转发、代理和静态文件

### 6.1 path forwarding

| 模式 | 行为 |
| --- | --- |
| `Preserve` | 将规范化后的 request path 原样转发。 |
| `Strip` | 允许 `Exact` 或 segment Prefix。`Exact` 的结果固定为 `/`；segment Prefix 移除已匹配的前缀，空结果转为 `/`。raw Prefix 与 Regex 不允许 Strip，保存时拒绝。 |
| `Replace` | 以模板生成 path。支持 `{path}`（规范化 path）、`{match}`（整个 matcher 命中）和 Regex `$0..$n` 捕获组。 |

query string 始终原样保留。替换模板的非法变量、未匹配捕获组引用、控制字符或生成的非绝对 path 都是配置错误。模板展开后的 path 按 URI path 规则编码，禁止通过模板插入 CR、LF 或 header 分隔符。

### 6.2 微服务定义与生命周期

service 是独立实体，可被多条 route 引用。其启动配置只公开 `ProcessStartInfo` 的安全子集：`FileName`、`ArgumentList`、`WorkingDirectory` 和 `Environment`；固定 `UseShellExecute = false`。用户若需要 shell 行为，须显式把 shell 本身设为 `FileName` 并传入参数。

- `FileName` 和 `WorkingDirectory` 必须为绝对路径；工作目录不存在时服务启动失败。
- 服务继承宿主环境变量，再由 service `Environment` 覆盖同名键；机密环境变量绝不写入日志。
- `ArgumentList` 每一项中的所有字面 `$PORT` 都替换为分配或显式端口。
- 子进程环境总是附加 `PORT=<port>` 和 `HOST=<configured-loopback-address>`。
- 上游地址仅为 `http://127.0.0.1:<port>` 或 `http://[::1]:<port>`，由 service 的 loopback 地址字段选择；子进程必须监听收到的 `HOST` 和 `PORT`。

service 启动模式为 `Eager` 或 `Lazy`。Eager 在节点加载配置后启动；Lazy 由首个请求触发，并合并同一 service 的并发启动。Lazy 请求等待服务通过 startup health check，超时或失败时返回 `503`。服务配置变更时，supervisor 先启动并验证新实例，再切换 route 所用实例并停止旧实例；端口不足或新实例不健康时保留旧的健康实例。禁用或删除服务后，引用它的 route 返回 `503`。

每个子进程处于独立 POSIX process group。停止时向整个进程组发送 SIGTERM，宽限期后发送 SIGKILL；子孙进程终止失败只写日志，不阻塞主服务停止。

**计划默认值，可配置：**

| 项目 | 默认值 |
| --- | --- |
| startup health timeout | 30 秒 |
| startup check interval | 1 秒 |
| steady health interval | 10 秒 |
| 单次 health timeout | 3 秒 |
| 连续失败阈值 | 3 次 |
| restart policy | `OnFailure` |
| restart backoff | 初始 1 秒，指数退避，上限 30 秒 |
| restart burst | 5 分钟内最多 10 次；超出后标记失败，等待显式恢复或配置变更 |
| SIGTERM 宽限期 | 15 秒，之后 SIGKILL |

health check 类型为 `Process`、`Tcp` 和 `Http`，全局默认可由 service 覆盖。默认使用 `Tcp` 检查 service loopback 地址与端口；`Process` 只确认主进程仍存活；`Http` 使用 GET，并可为每个 service 配置 URL、headers、成功状态范围和 timeout，默认成功范围为 `200-399`。

stdout 和 stderr 采用 UTF-8 按行读取，supervision 捕获契约保留有界的原始文本；Host 结构化日志只记录 service ID、stream、timestamp、日志级别、截断和丢弃聚合元数据，任意子进程输出文本绝不进入 Host 日志。无效 UTF-8 以 replacement character 表示；stdout 默认 `Information`，stderr 默认 `Warning`。

**计划默认值，可配置：** 单行最大 16 KiB；每 service 每秒最多 200 行且最多 1 MiB。超额行截断或丢弃，并以聚合计数日志报告；进程退出码、终止信号、启动失败和进程树清理失败始终记录。

### 6.3 HTTP/1.1 与 WebSocket 代理

核心入站和微服务出站均使用 HTTP/1.1。代理必须流式转发 method、body、response body 与 WebSocket，不得默认缓冲请求体。WebSocket 建立后不重试。

默认会移除 hop-by-hop headers 及 `Connection` 所列 header。WebSocket upgrade 是例外：核心验证合法 Upgrade 请求后，按协议重建必要的 `Connection: Upgrade` 与 `Upgrade` header，而不盲目透传任意 connection token。未配置改写时保留 `Host`。

对 request 与 response 的 header rewrite 使用 `remove -> set -> add` 顺序：

- header 名称按不区分大小写比较；`remove` 移除全部同名值，`set` 替换全部值，`add` 追加值。
- 正常多值 header 保留多值语义，`Set-Cookie` 不得被逗号合并。
- 可显式改写 `Host`、`Cookie` 与 `Set-Cookie`。
- 不允许增加、设置或保留 hop-by-hop headers；包含 CR/LF、NUL 或非法 header token 的规则和展开值必须拒绝。
- 模板变量仅为 `{clientIp}`、`{path}`、`{method}` 与 `{host}`；变量展开后再次执行 header value 校验。

client IP 只在 TCP peer 落入配置的可信 CIDR 时从 forwarding headers 解析。核心优先使用标准 `Forwarded`，缺失时使用 `X-Forwarded-For`，按由近到远的代理链剥离可信地址并取得首个非可信地址；无法取得时使用 TCP remote IP。直接 peer 不可信时，入站 `Forwarded`、`X-Forwarded-*` 一律移除后再代理。直接 peer 可信时保留其原始 forwarding headers。核心不配置 Host allowlist。

默认不重试。配置可针对任意 method 指定重试条件和尝试次数，但只有无 request body 的请求允许重试；流式、chunked 或具有 body 的请求一律不重放。重试采用指数退避加随机抖动。

**计划默认值，可配置，且可由 route 覆盖：**

| 项目 | 默认值 |
| --- | --- |
| retry attempts | `0` |
| 可配置的默认重试触发条件 | 连接失败、上游在尚未写 response 前断开 |
| retry backoff | 初始 200ms，指数退避，上限 2 秒 |
| 连接 timeout | 10 秒 |
| 首字节/response headers timeout | 30 秒 |
| 普通 HTTP 总 timeout | 100 秒 |
| WebSocket idle timeout | 120 秒 |

无法连接、上游提前断开或重试耗尽后返回 `502 Bad Gateway`；连接、首字节、总时长或 WebSocket idle 超时返回 `504 Gateway Timeout`。结构化日志需记录 route ID、service ID、attempt、失败阶段和耗时，不记录 body 或敏感 header。

### 6.4 静态文件

静态 target 的 root 必须为 route 配置中的绝对目录。经 `Preserve`、`Strip` 或 `Replace` 得到的 path 转为 root 下的相对文件路径：

- 只接受 `GET` 与 `HEAD`；其他 method 是 404 候选。
- 空相对 path 或指向目录时，在每一层目录查找固定 `index.html`。
- 解析后的 realpath 必须仍在 root realpath 之下；`..`、不存在文件、无 index 和越界 symlink 都视为 static miss。
- static miss 与无 index 会调用全局 fallback；fallback 不处理时返回 `404`。
- 禁止目录列表和预压缩文件协商。
- 支持标准 MIME 类型、弱 ETag、Last-Modified、If-Match/If-None-Match/If-Modified-Since 条件请求及单范围 Range 请求。

**计划默认值，可配置：** 默认响应 `Cache-Control: no-cache`，允许 route 覆盖为显式 `max-age`/`immutable` 策略。ETag 和 Last-Modified 始终启用，防止静态内容在没有版本化文件名时被长期错误缓存。

## 7. 扩展系统

### 7.1 信任边界与目录结构

扩展是可信 in-process 代码，不是安全沙箱。Collectible `AssemblyLoadContext` 只能帮助隔离依赖和释放资源；恶意或有缺陷的扩展仍可能访问进程内资源。因此仅加载由运维明确部署、审核和授信的扩展。

扩展位于可执行文件目录的 `extensions/<extension-directory>/`。每个目录必须包含以下两种 manifest 之一：`manifest.json` 或 `manifest.yaml`/`manifest.yml`；同时存在 JSON 与 YAML manifest 时拒绝加载该目录。

manifest 至少包括：

```text
schemaVersion
id
version
entryAssembly
entryType
dependencies
requiredHostApiVersion
```

`version`、`requiredHostApiVersion` 及 dependencies 使用 SemVer 与 SemVer range。加载器按依赖拓扑顺序加载，同一层按 manifest ID 字典序排序；重复 ID、循环依赖、缺少依赖、版本范围不满足、不兼容 host API、入口程序集越出扩展目录或未知 manifest 字段都拒绝加载。YAML parser 只允许基础 scalar/map/list，不处理 tags、anchor 驱动的对象构造或任意 CLR 类型。

### 7.2 宿主契约

每个扩展拥有私有 DI service provider、私有后台任务和 collectible ALC。扩展可访问稳定的 host contracts，但不能在运行时修改 root DI container 或全局 ASP.NET pipeline。跨扩展服务只能通过独立的 contracts assembly 显式 export/import，contract identity 和版本范围必须写入 manifest；重复 export、缺少 export 或 contracts 不兼容时拒绝加载。

Host API 至少提供：

- 带乐观版本、事务、通知和完整校验的 Config API，以及 route/service 管理能力。
- 扩展专属 JSONB 持久配置 API。
- 受控的后台任务、生命周期、日志、状态和 event API。
- extension handler 注册、注销、load、unload、reload API。
- 共享 contract 的 export/import API。

扩展不注册任意 HTTP endpoint 或 health endpoint。其 HTTP 接入仅限于 route target 指向的稳定 handler ID，以及全局唯一 fallback。handler 直接处理 `HttpContext` 并拥有 response 写入权；未加载、已停止或正在切换的 handler target 返回 `503`，handler 未处理异常返回 `500`。系统最多允许一个 fallback；fallback 在所有 404 候选前调用。

### 7.3 显式加载、卸载与重载

核心不使用 file watcher。管理扩展通过 Host API 显式发起 load/unload/reload。

reload 的状态机为：

1. 在新 collectible ALC 中加载并验证新 manifest、依赖、contracts、入口类型和配置兼容性。
2. 调用新实例 `Start(reloading: true)`，但尚不接管 route handler。
3. 旧实例停止接收新 handler 请求并执行 `Stop`/drain；切换窗口内指向该扩展的请求返回 `503`。
4. 等待旧 handler 请求和后台任务退出；超时后取消其任务并继续停止。
5. 调用新实例 `OnPreviousStopped`，再原子切换 handler 注册并使新实例开始服务。
6. 尝试卸载旧 ALC，并以弱引用和 GC 验证是否已释放。

新实例验证或 `Start(reloading: true)` 失败时，旧版本持续运行。旧版本停止阶段失败时，系统尝试恢复旧实例并停止新实例；恢复失败则扩展停止、其 handler route 返回 `503`。旧 ALC 因泄漏无法卸载时记录告警并保留残留 ALC，新版本仍可工作，后续 reload 不被自动阻断。

**计划默认值，可配置：** 扩展 handler drain timeout 为 30 秒，后台任务 stop timeout 为 30 秒，ALC 卸载验证在最多 3 轮 GC 后报告结果。

### 7.4 失败隔离与事件

handler、后台任务和事件订阅回调的异常必须在宿主边界捕获，并记录 extension ID、回调类别和异常摘要。每个扩展在全局可配置滑动时间窗口内累计这些异常；达到阈值后停止该扩展，取消其后台任务并注销其 handler。默认失败阈值为 10。

**计划默认值，可配置：** 失败统计窗口为 60 秒，三类回调共享计数。成功不重置滑动窗口计数；被停止的扩展只能通过 Host API 显式 load/reload 恢复，期间 handler route 返回 `503`。

事件总线仅在单一节点内存中运行，不持久化、不跨节点重放。每节点每扩展使用独立、有序、串行消费的 best-effort 队列；订阅异常不影响其他订阅者。核心至少发布 `ConfigurationSnapshotApplied`、`RouteChanged`、`ServiceStateChanged`、`PortLeaseChanged` 和 `ExtensionStateChanged`，扩展可定义自有版本化事件类型。

**计划默认值，可配置：** 每 extension 队列容量为 1024；队列满时丢弃 newest event、递增丢弃计数并写聚合告警。

## 8. 网络安全、资源限制与可观测性

服务预期部署在公网反向代理之后。核心只实现无法完全交由外层反代的约束：可信代理 CIDR 解析、转发 header 清理、动态 route/static 路径防护、限流和应用层资源限制。

限流使用按 client IP 分区的 token bucket，依次应用全局 bucket 和可选 route bucket。限流仅在节点内生效，不试图提供集群一致配额；WebSocket 只在握手阶段计入请求限流。客户端 IP 不可解析时使用 TCP remote IP。

默认全局和 route bucket 均为“不限制”。启用后的 token limit、tokens per period、period、queue length、拒绝策略和 `Retry-After` 都存入 PostgreSQL 配置快照并可热更新。

除限流外，核心实现全局业务配置化的资源限制：request body 最大大小、请求 header 操作上限、并发请求数和 request read timeout；适合按 route 收紧的限制允许 route override。超过限制时分别返回协议适当的 `413`、`431`、`429` 或 `408`。Kestrel 解析前必须存在固定硬安全上限；运行时快照只能设置不超过该上限的限制。

**计划默认值，可配置：** body 最大 30 MiB、header 总大小 32 KiB、每节点并发请求 1024、request read timeout 30 秒。生产环境应结合前置反代的限制统一配置，避免两层策略相互矛盾。

核心只输出基本结构化日志：启动/停止、migration、快照切换、route 结果摘要、代理失败、静态文件拒绝、端口租约、进程状态、extension 生命周期、失败阈值和限流/资源拒绝。不得记录 secrets、完整请求 body、Cookie、Authorization 或任意子进程输出文本。指标以 host events 的形式提供给扩展；核心不绑定 Prometheus、日志存储或健康 HTTP endpoint。

## 9. 测试与验收

自动化测试以集成测试为主。连接串从 `NEKOSTICK_TEST_PG` 读取，要求测试账号可创建和删除临时数据库。每个测试运行创建独立随机 UUID 命名的数据库或 schema，并在 `finally` 中清理；清理失败应使 CI 失败并输出不含 secrets 的诊断信息。并行测试运行必须互不共享数据库/schema。

GitHub Actions 在 Ubuntu 上执行，提供 PostgreSQL 16 service。目标 POSIX 兼容性通过 .NET 跨平台 API、Docker 工件和 systemd 单元模板保证，首期不在 macOS 或其他 POSIX 平台运行 CI 矩阵。

必须覆盖下列场景：

- idempotent migration、advisory lock、失败退出、Host Config API 乐观并发、原子批量提交、NOTIFY 加轮询恢复。
- 全部 matcher 类型、类型排序、priority、host/method 条件、末尾斜杠完整回退、dot segment、编码、prefix `*`、regex 编译错误和 timeout。
- Preserve/Strip/Replace、代理 HTTP/1.1/WebSocket、可信/不可信 forwarding headers、header rewrite、无 body retry、502/504 映射。
- 静态 index、range、ETag/conditional request、MIME、symlink 越界、目录列表和 cache policy。
- Eager/Lazy 子进程、端口租约、固定端口冲突、数据库离线、health/restart、进程组终止和 stdout/stderr 限流。
- manifest JSON/YAML、依赖拓扑、contracts、扩展 load/unload/reload、ALC 泄漏、失败阈值、事件队列、handler `503` 和全局 fallback reason。
- 多节点配置传播、断线后版本轮询恢复、已有快照继续服务以及无快照拒绝启动。

验收标准是上述集成测试在 GitHub Actions Ubuntu + PostgreSQL 16 service 中稳定通过，并且 Dockerfile、systemd unit 模板和 CLI 安全启动开关可按文档完成验证。
