namespace ChopItUp.Core.Messaging;

/// <summary>In-process change notification per room. Readers call <see cref="Changed"/> BEFORE
/// checking the store, then await the returned task; <see cref="Publish"/> completes the current
/// generation and starts a new one, so a post between "check" and "await" is never missed.</summary>
public sealed class MessageSignal
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _generations = new(StringComparer.Ordinal);

    public Task Changed(string roomId)
    {
        lock (_gate)
        {
            if (!_generations.TryGetValue(roomId, out var tcs))
            {
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _generations[roomId] = tcs;
            }
            return tcs.Task;
        }
    }

    public void Publish(string roomId)
    {
        TaskCompletionSource? done;
        lock (_gate)
        {
            _generations.Remove(roomId, out done);
        }
        done?.TrySetResult();
    }
}
