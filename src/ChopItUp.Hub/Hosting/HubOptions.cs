namespace ChopItUp.Hub.Hosting;

public enum HubCommand { Serve, RotateToken, PrintConfig, ImportSkill, SetClasses, ExportMemory }

/// <summary>Resolved startup options. Precedence: CLI args, then environment, then defaults.
/// Default data dir is <c>data\</c> beside the executable (release layout); dev and tests pass
/// an explicit directory. Port 0 = ephemeral (tests). A non-Serve command runs against the data
/// dir and exits: it binds no port and takes no hub lock, so it works while a hub is running.
/// <paramref name="WebRoot"/> is the built web client (M3 D3): null means <c>wwwroot\</c> beside the
/// executable, which is where the csproj's npm step lands it. It is not a CLI flag — the only reason
/// it is settable is so tests can point at a fabricated client without writing into the test output.
/// <paramref name="RoomsRoot"/> is where hub-created room directories go (M9).
/// <paramref name="ImportSkillPath"/> is the source directory for <c>--import-skill</c> (row 11 task
/// 5), already rooted with <see cref="Path.GetFullPath(string)"/> at parse time — the same M5 lesson
/// <c>--data</c> follows — so a relative path resolves against THIS process's working directory and
/// not against anything a later hub start does. <paramref name="Force"/> is <c>--force</c>: replace an
/// already-imported skill of the same name instead of refusing. <paramref name="OverlayPath"/> is
/// <c>--overlay &lt;odir&gt;</c> (row 20 task 1), only valid alongside <c>--import-skill</c>, rooted the
/// same way at parse time. <paramref name="SetClassesSpec"/> is the raw <c>&lt;id&gt;=&lt;classes&gt;</c>
/// text of <c>--set-classes</c> (row 20 task 2); the split on <c>=</c> and the class normalization
/// happen in <see cref="ChopItUp.Hub.Hosting.HostCommands"/>, not here — only the "has an <c>=</c>"
/// shape is a parse-time refusal. <paramref name="ExportMemoryPath"/> is the target directory for
/// <c>--export-memory &lt;dir&gt;</c> (row 24 task 4), rooted the same way <c>--import-skill</c> is
/// AND <see cref="Path.TrimEndingDirectorySeparator(string)"/>-ed, because <see cref="Path.GetFullPath(string)"/>
/// alone preserves a trailing separator, which would put the writer's staging directory inside the
/// target. A drive root is refused outright. <paramref name="AcceptNewSource"/> is
/// <c>--accept-new-source</c>, only valid alongside <c>--export-memory</c> (D9): it proceeds past a
/// target whose manifest names a different store root, which <c>--force</c> must never do.
/// <paramref name="OwnerPeerCheck"/> (row 29, D3) is <c>--owner-peer-check off</c> (or
/// <c>CHOPITUP_OWNER_PEER_CHECK=off</c>), default true: false disables the check that refuses an
/// owner-class bearer presented from inside a spawn, the recovery for a lookup failure that would
/// otherwise lock the owner out of every write. A missing value throws, same as <c>--rotate-token</c>'s.</summary>
public sealed record HubOptions(string DataDir, int Port, HubCommand Command = HubCommand.Serve, string? RotateParticipant = null, string? WebRoot = null, string? RoomsRoot = null, string? ImportSkillPath = null, bool Force = false, string? OverlayPath = null, string? SetClassesSpec = null, string? ExportMemoryPath = null, bool AcceptNewSource = false, bool OwnerPeerCheck = true)
{
    public const int DefaultPort = 8790;

    /// <summary>Where hub-created room directories go (M9 decision 2): `--rooms-root`, then
    /// `CHOPITUP_ROOMS`, then `%USERPROFILE%\ChopItUp\rooms` — a folder inside the profile, which D12
    /// allows, because the install dir is under C:\Self Apps and the data dir holds the tokens.</summary>
    public string RoomsRootPath => Path.GetFullPath(string.IsNullOrWhiteSpace(RoomsRoot) ? DefaultRoomsRoot() : RoomsRoot);

    public static string DefaultRoomsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChopItUp", "rooms");

    public static HubOptions Parse(string[] args, Func<string, string?> getEnv)
    {
        string? data = null;
        string? port = null;
        var command = HubCommand.Serve;
        string? rotate = null;
        string? rooms = null;
        string? importSkillPath = null;
        var force = false;
        string? overlayPath = null;
        string? setClassesSpec = null;
        string? exportMemoryPath = null;
        var acceptNewSource = false;
        string? ownerPeerCheck = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--data requires a value.");
                data = args[++i];
            }
            else if (args[i] == "--port")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--port requires a value.");
                port = args[++i];
            }
            else if (args[i] == "--rotate-token")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--rotate-token requires a participant name.");
                command = HubCommand.RotateToken;
                rotate = args[++i];
            }
            else if (args[i] == "--print-config")
            {
                command = HubCommand.PrintConfig;
            }
            else if (args[i] == "--rooms-root")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--rooms-root requires a value.");
                rooms = args[++i];
            }
            else if (args[i] == "--import-skill")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--import-skill requires a value.");
                command = HubCommand.ImportSkill;
                // Rooted here, not where SkillImport.Run happens to read it: a relative path must
                // resolve against THIS command's working directory (row 11 task 5, m5/m6), the same
                // rule --data already follows.
                importSkillPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--force")
            {
                force = true;
            }
            else if (args[i] == "--overlay")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--overlay requires a value.");
                // Rooted here for the same reason --import-skill is (M5): a relative path must resolve
                // against THIS command's working directory, not against anything a later hub start does.
                overlayPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--set-classes")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--set-classes requires a value.");
                var spec = args[++i];
                if (!spec.Contains('=') || spec.StartsWith('='))
                    throw new ArgumentException("--set-classes takes <participant>=<classes>; classes are comma-separated or empty to clear.");
                command = HubCommand.SetClasses;
                setClassesSpec = spec;
            }
            else if (args[i] == "--export-memory")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--export-memory requires a value.");
                // Rooted here for the same reason --import-skill is (M5): a relative path must resolve
                // against THIS command's working directory. TrimEndingDirectorySeparator runs AFTER
                // GetFullPath (claim 20) — GetFullPath alone preserves a trailing separator, which
                // would put the writer's staging directory inside the target.
                var rooted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++i]));
                if (string.Equals(rooted, Path.GetPathRoot(rooted), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"--export-memory cannot target a drive root ('{rooted}').");
                command = HubCommand.ExportMemory;
                exportMemoryPath = rooted;
            }
            else if (args[i] == "--accept-new-source")
            {
                acceptNewSource = true;
            }
            else if (args[i] == "--owner-peer-check")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--owner-peer-check requires a value.");
                ownerPeerCheck = args[++i];
            }
        }
        if (overlayPath is not null && command != HubCommand.ImportSkill)
            throw new ArgumentException("--overlay is only valid with --import-skill.");
        if (acceptNewSource && command != HubCommand.ExportMemory)
            throw new ArgumentException("--accept-new-source is only valid with --export-memory.");
        data ??= getEnv("CHOPITUP_DATA");
        port ??= getEnv("CHOPITUP_PORT");
        rooms ??= getEnv("CHOPITUP_ROOMS");
        // Row 29, D3: the flag wins over the environment; anything but "off" (case-insensitive) is
        // on, so a missing or garbled env value never accidentally disables the check.
        ownerPeerCheck ??= getEnv("CHOPITUP_OWNER_PEER_CHECK");
        return new HubOptions(
            Path.GetFullPath(string.IsNullOrWhiteSpace(data) ? Path.Combine(AppContext.BaseDirectory, "data") : data),
            int.TryParse(port, out var p) ? p : DefaultPort,
            command,
            rotate,
            RoomsRoot: string.IsNullOrWhiteSpace(rooms) ? null : rooms,
            ImportSkillPath: importSkillPath,
            Force: force,
            OverlayPath: overlayPath,
            SetClassesSpec: setClassesSpec,
            ExportMemoryPath: exportMemoryPath,
            AcceptNewSource: acceptNewSource,
            OwnerPeerCheck: !string.Equals(ownerPeerCheck, "off", StringComparison.OrdinalIgnoreCase));
    }
}
