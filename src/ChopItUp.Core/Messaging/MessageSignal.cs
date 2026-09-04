using ChopItUp.Core.Model;

namespace ChopItUp.Core.Messaging;

/// <summary>In-process change notification per room. Readers call <see cref="Changed"/> BEFORE
/// checking the store, then await the returned task; <see cref="Publish(string)"/> completes the
/// current generation and starts a new one, so a post between "check" and "await" is never missed.
///
/// This is also the ONE place a post announces itself outward: <see cref="Posted"/> fires whenever
/// <see cref="Publish(string,Message)"/> is called, carrying the stored message. The SignalR bridge
/// (Hub/Realtime/RoomHub.cs) subscribes to it, so every posting path — MCP tool or the M3 web API —
/// wakes <c>wait_for_message</c> and reaches connected browsers from this single call, never two
/// that could drift.</summary>
public sealed class MessageSignal
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _generations = new(StringComparer.Ordinal);

    /// <summary>Fires with the stored message every time <see cref="Publish(string,Message)"/> runs.
    /// Never fired by the message-less overload (used only where no new message exists to announce,
    /// e.g. a dedup that adds nothing).</summary>
    public event Action<Message>? Posted;

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

    /// <summary>Wakes any waiter for <paramref name="roomId"/> but announces nothing outward — for
    /// callers with no message to broadcast (kept for that case and for tests of the wakeup alone).</summary>
    public void Publish(string roomId) => PublishCore(roomId, null);

    /// <summary>Wakes any waiter for <paramref name="roomId"/> AND raises <see cref="Posted"/> with
    /// <paramref name="message"/> so the SignalR bridge can broadcast it. Every real post should call
    /// this overload, not the message-less one, so the room actually announces what changed.</summary>
    public void Publish(string roomId, Message message) => PublishCore(roomId, message);

    private void PublishCore(string roomId, Message? message)
    {
        TaskCompletionSource? done;
        lock (_gate)
        {
            _generations.Remove(roomId, out done);
        }
        done?.TrySetResult();
        if (message is not null) Posted?.Invoke(message);
    }
}
