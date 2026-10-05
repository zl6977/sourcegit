using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using SourceGit.Contracts;
using SourceGit.Remote;
using SourceGit.RemoteClient;

internal static class WorkingCopyMenuChecks
{
    internal static void Run(string serverPath)
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(null);
        var preferencesField = typeof(SourceGit.ViewModels.Preferences).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = preferencesField.GetValue(null);
        preferencesField.SetValue(null, new SourceGit.ViewModels.Preferences());
        var root = Path.GetFullPath(Path.Combine("artifacts", "working-copy-menus", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "server.json");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { serverPath, "--record", recordPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var client = new RemoteRepositoryClient();
        LeaseReplyProxy? proxy = null;
        RemoteRepositoryClient? faulty = null;
        RemoteWorkspace? workspace = null;
        try
        {
            Check(process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()?.StartsWith("SOURCEGIT_READY ") == true, "Server did not publish ready");
            var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), RemoteJson.Default.ServerRecord)!;
            client.ConnectAsync("127.0.0.1", record.Port, record.Token).GetAwaiter().GetResult();
            Git("init"); Git("config", "user.name", "Menu fixture"); Git("config", "user.email", "menu@example.invalid"); Git("config", "commit.gpgsign", "false");
            var deleted = Path.Combine(root, "deleted.txt");
            File.WriteAllText(deleted, "committed\n");
            Git("add", "deleted.txt"); Git("commit", "-m", "fixture");
            File.Delete(deleted); Git("add", "deleted.txt");
            proxy = new LeaseReplyProxy(record.Port, 0);
            faulty = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
            faulty.ConnectAsync("127.0.0.1", proxy.Port, record.Token).GetAwaiter().GetResult();
            workspace = new RemoteWorkspace(Guid.NewGuid().ToString("N"), "Menus", faulty, faulty.GetEnvironmentAsync().GetAwaiter().GetResult());
            var resource = workspace.Resource(root);
            var info = workspace.OpenAsync(resource).GetAwaiter().GetResult();
            var repo = new SourceGit.ViewModels.Repository(false, resource, workspace.Resource(info.GitDirectory));
            var vm = new SourceGit.ViewModels.WorkingCopy(repo);
            var change = new SourceGit.Commands.QueryLocalChanges(resource).GetResultAsync().GetAwaiter().GetResult().Single(c => c.Path == "deleted.txt");
            Check(change.Index == SourceGit.Models.ChangeState.Deleted, "Fixture was not a staged deletion");
            var selection = new SourceGit.ViewModels.ChangeSelection(new[] { change });
            var view = new SourceGit.Views.WorkingCopy();
            CheckMenus("missing file");
            Console.WriteLine("PASS Actual staged deletion builds staged/unstaged menus with Open and Reveal disabled");

            proxy.StatError = "Path is outside repository";
            CheckMenus("server rejects path");
            Check(!RepositoryFile.Exists(workspace.Resource("/missing")) && !RepositoryDirectory.Exists(workspace.Resource("/missing")), "Rejected existence check escaped to UI");
            try { RemoteWorkspace.Stat(workspace.Resource(deleted)); throw new Exception("Explicit stat error was suppressed"); }
            catch (IOException error) when (error.Message.Contains("Path is outside repository")) { }
            Console.WriteLine("PASS Actual RPC path rejection disables both context menus; explicit Stat still reports the error");

            proxy.StatError = null;
            faulty.DisposeAsync().AsTask().GetAwaiter().GetResult();
            CheckMenus("disconnected");
            Check(!RepositoryPath.Exists(workspace.Resource(deleted)), "Disconnected generic existence check threw or returned true");
            Console.WriteLine("PASS Disconnected remote file/directory/path checks and deleted-file menus remain safe");

            void CheckMenus(string reason)
            {
                var staged = view.CreateContextMenuForStagedChanges(repo, vm, selection);
                var unstaged = (ContextMenu)typeof(SourceGit.Views.WorkingCopy).GetMethod("CreateContextMenuForUnstagedChanges", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [repo, vm, selection])!;
                foreach (var menu in new[] { staged, unstaged })
                {
                    var items = menu.Items.OfType<MenuItem>().ToArray();
                    Check(!items.Single(item => Equals(item.Header, "Text.Open")).IsEnabled, $"Open enabled for {reason}");
                    Check(!items.Single(item => Equals(item.Header, "Text.RevealFile")).IsEnabled, $"Reveal enabled for {reason}");
                    Check(items.Any(item => Equals(item.Header, "Text.FileCM.SaveAsPatch")), "Missing file removed valid Git actions");
                }
            }
        }
        finally
        {
            workspace?.Unregister();
            if (faulty != null) faulty.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (proxy != null) proxy.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { if (!client.IsDisconnected) client.ShutdownAsync().GetAwaiter().GetResult(); }
            finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            preferencesField.SetValue(null, previous);
            process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        void Git(params string[] arguments)
        {
            var result = client.ExecuteGitAsync(new GitRequest("", arguments, ReadOnly: false, WorkingDirectory: root)).GetAwaiter().GetResult();
            Check(result.ExitCode == 0, "Fixture Git failed: " + result.Error);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
