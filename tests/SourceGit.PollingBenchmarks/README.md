# 取消轮询的受控对比

在 Windows 上使用同一测量源码，分别引用已构建的旧版本和当前版本的 RemoteClient/Contracts。每轮启动该版本自己的 Debug Server，使用 loopback TCP；握手必须验证相同协议和 BuildId。程序不会修改产品源码、已有服务器部署或用户仓库。

先分别构建两份 `tests/SourceGit.Remote.Tests/SourceGit.Remote.Tests.csproj`，使各自客户端与 Server 匹配，然后在仓库根目录运行：

```powershell
./build/scripts/measure-remote-polling.ps1 `
    -BaselineRoot artifacts/polling-baseline/source `
    -CurrentRoot . `
    -Output artifacts/polling-comparison-new
```

输出目录必须不存在。脚本先完成两份测量程序的构建、核对实际复制依赖的 SHA-256，然后依次运行旧、新、新、旧，禁止同时测量两个版本。测量期间不要并行运行构建或其他测试。

每轮建立四个独立空 Git 仓库，先预热一次命令，然后分别测量一个和四个打开仓库：每组 3 个 10s 空闲样本、5 个 1s 命令批次，四仓库批次同时提交四个操作。每轮共 30 次操作（初始化 4、预热 1、实际负载 25）。debug 日志计数统计所有成功完成的 GetOperation/WaitOperation 请求。

`plan.json` 记录配置、顺序、运行时、输入位置和测量源码摘要；每轮保存退出码、原始 JSON、debug 日志及命令时间戳。CPU 是客户端/Server 的进程 CPU 时间，未包含子 PowerShell 的 CPU；线程和句柄是样本末尾观察值，不能当作连续峰值。每次读取进程指标前调用 Refresh。

延迟由命令退出前写入的同机 Stopwatch 时间戳到客户端拿到结果计算，包含命令退出、整树/输出收尾与 RPC 交付；它不是服务端终态发布到客户端的准确延迟。分位数应报告样本数及计算方法，低样本 p95 仅用于描述。调度唤醒、GUI、Linux /proc 扫描、本地启动和 WSL 升级的性能不由本程序测量；它们仍需独立验收。

这是 Debug/Windows/空仓库的机制对比，不能据此宣称所有桌面或远程网络负载的 CPU 改善百分比，也不能宣称 Git 命令本身执行更快。正确性失败、握手拒绝或非零退出的轮次不计为通过。

同一工程另提供 `--lifecycle <server-dll> <server-exe> <fresh-output>`：实际 Local ConnectionManager 启动/认证关闭及独立管道后代收尾，每组一次预热、五次实测。它使用唯一 Local 记录命名空间，打开稳定进程句柄确认退出；进程树命令结果必须等到后代完成标记存在。比较时仍应先构建和核验两份引用，再按旧、新、新、旧执行，保存每轮退出码。该入口会启动实际 Windows 进程，只使用所指定版本的服务器和自有测试仓库。

`--wsl-upgrade <distro> <matching-linux-x64-package> <fresh-output>` 测量各版本实际 ServerDeployer 的首次启动及升级，每轮一次预热、五个样本，使用独立 `/tmp` 安装目录。需要 WSL 目标上的 python3/pidfd，仅用于测试退出观察。安装/上传不计时；旧版上层200ms升级循环按原代码复现，当前一次bootstrap携带旧身份；它不是整个 ConnectionManager/preflight/GUI 的端到端测量。同样先构建、核对依赖并交替运行。原生退出和自有record删除均确认后，才删除该样本的临时安装目录。
