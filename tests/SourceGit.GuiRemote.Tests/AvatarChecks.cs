using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SourceGit.Models;

internal static class AvatarChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    internal static void Run()
    {
        AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var root = Path.GetFullPath(Path.Combine("artifacts", "avatar-events", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var local = Image(0xFF0000FF); var remote = Image(0xFF00FF00);
        var localPath = Path.Combine(root, "local.png"); File.WriteAllBytes(localPath, local);

        using (var http = new HttpFixture())
        {
            var store = Path.Combine(root, "capacity");
            var manager = new AvatarManager(store, http.Url); manager.Start(); manager.Start();
            manager.Request("held@example.invalid", false);
            using var request = Pump(http.AcceptAsync());
            Check(Pending(manager) == 1, "Held network request was not owned");
            for (var index = 0; index < 20; index++) manager.Request("held@example.invalid", false);
            Check(Pending(manager) == 1, "Duplicate displays queued multiple requests");
            for (var index = 0; index < 1100; index++) manager.Request($"queued{index}@example.invalid", false);
            Check(Pending(manager) == 1024, "Avatar admission did not bound queued plus active work");
            // Block the UI: stopping an actual held HTTP request must not depend on dispatcher work.
            Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(manager.StopAsync))).WaitAsync(Limit).GetAwaiter().GetResult();
            Check(Pending(manager) == 0 && Queued(manager) == 0 && Directory.GetFiles(store).Length == 0, "Concurrent shutdown leaked queued references or temporary files");
            manager.Start(); manager.Request("after-stop@example.invalid", true);
            Check(Pending(manager) == 0, "Stopped manager restarted");
            Console.WriteLine("PASS Actual held HTTP avatar download deduplicates, bounds 1024 active/queued requests, and concurrent Stop drains ownership without waiting on UI or restarting");
        }

        using (var http = new HttpFixture())
        {
            var store = Path.Combine(root, "local");
            var manager = new AvatarManager(store, http.Url); var host = new Host(); manager.Subscribe(host); manager.Start();
            try
            {
                const string email = "local@example.invalid";
                manager.Request(email, false); using var old = Pump(http.AcceptAsync());
                manager.SetFromLocal(email, localPath);
                var selected = host.Image;
                Check(selected != null && host.Changes == 1, "Local avatar did not publish immediately");
                old.Respond(remote);
                manager.Request("barrier@example.invalid", false);
                using var next = Pump(http.AcceptAsync()); // Consumer passed old publication before starting this request.
                Check(host.Changes == 1 && ReferenceEquals(manager.Request(email, false), selected) && File.ReadAllBytes(StoreFile(store, email)).SequenceEqual(local),
                    "Old network response overwrote a newer local avatar or its disk cache");
                Console.WriteLine("PASS Actual HTTP response after SetFromLocal cannot replace the selected UI bitmap or overwrite its cache file");
            }
            finally { manager.StopAsync().WaitAsync(Limit).GetAwaiter().GetResult(); manager.Unsubscribe(host); Dispatcher.UIThread.RunJobs(); }
        }

        using (var http = new HttpFixture())
        {
            var store = Path.Combine(root, "force");
            var manager = new AvatarManager(store, http.Url); var host = new Host(); manager.Subscribe(host); manager.Start();
            try
            {
                const string email = "force@example.invalid";
                manager.Request(email, false); using var old = Pump(http.AcceptAsync());
                manager.Request(email, true); old.Respond(local);
                using var replacement = Pump(http.AcceptAsync());
                Check(host.Changes == 1 && host.Image == null && !File.Exists(StoreFile(store, email)), "Old generation published after force-refetch");
                replacement.Respond(remote); Pump(host.Published.Task);
                Check(host.Changes == 2 && manager.Request(email, false) != null && Pending(manager) == 0 && File.ReadAllBytes(StoreFile(store, email)).SequenceEqual(remote),
                    "New generation did not publish exactly once and own its cache");
                Console.WriteLine("PASS Force-refetch invalidates the in-flight generation; only the replacement HTTP response publishes and owns the cache");
            }
            finally { manager.StopAsync().WaitAsync(Limit).GetAwaiter().GetResult(); manager.Unsubscribe(host); Dispatcher.UIThread.RunJobs(); }
        }

        using (var http = new HttpFixture())
        {
            var store = Path.Combine(root, "publication"); var host = new Host();
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var manager = new AvatarManager(store, http.Url) { PublicationQueued = () => queued.TrySetResult() };
            manager.Subscribe(host); manager.Start(); manager.Request("pending@example.invalid", false);
            using var request = Pump(http.AcceptAsync()); request.Respond(remote);
            // Native HTTP and bitmap decode finish while the dispatcher is deliberately not pumped.
            queued.Task.WaitAsync(Limit).GetAwaiter().GetResult();
            Check(Directory.GetFiles(store, "*.tmp").Length == 1 && host.Changes == 0, "Publication barrier was not held");
            Task.Run(manager.StopAsync).WaitAsync(Limit).GetAwaiter().GetResult();
            Check(Directory.GetFiles(store).Length == 0 && Pending(manager) == 0, "Shutdown before publication leaked bitmap/file ownership");
            Dispatcher.UIThread.RunJobs();
            Check(host.Changes == 0 && manager.Request("pending@example.invalid", false) == null, "Late UI callback published after shutdown");
            manager.Unsubscribe(host);
            Console.WriteLine("PASS Shutdown with a decoded avatar queued for UI reclaims its temporary file/bitmap without UI progress; late publication is ignored");
        }

        using (var http = new HttpFixture())
        {
            var store = Path.Combine(root, "oversized"); var host = new Host();
            var manager = new AvatarManager(store, http.Url); manager.Subscribe(host); manager.Start();
            try
            {
                const string email = "oversized@example.invalid";
                manager.Request(email, false); using var request = Pump(http.AcceptAsync());
                try { request.Respond(new byte[9 * 1024 * 1024]); } catch (IOException) { } // Receiver rejects and closes the oversized response.
                Pump(host.Changed.Task);
                Check(host.Image == null && host.Changes == 1 && manager.Request(email, false) == null && Pending(manager) == 0 && !File.Exists(StoreFile(store, email)),
                    "Oversized HTTP response became a disk/bitmap cache or bypassed failed-download deduplication");
            }
            finally { manager.StopAsync().WaitAsync(Limit).GetAwaiter().GetResult(); manager.Unsubscribe(host); Dispatcher.UIThread.RunJobs(); }
            Check(Directory.GetFiles(store).Length == 0, "Oversized response left a partial cache file");
            Console.WriteLine("PASS Actual oversized HTTP avatar is bounded at 8 MiB, negatively cached without another request and leaves no partial file");
        }

        using (var http = new HttpFixture())
        {
            for (var index = 0; index < 25; index++)
            {
                var manager = new AvatarManager(Path.Combine(root, "lifecycle", index.ToString()), http.Url);
                Task.WhenAll(Task.Run(manager.Start), Task.Run(manager.StopAsync)).WaitAsync(Limit).GetAwaiter().GetResult();
                manager.StopAsync().WaitAsync(Limit).GetAwaiter().GetResult();
                manager.Start(); manager.Request("late@example.invalid", false);
                Check(Pending(manager) == 0 && Queued(manager) == 0, "Concurrent Start/Stop restarted or retained a request");
            }
            var stopped = new AvatarManager(Path.Combine(root, "never-started"), http.Url);
            stopped.StopAsync().GetAwaiter().GetResult(); stopped.Start(); stopped.Request("never@example.invalid", false);
            Check(!Directory.Exists(Path.Combine(root, "never-started")), "Stop before Start still created an avatar store/consumer");
            Console.WriteLine("PASS Twenty-five concurrent avatar Start/Stop races and stop-before-start never resurrect consumers or retain queued work");
        }
    }

    private static int Pending(AvatarManager manager) => ((Dictionary<string, long>)typeof(AvatarManager).GetField("_requesting", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!).Count;
    private static int Queued(AvatarManager manager) => ((System.Threading.Channels.Channel<(string, long)>)typeof(AvatarManager).GetField("_requests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!).Reader.Count;
    private static string StoreFile(string store, string email) => Path.Combine(store, Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(email))));
    private static byte[] Image(uint color)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var pixels = bitmap.Lock())
            for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) Marshal.WriteInt32(pixels.Address, y * pixels.RowBytes + x * 4, unchecked((int)color));
        using var stream = new MemoryStream(); bitmap.Save(stream); return stream.ToArray();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < Limit) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Avatar test exceeded its deadline"); task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Host : IAvatarHost
    {
        internal int Changes; internal Bitmap? Image;
        internal readonly TaskCompletionSource Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnAvatarResourceChanged(string email, Bitmap image) { Changes++; Image = image; Changed.TrySetResult(); if (image != null) Published.TrySetResult(); }
    }
    private sealed class HttpFixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        internal HttpFixture() { _listener.Start(); }
        internal string Url(string email) => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/" + Uri.EscapeDataString(email);
        internal async Task<Request> AcceptAsync()
        {
            var client = await _listener.AcceptTcpClientAsync().WaitAsync(Limit).ConfigureAwait(false);
            try
            {
                var reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 1024, true);
                while (await reader.ReadLineAsync().WaitAsync(Limit).ConfigureAwait(false) is { Length: > 0 }) { }
                reader.Dispose(); return new Request(client);
            }
            catch { client.Dispose(); throw; }
        }
        public void Dispose() => _listener.Stop();
    }
    private sealed class Request(TcpClient client) : IDisposable
    {
        internal void Respond(byte[] png)
        {
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n");
            client.GetStream().Write(header); client.GetStream().Write(png); client.Dispose();
        }
        public void Dispose() => client.Dispose();
    }
}
