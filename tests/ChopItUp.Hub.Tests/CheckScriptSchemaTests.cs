using System.Diagnostics;
using System.Text.RegularExpressions;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Tests;

/// <summary>The live checks and dry runs under <c>tools/</c> compare the hub's schema against
/// <c>tools/Get-ChopSchemaVersion.ps1</c>, which reads <see cref="ChopDb.LatestSchemaVersion"/> from
/// source, so a schema bump cannot leave a script asserting the old number. A fixture's starting
/// stamp (<c>fixture.stamped-v9</c>, <c>backup.stamped-v2</c>) stays a literal on purpose.</summary>
public sealed class CheckScriptSchemaTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly Regex[] LiteralLatest =
    [
        new(@"\bschema\s+-eq\s+\d", RegexOptions.IgnoreCase),
        new(@"\$ExpectedSchema\s*=\s*\d", RegexOptions.IgnoreCase),
        new(@"'migrated\.[^']*'.*user_version'\]\s*-eq\s*\d"),
    ];

    [Fact]
    public void Check_scripts_take_the_latest_schema_from_the_helper_not_a_literal()
    {
        var hits = new List<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(RepoRoot, "tools"), "*.ps1", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (LiteralLatest.Any(r => r.IsMatch(lines[i])))
                    hits.Add($"{Path.GetRelativePath(RepoRoot, path)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "Compare against tools/Get-ChopSchemaVersion.ps1 instead:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void The_helper_prints_ChopDb_LatestSchemaVersion()
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            ArgumentList = { "-NoProfile", "-File", Path.Combine(RepoRoot, "tools", "Get-ChopSchemaVersion.ps1") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, stderr);
        Assert.Equal(ChopDb.LatestSchemaVersion.ToString(), stdout.Trim());
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }
}
