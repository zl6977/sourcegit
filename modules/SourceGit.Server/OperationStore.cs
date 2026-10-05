using System.Collections.Concurrent;
using System.Security.Cryptography;
using SourceGit.Contracts;

namespace SourceGit.Server;

internal sealed class OperationStore
{
    internal sealed class Operation
    {
        public required string Fingerprint;
        public required string Id;
        public string HelperToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly object _gate = new();
        private readonly CancellationTokenSource _execution = new();
        private string _state = "accepted";
        private GitResult? _result;
        private string? _error;
        private FileResult? _fileResult;
        private int _interactions;
        private string? _terminationReason;
        private readonly TaskCompletionSource<OperationInfo> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<OperationInfo> Completion => _completion.Task;
        public string State { get { lock (_gate) return _state; } }
        public CancellationToken ExecutionToken { get; }
        public Operation() { ExecutionToken = _execution.Token; }
        public OperationInfo Info() { lock (_gate) return new(Id, _state, _result, _error, _fileResult); }
        internal bool BeginTermination(string reason)
        {
            lock (_gate)
            {
                if (!OperationState.IsActive(_state) || _state == "terminating") return false;
                _state = "terminating"; _error = _terminationReason = reason;
                return true;
            }
        }
        internal bool Complete(OperationInfo result)
        {
            lock (_gate)
            {
                if (!OperationState.IsActive(_state)) return false;
                if (OperationState.IsActive(result.State)) throw new ArgumentException("Completion must be terminal");
                _state = result.State; _result = result.Result; _fileResult = result.FileResult; _error = result.Error;
                _completion.TrySetResult(new(Id, _state, _result, _error, _fileResult));
                return true;
            }
        }
        public void TerminationFailed(Exception error)
        {
            lock (_gate)
            {
                if (!OperationState.IsActive(_state)) return;
                if (_state != "terminating")
                {
                    _state = "terminating";
                    _terminationReason = "Process cleanup could not be confirmed. Partial writes may remain. No rollback was performed.";
                }
                _error = _terminationReason + "; termination/cleanup still pending: " + error.GetType().Name + ": " + (error.Message.Length > 1000 ? error.Message[..1000] : error.Message);
            }
        }
        public bool EnterInteraction()
        {
            lock (_gate)
            {
                if (_state is not ("running" or "waitingInteraction")) return false;
                _interactions++; _state = "waitingInteraction"; return true;
            }
        }
        public void ExitInteraction()
        {
            lock (_gate)
            {
                if (_interactions > 0) _interactions--;
                if (_state == "waitingInteraction" && _interactions == 0) _state = "running";
            }
        }
        public async Task ExecuteAsync(TimeSpan timeout, Func<CancellationToken, Task<OperationInfo>> execute)
        {
            lock (_gate) { if (_state != "accepted") throw new InvalidOperationException("Operation already started"); _state = "running"; }
            var timeoutMessage = $"Execution deadline of {timeout.TotalSeconds:g} seconds exceeded. Partial writes and lock files may remain; refresh the repository before retrying. No rollback was performed.";
            var timer = new Timer(_ =>
            {
                if (!BeginTermination(timeoutMessage) && State != "terminating") return;
                try { _execution.Cancel(); } catch (Exception error) { TerminationFailed(error); }
            }, null, timeout, Timeout.InfiniteTimeSpan);
            await Task.Yield(); // Synchronous filesystem work must not block request admission.
            try
            {
                // Only this runner publishes a terminal result, after execution has
                // reaped its processes and finished its cleanup. The timer never does.
                Complete(await execute(ExecutionToken));
            }
            catch (Exception error)
            {
                Complete(new(Id, "failed", Error: ExecutionToken.IsCancellationRequested ? timeoutMessage : error.Message));
            }
            // Disposing the timer waits for its callback before disposing the CTS.
            await timer.DisposeAsync();
            _execution.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, Operation> _operations = new();
    private readonly object _gate = new();
    private bool _stopping;
    public IEnumerable<Operation> Values => _operations.Values;
    public bool HasActive => _operations.Values.Any(x => OperationState.IsActive(x.State));

    public Operation Register(string key, string id, string fingerprint, out bool fresh)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Client-generated operation ID required");
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Server is shutting down; reconnect before starting another operation");
            if (_operations.TryGetValue(key, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw new InvalidOperationException("Operation ID reused with different parameters");
                fresh = false;
                return existing;
            }
            if (_operations.Count >= 100000) throw new IOException("Operation history capacity reached; restart the server when idle");
            var operation = new Operation { Id = id, Fingerprint = fingerprint };
            _operations[key] = operation;
            fresh = true;
            return operation;
        }
    }

    // Closing admission and checking in-flight operations must be one atomic action.
    public void BeginShutdown()
    {
        lock (_gate)
        {
            if (HasActive)
                throw new InvalidOperationException(_operations.Values.Any(x => x.State == "terminating")
                    ? "Server has active operations: termination/cleanup still pending; query GetOperation before restarting"
                    : "Server has active operations; wait for them to finish before restarting");
            _stopping = true;
        }
    }
    public bool TryGetValue(string key, out Operation operation) => _operations.TryGetValue(key, out operation!);
    public bool TryBeginIdleShutdown()
    {
        lock (_gate) { if (HasActive) return false; _stopping = true; return true; }
    }
    public void Clear()
    {
        lock (_gate)
        {
            if (HasActive) throw new InvalidOperationException("Server has active operations; wait for them to finish");
            _operations.Clear();
        }
    }
}
