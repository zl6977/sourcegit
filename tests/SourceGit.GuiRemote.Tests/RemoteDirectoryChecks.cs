using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using SourceGit.Contracts;
using SourceGit.Models;
using SourceGit.Remote;
using SourceGit.RemoteClient;
using SourceGit.ViewModels;

internal static class RemoteDirectoryChecks
{
    internal static void Run(string? serverPath, string? recordPath, string? directoryPath)
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        var preferencesField = typeof(Preferences).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = preferencesField.GetValue(null);
        var preferences = new Preferences();
        preferences.Workspaces.Add(new Workspace());
        preferencesField.SetValue(null, preferences);
        // Keep the ordinary-directory fixture outside the checkout: Git recognizes parent repositories.
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-remote-directory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousConfig = SourceGit.Native.OS.BasicDirectories.ConfigDir;
        SourceGit.Native.OS.BasicDirectories.ConfigDir = root;
        Process? server = null;
        Task<string>? stderr = null;
        RemoteWorkspace? workspace = null;
        Launcher? launcher = null;
        var client = new RemoteRepositoryClient();
        var notifications = new List<Notification>();
        Action<Notification> capture = notifications.Add;
        Notification.Raised += capture;
        try
        {
            if (recordPath == null)
            {
                recordPath = Path.Combine(root, "server.json");
                var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { Path.GetFullPath(serverPath!), "--record", recordPath }) start.ArgumentList.Add(arg);
                server = Process.Start(start)!;
                stderr = server.StandardError.ReadToEndAsync();
                Check(Await(server.StandardOutput.ReadLineAsync())?.StartsWith("SOURCEGIT_READY ") == true, "Server did not publish ready");
            }
            var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), RemoteJson.Default.ServerRecord)!;
            Await(client.ConnectAsync("127.0.0.1", record.Port, record.Token));
            workspace = new RemoteWorkspace("directory-" + Guid.NewGuid().ToString("N"), "Directory fixture", client, Await(client.GetEnvironmentAsync()));
            var hostRoot = directoryPath ?? root.Replace('\\', '/');
            var plain = hostRoot + "/plain directory";
            Await(client.FileAsync(new FileRequest("", "mkdir", plain)));
            var path = workspace.Resource(plain);
            var location = Await(workspace.OpenLocationAsync(path));
            Check(location.Repository == null && !Stat(plain + "/.git").Exists, "Opening an ordinary directory initialized Git");
            launcher = new Launcher(null!);
            launcher.RestoreRemoteWorkspace = (_, _) => Task.FromResult(workspace);
            var node = new RepositoryNode { Id = path, Name = "plain directory", IsRepository = true };
            Await(launcher.OpenRemoteRepositoryAsync(node, null!));
            var page = launcher.ActivePage;
            Check(page.Data is RemoteDirectory && launcher.Pages.Count == 1 && page.Node == node, "Ordinary directory did not open in a named tab");
            var directory = (RemoteDirectory)page.Data;
            _ = new SourceGit.Views.LauncherPage { DataContext = page };
            Check(preferences.Workspaces[0].Repositories.Contains(directory.FullPath), "Ordinary directory was not saved in the workspace");
            launcher.OpenRepositoryInTab(node, null!);
            Check(launcher.Pages.Count == 1 && !notifications.Any(n => n.IsError) && !Stat(plain + "/.git").Exists, "History reopened a duplicate tab or reported Git fatal");
            Console.WriteLine("PASS Existing ordinary directory opens in a named, persisted GUI tab without creating .git; history reuses it");

            Await(directory.RefreshCommand.ExecuteAsync(null));
            Check(page.Data is RemoteDirectory && !Stat(plain + "/.git").Exists, "Refresh initialized the ordinary directory");
            directory = (RemoteDirectory)page.Data;
            Await(directory.InitializeCommand.ExecuteAsync(null));
            Check(Stat(plain + "/.git").Directory && page.Data is Repository && launcher.Pages.Count == 1, "Explicit initialization did not transition the same tab to a repository");
            Check(!notifications.Any(n => n.IsError), "Initialization reported errors: " + string.Join("; ", notifications.Where(n => n.IsError).Select(n => n.Message)));
            Console.WriteLine("PASS Refresh preserves the ordinary directory; explicit Initialize creates remote .git and opens Git in the same tab");
            launcher.CloseTab(page);
            Check(page.Data is Welcome && preferences.Workspaces[0].Repositories.Count == 0, "Closing initialized repository did not clear workspace");

            var external = hostRoot + "/external";
            Await(client.FileAsync(new FileRequest("", "mkdir", external)));
            var externalNode = new RepositoryNode { Id = workspace.Resource(external), Name = "external", IsRepository = true };
            Await(launcher.OpenRemoteRepositoryAsync(externalNode, null!));
            page = launcher.ActivePage;
            directory = (RemoteDirectory)page.Data;
            var init = Await(client.ExecuteGitAsync(new GitRequest("", ["init", "-q"], ReadOnly: false, WorkingDirectory: external)));
            Check(init.ExitCode == 0, init.Error);
            Await(directory.RefreshCommand.ExecuteAsync(null));
            Check(page.Data is Repository && launcher.Pages.Count == 1, "External initialization was not picked up in the same tab");
            launcher.CloseTab(page);
            Console.WriteLine("PASS External git init is picked up by explicit Refresh without an interval timer or duplicate tab");

            var corrupt = hostRoot + "/corrupt";
            Await(client.FileAsync(new FileRequest("", "mkdir", corrupt + "/.git")));
            ExpectError(workspace.OpenLocationAsync(workspace.Resource(corrupt)), "Invalid .git metadata was treated as an ordinary directory");
            ExpectError(workspace.OpenLocationAsync(workspace.Resource(hostRoot + "/missing")), "Missing path was treated as an ordinary directory");
            Console.WriteLine("PASS Missing directories and invalid .git metadata remain errors");

            Await(launcher.OpenRemoteRepositoryAsync(node, null!));
            Check(launcher.ActivePage.Data is Repository, "Existing repository no longer opens");
            launcher.CloseTab(launcher.ActivePage);
            var close = hostRoot + "/close";
            Await(client.FileAsync(new FileRequest("", "mkdir", close)));
            var closeNode = new RepositoryNode { Id = workspace.Resource(close), Name = "close", IsRepository = true };
            Await(launcher.OpenRemoteRepositoryAsync(closeNode, null!));
            var closePage = launcher.ActivePage;
            var closedDirectory = (RemoteDirectory)closePage.Data;
            launcher.CloseTab(closePage);
            Check(closePage.Data is Welcome && preferences.Workspaces[0].Repositories.Count == 0, "Closing the last ordinary tab did not return to Welcome");
            Await(closedDirectory.InitializeCommand.ExecuteAsync(null));
            Check(!Stat(close + "/.git").Exists, "Closed directory command initialized Git");
            Console.WriteLine("PASS Existing repositories still open; closing the last ordinary tab returns to Welcome and prevents stale initialization");

            Await(launcher.OpenRemoteRepositoryAsync(closeNode, null!));
            closePage = launcher.ActivePage;
            closedDirectory = (RemoteDirectory)closePage.Data;
            var restoring = new TaskCompletionSource<RemoteWorkspace>(TaskCreationOptions.RunContinuationsAsynchronously);
            launcher.RestoreRemoteWorkspace = (_, _) => restoring.Task;
            var refreshing = closedDirectory.RefreshCommand.ExecuteAsync(null);
            Check(closedDirectory.IsBusy && !closedDirectory.InitializeCommand.CanExecute(null), "Refresh did not disable concurrent initialization");
            launcher.CloseTab(closePage);
            restoring.SetResult(workspace);
            Await(refreshing);
            Check(closePage.Data is Welcome && preferences.Workspaces[0].Repositories.Count == 0, "Delayed refresh reopened a closed tab");
            launcher.RestoreRemoteWorkspace = (_, _) => Task.FromResult(workspace);
            Console.WriteLine("PASS Concurrent initialization is disabled during Refresh; late connection completion cannot reopen a closed directory tab");

            var bare = hostRoot + "/bare";
            Await(client.FileAsync(new FileRequest("", "mkdir", bare)));
            init = Await(client.ExecuteGitAsync(new GitRequest("", ["init", "--bare", "-q"], ReadOnly: false, WorkingDirectory: bare)));
            Check(init.ExitCode == 0, init.Error);
            Check(Await(workspace.OpenLocationAsync(workspace.Resource(bare))).Repository is { Bare: true }, "Bare repository was treated as an ordinary directory");
            ExpectError(workspace.OpenLocationAsync(workspace.Resource(plain + "/.git")), "A Git work-tree error was treated as an ordinary directory");
            Console.WriteLine("PASS Bare repositories keep normal detection; other Git errors remain errors instead of opening an ordinary directory");

            FileResult Stat(string file) => Await(client.FileAsync(new FileRequest("", "stat", file)));
        }
        finally
        {
            launcher?.CloseAll();
            if (launcher != null)
                Notification.Raised -= typeof(Launcher).GetMethod("DispatchNotification", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<Notification>>(launcher);
            Notification.Raised -= capture;
            workspace?.Unregister();
            try { if (!client.IsDisconnected) Await(client.ShutdownAsync()); }
            finally { Await(client.DisposeAsync().AsTask()); }
            if (server != null) { Await(server.WaitForExitAsync()); server.Dispose(); Console.Write(Await(stderr!)); }
            preferencesField.SetValue(null, previous);
            SourceGit.Native.OS.BasicDirectories.ConfigDir = previousConfig;
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static void ExpectError(Task task, string message)
    {
        try { Await(task); }
        catch (IOException) { return; }
        throw new Exception(message);
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1); // Bounded test dispatcher pumping, never product polling.
        }
        Check(task.IsCompleted, "Directory test exceeded its deadline");
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
