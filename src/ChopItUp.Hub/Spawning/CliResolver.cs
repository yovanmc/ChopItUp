namespace ChopItUp.Hub.Spawning;

/// <summary>How to start a CLI: the executable to hand to CreateProcess and the arguments that must
/// come before the caller's own. For a real <c>.exe</c> that is the exe and nothing; for a
/// <c>.cmd</c>/<c>.bat</c> shim it is <c>cmd.exe /d /c &lt;shim&gt;</c>, because a direct process
/// create of a shim finds nothing to execute and fails silently (LESSONS, M5 cli-shims — this bit
/// the project twice). <see cref="ResolvedPath"/> is what was found, for logs.</summary>
public sealed record ResolvedCli(string FileName, IReadOnlyList<string> LeadingArguments, string ResolvedPath);

public static class CliResolver
{
    private static readonly string[] ShimExtensions = [".cmd", ".bat"];

    /// <summary>Looks along <paramref name="pathVariable"/> (default: this process's PATH) for
    /// <c>name.exe</c> first — anywhere on the path — and only then for a shim. Both Claude Code
    /// (<c>claude.exe</c>) and Codex (<c>codex.cmd</c>) live in <c>%USERPROFILE%\.local\bin</c> on
    /// the owner's machine; the order matters where an npm shim and a native install coexist.</summary>
    public static ResolvedCli Resolve(string name, string? pathVariable = null)
    {
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .ToArray();

        foreach (var dir in dirs)
        {
            var exe = Path.Combine(dir, name + ".exe");
            if (File.Exists(exe)) return new ResolvedCli(exe, [], exe);
        }
        foreach (var dir in dirs)
            foreach (var ext in ShimExtensions)
            {
                var shim = Path.Combine(dir, name + ext);
                if (File.Exists(shim))
                    return new ResolvedCli(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", shim], shim);
            }

        throw new FileNotFoundException(
            $"'{name}' was not found on PATH as {name}.exe, {name}.cmd or {name}.bat. Install it, or sign in to the host that provides it, and restart the hub.");
    }
}
