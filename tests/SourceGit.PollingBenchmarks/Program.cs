if (args[0] == "--wsl-upgrade")
    await WslComparison.RunAsync(args[1], Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
else if (args[0] == "--lifecycle")
    await LifecycleComparison.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
else
    await PollingComparison.RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
