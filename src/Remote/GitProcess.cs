using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Remote
{
    public sealed record GitExecution(int ExitCode, byte[] Output, string Error, long Generation = 0);

    // Compatibility adapter for existing streaming parsers. It never starts Windows Git for a remote resource.
    public sealed class GitProcess : IDisposable
    {
        private readonly Process _local = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly MemoryStream _input = new();
        private readonly object _gate = new();
        private Task<GitExecution> _execution;
        private StreamReader _stdout;
        private StreamReader _stderr;
        private StreamWriter _stdin;
        private Task _outputPump;
        private Task _errorPump;
        private bool _receivedLiveError;
        private bool _readErrorLines;
        public ProcessStartInfo StartInfo { get; set; } = new();
        private bool IsRemote => RepositoryPath.IsRemote(StartInfo.WorkingDirectory);
        public event Action<object, GitOutputEventArgs> OutputDataReceived;
        public event Action<object, GitOutputEventArgs> ErrorDataReceived;
        public int ExitCode => IsRemote ? Execute().GetAwaiter().GetResult().ExitCode : _local.ExitCode;
        public long Generation => IsRemote ? Execute().GetAwaiter().GetResult().Generation : 0;
        public bool HasExited => IsRemote ? _execution?.IsCompleted == true : _local.HasExited;
        public StreamReader StandardOutput => IsRemote ? _stdout ??= new StreamReader(new DeferredStream(this, false), Encoding.UTF8) : _local.StandardOutput;
        public StreamReader StandardError => IsRemote ? _stderr ??= new StreamReader(new DeferredStream(this, true), Encoding.UTF8) : _local.StandardError;
        public StreamWriter StandardInput => IsRemote ? _stdin ??= new StreamWriter(_input, new UTF8Encoding(false), 1024, true) { AutoFlush = true } : _local.StandardInput;
        public static GitProcess Start(ProcessStartInfo start) { var process = new GitProcess { StartInfo = start }; process.Start(); return process; }
        public bool Start()
        {
            if (IsRemote) return true;
            _local.StartInfo = StartInfo;
            _local.OutputDataReceived += (_, e) => OutputDataReceived?.Invoke(this, new(e.Data));
            _local.ErrorDataReceived += (_, e) => ErrorDataReceived?.Invoke(this, new(e.Data));
            return _local.Start();
        }
        private Task<GitExecution> Execute()
        {
            lock (_gate)
            {
                if (_execution != null) return _execution;
                _execution = RemoteWorkspace.ExecuteGitAsync(StartInfo, _input.ToArray(), _cancel.Token, line =>
                {
                    if (!_readErrorLines) return;
                    _receivedLiveError = true;
                    ErrorDataReceived?.Invoke(this, new(line));
                });
                return _execution;
            }
        }
        public void BeginOutputReadLine()
        {
            if (!IsRemote) { _local.BeginOutputReadLine(); return; }
            _outputPump = Pump(StandardOutput, line => OutputDataReceived?.Invoke(this, new(line)));
        }
        public void BeginErrorReadLine()
        {
            if (!IsRemote) { _local.BeginErrorReadLine(); return; }
            _readErrorLines = true;
            _errorPump = PumpErrorsAsync();
        }
        private async Task PumpErrorsAsync()
        {
            var result = await Execute().ConfigureAwait(false);
            if (!_receivedLiveError)
            {
                using var reader = new StringReader(result.Error);
                while (reader.ReadLine() is { } line) ErrorDataReceived?.Invoke(this, new(line));
            }
            ErrorDataReceived?.Invoke(this, new(null));
        }
        private static async Task Pump(StreamReader reader, Action<string> emit)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) emit(line);
            emit(null);
        }
        public void WaitForExit() => WaitForExitAsync().GetAwaiter().GetResult();
        public async Task WaitForExitAsync(CancellationToken cancellation = default)
        {
            if (!IsRemote) { await _local.WaitForExitAsync(cancellation).ConfigureAwait(false); return; }
            await Execute().WaitAsync(cancellation).ConfigureAwait(false);
            if (_outputPump != null) await _outputPump.ConfigureAwait(false);
            if (_errorPump != null) await _errorPump.ConfigureAwait(false);
        }
        public void Terminate()
        {
            if (IsRemote) _cancel.Cancel();
            else if (!_local.HasExited) _local.Kill(true);
        }
        public void Dispose() { _local.Dispose(); _cancel.Dispose(); _stdout?.Dispose(); _stderr?.Dispose(); _stdin?.Dispose(); _input.Dispose(); }
        private sealed class DeferredStream(GitProcess owner, bool error) : Stream
        {
            private MemoryStream _stream;
            private async Task<MemoryStream> GetAsync()
            {
                if (_stream != null) return _stream;
                var result = await owner.Execute().ConfigureAwait(false);
                return _stream = new MemoryStream(error ? Encoding.UTF8.GetBytes(result.Error) : result.Output, false);
            }
            public override int Read(byte[] buffer, int offset, int count) => GetAsync().GetAwaiter().GetResult().Read(buffer, offset, count);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => await (await GetAsync().ConfigureAwait(false)).ReadAsync(buffer, token).ConfigureAwait(false);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _stream?.Dispose(); base.Dispose(disposing); }
        }
    }
    public sealed record GitOutputEventArgs(string Data);
}
