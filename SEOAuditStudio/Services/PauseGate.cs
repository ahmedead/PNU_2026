namespace SEOAuditStudio.Services
{
    /// <summary>
    /// Pause / resume switch for a running audit. The auditor waits on it before starting each request;
    /// requests already in flight finish normally, so pausing never loses or corrupts a result.
    /// </summary>
    public sealed class PauseGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource _resumed = CreateOpen();

        public bool IsPaused { get; private set; }

        /// <summary>Raised (on a worker thread) when the auditor actually stops sending requests / starts again.</summary>
        public event Action<bool>? StateChanged;

        public void Pause()
        {
            lock (_lock)
            {
                if (IsPaused) return;
                IsPaused = true;
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            StateChanged?.Invoke(true);
        }

        public void Resume()
        {
            TaskCompletionSource tcs;
            lock (_lock)
            {
                if (!IsPaused) return;
                IsPaused = false;
                tcs = _resumed;
            }
            tcs.TrySetResult();
            StateChanged?.Invoke(false);
        }

        /// <summary>Returns at once when running; otherwise waits until Resume (or cancellation, e.g. Stop).</summary>
        public Task WaitIfPausedAsync(CancellationToken ct)
        {
            Task wait;
            lock (_lock) wait = _resumed.Task;
            return wait.IsCompleted ? Task.CompletedTask : wait.WaitAsync(ct);
        }

        private static TaskCompletionSource CreateOpen()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }
    }
}
