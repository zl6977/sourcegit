# SourceGit C/S 架构改造执行计划

日期：2026-10-02  
状态：设计与执行计划，已根据独立审查修订，尚未实施

## 1. 目标与范围

将 SourceGit 改造成 Windows GUI 客户端与远端服务端协作的架构：

- Windows Client 负责界面、用户输入、连接管理和展示缓存。
- Server 运行在 WSL 或 SSH 远程主机上，负责 Git 命令、仓库文件读写、状态计算和文件监控。
- 仓库始终保留在远端，不需要复制或挂载到 Windows；Windows 客户端使用远端仓库时不依赖本机 Git。
- WSL 与 SSH 使用同一套业务协议，只在部署、启动和连接方式上有所区别。
- 保留现有 Avalonia 界面和可复用的 Git 解析逻辑，通过分阶段迁移完成现有功能的远端化。

首批服务端目标为 WSL 2 和通过 SSH 连接的 Linux 主机。先验证 Linux x64，再验证 Linux arm64；其他操作系统、WSL 1、容器嵌套连接和多人协作不纳入首批交付。若继续支持 Windows 本地仓库，也应通过本机 Server 使用同一套业务边界。

## 2. VS Code 调研结论与采用方式

以下区分官方资料确认的行为与本项目的设计选择。借鉴架构模式，自行实现 SourceGit Server 和协议。

| 官方资料确认的 VS Code 行为 | SourceGit 采用方式 |
| --- | --- |
| UI 在本地，需要访问工作区的扩展在工作区所在机器执行。 | 将 Git、仓库文件操作和监控放到 Server；Windows 保留视图与交互。 |
| 客户端自动安装、更新和管理与其版本匹配的 Server。 | 客户端部署匹配构建的 Server，进行协议握手，管理启动、复用、重连和退出。 |
| SSH 通信通过经过认证的 SSH 隧道；WSL 通信使用随机本地端口。 | 将启动通道与业务连接分开；SSH 通过隧道连接回环端口，WSL 通过本地端口连接。 |
| Remote SSH 可通过 SSH 执行安装脚本；远端下载失败时可在本地下载后传输。 | 启动器负责环境检测和部署；提供 Windows 下载并上传的路径，支持远端无法访问下载站点的环境。 |
| 远端资源有 remote authority；连接解析器负责将其解析为可连接的地址或通信通道。 | 使用稳定的连接 ID 标识目标环境，业务层通过连接抽象访问资源，不接触 SSH/WSL 启动细节。 |
| Remote 扩展及相关组件并非全部开源，VS Code Server 不面向其他客户端使用。 | 不依赖 VS Code Server 的二进制或私有协议。 |

参考资料：

1. [Remote Development FAQ](https://code.visualstudio.com/docs/remote/faq)：传输方式、生命周期、用户权限及组件使用范围。
2. [Supporting Remote Development](https://code.visualstudio.com/api/advanced-topics/remote-extensions)：UI/Workspace 职责、客户端与 Server 版本匹配。
3. [Remote Development using SSH](https://code.visualstudio.com/docs/remote/ssh)：SSH 连接、自动安装和本地下载回退。
4. [Developing in WSL](https://code.visualstudio.com/docs/remote/wsl)：WSL 服务端、随机本地 TCP 端口及环境设置。
5. [Remote SSH Troubleshooting](https://github.com/microsoft/vscode-remote-release/wiki/Remote-SSH-troubleshooting)：安装脚本、连接模式和启动日志。
6. [Remote authority resolver 接口源码](https://github.com/microsoft/vscode/blob/main/src/vscode-dts/vscode.proposed.resolvers.d.ts)：远端标识、地址解析、连接凭据及受管理的通信通道。该接口属于 VS Code proposed API，仅作为架构参考。

## 3. 当前代码中的改造入口

项目目前是 .NET 10 / Avalonia 的单个 GUI 项目。初步检查发现约 89 个 Commands 源文件和 136 个 ViewModels 源文件，实际工作量还包括 Views、Models 和 Native 中的直接系统调用。

| 入口 | 当前职责与迁移方向 |
| --- | --- |
| `src/Commands/Command.cs` | 在 GUI 进程启动 Git，依赖 Native、通知、日志、编辑器和 askpass。将进程执行与解析迁入服务端，通过接口替代 UI 依赖。 |
| `src/ViewModels/Repository.cs` | 混合仓库初始化、Git 查询、文件读写、监控、自动 fetch 和 UI 状态。拆出服务端仓库会话，ViewModel 仅订阅和展示数据。 |
| `src/Models/Watcher.cs` | 直接通过 IRepository 回调 UI 刷新。改为服务端发布仓库变化事件。 |
| `src/ViewModels/Launcher.cs` | 使用本机路径验证和打开仓库。改为连接选择、远端目录浏览及 OpenRepository 请求。 |
| `src/Models/RepositorySettings.cs` | 直接读写 Git common directory 中的设置，数据类型依赖 Avalonia。拆分纯 DTO 与客户端展示模型。 |
| `src/App.axaml.cs`、`src/Models/IpcChannel.cs` | 包含本地启动和编辑器交互入口。区分 Windows 应用 IPC 与远端 Git helper 回调。 |
| 其他 ViewModels、Views、Models、Native | 包含补丁、冲突文件、历史文件、外部工具、文件选择器和自定义命令操作，逐项按执行位置迁移。 |

迁移清单需覆盖每一个直接执行 Git、访问仓库文件或依赖仓库路径的入口，不能仅替换 Command 的进程启动方式。

## 4. 目标结构与依赖边界

```text
Windows Client（现有 Avalonia UI）
  ├─ Views / ViewModels / UI 设置
  ├─ Connection Manager
  │    ├─ WslConnector
  │    └─ SshConnector
  └─ RemoteClient / Repository API
             │
             │ 同一套请求、响应、事件与内容传输协议
             │ WSL：本地 TCP；SSH：SSH 隧道中的 TCP
             ▼
SourceGit Server（WSL / Linux）
  ├─ 连接认证、会话与操作管理
  ├─ RepositoryService / Git 执行与解析
  ├─ RepositoryWatcher / 状态快照
  ├─ 文件与内容服务
  └─ Git editor / askpass helper
```

建议新增项目，初期保留现有 GUI 项目路径以控制迁移范围：

| 项目 | 职责 |
| --- | --- |
| `src/SourceGit.csproj` | Windows GUI，引用 RemoteClient 与 Contracts。 |
| `modules/SourceGit.Contracts/` | 请求、响应、事件、错误与纯数据 DTO；不引用 Avalonia。 |
| `modules/SourceGit.Core/` | Git 执行、解析、仓库状态、文件操作；不引用 GUI。 |
| `modules/SourceGit.Server/` | 无界面的服务入口、协议处理、会话和生命周期；引用 Core 与 Contracts。 |
| `modules/SourceGit.RemoteClient/` | 协议客户端、连接器、部署器、重连；不承担 UI 渲染。 |
| `tests/` | 协议、仓库行为、连接生命周期及端到端测试。 |

新项目放在 `src/` 外，避免现有 `src/SourceGit.csproj` 的 SDK 默认递归源码包含将子项目源码或生成文件编入 GUI。阶段 1 同时更新 solution 和项目引用，验证每个项目的编译边界。参见 [.NET SDK 默认包含规则](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/overview)。

客户端可以进行图形布局、筛选展示和缓存，但服务端是仓库实际状态的权威来源。仓库命令和文件读取不得通过 Windows 路径 API 执行。服务端也不能直接引用 ViewModel、Dispatcher、弹窗或剪贴板。

## 5. 连接、部署与生命周期

### 5.1 连接配置

每个连接保存稳定 ID、显示名称、类型，以及 WSL 发行版/用户或 SSH Host 别名。SSH 交由系统 OpenSSH 解析用户配置，包括密钥、端口和跳板设置，保留主机密钥验证。连接配置不保存明文密码。

首版支持密钥/agent 认证，并提供按需显示的登录终端完成密码、MFA 和主机指纹确认；连接器不得将这些提示混入业务协议。SSH 目标需稳定落在同一主机，启动与隧道连接可能落到不同节点的集群入口不在首版支持范围内，握手必须核对实例 ID。相关 VS Code 行为与限制见 [SSH 排障文档](https://code.visualstudio.com/docs/remote/troubleshooting)。

仓库书签使用 `(connectionId, remotePath)`，不能只用路径去重；同一路径在不同主机上代表不同仓库。服务端负责规范化远端路径并返回仓库句柄、worktree 路径、git directory 和 common directory。

### 5.2 启动阶段

1. 客户端通过 `wsl.exe` 或 `ssh.exe` 检测目标 OS、架构、用户、Git 和 Server 安装情况。
2. 检查与客户端匹配的 Server 构建，缺失时上传或下载发布包，校验摘要后原子安装。
3. Server 按用户、构建版本安装在用户目录中，建议使用 `~/.sourcegit-server/versions/<build-id>/`。不同版本可以并存。
4. 使用安装锁防止重复部署，另用实例启动锁防止重复启动。实例记录包含构建、进程、端口、实例 ID 和受保护的凭据；用户目录与凭据文件分别限制为该用户可访问、可读写，验证存活和握手后才能复用。启动结果通过明确的机器可读记录返回，日志与结果区分，避免 shell 输出污染解析。
5. Server 绑定回环地址上的随机端口，提供受保护的连接凭据。启动器取得端口、进程/实例标识和协议版本。
6. SSH 启动器建立到该回环端口的隧道；WSL 启动器建立本地端口连接。验证 WSL 2 的实际网络配置与 localhost 可达性，失败时返回可诊断错误，不自动开放公网监听。

Server 必须能在启动命令和 SSH 隧道断开后继续运行：启动器脱离终端、重定向标准流至日志，并验证进程不依赖连接的存活。首版发布非 Native AOT 的自包含 Linux Server，减少目标机器安装 .NET runtime 的要求；阶段 1 定义和检查目标系统的原生运行依赖。GUI 的 AOT 配置不自动套用到 Server。连接凭据不得写入普通日志。

WSL 启动明确指定发行版和用户，采用非交互方式，不假设加载了用户的交互式 shell 配置。提供可选的 `~/.sourcegit-server/server-env-setup` 环境设置入口，规定为 Bourne shell 语法，在启动 Server 前执行；设置失败需返回日志。记录实际 Git 路径和必要能力，检查 PATH、代理及 agent socket 的可用性，避免记录秘密值。SSH 同样支持显式环境设置，不能依赖两种启动环境恰好一致。环境变化需要重启 Server，并遵守运行任务的退出规则。此做法借鉴 [VS Code WSL 的环境设置入口](https://code.visualstudio.com/docs/remote/wsl#_advanced-environment-setup-script)。

### 5.3 业务连接与重连

- 连接后进行认证和握手，交换 build ID、协议版本、能力与服务端实例 ID。首版客户端与 Server 配对发布，避免同时维护大量兼容组合。
- 首版同一远端用户和构建仅允许一个活动 GUI 所有者，同一 GUI 可打开多个仓库。所有者使用独立恢复凭据在断线后重新接入；第二个 GUI 明确报告该实例已被占用，不静默接管。所有权过期且没有活动任务后才能释放，启动锁和实例记录负责保护这个过程。完整多客户端共享后续扩展。
- 连接状态明确区分：连接中、安装中、启动中、同步中、已连接、重连中、失败。
- 短暂断线后重建通道、重新握手、恢复订阅并重新拉取快照。Server 实例变化时丢弃旧仓库句柄。
- 客户端在发送写请求前生成并保存 operation ID；Server 以所有者 ID 和 operation ID 登记操作，再启动 Git。同实例下重复 ID 返回已有操作状态，参数不同则拒绝。操作状态包括已接收、执行中、等待交互、成功、失败和结果未知；已完成状态至少保留到重连宽限期结束。客户端不得因断线自动重发 commit、push 等写请求；先查询操作结果，不能确定时显示结果未知并刷新仓库。
- 首版不承诺跨 Server 重启的恰好一次执行。实例重启后丢失的操作记录返回结果未知，由仓库实际状态辅助判断，不能仅因日志或记录缺失便断言操作未执行。增加“操作完成但响应丢失”的故障注入测试。
- Server 在无连接且无运行任务时延迟退出。普通写任务完成后再清理，不把关闭窗口等同于强制杀死 Git。首版将断线重连与交互等待宽限期设为可配置值，默认 5 分钟；等待交互的 helper 超时后以非零状态退出，Server 等待 Git 结束、刷新仓库并记录实际结果，不能无限等待无人响应的弹窗。
- 日志按连接、会话和 operation ID 关联，提供用户可见的连接诊断入口。

## 6. 业务协议和数据模型

候选方案为独立于传输方式的双向 RPC：长度前缀消息帧承载 JSON 请求、响应和事件，文件内容使用受限大小的二进制分块。这是 SourceGit 的设计选择，并非 VS Code 内部协议。阶段 1 用小型原型验证双向调用、取消、二进制分块和实际 Release/AOT 发布，同时评估兼容要求的成熟 RPC 实现；记录结果后固定一种方案，再扩大接口迁移。若自建帧协议，需要明确承担解析、背压、并发调度和错误恢复的维护成本。

核心字段包括 protocolVersion、requestId、sessionId、operationId 和明确的错误码。规定帧大小、超时、取消、背压和并发限制，避免大文件分块阻塞取消、心跳和交互消息；Contracts 使用源生成 JSON 元数据，并在 Windows GUI 的实际 AOT 发布产物中验证序列化。Server 首版自包含发布不要求 AOT；若后续采用 AOT，另行验证 Linux 构建工具链和系统最低版本。参见 [.NET Native AOT 文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。

| 接口类别 | 示例 |
| --- | --- |
| 环境与连接 | Handshake、GetEnvironment、GetCapabilities、Ping |
| 远端目录 | ListDirectory、ValidateRepository、CloneRepository |
| 仓库会话 | OpenRepository、CloseRepository、SubscribeRepository |
| 查询 | GetStatus、GetRefs、GetHistory、GetCommit、GetDiff、GetBlame |
| 内容 | ReadWorkingTreeContent、ReadRevisionContent、UploadPatch、DownloadArchive |
| 写操作 | Stage、Unstage、Commit、Checkout、Fetch、Pull、Push、Merge、Rebase |
| 操作管理 | GetOperation、CancelOperation、OperationProgress、OperationCompleted |
| 反向交互 | RequestCredential、RequestEditorInput、SubmitInteraction |
| 状态事件 | RepositoryChanged、ConnectionClosing |

业务请求使用结构化参数。普通 Git 功能不暴露任意 shell 字符串接口；已有自定义命令功能作为单独能力迁移，明确运行在远端。

历史和目录分页加载，diff 与文件内容按需获取。UI 不下载整个仓库，也不将服务端返回的绝对路径交给 Windows 的 File/Directory API。Linux 大小写、符号链接、换行符及特殊文件名都由远端语义处理。

## 7. 状态更新、并发和正确性

- Watcher 在服务端运行，合并短时间内重复事件，并发布受影响的数据类别：工作区、refs、历史、stash 等。客户端据此按需刷新。
- 每个仓库会话携带状态代次，客户端忽略旧查询结果；需要一致性的数据由服务端组合查询，并在检测到变化时重试或要求刷新。
- watcher 丢事件或溢出时触发完整刷新；保留手动刷新作为恢复手段。
- 写操作由服务端调度。共享同一 git common directory 的 worktree 需要协调共同影响 refs、配置等资源的操作，不能只按 worktree 路径加锁。
- 外部终端或其他 Git 客户端仍可能修改仓库，因此操作前后都需要验证和刷新；服务端调度不能替代 Git 自身的锁。
- 对只读查询支持取消；写操作按能力定义取消策略，不统一强杀进程。merge/rebase 的 continue/abort 使用明确业务操作。
- 自动 fetch 迁到服务端，以活动仓库会话和配置为依据调度，避免每个窗口各自重复执行。

## 8. 跨端功能处理

| 功能 | 执行位置与实现方式 |
| --- | --- |
| Git 与仓库托管平台认证 | 保留远端已有 credential helper、SSH key/agent；为 Git HTTPS askpass 与 SSH askpass 提供明确的 helper。当前 Command 对 credential.helper 和 SSH_ASKPASS 的 GUI 覆盖必须移除或改造，不能原样搬到 Server。 |
| 登录远端主机的 SSH 认证 | 由 Windows 连接器与 OpenSSH/登录终端完成，与远端 Git fetch/push 的认证分开。 |
| commit/rebase 编辑器 | 服务端安装无界面 helper，通过受用户权限保护的本地 IPC 联系 Server；以 operation ID、interaction ID 和操作凭据关联发起者，再请求 Windows UI 编辑。helper 写回远端文件并返回退出码，遵守断线和交互超时约定。 |
| 补丁导入与 archive 导出 | Windows 文件选择器选择本地文件；协议显式上传或下载内容，远端执行 Git 操作。 |
| 远端仓库/文件选择 | 通过远端目录接口浏览，界面标明连接目标和远端路径。 |
| 文本与图片 diff、历史内容 | 远端读取和解析必要数据，客户端渲染；大内容分块并限制缓存。 |
| 外部编辑器、diff/merge 工具 | 逐项定义运行位置；Windows 工具使用显式下载的临时副本，远端工具使用远端路径。回写需检测原文件是否变化。 |
| 自定义命令、Git hooks、LFS、签名 | 仓库相关执行留在远端。首版支持已配置好的非交互 hooks 和签名；askpass 不覆盖任意交互式 hook 或 GPG pinentry，额外终端/pinentry 桥接列为后续能力，界面报告具体限制。 |
| 设置 | UI 布局、主题、连接与书签保存在客户端；仓库设置由服务端访问；按连接及仓库标识保存 UI 状态。 |
| AI 提交消息 | diff 从服务端获取，AI 调用位置与凭据存放单独确定，避免隐式复制两端的凭据。 |

## 9. 执行阶段与完成条件

每阶段都应形成可构建、可验证的增量。迁移中尚未支持的远端功能显示明确状态，不能意外落回 Windows 本地执行。

### 阶段 0：完整盘点与边界设计

- 扫描 Commands 调用、File/Directory、Process、Native、路径拼接和 IPC 入口。
- 建立功能迁移清单，记录入口、执行位置、协议接口、交互回调、测试和完成状态。
- 固定首批目标平台、Git/runtime 依赖和协议草案。

完成条件：所有仓库相关入口都有明确归属；版本、路径、凭据和操作生命周期设计可供评审。

盘点不要求先完成所有功能的重构；优先确认只读功能及连接的边界，其余功能可在清单中保留待细化项，避免阻塞第一条端到端路径。

### 阶段 1：工程拆分与连接骨架

- 创建 Contracts、Core、Server、RemoteClient 项目，建立依赖边界。
- 提取 Git 进程执行器与最小查询，移除其 GUI 依赖。
- 完成协议帧、认证握手、ping、错误处理及结构化日志。
- 建立本机测试 Server，验证 UI 通过 RPC 访问它。
- 生成并运行 Windows Release/AOT 客户端和 Linux x64 自包含 Server；固定 JSON/RPC 实现，检查真实发布产物而非仅 Debug 构建。
- 在 WSL 与 SSH 各完成最小启动/握手验证，检查回环连接、环境初始化、登录认证入口和断线后 Server 存活；此时允许手动投放 Server，自动部署在后续阶段完成。

完成条件：Server 无需 GUI 即可运行；客户端与其独立构建；Release 产物完成真实 WSL/SSH 握手；协议异常、认证失败和版本不匹配可被明确报告。

### 阶段 2：WSL 只读功能贯通

- 实现 WSL 发行版选择、环境检测、Server 部署启动与本地端口连接。
- 改造 Launcher、Repository 和远端书签。
- 支持状态、分支、提交历史、提交详情、文本 diff 与远端 watcher。

完成条件：在 Windows 无 Git、无仓库挂载、WSL 无 .NET runtime 的条件下，通过实际发布包部署并打开仓库；远端终端修改文件或提交后，GUI 正确更新。

### 阶段 3：SSH 与恢复能力

- 实现 SSH Host 连接、部署、回环端口转发、日志及认证交互。
- 复用阶段 2 的所有业务接口和 UI。
- 完成安装并发控制、版本升级、连接复用、重连和空闲清理。

完成条件：仅通过 SSH 可访问且无 .NET runtime 的 Linux 主机能部署实际发布包并使用同一套只读功能；覆盖远端无下载网络、短暂断线、服务重启、版本变更、第二 GUI 接入及 SSH 登录认证失败。

### 阶段 4：日常 Git 写操作

- 迁移 stage/unstage、commit、checkout、分支操作、fetch、快进 pull 和 push。pull 在本阶段仅允许 `--ff-only`，分叉时清楚提示尚需 merge/rebase 能力；完整 pull 在阶段 5 开放。
- 本阶段可提供 stash 保存和列表；stash apply/pop 同样可能产生冲突，随阶段 5 的冲突恢复一起交付。
- 完成操作 ID、进度、日志、仓库写调度和结果查询。
- 接通 Git HTTPS/SSH askpass 和 commit 编辑器，验证已配置好的非交互 hooks/签名；不承诺通过同一个 helper 处理任意终端或 pinentry 输入。

完成条件：WSL 与 SSH 都能完成提交、快进同步和推送；分叉或已有冲突/进行中操作被明确报告，禁止把缺失的恢复能力表现为可用；断线不会导致写请求被自动重复执行，响应丢失时可按预先生成的 ID 查询结果；真实 helper 的断线、恢复与超时流程通过验证，失败结果与实际仓库状态一致。

### 阶段 5：高级功能与完整迁移

- 迁移 merge/rebase/cherry-pick/revert/reset、冲突处理及 interactive rebase。
- 先交付冲突展示、解决、continue/abort，再开放普通 merge/rebase pull 和 stash apply/pop；覆盖已有仓库中断操作的恢复。
- 迁移 submodule、worktree、LFS、bisect、GitFlow、blame、文件历史和图片 diff。
- 完成补丁、archive、外部工具、自定义命令和 AI 数据入口。
- 迁移设置、旧书签与 UI 状态；移除远端模式下残留的客户端仓库 I/O。

完成条件：迁移清单全部完成或明确记录受支持平台上的能力限制；远端路径不会被客户端当成本机路径执行。

### 阶段 6：发布与验收

- 扩展已有 Windows Client 和 Linux x64 发布验证至 Linux arm64，形成匹配发布包与摘要；本阶段是完整发布矩阵验收，基础发布产物已在阶段 1–3 验证。
- 完成干净环境安装、升级、回退、卸载和诊断文档。
- 按下述矩阵完成集成及端到端验证，修复具体剩余问题后发布。

完成条件：功能、连接恢复、平台边界和发布流程通过验收；用户可从 Windows 完成连接、部署和仓库操作。

## 10. 验证与验收矩阵

| 维度 | 必测场景 |
| --- | --- |
| 环境 | WSL 2 的 NAT/mirrored 网络、多个发行版、SSH Linux；首批 x64，随后 arm64；Windows 不安装 Git；远端无 .NET runtime；显式环境设置及 agent socket。 |
| 仓库 | 普通仓库、bare、worktree、submodule、空仓库、大历史、大 diff、二进制内容。 |
| 路径 | 空格、中文、特殊字符、大小写差异、符号链接；不同连接下相同路径。 |
| 连接 | 首次部署、重复连接、并发部署/启动、版本不匹配、SSH 认证失败、断线重连、Server 重启、第二 GUI 所有者接入、失效实例记录。 |
| 正确性 | stage/commit/checkout/push 等结果与远端 Git CLI 对照；执行后响应丢失、重复 operation ID、重启后的未知结果；外部进程修改、watcher 溢出、共享 common directory 操作；分叉 pull 与 stash 冲突。 |
| 交互 | Git HTTPS/SSH askpass、编辑器、rebase、非交互 hooks/签名；交互期间断线、超时及取消；不支持的终端/pinentry 交互给出明确限制。 |
| 性能 | 高延迟连接、快速文件变化、日志持续输出；分页、事件合并、内容分块和背压保持 UI 响应。 |
| 传输 | 非法/超大帧、无效连接凭据、隧道断开、敏感信息不出现在普通日志。 |

验证以真实临时仓库和实际 Git 结果为主；协议单元测试关注帧解析、取消、错误与重连状态，避免仅测试 DTO 属性。使用真实 WSL 与 SSH 环境完成最终验收，模拟传输不能替代它们。

## 11. 首个可用版本的交付界线

阶段 4 完成后形成首个功能受限的可用版本：用户能从 Windows 选择 WSL 或 SSH 主机，自动部署并连接 Server，打开远端仓库，查看状态、历史与 diff，完成 stage、commit、checkout、fetch、`pull --ff-only` 和 push，并在短暂断线后恢复。普通 pull、stash 恢复及冲突处理在阶段 5 成套交付。

完整改造需要继续完成阶段 5 和阶段 6。后续实施优先验证启动与连接生命周期，以及一条真实的仓库查询和状态更新流程，再逐项迁移现有功能。

## 12. 独立审查记录

2026-10-02：由一个独立 subagent 对照官方 VS Code 文档、当前源码与构建配置审查。结论是核心架构借鉴准确，原计划需补齐实施约定。修订包括：避开 GUI 默认源码包含的新项目布局；客户端预先生成 operation ID；按恢复能力安排 pull/stash 的阶段；明确远端环境设置、helper 的交互范围和超时；提前验证实际发布产物；规定单 GUI 所有者、启动锁和实例复用。保留 WSL 本地 TCP、SSH 回环隧道、仓库状态代次与 common directory 写协调，RPC 实现在阶段 1 原型后固定。

2026-10-03：用户指令推翻首轮"旧 owner 断连后 300 s 宽限期内拒绝其他 owner"的裁决：再次打开 GUI 连接 remote 时优先连接已有 server，不启动新实例，除非用户有意为之。Server 握手改为：不同 owner 在旧 owner 无任何活连接且无进行中操作时即被接受（不再等待宽限期）；同 owner（recovery token）始终接受；拒绝信息按原因区分（存在活连接 vs 操作进行中）。300 s 宽限期仅保留 server 空闲退出与操作交互等待两个用途。Server 套件新增接管回归项（断连→立即接管→活连接仍拒第三者→再接管）；产品启动器的"运行中实例复用、否则才启动"行为确认不变。同轮修复 `RemoteDirectoryBrowser` 浏览远端目录时的 NRE 崩溃（Avalonia 虚拟化列表回收容器时以 null 项调用 FuncDataTemplate），并将修复后的 linux-x64 Server 部署到用户 WSL 实例（BuildId `remote-1`）。
