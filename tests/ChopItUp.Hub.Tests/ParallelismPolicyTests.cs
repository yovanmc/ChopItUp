using System.Text.RegularExpressions;

namespace ChopItUp.Hub.Tests;

/// <summary>Guards the rules that make parallel collections safe in this assembly. A test class whose
/// source file touches process-wide state or machine-wide tables must sit in
/// <see cref="ProcessStateCollection"/>. The scan reads this project's sources only: state changed by
/// production code the tests call (for example the shell-token scrub in <c>HubHost.Build</c>) is outside
/// its reach and is recorded in docs/testing/hub-test-resources.md instead.</summary>
public sealed class ParallelismPolicyTests
{
    private const string SelfFile = "ParallelismPolicyTests.cs";
    private const string CollectionTag = "[Collection(ProcessStateCollection.Name)]";

    private static readonly Regex[] SharedStateCalls =
    [
        new(@"Environment\.SetEnvironmentVariable\s*\("),
        new(@"Console\.Set(Error|Out|In)\s*\("),
        new(@"Directory\.SetCurrentDirectory\s*\("),
        new(@"CultureInfo\.\w*Culture\w*\s*=[^=]"),
        new(@"Win32_Process"),
        new(@"GetExtendedTcpTable"),
        new(@"new\s+TcpListener\s*\("),
        new(@"Process\.GetProcesses\s*\("),
    ];

    private static readonly Regex ParallelSafe = new(@"//\s*Parallel-safe:(?<reason>.*)$", RegexOptions.Multiline);
    private static readonly Regex TopLevelClass = new(@"^(public|internal)\s+(?:(?:sealed|static|abstract|partial)\s+)*class\s+(?<name>\w+)", RegexOptions.Multiline);

    /// <summary>Every rule broken by <paramref name="files"/>, one line per offending class or file.</summary>
    public static IReadOnlyList<string> Violations(IEnumerable<(string Path, string Text)> files)
    {
        var found = new List<string>();
        foreach (var (path, text) in files)
        {
            if (!SharedStateCalls.Any(r => r.IsMatch(text))) continue;
            var safe = ParallelSafe.Match(text);
            if (safe.Success)
            {
                if (string.IsNullOrWhiteSpace(safe.Groups["reason"].Value))
                    found.Add($"{path}: '// Parallel-safe:' needs a reason");
                continue;
            }
            var classes = TopLevelClass.Matches(text);
            for (var i = 0; i < classes.Count; i++)
            {
                var start = classes[i].Index;
                var end = i + 1 < classes.Count ? classes[i + 1].Index : text.Length;
                var body = text[start..end];
                if (!body.Contains("[Fact") && !body.Contains("[Theory")) continue;
                if (!AttributesAbove(text, start).Contains(CollectionTag))
                    found.Add($"{path}: {classes[i].Groups["name"].Value} touches process or machine state but is not in {CollectionTag}");
            }
        }
        return found;
    }

    private static string AttributesAbove(string text, int classStart)
    {
        var lines = text[..classStart].Split('\n');
        var attributes = new List<string>();
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 && attributes.Count == 0) continue;
            if (!line.StartsWith('[')) break;
            attributes.Add(line);
        }
        return string.Join("\n", attributes);
    }

    [Fact]
    public void Classes_that_touch_process_or_machine_state_run_in_the_non_parallel_collection()
    {
        var project = Path.Combine(FindRepoRoot(), "tests", "ChopItUp.Hub.Tests");
        var files = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(Path.GetRelativePath(project, f)) && Path.GetFileName(f) != SelfFile)
            .Select(f => (Path.GetRelativePath(project, f), File.ReadAllText(f)))
            .ToList();

        Assert.True(files.Count > 50, $"Scanned only {files.Count} source files under {project}");
        var violations = Violations(files);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void An_untagged_test_class_calling_a_shared_state_api_is_reported()
    {
        const string text = "namespace N;\n\npublic sealed class A\n{\n    [Fact]\n    public void T() => Console.SetError(TextWriter.Null);\n}\n";
        Assert.Single(Violations([("A.cs", text)]));
    }

    [Fact]
    public void A_tagged_test_class_passes()
    {
        const string text = "namespace N;\n\n[Collection(ProcessStateCollection.Name)]\npublic sealed class A\n{\n    [Fact]\n    public void T() => Environment.SetEnvironmentVariable(\"X\", null);\n}\n";
        Assert.Empty(Violations([("A.cs", text)]));
    }

    [Fact]
    public void An_untagged_sibling_test_class_in_a_matching_file_is_reported()
    {
        const string text = "namespace N;\n\n[Collection(ProcessStateCollection.Name)]\npublic sealed class A\n{\n    [Fact]\n    public void T() => Console.SetOut(TextWriter.Null);\n}\n\npublic sealed class B\n{\n    [Theory]\n    public void U(int x) { }\n}\n";
        var violation = Assert.Single(Violations([("A.cs", text)]));
        Assert.Contains(" B ", violation);
    }

    [Fact]
    public void A_class_without_tests_needs_no_tag()
    {
        const string text = "namespace N;\n\npublic sealed class Fixture\n{\n    public Fixture() => Environment.SetEnvironmentVariable(\"X\", \"1\");\n}\n";
        Assert.Empty(Violations([("A.cs", text)]));
    }

    [Fact]
    public void A_parallel_safe_line_needs_a_reason()
    {
        const string bare = "// Parallel-safe:\nnamespace N;\n\npublic sealed class A\n{\n    [Fact]\n    public void T() => _ = Process.GetProcesses();\n}\n";
        const string reasoned = "// Parallel-safe: looks up its own child by pid\nnamespace N;\n\npublic sealed class A\n{\n    [Fact]\n    public void T() => _ = Process.GetProcesses();\n}\n";
        Assert.Single(Violations([("A.cs", bare)]));
        Assert.Empty(Violations([("A.cs", reasoned)]));
    }

    private static bool IsBuildOutput(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }
}
