using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SourceGit.Models
{
    public interface IAvatarHost
    {
        void OnAvatarResourceChanged(string email, Bitmap image);
    }

    public partial class AvatarManager
    {
        public static AvatarManager Instance
        {
            get
            {
                return _instance ??= new AvatarManager();
            }
        }

        private static AvatarManager _instance = null;

        [GeneratedRegex(@"^(?:(\d+)\+)?(.+?)@.+\.github\.com$")]
        private static partial Regex REG_GITHUB_USER_EMAIL();

        private readonly Lock _synclock = new();
        private string _storePath;
        private List<IAvatarHost> _avatars = new List<IAvatarHost>();
        private Cache _cache = new Cache(256);
        private readonly Dictionary<string, long> _requesting = new();
        private HashSet<string> _defaultAvatars = new HashSet<string>();
        private const int RequestLimit = 1024;
        private readonly Channel<(string Email, long Generation)> _requests = Channel.CreateBounded<(string, long)>(new BoundedChannelOptions(RequestLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Func<string, string> _downloadUrl;
        private long _generation;
        private bool _stopped, _capacityReported;
        private Task _consumer;
        internal Action PublicationQueued { get; set; }

        public AvatarManager() { }

        internal AvatarManager(string storePath, Func<string, string> downloadUrl)
        {
            _storePath = storePath;
            _downloadUrl = downloadUrl;
        }

        public void Start()
        {
            lock (_synclock)
            {
                if (_consumer != null || _stopped) return;
                _storePath ??= Path.Combine(Native.OS.BasicDirectories.CacheDir, "avatars");
                Directory.CreateDirectory(_storePath);
                if (_downloadUrl == null)
                {
                    LoadDefaultAvatar("noreply@github.com", "github.png");
                    LoadDefaultAvatar("unrealbot@epicgames.com", "unreal.png");
                }
                _consumer = Task.Run(ConsumeAsync);
            }
        }

        private async Task ConsumeAsync()
        {
            using var client = new HttpClient();
            try
            {
                await foreach (var request in _requests.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    var email = request.Email;
                    lock (_synclock)
                        if (_stopped || !_requesting.TryGetValue(email, out var current) || current != request.Generation) continue;
                    var md5 = GetEmailHash(email);
                    var url = $"https://www.gravatar.com/avatar/{md5}?d=404";
                    var match = REG_GITHUB_USER_EMAIL().Match(email);
                    if (match.Success)
                    {
                        var user = match.Groups[2].Value;
                        if (user.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)) user = user.Substring(0, user.Length - 5);
                        url = $"https://avatars.githubusercontent.com/{user}";
                    }
                    url = _downloadUrl?.Invoke(email) ?? url;
                    var localFile = Path.Combine(_storePath, md5);
                    var temporary = localFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    Bitmap img = null;
                    try
                    {
                        try
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(2));
                            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                                using (var writer = File.Create(temporary))
                                {
                                    var buffer = new byte[8192]; var total = 0;
                                    int count;
                                    while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                                    {
                                        total += count;
                                        if (total > 8 * 1024 * 1024) throw new IOException("Avatar exceeds 8 MiB download limit");
                                        await writer.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                                    }
                                }
                                using var reader = File.OpenRead(temporary);
                                img = Bitmap.DecodeToWidth(reader, 128);
                            }
                        }
                        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { throw; }
                        catch { } // Preserve negative caching after a failed download.

                        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Dispatcher.UIThread.Post(() =>
                        {
                            var candidate = Interlocked.Exchange(ref img, null);
                            try
                            {
                                lock (_synclock)
                                {
                                    if (_stopped || !_requesting.TryGetValue(email, out var generation) || generation != request.Generation) return;
                                    _requesting.Remove(email);
                                    if (candidate != null) File.Move(temporary, localFile, true);
                                }
                                var image = candidate;
                                _cache.AddOrUpdate(email, image);
                                candidate = null; // Cache and UI now own this bitmap.
                                NotifyResourceChanged(email, image);
                            }
                            catch (Exception error) { System.Diagnostics.Trace.WriteLine("Avatar publication failed: " + error.Message); }
                            finally { candidate?.Dispose(); published.TrySetResult(); }
                        });
                        PublicationQueued?.Invoke();
                        // At most one UI publication is queued. Shutdown need not wait for the UI to drain it.
                        await published.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref img, null)?.Dispose();
                        try { File.Delete(temporary); } catch (IOException) { }
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }

        public async Task StopAsync()
        {
            Task consumer;
            lock (_synclock)
            {
                _stopped = true;
                _requests.Writer.TryComplete();
                consumer = _consumer;
            }
            _shutdown.Cancel();
            try { if (consumer != null) await consumer.ConfigureAwait(false); }
            finally
            {
                lock (_synclock)
                {
                    _requesting.Clear();
                    while (_requests.Reader.TryRead(out _)) { }
                }
            }
        }

        public void Subscribe(IAvatarHost host)
        {
            _avatars.Add(host);
        }

        public void Unsubscribe(IAvatarHost host)
        {
            _avatars.Remove(host);
        }

        public Bitmap Request(string email, bool forceRefetch)
        {
            lock (_synclock) if (_stopped) return null;
            if (forceRefetch)
            {
                if (_defaultAvatars.Contains(email))
                    return null;

                _cache.Remove(email);
                lock (_synclock) _requesting.Remove(email);

                var localFile = SourceGit.Remote.RepositoryPath.Combine(_storePath, GetEmailHash(email));
                if (SourceGit.Remote.RepositoryFile.Exists(localFile))
                    SourceGit.Remote.RepositoryFile.Delete(localFile);

                NotifyResourceChanged(email, null);
            }
            else
            {
                if (_cache.TryGet(email, out var value))
                    return value;

                var localFile = SourceGit.Remote.RepositoryPath.Combine(_storePath, GetEmailHash(email));
                if (SourceGit.Remote.RepositoryFile.Exists(localFile))
                {
                    try
                    {
                        using (var stream = SourceGit.Remote.RepositoryFile.OpenRead(localFile))
                        {
                            var img = Bitmap.DecodeToWidth(stream, 128);
                            _cache.AddOrUpdate(email, img);
                            return img;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            lock (_synclock)
            {
                if (_stopped || _requesting.ContainsKey(email)) return null;
                if (_requesting.Count >= RequestLimit || !_requests.Writer.TryWrite((email, ++_generation)))
                {
                    if (!_capacityReported) System.Diagnostics.Trace.WriteLine("Avatar request capacity reached (1024); retry on a later display request.");
                    _capacityReported = true;
                    return null;
                }
                _requesting.Add(email, _generation);
                _capacityReported = false;
            }

            return null;
        }

        public void SetFromLocal(string email, string file)
        {
            lock (_synclock) if (_stopped) return;
            try
            {
                Bitmap image;

                using (var stream = SourceGit.Remote.RepositoryFile.OpenRead(file))
                {
                    image = Bitmap.DecodeToWidth(stream, 128);
                }

                _cache.AddOrUpdate(email, image);

                lock (_synclock)
                {
                    _requesting.Remove(email);
                }

                var store = SourceGit.Remote.RepositoryPath.Combine(_storePath, GetEmailHash(email));
                SourceGit.Remote.RepositoryFile.Copy(file, store, true);
                NotifyResourceChanged(email, image);
            }
            catch
            {
                // ignore
            }
        }

        private void LoadDefaultAvatar(string key, string img)
        {
            var icon = AssetLoader.Open(new Uri($"avares://SourceGit/Resources/Images/{img}", UriKind.RelativeOrAbsolute));
            _cache.AddOrUpdate(key, new Bitmap(icon));
            _defaultAvatars.Add(key);
        }

        private string GetEmailHash(string email)
        {
            var lowered = email.ToLower(CultureInfo.CurrentCulture).Trim();
            var hash = MD5.HashData(Encoding.Default.GetBytes(lowered));
            return Convert.ToHexStringLower(hash);
        }

        private void NotifyResourceChanged(string email, Bitmap image)
        {
            foreach (var avatar in _avatars)
                avatar.OnAvatarResourceChanged(email, image);
        }

        private class Cache
        {
            private readonly int _capacity;
            private readonly LinkedList<Item> _imgList;
            private readonly Dictionary<string, LinkedListNode<Item>> _key2img;

            private class Item
            {
                public string Key { get; }
                public Bitmap Image { get; set; }

                public Item(string key, Bitmap image)
                {
                    Key = key;
                    Image = image;
                }
            }

            public Cache(int capacity)
            {
                _capacity = capacity;
                _key2img = new Dictionary<string, LinkedListNode<Item>>();
                _imgList = new LinkedList<Item>();
            }

            public bool TryGet(string key, out Bitmap bitmap)
            {
                if (_key2img.TryGetValue(key, out var node))
                {
                    _imgList.Remove(node);
                    _imgList.AddFirst(node);
                    bitmap = node.Value.Image;
                    return true;
                }

                bitmap = null;
                return false;
            }

            public void AddOrUpdate(string key, Bitmap bitmap)
            {
                if (_key2img.TryGetValue(key, out var node))
                {
                    _imgList.Remove(node);
                    _imgList.AddFirst(node);
                    node.Value.Image = bitmap;
                    return;
                }

                if (_key2img.Count >= _capacity)
                {
                    var lastNode = _imgList.Last;
                    _imgList.RemoveLast();
                    _key2img.Remove(lastNode.Value.Key);
                }

                var newNode = new LinkedListNode<Item>(new Item(key, bitmap));
                _imgList.AddFirst(newNode);
                _key2img[key] = newNode;
            }

            public void Remove(string key)
            {
                if (_key2img.TryGetValue(key, out var node))
                {
                    _imgList.Remove(node);
                    _key2img.Remove(key);
                }
            }
        }
    }
}
