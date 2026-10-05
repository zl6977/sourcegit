using System.Net.Sockets;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class ProbeContractChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "record.json");
        var record = new ServerRecord(12345, "fixture-only", Guid.NewGuid().ToString("N"), Environment.ProcessId);
        var json = JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord);
        await File.WriteAllTextAsync(path, json);
        var executable = typeof(ProbeContractChecks).Assembly.Location;
        foreach (var exception in new Exception[] { new IOException("transient"), new SocketException(), new TimeoutException("probe deadline") })
        {
            var probes = 0;
            using var launcher = new LocalServerLauncher(executable, path, probe: (_, _) =>
            {
                if (++probes == 1) throw exception;
                return Task.FromResult(new HandshakeResult(record.InstanceId, Protocol.Version, Protocol.Build, []));
            });
            if (await launcher.StartAsync(NoUpgrade, default) != record || probes != 2 || await File.ReadAllTextAsync(path) != json)
                throw new Exception("Transient probe error replaced the instance or failed to retry");
        }
        Console.WriteLine("PASS IOException, SocketException and independent TimeoutException retry without replacing a live record");
        foreach (var exception in new Exception[] { new ArgumentException("invalid probe"), new InvalidOperationException("probe bug") })
        {
            var probes = 0;
            using var launcher = new LocalServerLauncher(executable, path, probe: (_, _) => { probes++; throw exception; });
            try { await launcher.StartAsync(NoUpgrade, default); throw new Exception("Nontransient failure succeeded"); }
            catch (Exception actual) when (ReferenceEquals(actual, exception)) { }
            if (probes != 1 || await File.ReadAllTextAsync(path) != json) throw new Exception("Nontransient error was retried/masked");
        }
        using var canceled = new CancellationTokenSource();
        var canceledProbes = 0;
        using (var launcher = new LocalServerLauncher(executable, path, probe: (_, _) => { canceledProbes++; canceled.Cancel(); throw new OperationCanceledException(canceled.Token); }))
        {
            try { await launcher.StartAsync(NoUpgrade, canceled.Token); throw new Exception("Canceled probe succeeded"); } catch (OperationCanceledException) { }
            if (canceledProbes != 1) throw new Exception("Canceled probe retried");
        }
        var recordProbes = 0;
        using (var launcher = new LocalServerLauncher(executable, path, probe: async (_, _) =>
        {
            recordProbes++;
            await File.WriteAllTextAsync(path, "malformed");
            return new HandshakeResult(record.InstanceId, Protocol.Version, Protocol.Build, []);
        }))
        {
            try { await launcher.StartAsync(NoUpgrade, default); throw new Exception("Malformed discovery record succeeded"); } catch (InvalidDataException) { }
            if (recordProbes != 1 || await File.ReadAllTextAsync(path) != "malformed") throw new Exception("Record read failure was treated as a network retry");
        }
        Console.WriteLine("PASS Nontransient probe and record errors preserve their cause; caller cancellation has no subsequent probe");
    }
    private static Task NoUpgrade(ServerRecord _, CancellationToken cancellation) => throw new Exception("Unexpected upgrade");
}
