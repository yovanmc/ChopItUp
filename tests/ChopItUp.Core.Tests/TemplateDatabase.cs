using ChopItUp.Core.Storage;

namespace ChopItUp.Core.Tests;

/// <summary>Builds the current-schema database once per test process with the production
/// <see cref="ChopDb.EnsureDatabase"/>, then copies that file into each store test's directory.
/// Building the schema from nothing is most of a store test's setup; a file copy takes a few
/// milliseconds. Each test still owns its own copy. A directory that already holds a database is
/// left alone. Callers still call <see cref="ChopDb.EnsureDatabase"/>, which finds the copy current
/// and returns. Tests of schema creation and migration build their databases themselves.
/// ChopItUp.Hub.Tests keeps its own copy of this helper: test assemblies run in separate processes
/// and do not reference each other.</summary>
internal static class TemplateDatabase
{
    public const string FileName = "chopitup.db";

    private static readonly Lazy<string> Template = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Copies the template to <c>chopitup.db</c> in <paramref name="dataDir"/> unless a
    /// database is already there.</summary>
    public static void SeedInto(string dataDir)
    {
        var target = Path.Combine(dataDir, FileName);
        if (File.Exists(target)) return;
        Directory.CreateDirectory(dataDir);
        File.Copy(Template.Value, target);
    }

    private static string Build()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_dbtemplate_" + Guid.NewGuid().ToString("N"));
        var db = new ChopDb(Path.Combine(dir, FileName));
        db.EnsureDatabase();
        // Pooling is off, so the last connection has closed and checkpointed the WAL into the file.
        if (File.Exists(db.DatabasePath + "-wal"))
            throw new InvalidOperationException("The template database still has a WAL file, so a copy of the main file would miss pages.");
        // A throw from a ProcessExit handler would fail the test host after its tests passed, so a
        // directory that cannot be removed is reported and left in the temp folder instead.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not delete the template database directory '{dir}': {e.Message}");
            }
        };
        return db.DatabasePath;
    }
}
