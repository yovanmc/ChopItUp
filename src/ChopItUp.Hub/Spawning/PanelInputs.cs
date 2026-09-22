using System.Diagnostics;
using System.Formats.Tar;
using System.Text;

namespace ChopItUp.Hub.Spawning;

/// <summary>Advisory panels read a bounded immutable Git tree, never the live working directory.</summary>
public static class PanelInputs
{
    public const long MaxBytes = 100L * 1024 * 1024;
    public const int MaxEntries = 10_000;

    public static async Task<string> ReadyCommitAsync(string directory, CancellationToken cancellation)
    {
        var top = Encoding.UTF8.GetString(await GitAsync(directory, ["rev-parse", "--show-toplevel"], 32 * 1024, cancellation)).Trim();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Panel needs the bound directory to remain the repository root.");
        var dirty = await GitAsync(directory, ["status", "--porcelain", "--untracked-files=normal"], 1024 * 1024, cancellation);
        if (dirty.Length != 0) throw new InvalidOperationException("Panel needs a clean repository. Commit or move uncommitted files first.");
        var head = Encoding.UTF8.GetString(await GitAsync(directory, ["rev-parse", "--verify", "HEAD^{commit}"], 1024, cancellation)).Trim();
        if (head.Length is not (40 or 64) || head.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Panel needs a repository with a committed HEAD.");
        return head;
    }

    public static async Task PrepareAsync(string? directory, string? commit, string root, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            if (directory is null)
            {
                foreach (var stage in new[] { "first", "second", "synthesis" }) Directory.CreateDirectory(Path.Combine(root, stage));
                return;
            }
            if (commit is null || await ReadyCommitAsync(directory, cancellation) != commit)
                throw new InvalidOperationException("Repository changed since preview. Refresh and send again.");
            // Read object bytes directly: git archive honors export-ignore/export-subst attributes,
            // which would silently omit or rewrite tracked files in an advisory snapshot.
            var archive = await ArchiveTreeAsync(directory, commit, cancellation);
            foreach (var stage in new[] { "first", "second", "synthesis" })
            {
                cancellation.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(archive, writable: false);
                await ExtractAsync(stream, Path.Combine(root, stage), cancellation);
            }
            if (await ReadyCommitAsync(directory, cancellation) != commit)
                throw new InvalidOperationException("Repository changed while preparing the panel. Refresh and send again.");
        }
        catch { Cleanup(root); throw; }
    }

    public static async Task ExtractAsync(Stream archive, string destination, CancellationToken cancellation)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        using var tar = new TarReader(archive, leaveOpen: true);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(copyData: false, cancellation)) is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new InvalidOperationException("Panel snapshot contains a link or unsupported Git entry.");
            var name = entry.Name.TrimEnd('/');
            var parts = name.Split('/');
            if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name)
                || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ') || p.Equals(".git", StringComparison.OrdinalIgnoreCase))
                || parts.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new InvalidOperationException("Panel snapshot contains an unsafe path.");
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(target) || seen.Count > MaxEntries)
                throw new InvalidOperationException("Panel snapshot contains colliding paths or too many entries.");
            bytes = checked(bytes + entry.Length);
            if (bytes > MaxBytes) throw new InvalidOperationException("Panel snapshot exceeds 100 MiB.");
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, cancellation);
            }
        }
    }

    private static async Task<byte[]> ArchiveTreeAsync(string directory, string commit, CancellationToken cancellation)
    {
        var tree = new UTF8Encoding(false, true).GetString(await GitAsync(directory, ["ls-tree", "-rz", "--full-tree", commit], MaxEntries * 4096L, cancellation));
        var files = new List<(string Name, string Object)>();
        foreach (var record in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            var fields = tab > 0 ? record[..tab].Split(' ') : [];
            if (fields.Length != 3 || fields[0] is not ("100644" or "100755") || fields[1] != "blob")
                throw new InvalidOperationException("Panel cannot review repositories containing symlinks, submodules or unsupported Git entries.");
            files.Add((record[(tab + 1)..], fields[2]));
            if (files.Count > MaxEntries) throw new InvalidOperationException("Panel snapshot exceeds 10,000 entries.");
        }
        var blobs = await GitAsync(directory, ["cat-file", "--batch"], MaxBytes + MaxEntries * 128L, cancellation,
            string.Join('\n', files.Select(f => f.Object)) + (files.Count == 0 ? "" : "\n"));
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            int offset = 0;
            long total = 0;
            foreach (var file in files)
            {
                var end = Array.IndexOf(blobs, (byte)'\n', offset);
                if (end < 0) throw new InvalidOperationException("Incomplete Git object header.");
                var fields = Encoding.ASCII.GetString(blobs, offset, end - offset).Split(' ');
                if (fields.Length != 3 || fields[0] != file.Object || fields[1] != "blob" || !int.TryParse(fields[2], out var size)
                    || size < 0 || (total += size) > MaxBytes || end + 1L + size >= blobs.Length)
                    throw new InvalidOperationException("Invalid or oversized Git snapshot object.");
                offset = end + 1;
                if (blobs[offset + size] != '\n') throw new InvalidOperationException("Invalid Git object terminator.");
                using var content = new MemoryStream(blobs, offset, size, writable: false);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Name) { DataStream = content });
                offset += size + 1;
            }
            if (offset != blobs.Length) throw new InvalidOperationException("Unexpected Git object output.");
        }
        return archive.ToArray();
    }

    public static void Cleanup(string root)
    {
        // Only callers' newly allocated private panel roots reach this method.
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("panel snapshot cleanup failed: " + e.GetType().Name); }
    }

    private static async Task<byte[]> GitAsync(string directory, string[] arguments, long limit, CancellationToken cancellation, string? input = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, StandardInputEncoding = input is null ? null : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git for panel preparation.");
        var errors = ReadGitOutputAsync(process.StandardError.BaseStream, 64 * 1024, timeout);
        var output = ReadGitOutputAsync(process.StandardOutput.BaseStream, limit, timeout);
        var write = input is null ? Task.CompletedTask : Task.Run(async () =>
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
        }, timeout.Token);
        try
        {
            await Task.WhenAll(output, errors, write, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new InvalidOperationException("Panel Git preflight failed. Ensure the directory is a clean repository with a committed HEAD.");
            return await output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    internal static async Task<byte[]> ReadGitOutputAsync(Stream stream, long limit, CancellationTokenSource cancellation)
    {
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation.Token)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidOperationException("Panel Git output exceeds its size limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch { cancellation.Cancel(); throw; }
    }
}
