namespace ChopItUp.Hub.Hosting;

/// <summary>Process-lifetime exclusive lock on the data dir. Two hubs on one data dir would share
/// the DB but not the in-memory <c>MessageSignal</c>, so <c>wait_for_message</c> on one would never
/// wake for posts on the other. Held until the host stops.</summary>
public sealed class HubLock : IDisposable
{
    public const string FileName = "hub.lock";
    private readonly FileStream _stream;

    private HubLock(FileStream stream) => _stream = stream;

    public static HubLock Acquire(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        try
        {
            return new HubLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Another hub already owns '{dataDir}' ({FileName} is locked). Run one hub per data directory.", e);
        }
    }

    public void Dispose() => _stream.Dispose();
}
