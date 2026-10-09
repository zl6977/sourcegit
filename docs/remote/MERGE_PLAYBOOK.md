# 主线跟踪与融合手册（MERGE_PLAYBOOK）

日期：2026-10-09
适用分支：`wsl-server-window-client`（跟踪 `origin/master` 的 release 版）
目的：每次主线发版后，把 release 合并进本分支并融合远端化设计时，用于判定冲突归属、指导解冲突和合并后验证。
本文的"做法"部分是经验性的，不是硬性约束；"原则"和"边界"部分是约束。

---

## 1. 总设计原则：Windows Client 与远端 Server 分离

这是本分支全部改造的根，一切融合决策从它推导：

- **Windows GUI 只负责**：界面与交互、连接管理、展示缓存、UI 设置（主题/布局/连接/书签）。
- **Server（运行在 WSL 或 SSH Linux 主机上）只负责**：Git 进程执行与解析、仓库文件读写、状态计算、文件监控、自动 fetch、askpass/editor helper。
- **仓库数据永远不落到 Windows 文件系统 API**。客户端拿到的是 `sourcegit-remote://<connectionId>/...` 形式的逻辑路径（见 `src/Remote/RepositoryPath.cs` 的 `Scheme`），业务层通过连接抽象访问，不接触 SSH/WSL 启动细节。
- **WSL 与 SSH 共用同一套业务协议**，只在部署、启动、连接通道上有区别。
- 详细架构与阶段计划见 `docs/remote/REMOTE_ARCHITECTURE_PLAN.md`；此处不重复。

**由此推导的融合总方针**：主线对"Git 执行 / 仓库文件 I/O / 路径处理 / 进程生命周期 / 仓库状态刷新"的任何改动，都不接受原样合入——必须把它翻译到正确的执行侧（Server 或 Client）再落地。主线对"纯 UI 呈现、本地资源"的改动可以直接接受。

---

## 2. 主线跟踪模型
- 上游仓库：`origin` = `github.com/sourcegit-scm/sourcegit`；自己的 push 远端：`gh-zzz`。
- 主线以 `release/v2026.NN` 分支 + tag（如 `v2026.21`）发版，节奏约 2~4 周一次。
- 每个 release 合并进来一次即可，不必跟踪 develop/中间提交。

### 2.1 当前拓扑事实（写于 v2026.21 基线）

本分支 = **主线基线（`v2026.21` = merge-base `a4ae633c`）** + **主线 develop 同步的 34 个功能/修复提交** + **本地远端化 4 个提交**（`635121ce` 实现 wsl/ssh → `9abc5824` 轮询改事件驱动 → `5cb14d83` 未初始化目录与历史连接 → `8c7384ad` SSH 重试安全策略）。

注意：本地 4 个提交是**叠加在 develop 同步之上**的。下次合并主线 release 时，主线 release 与这些 develop 提交高度重叠，大部分 develop 内容不会冲突；真正要防的是主线在 develop 之外又走的新提交（Statistics、locale、CI 等）。

### 2.2 每次发版的标准流程

```bash
git fetch origin --tags
# 找到最新 release tag
git merge v2026.NN          # 在自己的工作分支上合并
# ……解冲突（按第 4 节判定）……
# 按第 5 节验证
git commit                  # merge commit，保留双方历史，不要 rebase 改写
```

- 合并方向永远是 **主线 → 本分支**；不要把本分支历史推回主线可见位置。
- 用 merge 不用 rebase：本地已有多个大提交叠加在基线上，rebase 会把冲突重复 N 次。
- 建议在仓库开启 `git config rerere.enabled true`：主线每个文件都会被反复合并，rerere 记住重复出现的冲突解法（当前未开启）。
- 合并冲突后想看清"谁动了什么"：
  - `git log --oneline v2026.OLD..v2026.NN -- <冲突文件>` 看主线这段时间对该文件的全部改动；
  - `git diff 6ad3c8a0...HEAD -- <文件>`（三点）只看本地远端化对它的改动——**把两侧 diff 都读一遍再动手**，冲突只是两幅 diff 的投影。

---

## 3. 代码分层：融合时的三种归属

本地远端化改动相对基线的分布（相对 `6ad3c8a0`，约 245 个文件）：

### Tier A：完全新增，永不与主线冲突

主线不知道它们存在，合并时只会"带进来"，不会冲突。融合时视为**只读区**。

- `modules/SourceGit.Contracts|Core|Server|RemoteClient/`（协议 DTO、Git 执行与仓库服务、服务端入口、客户端连接/部署/重连）
- `tests/SourceGit.Remote.Tests|GuiRemote.Tests|PollingBenchmarks/`
- `docs/remote/*`、`build/scripts/publish-remote.ps1`
- 本分支新增的 src 文件：`src/Remote/*`（GitProcess、RemoteWorkspace、RemoteConnections、RemoteExternalTools、RepositoryFile/RepositoryPath/RepositoryDirectory、RepositorySnapshot）、`src/Models/FetchSchedule.cs`、`src/ViewModels/RemoteDirectory.cs`、`src/Views/RemoteDirectoryBrowser|RemoteInteraction|RemoteRepositories|TimeDisplayVisibility`

### Tier B：上游文件里的"接缝"（seam）

在主线原有文件里插入的远端钩子，冲突高发区。特征：主线文件本身结构不变，只在关键调用点插入 `if (IsRemote(...))` 分支、把裸 API 换成 Remote 垫片、或订阅 Server 事件。

接缝的惯用形态（解冲突时认这些模式）：

1. **垫片替换**：`File.*` → `SourceGit.Remote.RepositoryFile.*`；`Path.*` → `RepositoryPath.*`；`Directory.*`/`new DirectoryInfo` → `RepositoryDirectory.*`/`RepositoryDirectoryInfo`；这些垫片内部按路径 scheme 自动本地/远端二选一。本地合法路径行为不变，所以**主线对同一行的本地逻辑改动可直接保留，只需把其文件/路径 API 重新套上垫片**。
2. **`IsRemote` 守卫分支**：`Command.CreateGitStartInfo` 里远端走 `sourcegit-server` 转发；`Native.OS.OpenInFileManager/OpenTerminal` 里远端拒绝并 Notification；`OpenWithDefaultEditor` 远端先下载到临时文件。形态都是"开头插入 if 块，原逻辑留在 else/后续"。
3. **查询结果快照代次**：`Repository.cs` 里 `TryAcceptSnapshot(...)`（`RepositorySnapshot(InstanceId, RepositoryId, Generation)`），远端仓库忽略旧代次结果。
4. **订阅/事件点**：`Watcher.cs`、`AvatarManager.cs` 由轮询改为事件驱动；`Repository.cs` 的自动 fetch 改走 `RemoteWorkspace.ConfigureAutoFetchAsync`。

高冲突接缝文件（本分支改动量大、主线也常改）：
`src/Commands/Command.cs`、`src/ViewModels/Repository.cs`（+311 行）、`src/Models/Watcher.cs`（近乎重写 470 行）、`src/Models/AvatarManager.cs`（重写 199 行）、`src/App.axaml.cs`、`src/ViewModels/Launcher.cs`/`LauncherPage.cs`、`src/Native/OS.cs`、`src/Views/Launcher*.axaml`、`src/Views/Repository.axaml`、`src/Views/Welcome.axaml`、`src/Commands/Query*`（Blame/QueryBranches/QueryLocalChanges/QueryRepositoryStatus/QuerySingleCommit 五个 RPC 直查接缝）。

### Tier C：结构性重写

`Watcher.cs`、`AvatarManager.cs` 这类"实现整体替换、只保留对外 API 形状"的文件。主线若改动其**公开行为**（新事件、新过滤逻辑），按第 4 节翻译到 Server/Client 后在重写版上重新实现；主线若只改内部实现细节，通常直接采用本地版本，然后人工对照主线 diff 找丢掉的语义。

### 构建接缝（一次性核对，易漏）

- `SourceGit.slnx`：`/modules/`、`/tests/` 文件夹条目。主线动 slnx 时保留本地块。
- `src/SourceGit.csproj`：`InternalsVisibleTo=SourceGit.GuiRemote.Tests` + 两个 ProjectReference（Contracts、RemoteClient）。主线加包引用/编译项时注意别丢这两行。
- `global.json`：本地 SDK 钉为 `10.0.100`（主线 `10.0.0`，rollForward latestMajor 下无行为差）。**建议：合并时取主线版本**，本地钉法只是当时的产物。
- `.gitignore`：`artifacts/`、`build/`、`.remote-io-inventory.txt` 条目。

---

## 4. 冲突判定规则（核心）

对每个冲突，先查主线这段改动的**性质**，再决定归属：

| 主线改动性质 | 处理 |
| --- | --- |
| 纯 UI：布局、axaml 控件、主题、图标、locale、翻译、Statistics/图表、快捷键、外观修复 | **直接取主线**（`--theirs`），除非本分支在相邻处加了远端 UI（如 Launcher 的远端连接/目录选择块——取主线后把本地块重新贴回） |
| 本地仓库功能，远端模式下不存在/不适用（文件浏览器打开、本机终端、扫描本地仓库、本机 git 二进制探测） | 取主线，然后在紧邻处**补回守卫**：远端路径拒绝或改走 Server（对照 `Native.OS.OpenInFileManager` 模式） |
| Git 执行语义：命令行参数、`--no-pager -c ...` 前缀、credential.helper/SSH_ASKPASS 处理、safe.directory、进程启动方式（setsid 等） | **翻译成 Server 侧改动**。这些逻辑属于 `modules/SourceGit.Core` 的 Git 执行/解析；GUI 侧 `Command.cs` 只保留"远端→转发 sourcegit-server、本地→原样"的分叉。主线改 GUI 的 git 命令行时，问：这条参数在远端执行环境是否同样需要？需要就同步改 Core + Server 协议测试，不需要就在注释中说明并保持分叉 |
| 仓库状态/查询逻辑：status 解析、ref 读取、watcher 事件合并、fetch 调度、packed-refs、快照一致性 | **翻译成协议/Server/Client 三层改动**：数据类别是否要进 Server 快照发布？Generation 是否要推进？客户端订阅是否要新事件？GUI 侧只接受翻译后的事件流，不照抄轮询/直读代码 |
| Models/Settings：`RepositorySettings.cs`、`Preferences.cs`、`Remote.cs`（git remote URL 模型）等直接读写 common dir | 主线改动若涉及**仓库级数据**，必须经 `RepositoryFile/RepositoryPath` 垫片或 Server 接口；若涉及 **UI 级数据**（主题、布局），取主线原样 |
| 新增本地功能（如"在系统文件浏览器中打开仓库"） | 取主线实现 + 加远端守卫（参照主线自己的"不适用"通知模式），不要让远端仓库静默执行本机路径 API |
| locale / `TRANSLATION.md` / 纯文档 | 永远取主线；本分支新增的 UI 文案键单独追加（当前远端 UI 尚未走 locale 键，未来若加，追加在文件尾部即可减少冲突） |

**判定不了时的保守规则**：主线新行为先在本地路径上采用、在远端路径上显式拒绝并报 Notification（"该功能尚不支持远端"），把完整实现挂到迁移清单，绝不静默回退到 Windows 本地执行——这是架构计划第 9 节的完成条件，也是融合时最容易破的纪律。

---

## 5. 合并后验证清单（固定动作）

1. **接缝完整性扫描**：Tier B 文件解冲突后，逐个搜索垫片与守卫是否被主线改动"洗掉"：
   - `grep -n "RepositoryFile\.\|RepositoryPath\.\|RepositoryDirectory" src/App.axaml.cs src/Native/OS.cs ...` 对照 3.3 节清单；
   - `Command.cs` 的 `CreateGitStartInfo` 远端分叉、`App.axaml.cs` 的 rebase-todo/COMMIT_EDITMSG/history/blame 垫片是否仍在。
2. **构建**：`dotnet build SourceGit.slnx -c Release`；Release/AOT 下验证 Contracts 的源生成 JSON（主线若加 Newtonsoft/反射序列化会破 AOT 边界）。
3. **测试**：`tests/SourceGit.Remote.Tests` 与 `tests/SourceGit.GuiRemote.Tests` 全跑（它们内含协议、启动、生命周期、接管、重试风暴等回归项，主线改行为后这两套件就是融合的回归网）。
4. **发布产物**：`build/scripts/publish-remote.ps1` 产出 linux-x64 Server 包；与客户端 BuildId 配对握手（版本不匹配必须被明确报告——这是协议规定，主线改版本号逻辑时注意）。
5. **冒烟**：WSL 打开一个真实仓库 → 看历史/diff/stage/commit/push → 远端终端改文件验证 watcher 事件刷新；SSH 至少一次连接握手。
6. **文档同步**：`docs/remote/IMPLEMENTATION_STATUS.md` 追加本次合并的 release 号与融合要点；功能迁移清单上把本次主线新增的本地功能登记为"待远端化"。

---

## 6. 非约束性经验（rules of thumb）

- **先读两侧 diff，再读冲突块**。冲突标记里的"ours/theirs"没有信息量，`git log -p` 的两条时间线才有。
- **接缝保持"可被洗掉"**：写钩子时尽量独立成块（守卫分支、垫片调用），少与主线逻辑交叉重写——将来主线任意改写该函数，冲突呈现为"块被整体挪走"，重贴即可。融合成本与接缝侵入度成正比。
- **垫片优先于分支判断**：能用 `RepositoryFile.ReadAllText` 自动路由的场景，不要在每个调用点写 `if (IsRemote)`。主线作者从不看 Remote 命名空间，垫片让他们的改动天然存活。
- **主线重构你的接缝文件时（如 Watcher 大改），顺势对齐结构，保留事件语义映射表**（主线事件类别 → Server 发布的数据类别）。
- **develop 内容别单独再追**：release 分支已含 develop 合流；等 tag 出即可。
- **每次合并保持小**：宁可漏一个 release 也不要攒两个——主线对 `Repository.axaml`/`WorkingCopy.axaml`/`LauncherPage.axaml` 的 UI churn 很高（近 60 次主线提交内分别 9/8/5 次），攒着合会让接缝重贴成本翻倍。
- **合并提交里不要顺手做功能重构**；发现的接缝漂移另开提交，保持"一次 merge = 一次可回放的状态"。
- **给 rerere 机会**：同一文件第 N 次出现同款冲突时，解法几乎总是上次的；rerere 会自动复用，值得开启。
- **主线新增"打开路径/执行进程/读写仓库文件"类功能时，先问执行位置再问功能**——这是第 1 节原则的日常投影，也是每次 merge 最常见的决策点。
