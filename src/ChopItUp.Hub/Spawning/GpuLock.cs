using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

/// <summary>Who holds the GPU: enough for the next holder to stop that holder's render if it died
/// without releasing.</summary>
public sealed record GpuHolder(int HubPid, string Job, string Folder, string? CancelCommand);

/// <summary>One machine-wide GPU slot shared by every hub on the machine, so a scratch hub and the
/// live hub never render at once. It is a lock file opened with <see cref="FileShare.None"/>: the OS
/// closes the handle when the holding process dies, it has no thread affinity, and the file carries
/// the holder record. A clean release empties the record. A record found on acquiring was left by a
/// holder that died, whose GPU service may still be rendering, so the new holder gets it back as
/// <see cref="Lease.Stale"/> and stops that render before starting its own.</summary>
public sealed class GpuLock(string path, TimeSpan? pollInterval = null)
{
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChopItUp", "gpu.lock");

    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);

    public string Path => path;

    public sealed class Lease : IDisposable
    {
        private FileStream? _file;

        internal Lease(FileStream file, GpuHolder? stale) { _file = file; Stale = stale; }

        /// <summary>The record a holder that died left behind, or null.</summary>
        public GpuHolder? Stale { get; }

        /// <summary>Empties the record, then closes the file.</summary>
        public void Dispose()
        {
            var file = Interlocked.Exchange(ref _file, null);
            if (file is null) return;
            try { file.SetLength(0); file.Flush(flushToDisk: true); }
            catch (IOException e) { Console.Error.WriteLine($"gpu lock: record not cleared: {e.Message}"); }
            finally { file.Dispose(); }
        }
    }

    /// <summary>Waits until the slot is free or <paramref name="cancellation"/> fires, which throws
    /// <see cref="OperationCanceledException"/>.</summary>
    public async Task<Lease> AcquireAsync(GpuHolder me, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (TryAcquire(me) is { } lease) return lease;
            await Task.Delay(_poll, cancellation);
        }
    }

    /// <summary>The slot if it is free now, else null.</summary>
    public Lease? TryAcquire(GpuHolder me)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        FileStream file;
        try { file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }   // another holder has it open
        try
        {
            var stale = ReadRecord(file);
            file.SetLength(0);
            JsonSerializer.Serialize(file, me);
            file.Flush(flushToDisk: true);
            return new Lease(file, stale);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static GpuHolder? ReadRecord(FileStream file)
    {
        if (file.Length == 0) return null;
        file.Position = 0;
        try { return JsonSerializer.Deserialize<GpuHolder>(file); }
        catch (JsonException) { return new GpuHolder(0, "?", "", null); }   // torn write: a holder died, nothing to stop
        finally { file.Position = 0; }
    }
}
