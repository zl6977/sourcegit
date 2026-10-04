# C/S 架构计划独立复核

日期：2026-10-02。对象：当前已包含 §12 审查修订记录的 `REMOTE_ARCHITECTURE_PLAN.md`。本报告仅审查计划与官方资料，不评价实施代码，也不修改计划。

结论：**核心架构正确借鉴了 VS Code，可以执行；仍有几处约定需要写明确，尤其是 Windows SSH 认证、多连接所有权、阶段 4 checkout 的恢复边界，以及完整迁移的验收口径。** 无需推翻 WSL 本地端口、SSH 隧道和独立 Server 的总体设计。

## 已核实的借鉴与项目自有选择

| 计划章节 | 复核结论与依据 |
| --- | --- |
| §2、4 本地 UI / 远端工作区执行 | 符合 VS Code 的 UI 与 Workspace 执行位置划分。[官方扩展架构](https://code.visualstudio.com/api/advanced-topics/remote-extensions) |
| §2、5.2 SSH bootstrap 与业务隧道分开 | 官方排障文档确实描述先安装/启动 Server、再建立端口隧道的两次连接，也说明动态分配主机可能让两次连接落在不同节点。计划已有稳定主机与实例核对约定，应保留。[官方 SSH 排障](https://code.visualstudio.com/docs/remote/troubleshooting#_connecting-to-systems-that-dynamically-assign-machines-per-connection) |
| §2、5.2 WSL 随机本地端口、SSH 认证隧道 | 与官方 FAQ 一致。但“随机本地端口”不能推导所有 WSL 配置必然可达；计划已要求 NAT/mirrored 实测和失败诊断，合理。[官方 FAQ](https://code.visualstudio.com/docs/remote/faq)、[WSL 网络文档](https://learn.microsoft.com/en-us/windows/wsl/networking) |
| §5.2 WSL 不依赖交互 shell、env-setup | 正确借鉴官方 WSL 启动前的 Bourne shell 环境入口。[官方 WSL 环境设置](https://code.visualstudio.com/docs/remote/wsl#_advanced-environment-setup-script) |
| §2 本地下载后传输、自有 Server | 官方说明远端下载失败可回退到本地下载/传输；Server 不面向其他客户端使用。自行实现是合适边界。[官方 FAQ](https://code.visualstudio.com/docs/remote/faq) |
| §5.3、6、7 所有者、5 分钟宽限、操作 ID、JSON 帧、common-directory 调度 | 属于 SourceGit 的设计。官方架构资料不能证明这些是 VS Code 的具体实现或默认值。计划已将 RPC 标为项目选择；建议对这些生命周期规则也明确同样性质。 |
| §8 保留远端 credential helper，并桥接 askpass | 方向合理；这是 Git 执行边界的项目设计，不能称为 VS Code credential helper 实现的复制。credential helper 可缓存/生成凭据，askpass 是提示程序，二者不是同一个职责。[Git 官方凭据文档](https://git-scm.com/docs/gitcredentials) |

## 必须修改的实施约定

### M1：登录终端必须参与真实 SSH 连接认证

**章节：§5.1、§5.2、阶段 1/3。** 当前“首版 key/agent”与“登录终端完成密码、MFA”两句话容易被执行为：单独打开一次 `ssh host`，成功后再以非交互 SSH 运行 bootstrap 和隧道。这样无法保证后续进程获得密码/MFA 认证。

官方的登录终端是在 VS Code 执行 SSH 命令时展示认证提示；其 `ControlMaster` 复用建议限定 macOS/Linux 客户端。因此不能将该复用方案直接假定在 Windows 可用。[官方认证说明](https://code.visualstudio.com/docs/remote/troubleshooting#_enabling-alternate-ssh-authentication-methods)

**改法：** 首版继续保证 key/agent；同时明确密码/MFA 的交互必须接入实际 bootstrap 和 tunnel 进程，每次需要认证时均可提示，或先验证 Windows 可用的单个已认证连接方案。若登录按钮仅准备 known_hosts/agent，必须这样命名，不能宣布密码/MFA 连接已支持。补充“两条连接均需要密码/MFA”及首次主机密钥确认用例。

### M2：区分连接配置、GUI 所有者和 Server 实例

**章节：§5.1、§5.3。** 同一 GUI 可打开多个仓库，但同主机/用户可以存在两个连接配置、SSH Host 别名或 WSL 与 SSH 两种入口。每个连接配置各生成 owner，会把本 GUI 的第二条连接当成“第二 GUI”；同 owner 但 Server 只允许一条连接也会冲突。

**改法：** 明确 owner ID 属于 GUI 生命周期；connection ID 属于书签配置；Server instance ID 属于服务实例。同 GUI 在识别出相同实例后复用一个业务会话和恢复凭据，仓库分别持有租约；配置别名可保持独立书签标识。第二个 GUI 仍按现有规则拒绝。增加同 GUI 双配置指向同实例、不同实例、关闭一个仓库不影响另一个的验收。此处是项目内部一致性问题，不是 VS Code 官方规则。

### M3：阶段 4 checkout 必须限定不会引入未交付的冲突恢复

**章节：阶段 4/5、§11。** pull 与 stash 的阶段依赖已经修正；checkout 尚未同样明确。普通 checkout 可安全拒绝覆盖本地修改，但 merge checkout/自动 stash 再恢复可能引入冲突，已有进行中的 merge/rebase 也需要阶段 5 的恢复能力。[Git checkout 官方说明](https://git-scm.com/docs/git-checkout)

**改法：** 阶段 4 支持普通 checkout，并展示 Git 的拒绝原因与现有操作状态；禁止会创建冲突的 merge checkout 和隐式 stash 恢复。强制丢弃修改只能是用户明确选择的独立动作。阶段 5 再开放完整切换流程。补充 dirty/index/未跟踪文件、未合并条目及中断操作的测试，不能以“checkout 成功过一次”代替验收。

### M4：明确完整迁移与平台能力限制的区分

**章节：阶段 5 完成条件、§8、§10、§11。** “迁移清单全部完成或明确记录……能力限制”可能把未迁移功能标为限制后宣布完整改造完成，与 §1 保留功能和 §11 完成阶段 5/6 的目标冲突。

**改法：** 完成条件改为：支持平台上的清单项须有实现与测试证据；仅 §1/§8 预先约定的排除项，以及由可验证平台依赖造成的限制可以例外。普通 merge/rebase pull、stash 恢复、外部工具、图片 diff、AI 等不能仅写“暂不支持”算完成。平台缺少 LFS/GitFlow 等依赖应报告“需要安装”，并在有依赖的验收环境证明功能。新增例外需记录原因和范围变化。

### M5：阶段 0 必须准备真实验收环境与门槛

**章节：阶段 0/1/6、§10。** 真 SSH、arm64、WSL 两种网络、Windows 无 Git、Linux 无 runtime 的要求正确，但计划未规定环境供应、允许的测试目标、性能阈值和证据格式。缺这些准备，阶段 1 就可能因没有 SSH 目标停住；“高延迟保持响应”等也难客观判定。

**改法：** 阶段 0 追加环境清单及可访问性检查：独立测试 SSH 主机、x64/arm64 runner、干净 Windows/WSL 镜像、凭据测试仓库和网络模式。为大历史/大 diff/延迟/持续输出规定具体数据规模、操作和响应阈值。每个验收格记录版本、命令、GUI 操作、实际结果及产物路径；没环境标“待验证”，保留完成门槛，不能用模拟替代真实环境。

## 建议调整

1. **§5.2、§5.3、§8：区分持久远端 agent 与 SSH 转发 agent。** 后者 socket 依赖提供转发的 SSH 连接，脱离终端的 Server 不能把其地址永久当可用环境。采用逐会话 agent 环境，恢复后重新获取；已有使用旧 socket 的 Git 任务应明确失败和结果状态。验收 agent 来源、隧道断开及复接后的 fetch/push。[官方 agent 转发说明](https://code.visualstudio.com/docs/remote/troubleshooting#_making-local-ssh-agent-available-on-the-remote)
2. **§5.2、阶段 3/6：补发布 manifest。** 固定 build ID 的来源、协议版本、OS/架构、最低系统依赖和摘要；安装内容不可用相同 ID 静默替换。升级与回退使用匹配客户端/Server 对，不在活动写任务期间替换进程。单纯不同目录并存尚未定义用户如何回退。这是项目设计补充，不能照抄 VS Code 的原生依赖阈值。
3. **§5.3、6、7：把 Git 写、文件回写和内容上传的操作语义连起来。** 原计划已有 operation ID、未知结果和响应丢失测试，不能再视为缺失。进一步规定哪些文件修改也属于写操作、分块上传完成前是否可见、如何原子提交及查询结果；为恢复凭据、订阅、仓库句柄添加释放规则。取消返回“用户停止等待”或“仓库任务已中止”应分别明确。
4. **§7：补快照与查询代次的具体约定。** watcher 只能提供失效通知，不能成为原子快照保证。规定查询结果携带代次、客户端应用条件、服务端组合查询前后检测方式和并发变化重试上限；common-directory 协调保留。此项是为了让已有正确性要求能写成可测试实现。
5. **§1、阶段 0/1：固定 Windows 本地仓库的路线。** 计划已规定若保留则经 Local Server，这一点正确。明确该路线属于本次交付，并把本机 Server 部署、启动和旧书签迁移列入任务，避免最终只迁移远端而遗漏本地业务边界。
6. **阶段 1/5：细分交付并保留最终范围。** 阶段 1 先做最小 UI RPC 与发布序列化验证，再完成真实连接；阶段 5 按冲突恢复、嵌套仓库与扩展 Git 功能、内容与工具、设置与剩余入口分批验收。每批附原 UI 入口、API、恢复策略和测试。原功能规模很大，阶段名称不应被理解为一次提交或固定短工期。成熟 RPC 的原型评估保留，不宜在评估前把自建协议视为定案。

当前修订版已经解决新项目递归编译、客户端预先生成 operation ID、pull/stash 的阶段依赖、helper 超时、发布验证前置等问题；本轮不重复将这些列为缺陷。上述修改主要让剩余交互、生命周期与验收约定能够直接转成工程任务。
