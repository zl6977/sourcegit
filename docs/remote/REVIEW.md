# Remote 架构实施独立审查

依据：`REMOTE_ARCHITECTURE_PLAN.md`（2026-10-02），审查涵盖阶段 0–6 全部交付。受限首版不等于完整改造完成。状态：待实现 / 待验证 / 通过 / 问题；只有源码及运行证据充分才标记通过。

## 完整需求验收表

| ID | 需求及计划章节 | 证明完成所需证据 | 当前结论 |
| --- | --- | --- | --- |
| B01 | §1、4 Windows 仅 GUI；Git、仓库 I/O、监控均在 Server；无本机 Git、无挂载 | 仓库入口清单逐项源码检查；Windows 无 Git 环境实际 GUI 操作 | 待实现 |
| B02 | §1、4 四个独立 modules 项目及无 GUI 依赖的 Server/Core/Contracts | csproj、solution、独立构建、依赖检查 | 待实现 |
| B03 | §1 保留现有功能并支持 Linux x64/arm64、WSL2/SSH | 功能清单全部完成和矩阵证据；平台能力限制须合理，不得替代必迁移功能 | 待实现 |
| C01 | §5.1 稳定 connection ID，WSL 发行版/用户、SSH Host、无明文密码 | 配置与 GUI；同路径跨连接书签隔离、远端规范化路径及句柄测试 | 待实现 |
| C02 | §5.1 SSH 配置/key/agent、主机密钥验证、密码/MFA/指纹登录入口 | OpenSSH 启动参数、真实认证及失败测试、实例核对 | 待实现 |
| C03 | §5.2 检测 OS/arch/user/Git/build、无 runtime 自包含包 | 探测/部署源码、真实干净环境发布安装 | 待实现 |
| C04 | §5.2 下载/本地上传回退、摘要、原子安装、版本并存、安装锁 | 真实远端无网络部署、并发安装、错误摘要测试 | 待实现 |
| C05 | §5.2 启动锁、实例记录、权限、握手后复用 | 并发启动、失效 PID/记录、端口实例不符测试；凭据不泄漏 | 待实现 |
| C06 | §5.2 回环随机端口、WSL 本地连接、SSH 回环隧道、日志与机器记录分开 | 实际 WSL NAT/mirrored 和 SSH；非交互启动、stdout 噪声测试 | 待实现 |
| C07 | §5.2 启动 SSH/WSL 进程结束后 Server 存活 | 真实断开启动命令、隧道后 PID/重连证据 | 待实现 |
| C08 | §5.2 env-setup、明确发行版/用户、Git/PATH/proxy/agent 能力、变化重启 | 成功/失败 setup，agent socket、运行任务下重启测试 | 待实现 |
| C09 | §5.3 build/protocol/capability/instance handshake、认证错误 | 实际发布产物握手、认证与版本错误用例 | 待实现 |
| C10 | §5.3 单 GUI 所有者、独立恢复凭据、第二 GUI 拒绝、宽限期与任务保护 | 多客户端/断线复接/所有权过期测试 | 待实现 |
| C11 | §5.3 七种连接状态、重建通道/订阅/快照、重启丢旧句柄 | GUI 断线与恢复、Server 重启效果 | 待实现 |
| C12 | §5.3 客户端先保存 operation ID、owner+ID 去重、不同参数拒绝 | 成功但响应丢失、重复请求、不同参数、断线不自动重发测试 | 待实现 |
| C13 | §5.3 操作六种状态、查询、重启未知、保留结果及刷新 | 生命周期源码、重启结果未知及实际 Git 对照 | 待实现 |
| C14 | §5.3 无连接/无任务延迟退出、写任务完成、helper 5 分钟可配置超时 | 关闭窗口活动任务、等待交互断线/恢复/超时真实 helper 测试 | 待实现 |
| C15 | §5.3 连接/会话/operation 关联日志与 GUI 诊断 | GUI 入口与日志、秘密值排除检查 | 待实现 |
| P01 | §6 固定 RPC 实现并记录原型评估、双向、取消、二进制、Release/AOT | 设计记录与真实 Windows AOT/Linux 自包含运行 | 待实现 |
| P02 | §6 version/request/session/operation/error、帧限制/timeout/backpressure/并发 | 畸形/超大帧、慢消费者、大内容下心跳/取消/交互响应测试 | 待实现 |
| P03 | §6 源生成 JSON、完整环境/目录/会话/查询/内容/写/操作/交互/事件接口 | Contracts 元数据、实现与调用源码、真实序列化 | 待实现 |
| P04 | §6 结构化普通操作；自定义命令单独能力、远端执行 | API 参数及命令构造审查、特殊路径测试 | 待实现 |
| P05 | §6 历史/目录分页、diff/content 按需、二进制分块 | 大历史/大 diff/二进制查询、缓存及流量证据 | 待实现 |
| S01 | §7 Server watcher 合并事件、影响类别、overflow/full/manual refresh | 外部终端更改 refs/worktree/stash、溢出测试及 GUI 效果 | 待实现 |
| S02 | §7 状态代次忽略旧结果、组合一致性与重试 | 乱序结果/并发修改测试和 UI 源码 | 待实现 |
| S03 | §7 common directory 写协调、外部 Git 锁及前后刷新 | 共享 worktree 并发写、外部修改真实仓库对照 | 待实现 |
| S04 | §7 read 取消、按能力 write 取消、continue/abort、Server 自动 fetch | 操作取消/恢复、会话调度防重复测试 | 待实现 |
| F01 | §8、阶段2 远端 Launcher/目录/书签/Repository；status/refs/history/commit/text diff | WSL/SSH 实际 GUI 页面及 watcher 更新 | 待实现 |
| F02 | 阶段4 stage/unstage/commit/checkout/分支/fetch/ff pull/push/stash save/list | WSL/SSH 真仓库结果与 Git CLI 对照；分叉/冲突/进行中正确限制 | 待实现 |
| F03 | §8 HTTPS/SSH Git askpass、保留远端 credential helper/key/agent | 真实 HTTPS/SSH 提示与 helper；不覆盖既有 helper；登录 SSH 与 Git SSH 分离 | 待实现 |
| F04 | §8 commit/rebase editor 安全 IPC、op/interaction/token、远端文件写回 | 真 helper 无头调用及 GUI 编辑；断线、恢复、取消、超时 | 待实现 |
| F05 | 阶段5 merge/rebase/cherry-pick/revert/reset/冲突/interactive rebase/continue/abort | 真实冲突、已有中断恢复；UI 解决并继续/中止 | 待实现 |
| F06 | 阶段5 完整 merge/rebase pull、stash apply/pop | 分叉 pull 与 stash 冲突及恢复；不得永久以 ff-only 代替 | 待实现 |
| F07 | 阶段5 submodule/worktree/LFS/bisect/GitFlow | 各现有功能远端 GUI 入口、真实行为验证 | 待实现 |
| F08 | 阶段5 blame/文件历史/图片 diff | 远端内容读取、UI 正确渲染及二进制测试 | 待实现 |
| F09 | §8 补丁上传/archive 下载/Windows 文件选择 | 实际上传应用/本地导出产物、大小/取消验证 | 待实现 |
| F10 | §8 外部工具运行位置/临时副本/回写变化检查、自定义命令/hooks/LFS/签名 | 真实工具及回写冲突；非交互 hook/签名；终端/pinentry 限制明确 | 待实现 |
| F11 | §8 UI/仓库设置划分、旧书签迁移、connection+repo UI 状态 | 保存/重启/迁移测试，Server 仓库 settings，无远端路径本机 I/O | 待实现 |
| F12 | §8 AI diff 来源、调用位置/凭据明确 | 原功能接入源码；凭据不跨端隐式复制 | 待实现 |
| G00 | 阶段0 全 Commands/File/Directory/Process/Native/path/IPC 盘点 | 所有入口逐项功能/位置/API/interaction/test/status 清单 | 待实现 |
| G01 | 阶段1 独立工程、UI RPC、Release/AOT、WSL/SSH 最小握手 | 构建和运行结果，不以 Debug 替代发布验证 | 待实现 |
| G02 | 阶段2 WSL 自动部署全只读、无本机 Git/挂载/远端 runtime | 实際环境和 GUI 录像/截图或操作日志 | 待实现 |
| G03 | 阶段3 SSH 自动部署与全部恢复边界 | 离线远端、升级、并发、断线、重启、第二 GUI、认证失败证据 | 待实现 |
| G04 | 阶段4 双连接日常写与真实 helper/失响应 | F02–04、C12–14 实际通过 | 待实现 |
| G05 | 阶段5 全清单完成与无远端本机 I/O | F05–12 和逐项残留审查 | 待实现 |
| G06 | 阶段6 匹配 Windows/x64/arm64 发布包摘要、安装/升级/回退/卸载/诊断文档 | 实际发布产物全矩阵及运行验收记录 | 待实现 |
| T01 | §10 环境、普通/bare/worktree/submodule/空/大/二进制、特殊路径/符号链接 | tester 验证矩阵逐格证据；模拟传输不可替代真实 WSL/SSH | 待实现 |
| T02 | §10 高延迟/快速变化/持续日志、取消/分页/背压 UI 响应 | 负载效果证据及 GUI 响应验证 | 待实现 |

## 初始源码审查（实施前）

当前 `git status` 仅计划文档未跟踪，尚无 modules 或远端接入。`src/SourceGit.csproj` 只引用 AvaloniaEdit；`SourceGit.slnx` 只包含 GUI 和依赖项目。

已确认的迁移重点：

- `src/Commands/Command.cs:161–189` 使用 Native 获取本机 Git 与 credential helper，再创建本机进程。仅代理 Git stdout 无法满足文件、认证及执行位置边界。
- `src/ViewModels/Repository.cs:420–445` 本机读取 commondir、settings 并创建 watcher；466 写 MERGE_MSG；541/546 读仓库文件；1907 检查本地锁；1929 GUI 自动 fetch。
- `src/ViewModels/Launcher.cs:83/318/396/477–502` 本机目录和 `.git` 检查。新远端入口需直接调用 Server，不得把 Linux remotePath 传入这些旧入口。
- `src/Models/RepositorySettings.cs:70–109` 本机访问 common directory；`src/Models/Watcher.cs:27–63` 本机 FileSystemWatcher。必须检查所有旧 UI 动作也采用远端业务边界，不能只增一个并列只读页面。

以上为待迁移事实，当前尚不属于已实施缺陷。后续以新代码为依据持续记录审查结论。

## 第一轮实施审查（源码快照，修复待复核）

四个 modules 项目和 `src/Remote` 已出现；现在具有 RPC、启动器、Core、Server、Git 与文件 facade。以下结论针对首次读取快照，代理实现仍在变动，修复后必须复核，不能将问题列表当作最终状态。

| 编号 | 发现与源码依据 | 风险/要求 | 已通知 |
| --- | --- | --- | --- |
| R01 | `RemoteWorkspace.FileAsync` 接收 overwrite/truncate/createNew，但构造 FileRequest 未传这些字段 | 写短文本保留旧尾部、覆盖移动失败；CreateNew 竞态。必须传 flags 并测试 | root |
| R02 | `GitPolicy.Reads` 包含整个 reflog；IsQuery 只找 branch/config 查询选项 | reflog delete/expire 或混合写参数可假冒 readOnly，无 operation ID | executor |
| R03 | `RepositoryService.FileAsync` 写、删、move 不经仓库锁与 operation 登记 | 文件修改与 Git 并发、失响应无法查询实际结果 | executor |
| R04 | Server 操作表只以 operationId 为键，所有权更换不分 owner | 新 GUI 访问或冲突旧 owner 操作；须 owner 隔离 | executor |
| R05 | `HandleAsync` 在读取循环 await RequestsGate(16) | 满负载后 cancel/ping/interaction 消息无法读入，可能死锁 | executor |
| R06 | `PrepareProcess` 将 Server 总 token 交给 Git/helper；非操作专用凭据 | 违背计划 op/helper 凭据隔离约定 | executor |
| R07 | `RunAsync` 全内存收集 stdout/stderr；GitProcess DeferredStream 等退出 | 长 fetch/push/日志没有实时 progress，内存随输出增长；分块结果不是执行流 | executor/root |
| R08 | `GitProcess.Execute` 首次读输出时快照 stdin | 若旧 Command 先 BeginOutputReadLine 再写输入，patch/update-index 等输入丢失；需检查调用顺序 | root |
| R09 | `RepositoryDirectoryInfo` 忽略递归 EnumerationOptions；RepositoryStream 无 SetLength | 兼容 facade 与真实 System.IO 语义差异，须检查现有调用行为 | root |
| R10 | `RepositoryPath.RemotePath` 将反斜杠转斜杠 | Linux 合法反斜杠名称不可访问，违反远端文件名语义 | root |
| R11 | 只有 URI 参数转远端路径，C盘临时 patch/archive/tools 路径原样送 Git | 未证明真实跨端导入导出和外部工具可用，须逐调用显式传输 | root |
| R12 | Bootstrap 探测 aarch64 但默认取 linux-x64 包 | arm64 自动部署失败 | executor |
| R13 | ServerRecord 复用仅验证 PID 存活；未先握手验证，失败未重新部署启动 | PID 重用/错误监听者导致永久坏记录 | executor |
| R14 | ExecuteGitAsync 内部 polling 当前 client；重连新 client 未恢复该 polling | 操作 ID 已生成但 GUI 未持久保留/显示，失响应后结果无法自动恢复 | executor/root |
| R15 | DrainTunnel 顺序读完 stdout 再读 stderr | SSH stderr 管道满时进程阻塞，须同时 drain | executor |
| R16 | 操作表无清理；同步 facade 在 UI 使用 GetResult | 长会话资源增长、高延迟界面卡顿；需限界和真实效果验证 | executor/root |
| R17 | QueryLocalChanges/Rebase/InteractiveRebase 的 Args 前置固定 `-c`；RemoteWorkspace 只剥离 core.editor=true，GitPolicy 取 args[0] 作 verb | 远端 status 和 rebase 直接被拒绝；需要允许配置的结构化传递，保留解析语义 | executor/root |
| R18 | RemoteWorkspace 按已开父仓库前缀选句柄；Core 有句柄时忽略 WorkingDirectory、固定 repo.WorktreePath | submodule Git 查询/写操作误执行于父仓库，需独立句柄及验证后的 cwd | executor/root |
| R19 | OpenAsync 查找后创建非原子，Close 直接销毁复用句柄 | 并发打开重复 watcher；多 tab 关闭一页可能使别页过期，需引用语义 | executor |

尚未有实际 GUI/WSL/SSH/Release/AOT/arm64 运行证据，因此全验收表保持待验证，不能因项目源码出现标记通过。

## 第二轮源码复核

环境权限调整后恢复读取。已从当前源码确认 R01 flags、R03 File operation 与 common gate、R04 owner key、R05 控制请求独立于请求 gate、R06 每操作 helper token、R12 架构包选择、R15 同时 drain 两管以及 R17 config 白名单、R18 嵌套仓库精确句柄的修复方向。这些属于“源码已修复，运行待验证”。R07 已加入 stderr OperationProgress 与 stdout 大小上限，但 GUI 日志接入和大内容完整限界仍未证明。R16 从无界改为 100000 操作记录硬限，不等于完整的宽限期清理。R19 executor 报告 refcount/session gate 修复，需结合客户端租约再次检查。

新发现：

| 编号 | 当前源码事实 | 必须核对/修复 |
| --- | --- | --- |
| R20 | Repository.Open 仍创建 GUI AutoFetch timer | §7 明确自动 fetch 在 Server 调度，需迁移和配置 API |
| R21 | Merge/Rebase/StashChanges/GitFlow 仍使用 Native.OS.GitVersion/GitFlowVersion | Windows 无 Git 时不能以本机版本决定远端支持能力 |
| R22 | RemoteRepositories 仅文本填写 target/path；无发行版选择、目录浏览、SSH 登录动作 | 连接/目录 UI 完成条件未满足；CreateLoginTerminal 存在不代表用户可操作 |
| R23 | RemoteConnections 有 manager 后不随 options 更新重建 | 修改/保存 Host 后可能继续旧主机，连接稳定 ID 必须映射当前配置 |
| R24 | Connected event 触发 fire-and-forget Rebind，同时 ConnectAsync 又可能 Rebind | 重绑定需串行/单入口避免清空订阅、重复 Open |
| R25 | RemoteWorkspace 丢 GitResult.Generation，仅按代次过滤事件 | §7 忽略旧查询结果尚未实现，组合数据一致性未证明 |
| R26 | Server watcher 包含所有元数据；刷新全部类别 | Git status 写 index/untrackedCache 可能造成自激刷新；需要真实快速变化效果测试 |
| R27 | RunCustom 未经 common-dir gate、未 PrepareProcess；stdout 不实时进度 | 自定义仓库命令仍应协调写与 helper 交互 |
| R28 | 每个 write chunk Hash 整文件；ExpectedHash 无 GUI caller | 大上传 O(N²)；分块回写非原子，外部工具文件变化检查未落实 |
| R29 | operation capacity 检查先于 GetOrAdd | 达硬限后已存在 ID 同样被拒，破坏重连去重查询；须只拒新操作 |
| R30 | GUI 多次实际 Open；未发现对应 CloseRepository | 服务 refcount 新增后必须用租约/缓存匹配释放，窗口关闭不可泄漏 watcher |
| R31 | Path.GetFullPath 不解析 symlink；URI Normalize 先消解 `..` | symlink 别名可能使同一 commonDir 锁分裂；link/../ 远端语义须服务端解析 |
| R32 | Native.OS.OpenInFileManager/OpenWithDefaultEditor 无 remote URI 防护 | Windows 外部工具入口仍可收到远端 URI，必须显式远端动作或临时下载 |
| R33 | nonremote GitProcess/File facade 保留 GUI 本机 Git/仓库 I/O | §1 继续支持 Windows 本地仓库也应经 Local Server 同业务边界，需迁移或明确取消该支持范围 |
| R34 | SSH 业务与启动均 BatchMode；独立登录终端不能自动复用密码会话 | 真正密码/MFA/HostKey 登录流程需验证复用能力，不能用存在登录按钮代替可执行连接 |
| R35 | Protocol.Build 固定 remote-1；已安装 executable 则跳过包摘要核验 | 构建更新同 ID 可静默复用旧 Server；实际版本/commit/manifest 与升级回退流程须完成 |

独立源码复核尚不能证明完整改造通过；特别需要 tester 提供真实发布包、WSL/SSH 和 GUI 功能矩阵证据。

## 第三轮实现复核与测试覆盖核查

已读取当前 Core/Server/RemoteClient、RemoteWorkspace/RemoteConnections、GitProcess、DiffTool/MergeTool/ExecuteCustomAction，以及两个 runner 的源码和 `TEST_RESULTS.md`。报告中的 Windows Server 18 项与无渲染 GUI adapter 7 项有相应测试代码；这些测试明确不等于真实 GUI、SSH、arm64 或完整发布验收。真实 Linux 17 项结果在测试文档中仍等待产物核对。

源码修复进展：

- R20：Repository 远端路径不再启动本机自动 fetch timer，改为 Server ConfigureAutoFetch；Server 按 common directory 管理 timer，读远端 disableautofetch/sshkey。Windows runner 的慢测试确实等待 Server 自动取 refs，而非发 client fetch，覆盖基础调度和 disabled remote；未覆盖重配/断线/多 worktree 长 fetch。
- R21：Merge/Rebase/StashChanges 改为 RemoteWorkspace.GitVersion；GitFlowVersion 查询远端能力；环境变化重连会重新查询。支持能力的真实远端依赖矩阵仍待验证。
- R23、R24、R30：配置变化会销毁旧 manager；Workspace 仓库 gate 串行重绑定，memoize handle、same-instance 复用及最后 subscription 关闭；7 项 adapter 包括 final lease release。配置变化重建身份的后续问题见 R37。
- R27：Core.ExecuteCustomAsync 已获取 common gate、调用 PrepareProcess，并提供 stdout 输出事件；GUI 自定义动作尚未接入该 API。
- R28：普通 write 不再每 chunk Hash 整文件，hash 独立接口已出现；客户端外部回写 ExpectedHash/原子性仍未证明。
- R29：容量判断对已登记 ID 例外，源码修复；边界测试待补。
- R31：Core.RemotePaths 采用 Linux realpath；URI Normalize 仍在调用 Server 前消解 `..`，symlink/../ 的客户端语义问题仍在。
- R09：RepositoryStream.SetLength 与 Append 不存在文件已实现；目录递归 EnumerationOptions 仍未实现。
- R10：RemotePath 不再替换 Linux 反斜杠，源码修复。
- R22：Launcher 新菜单已接 RemoteRepositories，出现远端目录浏览及登录动作；真实可见 GUI 操作和两阶段 SSH 认证尚未验证。

| 编号 | 当前明确问题 | 所需修复/验证 |
| --- | --- | --- |
| R36 | DiffTool.Open 只调用 GitProcess.Start，远端 Start 直接返回 true，Execute 直到读/Wait 才触发；本入口不读不等 | 远端外部 diff 静默不运行；完整跨端工具必须实际启动、下载临时文件、回写检查。GitPolicy 也不含 difftool/mergetool，原 Windows tool 配置不应送远端 |
| R37 | manager 各自生成 owner/recovery；任何 options 变化重建 manager | 即便改 Name/PackagePath 也会在同 Server 五分钟恢复宽限内被拒；同 GUI 不同 alias 连接同实例仍有冲突，需 GUI 身份/实例会话复用 |
| R38 | ExecuteCustomAction Run/RunAsync 仍使用 Windows System.Diagnostics.Process，WorkingDirectory 是 repo URI | 远端自定义命令无法执行，Server API 存在不代表功能完成 |
| R39 | Server 握手响应后立即补推 pending interaction；client reader 在 Workspace.Bind 前已运行 | pending editor/askpass 请求可能在重连事件订阅前丢失；需要 Ready/补拉或缓冲和 interactionId 去重，真实断线 helper 测试 |
| R40 | PendingOperations 只复制到新 client，原 ExecuteGitAsync polling 未接续新通道 | 写请求恢复结果仍缺少完整 UI 流程；未发送重试与能继续查询结果是两个不同要求 |
| R41 | Commands.Config.Get 无 --get，被当写操作；GitPolicy 禁全部 core.* | 普通查询和 core.autocrlf/ignorecase/filemode 设置被误限制，现有 user.name/email 测试不能覆盖；须精确可执行配置规则及设置迁移 |
| R42 | Server 返回原始 Git stdout，GUI Commands Query* 继续执行主要解析/状态计算 | 这是过渡 adapter；§3/4 规定 Core/Server 解析及语义 DTO/API 迁移尚未完成，须功能清单逐项落实 |

R25 旧查询过滤仍未修：GitExecution 不携带 GitResult.Generation，事件过滤不能防止旧查询覆盖新 UI；R32 外部工具、R33 本机 Server 全边界和 R35 匹配 build 升级路线仍待完成。所有完整验收项仍需以对应范围的运行证据判定，不能根据 18+7 基础场景推广为阶段 0–6 完成。

## 第四轮运行复核（2026-10-02）

本轮对 R25/R32/R35/R36/R38/R41 逐项对照当前源码与本轮实际运行证据复核；第三轮表中的状态以下表为准。

| 编号 | 结论 | 源码/运行证据 |
| --- | --- | --- |
| R25 | **适配器缺口已修复并测试；消费者端随 R42** | `GitExecution` 携带 `Generation`（GitProcess.cs:10），`GitProcess.Generation` 暴露（:32），`RemoteWorkspace.cs:250` 由 `GitResult.Generation` 传播；新增 GUI 套件项 `PASS Client adapter carries repository generation for stale query detection`（写后 generation 严格递增）。GUI 端尚无按 generation 比较丢弃旧结果的消费逻辑（src/Commands 与 ViewModels 无 Generation 消费）；每轮刷新 CTS+token 模式（Repository.cs:1339-1343）已防同类刷新过期覆盖；§7"忽略旧查询结果"的完整消费随 R42 语义 DTO 迁移（StatusSnapshot.Generation 消费者）落地。 |
| R32 | **已修复并测试** | `OS.cs`：`OpenInFileManager` remote→仅通知；`OpenTerminal` remote 检查先行（远端仓库不打开本地终端，先于空 shell 判断）；`OpenWithDefaultEditor` 先经 `DownloadRemoteFileForEditor`（OS.cs:299-316）下载到临时文件再启动真实编辑器（文件保留供编辑器持有）。GUI 套件项 `PASS Native open actions reject remote URIs with explicit actions`。 |
| R35 | **产品路径已缓解；残留已记录** | 发布构建号内容派生：`publish-remote.ps1:11-24` 对 `modules/+src/` 源文件排序取 SHA256 → `remote-<20hex>`，经 `-p:SourceGitBuildId` 生成 `BuildIdentity`（SourceGit.Contracts.csproj:4-10）；`remote-1` 仅为 Debug 默认。安装跳过以 `versions/<build>/sourcegit-server` 存在为键（ConnectionManager.cs:157）：同 ID ⇒ 同源码内容，复用正确；新 ID 必走双摘要校验安装（客户端 SHA256 :164-167 + 远端 `sha256sum -c` :170）。握手强校验 `Version`+`Build` 匹配（Program.cs:97），旧/不匹配 Server 显式拒绝，不存在静默复用。包附 `.manifest.json`（buildId/runtime/sha256/protocolVersion，publish-remote.ps1:34）；`versions/<id>/` 版本并存，回退 = 客户端回退（旧版本保留在盘上）。残留：Debug 构建共享 `remote-1`，跨 Debug 重建可能复用旧 Debug Server（仅开发环境；可删 `~/.sourcegit-server/versions/remote-1` 强制重装）；无显式回退 API 与 commit 元数据。 |
| R36 | **已修复并测试** | `DiffTool.Open` 远端分支 → `OpenRemoteAsync` → `RemoteExternalTools.DiffAsync`（DiffTool.cs:25-29,54-69）；`MergeTool` → `RemoteExternalTools.MergeAsync`（MergeTool.cs:25-27）。GUI 套件三项：`PASS Windows external diff receives correct Linux snapshots and quoted paths`、`PASS Windows external merge downloads Git stages, uploads result and stages remotely`、`PASS Windows external merge rejects stale result without overwriting remote edits`。 |
| R38 | **源码已修复；专项 GUI 项待补** | `ExecuteCustomAction.Sure` 远端分支 → `RunRemoteAsync` → `workspace.ExecuteCustomAsync`（ExecuteCustomAction.cs:229-239,299-310，stdout 逐行入日志）；`${REPO}` 以 `ExecutionPath` 替换（:251-253）。Core.ExecuteCustomAsync 经 common gate + PrepareProcess（第三轮已核）。GUI 套件尚无远端自定义命令专项。 |
| R41 | **源码已修复；回归项待补** | `Commands/Config.cs:69,75` `config --get {key}`，被 `GitPolicy.IsQuery`（RepositoryService.cs:335）识别为查询；可执行限制由"禁全部 core.*"改为精确 `IsExecutableConfiguration`（:316-327，仅 core.editor/sshcommand/hookspath、credential.*.helper、filter.*.process/clean/smudge、diff.*.command 等 exec 类键且仅写操作）；core.autocrlf/ignorecase/filemode 的读取与设置均放行。GUI 套件无 core.* 配置专项（现有 user.name/email 项不能覆盖）。 |

运行证据（本轮实际执行）：

- `dotnet build SourceGit.slnx`：0 Warning / 0 Error（R32+R25 代码与测试变更之后）。
- GUI adapter 套件（本地 WSL Server）：13/13 PASS，33.75 s；新增 R32、R25 两项如上。
- Windows Server 套件（--server 本地模式，git 2.53.0.windows.2）：本轮重跑 21/21 PASS，39.8 s，日期已刷新。

范围决策记录：R33（本地仓库非远端路径保留本机 I/O）为支持范围决策——本地仓库不在本轮迁移范围，继续原生路径；"本地仓库亦经 Local Server 同业务边界"保留为未采纳选项，不视为缺陷。

R39/R40 维持第三轮结论未变；R42（语义 DTO 迁移）为最大剩余代码缺口，同时承载 R25 消费者端与 R41/R38 专项测试的落点。

WSL 验收执行（同日续，2026-10-03 前）：新发布包 `remote-4358e2702f8cc93654ba` 于 Ubuntu-24.04（WSL2，内核 6.18.33.1，git 2.55.0）实际安装运行——connector 部署/握手/空仓库查询 PASS；全量套件 `--record` 21/21 PASS（11.7 s）；命令与范围见 TEST_RESULTS.md"R25/R32 code re-validation"节。

本轮附加发现（已处置）：

1. **R35 Debug 残留实际发生**：未盖章的 Debug 测试客户端（Build=remote-1）跳过安装、复用 23:58 已装的旧 `remote-1` Server，其"PASS"不能作为新包验收证据；已用 `-p:SourceGitBuildId=<包ID>` 重跑方为有效。验收流程要求测试客户端与包 BuildId 一致。
2. **同 record 路径双启动竞态**：绕过 Bootstrap `flock` 手工双启动 Server 时，先退出实例的 finally 清理会删除后被覆盖的实例记录（本轮手工排障时实际发生，导致记录丢失、Server 存活但不可用）。产品启动脚本以 `flock` 串行且 record 按 build 隔离，不受影响；手工操作不得绕过 flock。
3. **测试 harness 跨文件系统缺陷**：全量套件 `--record` 模式原默认 `testRoot` 为客户端本地 Windows 路径，Linux Server 按自身 CWD 相对解析，产生字面 `c:\...` 目录并致 watcher/linked-worktree 两场景失败；已修复为 `--root` 缺省时取 Server 主目录下 `sourcegit-remote-tests-<guid>`（与早期独立运行的显式 `--root /tmp/...` 用法一致），污染目录已从工作树清除。
## 第五轮：语义状态/分支迁移与自动连接（2026-10-03）

本轮实际运行：Windows Server 套件 23/23 PASS（git 2.53.0.windows.2）、`--slow` 24/24 PASS（89.97 s）、WSL 验收新包 `remote-s2` 于 Ubuntu-24.04（git 2.55.0）PASS（部署/握手/空仓库查询）。src 编译 0 CS 错误（`src/bin` 复制被用户运行中的 GUI 锁定，MSB3021/3027，非编译错误；最终 0/0 全量构建与 13 项 GUI 套件待 GUI 关闭后执行）。

### 00:21 WSL 连接失败诊断

GUI 日志止于 `Synchronizing → Server connection closed`。同流程以测试客户端原样重放（probe→skip-install→start→handshake→connected）PASS 4.5 s，Server 健康、记录完整、不可复现；结论为瞬态（[INFERENCE] WSL2 NAT 转发抖动），不判为代码缺陷。处置：Server 握手异常不再静默吞掉——`Program.cs` `HandleAsync` 在 ready 前失败时输出 `SOURCEGIT_REJECTED <异常类型>: <消息>` 到 stderr（进入 `server-<build>.log`）；同轮 WSL 排障中该日志实际捕获到一条 `SOURCEGIT_REJECTED InvalidOperationException: Instance owned by another GUI`，证明其价值。

### 已保存远端仓库自动连接（用户请求）

`Launcher.OpenRemoteRepositoryAsync` 重写：`_remoteOpening` HashSet+gate 去重；90 s `CancellationTokenSource` 超时；`IOException|SocketException|EndOfStreamException` 重试 3 次、间隔 2 s；首次尝试发进度通知、最终失败发错误通知。启动时 `RestoreAsync` 打开全部已保存仓库，远端项经此路径自动连接；`ConnectionManager` 既有断线重连（2 s）与 `Connected` 事件重绑定不变。

### R10 残留修复（已存路径中的反斜杠）

`RepositoryPath.StoredPath(path)`（RepositoryPath.cs:12）：远端路径仅 `TrimEnd('/')`，本地 `Replace('\\','/')`。8 个入库点改用：RepositoryNode.cs:25、Repository.cs:415-416/1584、Preferences.cs:560、Launcher.cs:294/447/527。R10 行状态更新为"已修复"（URI 语义由 URI 归一化保证，持久化语义由 StoredPath 保证）。

### R42 slice 1：状态语义化（status → GetStatus）+ R25 消费者

- `StatusSnapshot` 扩展 `HeadOid/Branch/Upstream/Ahead/Behind`（Protocol.cs:61）。
- Core `StatusAsync` 改用 `status --porcelain=v2 --branch -z --untracked-files=all --ignore-submodules=dirty` 并解析 v2。**格式事实（本次核实，文档 git-status 2.53）**：现代 git（≥2.45）普通条目为 `1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>`，rename 多 `<X><score>`，unmerged 以 `u` 开头（4 个 mode + 3 个 oid），untracked 为 `? <path>`；XY 未变位为 `.`（非空格）；`<sub>` 4 字符（非 submodule 为 `N...`）。解析器按首 token OID 特征兼容新旧两代格式（路径含空格安全：路径为行尾剩余全部字符）。
- `QueryRepositoryStatus`/`QueryLocalChanges` 增加远端分支：`RemoteWorkspace.ForPath → OpenTransientAsync → GetStatusAsync` → DTO 映射（`MapChange/MapState`，冲突对映射 `ConflictReason`，untracked 归一为 index=" " work="?"）；本地路径不变。
- **R25 消费者端落地（§7"客户端忽略旧查询结果"）**：`QueryLocalChanges.Generation` 快照代次；`Repository.RefreshWorkingCopyChanges`（:1147 同模式）与 `RepositoryNode.UpdateStatusAsync` 均增加 `generation < _lastGeneration` 丢弃逻辑，与既有 CTS token 丢弃互补（覆盖"慢响应的新查询在更新事件处理之后返回"交错）。
- **本地路径 off-by-one（确认缺陷，已修复）**：`QueryRepositoryStatus` 本地解析按 `行数-3` 计变更数，非 tracking 分支仅 2 行头 → N 个变更显示 N-1（N=1 时显示 0）。修复为按非 `#` 行计数；以真实仓库验证（非 tracking：3 行输出含 1 变更，旧公式得 0、新算法得 1）。
- 新增 Server 套件项 `PASS Semantic status snapshot carries branch, changes and generation`（分支头、worktree/index 变更态、提交后代次严格递增、提交后消失）。

### R42 slice 2：分支语义化（branch → GetRefs）

- `RefInfo` 扩展 `CommitterDate/WorktreePath/Ahead/Behind`，`RefsSnapshot` 扩展 `IsDetachedHead/DetachedOid`（Protocol.cs:62-63）。
- Core `RefsAsync`：`for-each-ref` 增加 `%(committerdate:unix)`/`%(worktreepath)`；`%(upstream:track)` 服务端解析为 Ahead/Behind 计数（GUI 不再需要 `QueryTrackStatus` 二次查询）；detached 检测用 `status --porcelain=v2 --branch -z --untracked-files=no` 的 `# branch.head (detached)` 头 + `rev-parse --short`。**不扩展 GitPolicy 白名单**（`symbolic-ref` 虽有只读形态但同 verb 存在写形态，纳入 Reads 会破坏"写必须带 operation ID"不变量；已核实 `rev-parse --verify -q --symbolic-full-name HEAD` 在 detached 下输出 `HEAD` 且退出 0，不可用）。
- `QueryBranches` 远端分支 → `GetRefsAsync` 映射（含 detached 伪条目、`IsUpstreamGone` 由 ref 存在性判定）；`RefreshBranches` 增加 generation 消费。
- 新增 Server 套件项 `PASS Semantic refs snapshot lists branches and detects detached head`（当前分支标记、新分支出现、detach/attach 两态）。

### R42 slice 3：提交详情语义化（show → GetCommit）

- `CommitInfo` 增 `Decorations` 字段；Core `CommitAsync`/`HistoryAsync` 格式串均加 `%D`（11 字段，ParseCommits 步长 10→11，历史分页同步携带装饰，GUI 主历史经过渡通道不受影响）。
- `QuerySingleCommit` 远端分支 → transient 开句柄 + `GetCommitAsync` → 映射（`User.FindOrAdd(name±email)`、`ParseDecorators`）；同步 `GetResult()` 对远端路径路由 `GetResultAsync().GetAwaiter().GetResult()`（与 Repository.cs:425 既有模式一致；三个同步消费者 App 启动/Compare/Conflict 中 Conflict 在 Task.Run 内，Compare 此前对远端本已失效，本次修复）。
- 新增 Server 套件项 `PASS Semantic commit detail carries fields and decorations`（字段、`HEAD -> main` 装饰、root 无装饰、伪 SHA 报错）。

### R42 slice 4：blame 语义化（blame → GetBlame）

- 协议 `BlameLineInfo(CommitSHA, File, Author, Timestamp, IsFirstInGroup)` + `BlameData(Generation, IsBinary, Lines, Content)`；`QueryRequest` 增 `IgnoreWhitespace`（blame `-w`）。
- Core `BlameAsync`：`git blame -f -t [-w] rev -- file`，服务端正则解析（与 GUI `REG_FORMAT` 同式），含 NUL 二进制判定、相邻同 SHA 分组、边界提交（`^` 短 SHA）统一为最短长度——**与 GUI 解析器逐条对齐**（边界短 SHA 统一是 GUI 既有行为，测试按统一后域比较）。
- `Blame` 命令远端分支 → transient 开句柄 + `GetBlameAsync` 映射；本地路径不变。
- 新增 Server 套件项 `PASS Semantic blame maps lines to commits`（两提交两行归属、分组、内容重建、root revision 单行、二进制文件 IsBinary）。

### deleteTree 只读文件缺陷（本轮发现并修复）

- **现象**：新 blame 套件的收尾 `deleteTree`（远程仓库删除流程）在本机失败：`Access to the path '<对象文件>' is denied`。
- **排查**（独立探针逐步排除）：非 watcher 句柄（无 watcher 对照组同样失败）、非 git 进程残留（无 git 进程时仍失败）、非 .NET 启动继承（经 `cmd /c` 同样失败）；逐文件探测发现**仅 `.git/objects/xx/` 对象文件失败且可以 FileShare.Delete 打开** → 查属性 = **ReadOnly**。本机 git 创建松散对象时带只读属性（环境行为，安全迷你过滤器一类）。`Remove-Item -Force` 会清只读故外部删除成功，.NET `Directory.Delete` 不清 → 抛异常。
- **修复**（RepositoryService.cs）：`delete`/`deleteDirectory`/`deleteTree` 删除前清除只读位（仅 Windows；`deleteTree` 递归清文件+目录）。这是真实产品修复：远端仓库删除对工作树含只读文件的仓库此前必然失败。
- **验证**：修复前该套件项 100% 失败，修复后 `TOTAL 26 passed; 0 failed`。

### R42 剩余项裁决（历史/diff 维持过渡通道）

- **提交历史（QueryCommits→GetHistory）**：`BuildHistoryParams` 的过滤器参数面（order/reflog/first-parent/simplify/分支过滤模式）超出当前 `QueryRequest` 结构化字段；迁移需扩展协议且 GUI 主历史视图是最复杂消费者。裁决：维持 `ExecuteGit` 过渡通道（功能可用，`%D` 装饰已同步进入 `HistoryAsync` 供未来语义消费）。
- **文本 diff（Diff→GetDiff）**：GUI `Diff` 解析器产出富模型（行内高亮前缀/后缀、LFS 标记、mode 变更、no-newline 标记），远超 `DiffLine(Kind, Text, OldLine, NewLine)`；原始 diff 字节流恰是解析器所需，raw 通道下已功能可用。语义化 = 服务端重写 diff 渲染语义 + 协议扩展，收益仅为少一条 raw 命令。裁决：维持过渡通道。

### R19：仓库句柄生命周期（本轮修复）

- **泄漏确认**：`ExecuteGitAsync`（过渡通道全部命令）与各 Query* 远端分支用 `OpenAsync` 开句柄但无订阅跟随 → 永不关闭；Server 端 session（含 inotify watcher）整个连接生命周期泄漏。launcher 含 N 个远端节点时，状态刷新累积 N 个永久 watcher。
- **修复**：利用 Server 既有引用计数（`OpenRepository` RPC 按 worktree 去重共享 watcher 并 `References++`，`CloseRepository` `References--` 归零释放；RepositoryService.cs:39-40,73）。`RemoteWorkspace` 增 `OpenTransientAsync/ReleaseTransientAsync`（每次命令使用自取一个引用、结束必还；release 失败经 `ConnectionError` 上报、不逃逸出 finally）。`ExecuteGitAsync`、`ExecuteCustomAsync`、三个查询命令、`RemoteExternalTools.ReadBlobAsync` 全部改 transient 配对；open-flow 两站点（Launcher/RemoteRepositories）保留持久 `OpenAsync`（订阅跟随）。在飞命令与订阅共享 session：任一引用持有期间 watcher 不释放。
- **验证**：Server 套件新增 `PASS Shared repository handles release the watcher only after the last close`（双开同 session；首个 close 后查询与 watcher 事件仍工作；末个 close 后句柄过期；重开新 session）。独立 smoke（`build/smoke-transient`，引用编译产物 SourceGit.dll，绕开用户 GUI 的文件锁）4/4：command-only open/release、释放后重开、活订阅下命令释放不销毁 session（watcher 事件仍发生）、取消订阅后命令可用。

### slice 1 回归：语义 status 丢失 `--no-optional-locks`（本轮发现并修复）

- **现象**：GUI 适配器套件 `GUI status preserves remote index with --no-optional-locks` 项失败——远端只读 status 后索引字节变化（本地路径 `QueryLocalChanges` 恒带 `--no-optional-locks`，索引不刷新；slice 1 远端分支走 `GetStatus` RPC 后该标志丢失，服务器 `git status` 刷新了索引）。
- **排查**：先尝试在 status 参数尾部追加 `--no-optional-locks`——git 报 `unknown option`：该选项只能出现在 `git` 与子命令之间（本地 `Args` 恰以此形式拼接）。Server raw 通道的既有做法是环境变量 `GIT_OPTIONAL_LOCKS=0`（RepositoryService.cs:93，版本无关、位置无关）——语义 RPC 应复用同一机制。
- **修复**：`QueryRequest` 增 `NoOptionalLocks`（默认 false）；`RepositoryQueries.Execute` 将其传入 `GitRequest`（Server 注入 `GIT_OPTIONAL_LOCKS=0`）；`GetStatusAsync` 增参；`QueryLocalChanges` 保存构造参数并在远端分支透传（`QueryRepositoryStatus` 本地本就不带该标志，保持 false 与本地一致）。
- **验证**：GUI 适配器套件 13/13 通过（含该回归项）；`--server` 套件复验全绿。

### v2 status 解析器对照真实 Git 输出的复审（本轮）

- 对照 git 2.53.0.windows.2 实际输出逐型复核：`0`（未变）、`1`（无 worktree 独立 hash，6 字段，无 original 行）、`2`（**恰为** rename 条目，`<hW>` 槽位为 `R<score>`，后随一条 NUL 终止的 original path 记录）、`u`（4 个 mode 字段 + 3 个 oid + path，**无** `<hW>`；`[gone]` 类冲突 h 字段可为 `-`，`IsOidField("-")` 已按 oid 处理）。解析器各分支与实测一致，未发现错误。
- 新增套件项固化此前未覆盖的组合：`PASS Semantic status preserves multiple changes, renames and untracked files`（普通 worktree 变更 + 未跟踪 + `git mv` rename 三条共存，rename 携带 `OriginalPath`）与 `PASS Semantic status reports tracking upstream and ahead/behind`（`push -u` 后 `Upstream == "origin/main"`、领先 1 提交时 `Ahead == 1 / Behind == 0`，固化 `# branch.upstream`/`# branch.ab` 带符号解析）。

### 传输层畸形帧证据（验收矩阵"非法/超大帧"项）

- 既有防护复核：读侧 4 MiB `MaxFrame`（超限在分配前拒绝）、写侧同限；Server 逐连接 try/catch 隔离（畸形 JSON/断流 → Error 帧 + 关连接，不影响其他连接与 accept 循环）；15 s 握手超时、128 在飞请求上限（超限回 "Too many pending requests"）、helper 6 分钟超时；Client ReadLoop 全异常路由到断线/重连（无未观察异常），chunk 累积 256 MiB 上限，事件队列 4096 背压（超限明确报错要求重连）。
- 新增套件项 `PASS Server rejects malformed frames and keeps serving`：超大帧头（声称 5 MiB）与非 JSON 帧体各触发独立连接的拒绝响应；随后既有会话查询与新 `Probe` 握手均成功——关闭验收矩阵该证据线。
### 残留项裁决（R09/R31）

- **R09**：唯一递归消费者 `WalkFiles`（App.Extensions.cs:93）显式 `RecurseSubdirectories = false` + 手动逐层递归（maxDepth），远端单层 `List` 与其语义等价；`SetLength`/Append 第三轮已实现。裁决：无需修改，记录范围决定。
- **R31**：Server realpath 为唯一规范化键（commonDir 锁、worktree 去重、watcher 身份），第三轮已落地；客户端文本 `..` 消解不改变身份（发送前简化），安全。残留：`OpenAsync` memoize 按原始别名查找，别名路径（符号链接）每次多一轮 open+close RPC 往返（结果正确，仅订阅打开时发生，低频）——记为已知低效，非缺陷。

### 远端 submodule 支持：两个产品缺陷（本轮发现并修复）

- **缺陷 1：ConfigOverrides 白名单拦截 `protocol.file.allow`**。GUI `Submodule.AddAsync` 以 `-c protocol.file.allow=always submodule add ...` 起头；raw 通道（RemoteWorkspace.ExecuteGitAsync:226-230）把前导 `-c` 对剥离进 `GitRequest.ConfigOverrides`，但 Server 白名单（RepositoryService.cs:98）只放行 4 个键 → 每次远端 `submodule add` 抛 `UnauthorizedAccessException: Unsupported command config override`。修复：白名单增 `protocol.file.allow`，值固定为 `protocol.file.allow=always`（沿用既有的严格值校验模式；该键是传输层文件访问许可，不可执行、无注入面）。
- **缺陷 2：`Submodule.UpdateAsync` 缺文件传输标志**。git >= 2.38 安全默认禁止 file 传输（`fatal: transport 'file' not allowed`）：全新克隆上 `submodule update --init` 且 submodule URL 为文件路径时必失败（本地复现确认：原仓库 deinit 后重 update 因复用 `.git/modules` 而不失败）——本地与远端均受影响，Server 端全新部署流程按构造必中。修复：UpdateAsync 参数补齐该标志（Submodule.cs，与 AddAsync 一致）。
- **测试形态**：Server 套件用 raw 通道契约形态（`GitRequest` 直接带 `ConfigOverrides: ["protocol.file.allow=always"]`，verb 干净）；GUI 适配器套件用真实 `SourceGit.Commands.Submodule` 类（覆盖 `-c` 剥离 → ConfigOverrides 全链路）。
- **并发安装竞争（验收矩阵"并发安装竞争"项）**：Server 的 per-common-directory 操作门（ExecuteAsync 对读写 git 操作统一取同一 `SemaphoreSlim(1)`）把同仓库的并发 git 调用串行化。本地验证无门时的竞争形态：同一 worktree 上两个直接 `git submodule update --init` 进程，一方 `fatal: destination path ... already exists`（clone 落点竞争），终态一致但调用方报错。经 Server 时首个 clone、第二个 no-op，两者均 exit 0。
- **验证**：`PASS Remote submodule add and concurrent fresh-clone init work through the server`（内层 bare + seed 推送、外层 `submodule add` 带 override——直接证明白名单修复、`submodule status` 注册、全新克隆 + **两个并发** `submodule update --init` 均成功并落出 `inner/inner.txt`）；`PASS GUI Submodule command adds and initializes remote submodules`。
- **环境事实**：本机 `core.autocrlf=true`（system+global），commit→clone→checkout 往返把 LF 归一为 CRLF（WSL 保持 LF）。测试断言做 CRLF 归一化比较；Server 不覆盖用户 EOL 配置（远端行为与本地一致是目标）。

### 产品启动器端到端验证：SSH 与 WSL（本轮）

- **验证对象**：`ConnectionManager` 的两种产品启动路径。SSH（`Kind="Ssh"`）：SSH 环境探测、包自动部署（本地上传→stdin 管道→`sha256sum -c` 校验→`flock` 下版本目录安装）、`nohup` 脱离启动、`ssh -N -L` 回环隧道、token 握手、实例 ID 核对（计划"SSH 目标需稳定落在同一主机，握手必须核对实例 ID"）；真实 OpenSSH 服务端为 Ubuntu-24.04 WSL 常驻 root `sshd`（端口 22，WSL 2 localhost 转发可达）。WSL（`Kind="Wsl"`）：`wsl.exe --distribution` 探测/安装/启动 + 随机本地端口直连（计划"WSL 通信使用随机本地端口"）。
- **结果**：SSH 全新部署（先删安装）`Environment → Installing → Starting → Opening tunnel → Synchronizing → Connected`，7.7 s，隧道内 git 2.55.0 应答；已安装快路径 2.1 s。WSL 启动器（`Kind="Wsl"`，`wsl.exe --distribution … --exec sh -c` 探测/安装/启动，直连 WSL localhost TCP，无隧道）：`Environment → Starting → Synchronizing → Connected`，2.4 s（已安装快路径），git 2.55.0 应答；全新安装变体由 WSL 验收运行覆盖（同一 `Bootstrap` 代码路径）。`build/smoke-sshdeploy`（引用编译模块，stamp `remote-s2`，`args[2]` 选 kind）承载，setup（临时密钥/临时 config 别名/known_hosts）全部事后还原，系统配置零残留。
- **KEX 不兼容（环境事实）**：Windows OpenSSH 9.5p2（LibreSSL 3.8.2）与 OpenSSH 9.6 服务端协商 `sntrup761x25519-sha512@openssh.com` 后客户端无法执行（`choose_kex: unsupported KEX method`）——不加 per-host `KexAlgorithms` 固定则全部连接失败。计划把 SSH 选项交给系统 OpenSSH 与用户配置（`KexAlgorithms` 属此类），连接器不改；面向用户的排障提示属后续文档项。
- **owner 宽限期裁决（2026-10-03 被用户指令推翻，改为立即接管）**：原裁决：新 GUI 进程 = 新 owner 身份；旧 owner 断连后 300 s 宽限期内（`SOURCEGIT_GRACE_SECONDS` 可调），其他 owner 的握手被拒（`Instance owned by another GUI`）；设计意图是保护旧 GUI 临时断网重连（仍持原 owner）不被新 GUI 抢先，论据为"TCP 层无法区分崩溃与断网，宽限期是唯一安全解"。用户指令推翻该裁决：再次打开 GUI 连接 remote 时先连接已有 server，不要启动新实例，除非用户有意为之。实现（`SourceGit.Server/Program.cs` 握手）：不同 owner 在**无任何活连接**且**无进行中操作**（`accepted`/`running`/`waitingInteraction`）时即被接受，宽限期条件从该判定中移除；同 owner 始终接受（recovery token 快路径不变）。后果与边界：(a) 崩溃后重开立即连上既有实例——原报告场景解决；(b) 第一 GUI 仍连着时第二 owner 继续被拒（无脑裂）；(c) 静默断网（无 RST、server 仍见旧 owner 活连接）时新 GUI 照旧被拒——安全；第一 GUI 其后重连会得到可诊断的 `Instance owned by another GUI`（对应用户"除非有意"语义）；(d) 操作进行中时拒绝信息改为 `Server has active operations; wait for them to finish`（与 owner 冲突区分）。300 s `SOURCEGIT_GRACE_SECONDS` 保留其另外两个角色：空闲监听停止（server 空闲退出）与操作交互等待超时。回归：Server 套件新增项 "A new GUI owner can take over after the previous owner disconnects"（A 断连→B 接管→B 连着时 C 被拒→B 断连→A' 再接管）；"Reject simultaneous second GUI owner" 项保持有效。
### 重连路径与隧道泄漏修复（本轮）

审计 `ConnectionManager` 的重连路径发现并修复四个缺陷（均由新增的 `build/smoke-reconnect` 端到端烟雾验证：连接→杀 Server→观察自动重连→断言无泄漏）：

- **隧道替换泄漏**：`ConnectAsync` 在建立 SSH 隧道前对旧隧道调用 `_tunnel?.Dispose()`，但 `Process.Dispose` 不终止仍在运行的进程——每次 SSH 重连都泄漏一个持 `-L` 转发的 `ssh -N` 进程。修复：替换前 `Kill(true)` 再 `Dispose`，与 `DisposeAsync` 的既有清理逻辑一致。
- **`ConnectAsync` 无并发保护**：`OnDisconnected` 的重连循环与外部显式 `ConnectAsync` 可能并发，各自建立隧道/客户端并竞争 `Client` 交换。修复：`_connectGate`（`SemaphoreSlim(1)`）串行化整个连接体；并在已取得 record 后加快速路径——若当前 `Client` 仍连接且 `InstanceId` 与 record 相同，直接复用现有通道，避免重复建隧道/通道。
- **`PendingOperations` 迁移是死代码**：`ConnectAsync` 交换客户端时把旧客户端的 `PendingOperations` 逐条拷到新客户端，但全仓无任何消费者读取迁移后的字典（`PendingOperations` 只在 `RemoteRepositoryClient` 内部 add/remove），且该迁移发生在旧客户端 `DisposeAsync` 之后。删除该行。
- **`DisposeAsync` 非幂等**：`ConnectionManager`/`RemoteRepositoryClient` 的 `DisposeAsync` 对已 dispose 的 `CancellationTokenSource` 再次 `Cancel()` 会抛 `ObjectDisposedException`（`await using` + 显式 dispose 即触发）。BCL 要求 `IAsyncDisposable` 可安全多次调用。修复：两者各加 `Interlocked.Exchange` 一次性门。

烟雾结果：WSL 与 SSH 两种 kind 均 `PASS`——快速路径复用活动客户端、杀 Server 后自动重连到新实例、重连后 Git 查询可用、SSH 重连后活动 `-L` 隧道恰为 1 个（按目标主机过滤计数，排除环境内无关隧道）。

### 状态更新

- R10：已修复（URI 归一化 + StoredPath 入库点）。
- R19：已修复并测试（transient 引用对 + Server 引用计数；见上节）。
- R09/R31：裁决记录（范围决定/已知低效），见"残留项裁决"节。
- R25：消费者端已落地（status 两条管线 + 分支管线）；其余查询命令随后续 R42 slice。
- R42：slice 1（status）、slice 2（refs）、slice 3（提交详情 GetCommit）、slice 4（blame GetBlame）完成并有专项回归；提交历史与文本 diff 维持 `ExecuteGit` 过渡通道（裁决见"R42 剩余项裁决"节）。slice 1 的 `--no-optional-locks` 回归已由 GUI 适配器套件捕获并修复（见上节）。
- GUI 适配器套件（14 项，覆盖真实 GUI 适配器：文件读写、status/count、pathspec/临时文件、外部 diff/merge 下载上传、远端 submodule 增建+全新克隆初始化、租约释放、generation 携带）：本轮全部改动后复验 **14/14 通过**（`tests/SourceGit.GuiRemote.Tests`，经 `OutputPath` 旁路用户 GUI 的 bin 锁，client 与 server 同 BuildId `remote-s2`）。
- 远端 submodule：两个产品缺陷已修复并测试（ConfigOverrides 白名单 + UpdateAsync 文件传输标志，见"远端 submodule 支持"节）；Server 套件（含并发安装竞争）与 GUI 适配器套件各有专项回归。
- 产品启动器（SSH + WSL）：端到端验证通过（真实 OpenSSH 服务端 + WSL localhost；全部署 + 快路径 + owner 排他观察，见"产品启动器端到端验证：SSH 与 WSL"节）；KEX 不兼容记为环境事实。
- 重连路径：四个缺陷修复并验证（隧道替换泄漏、`ConnectAsync` 并发保护+同实例快速路径、`PendingOperations` 死迁移、`DisposeAsync` 幂等；见"重连路径与隧道泄漏修复"节，`build/smoke-reconnect` WSL+SSH 双 PASS）。
- owner 接管：按用户指令由"300 s 宽限期内拒绝"改为"无活连接且无进行中操作时不同 owner 立即接管"（"owner 宽限期裁决"节）；Server 套件 31/31（含新接管项）+ WSL 端到端验证；错误信息按冲突类型区分（owner 冲突 vs 操作进行中）。
- 交互模拟（目标：client 与 server 分别单独验证 + 完整交互）：`build/interaction-test` 对用户在用的 WSL 实例（`remote-1`）跑四阶段——A：裸协议（无 client 库）Probe/Handshake/Ping/GetEnvironment；B：client 库单独（握手/环境/mkdir+stat/init/commit/GetStatus/GetHistory）；C：完整产品流程（连接已有实例 → git 写 → 外部 kill server → 自动重连到新实例 → 两个 commit 均存活）。17/17 检查通过，14.3 s（`artifacts/interaction-1.log`，可重复）；连接超时修复后复跑 17/17，14.68 s（`artifacts/interaction-2.log`）。
- 连接卡死根因与修复（用户第二次报告，14:32）：`RemoteRepositoryClient.ConnectAsync` 的 TCP 连接**没有单次超时**——SYN 被黑洞时（观察到的触发：WSL 2 对新监听端口的懒转发，GUI 是 fresh 端口后几秒内第一个连接的进程）每次重试空等 ~21 s OS 重传超时，50 次 ≈ 17 分钟"Connecting"无反馈。现每次尝试 5 s 上限（与 `ProbeAsync` 的 3 s 同模式），超时报 `SocketException(10060)` 让 manager 重试循环重建 client，并在对话框诊断里记录 `connect attempt N/50 failed: …`；最坏 ≈ 4.3 min 且有进度可见，转发恢复后一个尝试周期内自愈。14:32 现场取证（TEST_RESULTS.md"Connection hang fix"节）：GUI 14:32 正常启动了 server、无 launcher 进程卡住、server 与端口均健康、同一实例同一流程的 console 复现在秒级通过。
- 连接对话框（用户反馈"有些 button 没什么用"，随后指正：WSL 选择器放下拉、browse 才是有用的，再加手动取消按钮）：最终形态 = `Connect and open` + **`Cancel connection` 按钮**（仅连接进行中可用；每次尝试一个与窗口生命周期链接的 `CancellationTokenSource`，点击即中止整条链路——启动脚本/wsl.exe launcher、TCP 重试循环、握手——日志记 "Connection cancelled."，两按钮恢复可用，一键重试）+ **内联可编辑 WSL 发行版下拉**（`wsl.exe --list --quiet` 填充，仅 Wsl 传输显示；SSH host 为仅 Ssh 显示的文本框）+ **恢复的 Browse remote directories**。`RemoteDirectoryBrowser` 重写为无数据模板（字符串条目 + 并行 `FileEntry` 列表），`FuncDataTemplate` null-item 闪退面整体移除（NRE 修复记录保留为历史）。取消语义经冒烟验证：伪造卡死的握手（记录端口指向静默监听器），取消后 5.1 s 内 OCE 传播、移除障碍后新 CTS 2.7 s 重连成功。`Prepare SSH host / agent` 与其 `CreateLoginTerminal` 保持移除。全解决方案重建 0 警告 0 错误。
- 远端目录浏览器卡死（用户报告：点 Browse remote directories 立刻卡死）：根因是**客户端异步链捕获了调用方的 SynchronizationContext**——GUI 在 UI 线程发起连接，`RemoteRepositoryClient` 的读循环与 RPC 调用链（写请求的 `await`、读响应的 `await`）的 continuation 全部被投递到 UI 调度器；浏览窗口打开后 `RemoteWorkspace.List` 用 `GetAwaiter().GetResult()` 同步等待 RPC，UI 线程阻塞，而投递响应/写完请求的 continuation 正等在这个被阻塞的 UI 调度器上 → 永久死锁（console 无 SynchronizationContext 故无法复现）。修复：整个客户端库（`RemoteRepositoryClient`、`ConnectionManager`、`Protocol`）所有 `await` 加 `ConfigureAwait(false)`（库代码不应捕获调用方上下文），读/事件循环另用 `Task.Run` 启动双保险。回归项加入 GUI 适配器套件（"UI-thread sync-wait on RPC does not deadlock protocol loops"：安装队列式 SynchronizationContext，连接时泵送、同步 RPC 时不泵送——修复前挂死，修复后通过）。此修复同时解除 `RepositoryFile`/`GitProcess` 等所有同步适配路径的同类死锁面。
- 僵尸 owner 通道钉死实例（用户报告：连上 WSL 仓库后圆环进度一直转、"不停的重试"）：现场取证——GUI 进程在跑但到实例端口**零 TCP 连接**；WSL 侧 server 有**两条 ESTABLISHED** 幽灵 socket（16:14:46/16:16:01 两次崩溃的残留，WSL 2 NAT 未把 FIN/RST 送到 Linux 侧）。Server 的读循环只在 EOF/错误时结束，静默但 ESTABLISHED 的通道永不结束 → `_connection`/`_owner` 永久钉死 → 新 owner 握手被拒 **"Instance owned by another GUI"**（`build/owner-probe` 对活实例完整握手复现）→ `ConnectionManager.OnDisconnected` 每 2 s 一轮"probe 成功→握手被拒"无限循环 = 用户看到的重试；钉死的 owner 同时阻塞空闲退出，旧实例永不消失。修复（Server）：对 60 s 内无任何入站消息的通道执行 `Client.Close()`（读循环故障退出→finally 释放所有权；活客户端每 10 s 一次 Ping，6× 裕量；`SOURCEGIT_CHANNEL_GRACE_SECONDS` 可覆盖，模式同 `SOURCEGIT_GRACE_SECONDS`）；timer 回调加 try/catch（timer 线程未处理异常会直接杀进程——曾观测到套件中途 server 自杀一例，加守卫后复跑 32/32 干净）。回归项："A new owner can take over after a silent owner channel expires"（专用 server 3 s 宽限：裸 socket 握手后静默的通道 → 活 owner 先被拒 → 宽限后被接管且查询可用），Server 套件 **32/32**（`artifacts/r8-server32.log`）。
- 同 build id 换包内容无法落地（部署缺陷）：安装快路径只查 `test -x`，`versions/<build>/` 里的旧二进制永远复用，修复无法到达已部署实例。现安装时写 `.package-sha256` 标记，快路径要求"二进制存在且标记 == 当前包摘要"（缺标记 = 旧部署，自动重部署）；重部署时替换二进制并**先重启**正在运行的旧实例。`remote-1` 包已带修复重发布（linux-x64/arm64）。
- 远端目录选择器不可滚动（用户报告：84 个条目无滚轮/滚动条）：`StackPanel` 给 `ListBox` 无限高 → 全量渲染溢出 560×440 窗口，内置 ScrollViewer 内容不超视口永不生效。改 `Grid`（`Auto,*,Auto`：标题/列表/按钮行），ListBox 高度受限后滚轮与滚动条恢复；按钮行为右对齐水平面板（Choose + Cancel）。编译 0 错误，视觉验收待用户 F5。
- 刷新反馈环（用户报告：连接已修复但仍一直转圈，目录 `gitRepo/test`）：server 侧全量请求/事件追踪（`SOURCEGIT_DEBUG_LOG=1`，env 门控）钉死根因——**742 个 `RepositoryChanged` 与 742 个 `OperationCompleted` 严格 1:1**、33 次运行中取消的 `GetRefs`、约 2.8 万次临时句柄 Open/Close、15 s 文件系统观察窗口内**零实际变更**：每个事件都由**操作完成后的 Notify** 触发（`RepositoryService.ExecuteAsync` 对非只读操作发 `RepositoryChanged`），不是 watcher。GUI 的分支查询（`git branch -l --all …`、`git branch --show-current`）与标签查询（`git tag -l …`）不在 `RemoteWorkspace.IsReadOnly` 的只读动词表里 → 每次刷新把查询注册成 server 操作 → 每次操作完成发一个事件 → GUI 100 ms watcher tick 再排一轮 branches/commits/tags → 自我维持，峰值 ~80 事件/s；每仓库 git 门把 ~400 ms 查询串行成积压。被取代的在途刷新在取消早退路径不重置 `Histories.IsLoading` → 历史页圆环常亮 = 用户看到的"一直转圈"。修复：`IsReadOnly` 补 `branch`/`tag` 只读列表形式 + `ls-remote`（只读查询不再产生操作 id 与操作后事件；真实写操作仍精确 Notify 一次）；`RefreshCommits` 对非取消异常兜底清 `IsLoading`（被取代的刷新把标志移交给接替者，语义不变）；回归测试中又挖出第二个较隐蔽的驱动源——Windows 宿主上 `.git` 在被观察的工作树内部，而 `QueryBranches` 实际走 `GetRefs` RPC，其内部 `status --branch` 不带 `GIT_OPTIONAL_LOCKS=0` 会刷新索引 → 改写 `.git/index` → watcher 再发事件（工作树频繁变动时每个分支查询都自我触发一次）——`RepositoryQueries.RefsAsync` 现对该内部 status 强制 no-optional-locks（refs 快照只需要 detached-HEAD 头，索引刷新毫无用处）。GUI 套件新回归项 R26：branch/tag/current-branch 查询不得推进 server 仓库 generation（generation 探针自身也必须非破坏：裸 `git status` 探针会刷新索引、在自身之下推进 generation，故用 `--no-optional-locks status` + 前序 commit 的 debounce 沉降等待）。验证：server 套件 32/32（`r11-server32.log`）、GUI 适配器套件 16/16（`gui-adapter-r18.log`）、WSL 真实部署（标记重部署替换运行中实例）通过（`wsl-deploy-r9.log`）、交互模拟 17/17（`interaction-r9.log`）；`remote-1` 已重发布（linux-x64 sha256 `3b3b8919…`）。用户侧最终验收（圆环不再转）待 F5 复测；新 server 的 env 门控追踪（WSL `~/.sourcegit-server/server-env-setup` 仍导出 `SOURCEGIT_DEBUG_LOG=1`）应显示 branch/tag 查询 `"readOnly":true` 且空闲期零 `gen-source=operation` 事件。
- 刷新反馈环用户复测通过（"这次好了，不再转圈了"）：新会话 WSL 日志（883 行，重部署后的新实例）——**6 个 `RepositoryChanged` 全部来自 watcher debounce（用户自己的文件操作），`gen-source=operation` 为零**，自我维持环已消除。同一日志发现 4 个 `FAILED` 请求（"Unsupported command config override"）且当时不落 payload（既有日志缺口）：GUI 中携带白名单外 `-c` 覆盖的命令只有外部 `difftool`/`mergetool`——即**外部 diff/merge 工具在远端仓库上目前静默失败**。server 现于 `FAILED` 时记录请求 payload（同 400 字截断），下一轮带追踪会话可正面识别失败调用，再改工具白名单（不猜）。
- WSL 仓库 UNC 显示路径（用户要求：列表显示的是 `sourcegit-remote://…` 不透明 URI，希望显示 `\\wsl.localhost\Ubuntu-24.04/home/lizh/gitRepo/…`；追加要求：WSL 路径默认用 Linux 风格正斜杠，两种分隔符同时可用）：**纯显示层**——`sourcegit-remote://` URI 保持为存储/路由的规范形式（`RepositoryPath` 的不变量注释已更新为"友好形式不得回流到路由/I/O/持久化"）。`RemoteConnections.WslDisplayPath(path, options)` 纯函数：Windows + 已存连接 `Kind=Wsl` + 连接 id 匹配 + 发行版名不含路径分隔符 → `\\wsl.localhost\<发行版>/<宿主路径>`（UNC 头保留反斜杠——UNC 语法要求；Linux 部分用正斜杠——用户指定的 Linux 风格，Windows 在 UNC 头之后两种分隔符均可互换接受）；其余输入 → null → 显示不变。`DisplayPath(path)` 用按文件 mtime 缓存的连接配置读取包装它（列表渲染无 I/O 开销）。新转换器 `PathConverters.RemoteDisplayPath`（本地路径回退到原有 home 相对显示）替换了全部 6 处显示绑定：欢迎页仓库列表、启动器页签 tooltip、命令面板条目与 tooltip、删除仓库对话框、工作树路径信息行。GUI 套件新回归项 R27 固定该格式（UNC 头反斜杠 + Linux 部分正斜杠，空格/unicode）与 null 情形（SSH/未知连接/本地路径）。验证：GUI 适配器套件 **17/17**（反斜杠形 `gui-adapter-r19.log`、最终正斜杠形 `gui-adapter-r20.log` 101 s）、server 套件 **32/32**（`r12-server32.log`）、WSL 真实部署通过（`wsl-deploy-r10.log`）、交互模拟 **17/17**（`interaction-r10.log`）；`remote-1` 重发布（linux-x64 sha256 `baa35ca4…`，含 FAILED payload 日志）；GUI 0 错误 0 警告。视觉验收（列表实际渲染）待用户 F5；`SOURCEGIT_DEBUG_LOG=1` 再保留一轮会话（确认空闲日志干净 + 抓到 difftool/mergetool 的失败 payload），之后移除。
- 打开历史的反斜杠地址"打不开"（用户报告：点 + 号出现的仓库历史里，反斜杠地址打不开；要求内部转换机制，遇到不标准地址保守转成支持的形式）：地面真值（用户实点 + 数据核对）——历史/节点存储本来就是规范形式（UNC 全正斜杠、remote URI），存储层无"不标准地址"；用户看到的反斜杠是显示层（上一轮 `WslDisplayPath`）；实点一个反斜杠（remote）条目**能打开**，只是 WSL server 空闲 5 分钟退出后冷启动要数秒到十几秒，期间"Connecting to WSL…"通知不消失（通知本无自动过期机制），形成"打不开"的观感。处理：① 显示统一——`WslDisplayPath` 扩展覆盖本地 `//wsl.localhost/<发行版>/…` UNC 路径（就地解析发行版，无需已存连接），与 remote URI 渲染同一 `\\wsl.localhost\…` 形式，两类 WSL 条目不再外观不同（用户此前正是被这种外观差异引导出"反斜杠=坏"的判断）；② 保守归一化——`Launcher.TryOpenRepositoryFromPath`（对话框/拖放/文件激活的唯一入口）对非 remote 用户输入先归一化为正斜杠规范形再做 I/O，remote URI 原样通过；③ 失效条目反馈——`RepositoryNode.Open` 对路径已不存在的本地条目原来是静默无反应，现给出与页签路径相同的"Repository does NOT exist any more"错误通知；④ Connecting 通知在打开成功/失败后由 `finally` 移除（`Notification.Send` 返回实例，按引用跨页面移除；不做全局自动过期）；⑤ 部署环境 `~/.sourcegit-server/server-env-setup` 增加 `SOURCEGIT_GRACE_SECONDS=1800`（server 代码本就读取），空闲保活 5 分钟 → 30 分钟，历史点击的冷启动大幅减少。另：Windows 快捷方式机制实测（临时 WScript.Shell 探针，已删）——`\\wsl.localhost\…` 与混合形式可保存可解析；**全正斜杠 `//wsl.localhost/…` 被 Windows 本身拒绝**（TargetPath 赋值抛异常、回读为空）——若用户建 .lnk，混合显示形式可直接复制使用。启动参数打开仓库的 WSL 冷启动重试曾实现，用户明确不需要该功能，已撤销。验证：GUI 套件 **17/17**（R27 扩本地 UNC 用例）、`--server` 套件 **32/32**、GUI 编译 0 错 0 警；视觉验收待用户下次启动。
## 第六轮：右键闪退修复与保活确认（2026-10-03）

- 历史条目右键闪退（用户报告：+ 号历史里**有的**条目右键闪退、有的不会）：三份崩溃日志（21:49:43 / 22:09:54 / 22:10:22）同一堆栈——`Welcome.OnTreeNodeContextRequested` → `RepositoryDirectory.Exists(node.Id)` → `RemoteWorkspace.Stat` → `RemoteWorkspace.ForPath` 抛 `IOException("Reconnect to the remote environment…")`，UI 线程未处理 → 进程退出。"有的会有的不会" = 该节点所属连接的工作区在点击时是否还连着。修复 = 把 I/O 边界做非抛异常：`RepositoryDirectory.Exists` / `RepositoryFile.Exists` 对"未连接工作区的 remote 路径"返回 false（`RemoteWorkspace.IsConnected` 守卫；存在性检查是 UI 线程同步调用，抛异常即闪退，"此刻无法验证"读作"不存在"是正确语义；真正打开走 `RepositoryNode.Open` → `RestoreAsync` 的正规重连流程）。配套：欢迎页右键菜单的门控与打开门控对齐——remote 节点**总是**拿全量菜单（原先断连节点落进"不存在"分支只剩"删除"）；点击 Open 即触发重连。GUI 套件新回归项 R28 固定断连路径的 `Exists` 返回 false 不抛异常（目录/文件两形式，即原崩溃调用）。
- 远端仓库 Stage All 闪退（同会话现场发现，崩溃日志 22:22:23）：`WorkingCopy.StageChangesAsync` / `UnstageChangesAsync` 用原生 `new StreamWriter(pathSpecFile)` 写 git pathspec 文件，remote 仓库的临时文件是 `sourcegit-remote://…/tmp/sourcegit-…` URI，.NET 按相对本地路径解析（`C:\gitRepoWin\sourcegit\sourcegit-remote:\…`）→ `IOException` → UI 线程闪退。全仓审计 `GetTempFileName` 的 12 处调用点，仅这两处用了原生文件 API，其余（`StashChanges`/`UseTheirs`/`UseMine`/`Discard`/`Commit`/`Tag`/`Apply`/`RemoteExternalTools`）均已用远程感知的 `RepositoryFile` API。改为 `WriteAllLinesAsync`（与相邻已验证模式一致；本地路径分支 `File.AppendAllLinesAsync` 行尾行为与原 `WriteLineAsync` 相同，本地语义不变）。命令层 `ExecuteGitAsync` 本就把 `--pathspec-from-file=sourcegit-remote://…` 参数转成 server 宿主路径（`key=value` 形式同覆盖），机制本身已被 GUI 套件现有项（:121-129 远程临时文件 + Add + commit）回归覆盖。
- 保活确认（用户要求"client 端要有 heartbeat 保活"）：审计 + 日志取证——**心跳已存在且工作**：客户端每 10 s 一次 `Ping`（`RemoteRepositoryClient` heartbeat timer）；server 空闲退出要求**零通道**（`_connection == null` + 宽限期），日志显示关闭最后一个 remote 页签后 Ping 持续、server 不退出；通道意外断开时客户端自动重连（5 分钟窗口，2 s 周期）；owner 通道关闭时所有权移交存活通道。真正缺口只剩"GUI 关闭/崩溃后"——此时无 client 可保活，server 在宽限期满退出。处置：部署环境 `SOURCEGIT_GRACE_SECONDS` 30 min → **120 min**（`~/.sourcegit-server/server-env-setup`，server 启动时读取，无代码改动；server 进程很轻，用户 WSL 24/7 无成本）。不新增任何"heartbeat"代码——现有机制已满足会话内保活，再造一份是纯冗余。
- WSL UNC 仓库"点不开、页签全白"（用户报告：历史里有些 wsl repo 点不开，点完 tab 里是空白；现场锁定条目 = `\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\ddbot-benchmark`）：根因是 **git 的 `dubious ownership` 安全检查**——Windows 侧 git 经 WSLg 9P 共享访问 WSL 文件时，文件属主被映射成外部 SID，与当前 Windows 用户不符 → git 直接 `fatal: detected dubious ownership` 拒绝一切操作（18 个 UNC 仓库实测 16 个被拒，仅 ComfyUI/test 通过——属主映射逐仓库不同，正对应"有些能开有些不能"）。修复：`RepositoryPath.IsWslUnc` + `SafeOwnershipArg`——**仅**对 WSL UNC 路径的本地 git 启动注入 `-c safe.directory=*`（普通本地路径保留检查——它防的是运行非属主仓库的 hooks，不能无差别关闭；remote URI 在 server 侧以正确属主运行，不加）。覆盖全部 5 个本地 git 启动点（`Command.CreateGitStartInfo` 主路径 + `QueryFileContent`×2 / `SaveChangesAsPatch` / `SaveRevisionFile` / `UpdateIndexInfo`）。GUI 套件新回归项 R29 固定该行为。用户复测通过（ddbot-benchmark 与其他 ddbot-* UNC 条目正常打开）。
- 验证：GUI 编译 0 错误 0 警告；GUI 适配器套件 **19/19**（新 R28 + R29）；server 套件 **32/32**；现场复测**全部通过**（用户确认：右键断连节点不闪退 + 全量菜单、remote 仓库 Stage All 正常、关页签后 server 存活——日志见持续 Ping、ddbot-benchmark 等 UNC 条目正常打开）。追踪会话收尾：`SOURCEGIT_DEBUG_LOG` 已从 launcher env 移除（追踪窗口 2755 行，空闲干净：13 个 `RepositoryChanged` 中 11 debounce / 2 operation，无反馈环）；`SOURCEGIT_GRACE_SECONDS=7200` 保留（保活交付的一部分）。日志捕获的唯一 `FAILED` 类 = 只读提交签名展示查询（`git show --format=%G?%n%GS%n%GK` + `-c gpg.ssh.allowedSignersFile=/dev/null`）不在 server 白名单 → remote 仓库签名徽章静默缺失（降级不报错），列为候选白名单项待用户决策；上轮遗留的 difftool/mergetool 白名单问题**已作废**——现行代码 remote 外部工具走 `RemoteExternalTools`（文件上传/下载）或无 `-c` 的 `difftool -g`/`mergetool -g`，不会触碰白名单。

## 第七轮：历史记录去重（2026-10-03）

- 用户报告：历史里有重复记录，因为添加时没检查是否重复。现场数据审计发现**两类**重复：① `remote-connections.json` 里 **9 条完全相同**的 Ubuntu-24.04 WSL 连接（仅 Id 不同）——Remote 对话框每次"Connect and open"都生成新 GUID（`_newId`）且只与当前选中的 profile 比对，`Save` 又只按 Id 去重，新 Id 永远不命中；② 欢迎页树里同一物理 WSL 仓库经两条路线各存一条节点（`ComfyUI`、`test` 同时有 `//wsl.localhost/…` UNC 节点和 `sourcegit-remote://<connId>/…` 节点）——节点身份是存储路径字符串，两条路线对同一仓库产生两个不同字符串，`FindNode` 只做精确字符串比较。
- 修复（添加时去重，所有添加路径收敛到两个接缝）：① 连接身份 `RemoteConnections.SameIdentity(saved, kind, target, user)` = 同传输 + 同目标（WSL distro 名 / SSH host alias 均大小写不敏感解析，`OrdinalIgnoreCase`）+ 同用户（用户名大小写敏感，null == ""）。`Save` 遇到身份孪生时**复用已存 Id**（更新而非追加）；`RemoteRepositories.GetOptions` 改为对**全部**已存 profile 匹配（原先只比对选中项），使 manager/workspace 挂到既有连接上。② 节点孪生 `RemoteConnections.WslTwinId(path, options)`（纯函数）在两条 WSL 路线的存储形式之间互映（remote URI ↔ `//wsl.localhost/<distro>/<path>`，distro 大小写不敏感；非 WSL / 未知连接 / 裸 distro / 本地路径返回 null）。`Preferences.FindNode` 在精确匹配落空后回退孪生查找——Clone / Open-local / Remote 对话框（`FindOrAddNodeByRepositoryPath`）与 Launcher 两处 `FindNode ?? new` 全部受益：第二条路线打开时解析到既有节点而非新建。**既有节点的路线胜出**（不迁移已开页签与 workspace 条目，无行为变化）。
- 一次性数据清理（GUI 关闭下执行；备份于 `AppData/Roaming/SourceGit/backup-dedup-20261003/`）：连接 9→1（首条为规范 Id）；引用被删连接 Id 的 4 处节点 Id 重写到规范 Id（thunderbird_bridge / snake_demo 保留，ComfyUI / test 的 remote 路线副本删除、保留 active workspace 在用的 UNC 条目）；校验 0 残留引用、0 重名、workspace 完好。
- 验证：GUI 编译 0 错误 0 警告；GUI 适配器套件 **21/21**（新 R30：身份矩阵——大小写/空用户/用户/传输/目标反例 + 孪生矩阵——remote→UNC、UNC→remote、distro 大小写、SSH/未知/裸/本地/非 WSL UNC 的 null；`artifacts/gui-suite-r33.log`）。现场复测**全部通过**（用户确认）：欢迎页 ComfyUI / test 各一条；Remote 对话框 profile 列表仅一个 Ubuntu-24.04、重复添加不产生新条目；snake_demo / thunderbird_bridge 在 Id 重写后正常打开。无新崩溃日志。
- 已知边界（不在本轮范围）：Windows 本地路径的大小写变体（`C:\x` vs `c:\X`）仍会建两条节点——`FindNodeRecursive` 是 Ordinal 比较，跨平台改法需按路径类型区分大小写规则（WSL UNC 的 Linux 段**必须**大小写敏感，不能整串忽略大小写），留待需要时单独设计。

## 第八轮：WSL UNC 仓库不自动刷新——文件系统监视失效，降级轮询（2026-10-03）

- 用户报告：从 remote git repo fetch 回新 commits 后，GUI 不自动刷新、看不到取回的新 commits。排查分两步。**第一步证明 remote-URI 路线的事件链完好**（一次性探针：独立 server + bare/clone fixture，走与 GUI 完全相同的命令路径 `fetch --progress --verbose --tags <remote>`）：server 端 fetch 完成即发 `RepositoryChanged`（operation 通知 + 文件监视 debounce 各一次），client 收到，`RemoteWorkspace.Changed` 正确路由到 `Subscribe` 回调（GUI Watcher 注册的正是这个回调）——路由回调计数 0→2（server 侧 fetch）→4（外部 fetch）。**第二步定位真正的断点在另一条路线**：用户 preference 28 个仓库节点里 21 个是 `//wsl.localhost/Ubuntu-24.04/…` **UNC 节点**——而且恰是这批仓库才有 upstream remote（`sourcegit-remote://` 节点 test/thunderbird_bridge/snake_demo 均无 remote，根本无法 fetch；ComfyUI/GPT-SoVITS/ddbot-* 等有 remote 的全部是 UNC）。GUI 把 UNC 路径当本地仓库，`Watcher` 在 UNC 路径上建 Windows `FileSystemWatcher`——实测（监视 UNC 目录 + WSL 侧改文件）**零事件，`Error` = `Win32Exception (1) Incorrect function`**：WSLg 共享根本不支持 `ReadDirectoryChangesW`。而 `Watcher` 构造函数对启动失败**静默吞掉**（`EnableRaisingEvents` 的裸 catch、无 `Error` 处理器）→ 每个 UNC 页签**没有任何刷新触发源**：fetch 后、文件改动后、终端提交后都不刷新。这就是用户报告的症状（他在 GUI 里能 fetch 的仓库恰好全是 UNC）。
- 修复（仅 GUI 侧 `src/Models/Watcher.cs`；**无 server 改动、无需重发 remote-1、无需重启 WSL server**）：文件监视不可用时降级为周期轮询，不再静默永不刷新。三个失效接缝统一置 `_degraded`：① FSW 创建块包进 try/catch（路径连创建都不行——目录不存在/.NET 10 的 `FileSystemWatcher.Path` 对不存在目录直接抛 `ArgumentException`——原来会逃出构造函数）；② 每个 `FileSystemWatcher` 挂 `Error` 处理器（UNC 即此路径：共享在监视启动后拒绝）；③ 原 `EnableRaisingEvents` catch 同时置降级。`Tick`（100 ms 定时器不变）在 `_degraded` 且到期时重置轮询截止期（`PollIntervalSeconds` 默认 **30 s**，首期限惰性计算以支持构造后设置间隔）并用 CAS `MaxFlag` 把五个刷新标志抬到 `now - 1`（绝不压掉更新的标志），由下方既有标志处理路径执行全量刷新（分支/工作树/工作副本/子模块/stash/标签/历史）。**本地磁盘仓库完全不受影响**：监视正常、`_degraded` 恒 0、刷新仍是事件驱动即时进行。成本：只有"OS 拒绝监视"的打开页签才轮询，每 30 s 一轮有界刷新（每页签 ≈6–8 个只读 git 查询走 UNC）；fetch 弹窗的 `LockWatcher` 窗口照旧抑制 tick（截止期错过后下一个未锁定 tick 即刷新）。
- 验证：GUI 编译 0 错误 0 警告；GUI 适配器套件 **22/22**（新 R31：对不存在目录建 `Watcher`——构造期抛异常这条失效模式——1 s 轮询间隔，真实 `Tick` 路径必须持续执行全量刷新；`artifacts/gui-suite-r36.log`）。用户**实际 UNC 路径**端到端（`Watcher` + 桩仓库挂 `\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\test`，2 s 间隔）：分支/历史/工作副本/标签/stash 全部按轮询节奏刷新——PASS。现场验收：用户在 WSL UNC 仓库（如 ComfyUI，有 origin）fetch 后，约 30 s 内分支列表/历史自动更新、无需手动刷新。
- 已知边界（不在本轮范围）：① UNC 仓库的刷新粒度是 30 s 轮询而非实时事件——要事件级实时需把这些仓库改走 `sourcegit-remote://` 路线（server 侧 inotify 监视，机制已验证可用），属于路线迁移的产品决策（server 不在线时该路线不可打开），留待需要时设计；② 轮询期间每个打开的 UNC 页签每 30 s 约 6–8 个只读查询，大仓库（ComfyUI）单次查询经 9P 较慢，属可接受开销，若用户反馈卡顿可上调 `PollIntervalSeconds`。

## 第九轮：WSL 仓库全量迁移到 server 路线——事件级刷新（2026-10-04）

- 用户裁决：不接受第八轮的 30 s 轮询折中——所有 WSL 仓库都应走 `sourcegit-remote://` 路线（server 侧 inotify 监视），事件级刷新、不要轮询。这同时解决了第八轮记录的两个已知边界（30 s 粒度；大仓库经 9P 的查询开销——git 现在走 WSL 本地文件系统而不是 9P 共享）。
- 安全性依据：`Launcher.OpenRemoteRepositoryAsync` 对 remote-URI 页签**已有自动连接**（3 次尝试、"Connecting to …" 通知、`RestoreAsync` → server 不在线时完整部署+启动 WSL server）；节点身份即存储路径字符串，所有下游消费者（git 命令、文件操作、Watcher）都按 `RepositoryPath.IsRemote` 分派——把 remote-URI 形式存为节点 `Id`，整个技术栈零逐点改动即切换路线。
- 实现（仅 GUI 侧；**无 server 改动、无需重发 remote-1、无需重启 WSL server**）：
  - `RemoteConnections.PreferredRoute(path, options)`：WSL UNC 路径在有对应 distro 的已存连接时解析为 remote-URI 孪生；remote/非 WSL/本地路径原样返回。
  - 添加时归一化收敛在两个节点身份接缝：`Preferences.FindOrAddNodeByRepositoryPath`（对话框/克隆/拖拽/启动参数全走这里）与 `Launcher.OpenRepositoryInTab(string, …)`——新添加的 WSL 仓库直接以 server 路线出生。
  - 加载时一次性迁移（`Preferences.MigrateWslUncNodes` + 纯函数规划器 `PlanWslRouteMigration`）：每个有对应连接的 WSL UNC 节点改名为 remote-URI 孪生（若孪生节点已存在则保留孪生、丢弃 UNC 副本并合并子节点）；workspace `Repositories` 引用同步改写；首次写盘前一次性备份 `preference.json.bak-wslroute`。幂等：无 UNC 节点 → 无操作、无写盘。
  - 迁移在 `SetCanModify()` 里重试——实例 getter 运行在应用解除只读模式**之前**，`Save()` 在只读期被静默拦截（第一轮实现里备份已生成但写盘被吞，即此原因）；只读守卫使 getter 侧调用成为无操作而不是丢失的保存。
  - 第八轮 `Watcher` 轮询兜底**保留**：它是任意不可监视本地路径（网络共享、OneDrive、已删除目录）的通用安全网；迁移后不再有 WSL 仓库触发它。
- 验证：GUI 编译 0 错误 0 警告；GUI 适配器套件 **23/23**（新 R32：`PreferredRoute` 矩阵——UNC→孪生、distro 大小写、无匹配连接、非 WSL UNC、本地、已 remote——+ `PlanWslRouteMigration` 矩阵含嵌套子节点与无变更情形；`artifacts/gui-suite-r38.log`）。用户真实 profile 数据迁移（28 节点：19 WSL UNC + 2 remote + 7 本地）：新构建首次启动即生成备份并把 19 个 UNC 节点 `Id` 与 workspace 引用全部改写为 `sourcegit-remote://<connectionId>/…`，节点数与名称不变、无重复节点。
- 已知边界（不在本轮范围）：① 迁移后打开 WSL 页签依赖 WSL server 在线/可自动部署——server 部署失败时该页签不可用（与既有 remote 节点行为一致，有明确错误提示）；② `safe.directory` UNC 注入（R29）与 `FindNode` 孪生回退保留，作为残留 UNC 使用的防御层。

## 第十轮：remote 提交签名查询——白名单放行 signers 覆盖（2026-10-04）

- 背景：第七轮审计遗留项——remote 仓库的签名徽章不显示。链路：GUI 的 `QueryCommitSignInfo` 发送 `-c gpg.ssh.allowedSignersFile=/dev/null`（不显式指定 signers 文件时 git 会对 SSH 签名尝试**网络取公钥**，GUI 用空文件钉死本地验证语义）；remote 路线下客户端把行首 `-c` 对提升为结构化 `ConfigOverrides` 字段，server 端白名单（`RepositoryService.ExecuteAsync`）没有这个 key → `Unsupported command config override` → 查询失败 → 徽章**静默缺失**。
- 修复（server 端 `modules/SourceGit.Core/RepositoryService.cs`）：白名单加入 `gpg.ssh.allowedSignersFile`，**值钉死**为 `gpg.ssh.allowedSignersFile=/dev/null`（与 `protocol.file.allow=always` 同一钉值模式）。该 key 是只读验证行为、无命令执行面；钉值保持白名单封闭——只放行 GUI 实际发送的那一个字符串，不开放任意 signers 路径。
- 部署：包以**同一 build id `remote-1`** 重发（新 sha256 `c0b56ea9…`）；内容变更 marker（`versions/remote-1/.package-sha256`）在下次产品连接时自动触发原地 redeploy（旧实例 `5e7ccb73…` → 新实例 `366416ca…`）——即"同 id 换内容"的既定升级机制，无需手工重启。
- 验证：Windows server 套件 **33/33**（新条目 `Commit signinfo config override accepted, other values rejected`：钉值放行、异值拒绝、未知 key 拒绝；`artifacts/server-suite-r39.log`）。用户**升级后的真实 WSL server** 上跑 GUI 完全相同的 signinfo 命令（ComfyUI 真实仓库）：exit 0、返回三行状态（未签名 HEAD → `N`，与本地仓库同 commit 行为一致）；异值/未知 key 照旧拒绝。
- 已知边界（不在本轮范围）：签名徽章的**渲染**仍取决于 commit 是否真带签名（未签名 commit 无徽章是正确行为）；有签名的 WSL 仓库 commit 打开详情时徽章即恢复显示（server 现在能跑通验证命令）。

## 第十一轮：SSH 通道对齐 WSL 设计——密码/终端/每连接选项（2026-10-04）

- 背景：goal 要求 SSH 通道与 WSL 路线遵循同一设计模式（VSCode 式）。用户真实目标 `bgo-1962`（Linux，用户 `lizh`，**密码认证**）实测连不上，暴露三个产品级缺口：① 对话框 "Remote user" 字段对 SSH 是**死字段**（两条 ssh 命令都不发 `-l`；Windows OpenSSH 缺省时回退 `USERDOMAIN\USERNAME` = `norce\lizh`，用户正是踩中：只能把 user 塞进 host 框）；② 认证硬编码 `BatchMode=yes`，纯密钥/agent，无任何密码路径；③ 无每连接 OpenSSH 选项挂点（KEX pin 这类 per-host 配置无处安放）。
- 方案与决策：
  - **User 字段接 `-l`**（放在 `--` 之前，launcher 与 tunnel 都发）；`user@host` 写法仍兼容（`-l` 优先，OpenSSH 标准语义）。
  - **`SshOptions` 自由文本**（空格分 token，两条命令都追加）：per-host 配置的产品内挂点；命令行 `-o` 优先级高于 `~/.ssh/config`，可覆盖而不破坏系统 config。
  - **存储密码（静默）= 主密码模式**：对话框密码经 Windows DPAPI（`CurrentUser`）加密后存入连接 JSON（`SshPassword` 密文，明文不落盘）。连接时解密、写 `%TEMP%` 每连接 askpass 对（密码侧文件 + `type` 它的单行 .bat——**不能**把密码直接拼进 .bat：`& % ^` 等 shell 元字符会截断合法密码），按进程注入 `SSH_ASKPASS` + `SSH_ASKPASS_REQUIRE=force`（不进全局环境）。一条连接的**所有** ssh 进程（探测/安装/启动/隧道/重连）静默完成认证——一次对话框输入覆盖全生命周期；`NumberOfPasswordPrompts=1` 限制错密码每进程只提示一次（fail2ban 友好）。存储密码模式同时加 `StrictHostKeyChecking=accept-new`：无终端时 OpenSSH 的首次 host-key 确认会挂死，首 key 自动接受（后续变更仍硬失败）；非 Windows 上对话框拒绝持久化密码、降级为终端模式（明文密码绝不上盘）。
  - **终端登录（回退）**：`InteractiveSsh` 且无存储密码 → 去 `BatchMode` + 可见 console（`CreateNoWindow=false`），OpenSSH 原生 UI 呈现密码提示与首次 host-key 确认（与"SSH 选项委托系统 OpenSSH"的既定设计一致）。无 multiplexing 时每个 ssh 进程独立认证，一次连接可能提示 2–3 次——UI 文案如实标注。
  - **ControlMaster 方案被实测否决**：初版设计用 per-connection control master 让隧道/重连复用首次认证（VSCode 对密码主机的官方推荐）。实测 Windows OpenSSH（Win32-OpenSSH）**不实现连接 multiplexing**：任何 `ControlPath` 运行时必报 `getsockname failed: Not a socket`（本机 9.5 复现，盘符与 `\\.\pipe\` 两种路径均失败；对应 vscode-remote-release#4253、Win32-OpenSSH#405）。若保留，**所有** SSH 连接（含密钥认证）都会崩溃——已整体移除；askpass 设计使 multiplexing 变为不必要（每进程静默再认证）。
  - 失败提示可操作化：key 模式失败且 stderr 含 `Permission denied`/`Host key verification failed` 时追加具体补救（设密码 / 终端登录 / 终端里先 `ssh <host>` 信任 host key）。
- 验证：新验收 harness `build/ssh-interactive-smoke`（自包含：WSL 内 root sshd `127.0.0.1:42327` + 专用 `sgtest` 用户与 home——**不触碰** lizh 的生产 WSL server；每场景独立 known-hosts；KEX pin 经 `SshOptions` 行使）：A 密钥回归（`-p/-i/IdentitiesOnly/KexAlgorithms/UserKnownHostsFile` 全走 SshOptions，35 MiB 部署+隧道+git）PASS；B 存储密码（`PubkeyAuthentication=no` sshd，**零 console 窗口**完成部署+隧道+握手）PASS；C 杀 server → 自动重连**静默**再认证 → git 恢复 PASS；D 终端模式（harness askpass 顶替 console 里的人，证明无 `BatchMode` 参数集可认证）PASS。GUI 0 错 0 警；GUI 套件 23/23（`artifacts/gui-suite-r40.log`）。
- 遗留：用户 `bgo-1962` 实连待重测（host `bgo-1962` / user `lizh` / 存密码 → 预期首连自动接受 host key、静默部署、事件级刷新与 WSL 路线一致）。

