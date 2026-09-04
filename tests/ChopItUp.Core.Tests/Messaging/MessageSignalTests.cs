using ChopItUp.Core.Messaging;

namespace ChopItUp.Core.Tests.Messaging;

public sealed class MessageSignalTests
{
    [Fact]
    public async Task Changed_completes_when_room_is_published()
    {
        var signal = new MessageSignal();
        var changed = signal.Changed("general");
        Assert.False(changed.IsCompleted);
        signal.Publish("general");
        await changed.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Publish_only_wakes_its_own_room()
    {
        var signal = new MessageSignal();
        var general = signal.Changed("general");
        var other = signal.Changed("other");
        signal.Publish("other");
        Assert.False(general.IsCompleted);
        Assert.True(other.IsCompleted);
    }

    [Fact]
    public void Generation_taken_before_publish_observes_it_even_if_awaited_later()
    {
        // The reader takes the generation FIRST, then checks the store, then awaits. A publish that
        // lands between check and await must still complete the generation it took.
        var signal = new MessageSignal();
        var gen = signal.Changed("general");
        signal.Publish("general");
        Assert.True(gen.IsCompleted);
        Assert.False(signal.Changed("general").IsCompleted);
    }
}
