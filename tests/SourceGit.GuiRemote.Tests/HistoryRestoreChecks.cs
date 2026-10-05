using SourceGit.Contracts;
using SourceGit.Remote;
using SourceGit.RemoteClient;

internal static class HistoryRestoreChecks
{
    internal static async Task RunAsync(string serverPath)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "history-restore", Guid.NewGuid().ToString("N")));
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        var directories = SourceGit.Native.OS.BasicDirectories;
        var previousConfig = directories.ConfigDir;
        directories.ConfigDir = root;
        RemoteWorkspace? workspace = null;
        try
        {
            var options = new ConnectionOptions("history-" + Guid.NewGuid().ToString("N"), "History fixture", "Local", "", LocalServerPath: serverPath);
            RemoteConnections.Save(options);
            var resource = RepositoryPath.Remote(options.Id, repository.Replace('\\', '/'));
            workspace = await RemoteConnections.RestoreAsync(resource);
            var instance = workspace.Client.InstanceId;
            var init = await workspace.Client.ExecuteGitAsync(new GitRequest("", ["init"], ReadOnly: false, WorkingDirectory: repository));
            Check(init.ExitCode == 0 && workspace.Connected, "History restore did not start its configured server automatically");
            await workspace.OpenAsync(resource);
            Check(await RemoteConnections.RestoreAsync(resource) == workspace && workspace.Client.InstanceId == instance, "Connected history route was not reused");
            Console.WriteLine("PASS Actual history Restore reads saved configuration, starts its server and reuses an already connected route without manual Connect");
            await RemoteConnections.DisconnectAllAsync();
            workspace = await RemoteConnections.RestoreAsync(resource);
            Check(workspace.Connected && workspace.Client.InstanceId == instance, "History restore did not automatically reconnect to its surviving server");
            await workspace.OpenAsync(resource);
            Console.WriteLine("PASS Actual history Restore reconnects after all GUI channels close and opens the same repository/server");
        }
        finally
        {
            try { if (workspace is { Client.IsDisconnected: false }) await workspace.Client.ShutdownAsync(); }
            finally { await RemoteConnections.DisconnectAllAsync(); directories.ConfigDir = previousConfig; }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
