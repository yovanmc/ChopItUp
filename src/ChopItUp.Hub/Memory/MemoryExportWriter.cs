using System.Text;
using System.Text.Json;
using ChopItUp.Core.Memory;

namespace ChopItUp.Hub.Memory;

/// <summary>What one run reported: the exit code T4 returns verbatim (pass 2 M5), the target actually
/// acted on, how many entries were exported, the previous-export directory this run retained (its
/// name says whether it is the reusable slot or a timestamped one — <see langword="null"/> when the
/// target did not exist before this run), and any staging directory left by ANOTHER run that this run
/// found and reported rather than touched.</summary>
public sealed record ExportResult(int ExitCode, string Target, int Exported, string? PreviousDir, IReadOnlyList<string> OtherStagingDirs);

/// <summary>Stages the whole export in a sibling directory, classifies the target against the
/// previous export's manifest, re-checks that classification immediately before touching anything,
/// then swaps the staged directory into place — so the target is never in a state where its files
/// and its manifest disagree (D3). The order of operations below IS the safety property (plan T3);
/// the two critique passes that shaped this plan found six defects in exactly this ordering, so it is
/// not reordered or simplified here. Modelled on <c>tools/Deploy-ChopItUp.ps1</c>'s own doc comment.
///
/// Order of operations:
///   1. Render the export (<see cref="MemoryExport.Render"/>). A refusal here (over-cap) touches
///      nothing and maps to exit 6, never the shared exit-3 mapping (claim 21, pass 2 M5).
///   2. Verify the target against its manifest (<see cref="ExportManifest.Verify"/>). <c>Absent</c>
///      and <c>Clean</c> proceed silently. <c>DifferentSource</c> refuses naming both roots and is
///      overridable only by <c>acceptNewSource</c>, never <c>force</c> (D9); <c>Foreign</c>,
///      <c>Unreadable</c> and <c>Drifted</c> refuse listing every affected path and are overridable
///      by <c>force</c>. Every override prints a list byte-identical to the refusal's, from the same
///      <see cref="FormatAffectedPaths"/> (D2, AC6).
///   3. Clear the reusable previous slot (<c>&lt;targetDir&gt;.chopitup-export-previous</c>) if it
///      exists, before anything is moved. Without this the THIRD consecutive export dies:
///      <c>Directory.Move</c> throws onto an existing destination (claim 22, pass 2 B1).
///   4. Stage the whole export — every rendered file, the vendor's <c>MEMORY.md</c> index, and the
///      manifest describing them all — into <c>&lt;targetDir&gt;.chopitup-export-tmp-&lt;nonce&gt;</c>.
///      Any OTHER staging directory already sitting beside the target is reported, never deleted —
///      it may be a concurrent run's half-built stage.
///   5. Re-verify the target, right before touching it (pass 2 M6): D11 stops the hub, not a vendor
///      session writing into the target between steps 2 and 5. If the verdict changed, abort — the
///      freshly built stage is deleted (it holds only reproducible bytes and was never exposed to the
///      target) and nothing on the target side is touched.
///      Retention name (D8, pass 2 B3): the aside directory is the SUPERSET test over the two
///      manifests — reusable only when the replaced target was <c>Absent</c> (nothing to lose) or
///      <c>Clean</c> AND every key its manifest names is also produced by this export. Otherwise it
///      is timestamped and never auto-deleted. <c>Foreign</c>/<c>Unreadable</c>/<c>Drifted</c>/a
///      different-source override always land in the timestamped form — their recorded manifest (if
///      any) cannot be trusted to describe what is actually there.
///   6. On failure of the second move, the aside directory is moved back; the reported state is a
///      FRESH <see cref="Directory.Exists"/> check on the target either way, never the state assumed
///      going in (pass 2 M14) — the thing that made the swap fail is often still there.
///   7. Print <c>EXPORT_RESULT: { ... }</c> as the last line: target, count, the previous directory
///      and whether it changed since the last export.</summary>
public static class MemoryExportWriter
{
    private const string PreviousSuffix = ".chopitup-export-previous";
    private const string TmpSuffix = ".chopitup-export-tmp-";

    public static ExportResult Run(MemoryStore store, string targetDir, bool force, bool acceptNewSource, TextWriter output, TextWriter error) =>
        Run(store, targetDir, force, acceptNewSource, output, error, afterInitialVerify: null);

    /// <summary>The 7-arg overload carries a test-only hook fired right after step 2's initial
    /// <see cref="ExportManifest.Verify"/> decides to proceed, and before anything else — the seam
    /// pass 2 M6 is about, and one no test can reach by racing a real second thread inside one call.</summary>
    internal static ExportResult Run(MemoryStore store, string targetDir, bool force, bool acceptNewSource,
        TextWriter output, TextWriter error, Action? afterInitialVerify) =>
        Run(store, targetDir, force, acceptNewSource, output, error, afterInitialVerify, afterAsideMove: null);

    /// <summary>The 8-arg overload adds a second test-only hook, fired right after step 6's FIRST
    /// <c>Directory.Move</c> (target aside) succeeds and before the SECOND (stage into place) is even
    /// attempted. It hands the test the exact stage and aside directory names — both carry a
    /// GUID/timestamp no test can predict ahead of the call — so a test can force the second move to
    /// fail deterministically. This is the seam AC8's restore branch needs: no test can reach "the
    /// second move fails after the target is already moved aside" by racing a real second thread
    /// inside one call, the same reasoning <paramref name="afterInitialVerify"/> is here for.</summary>
    internal static ExportResult Run(MemoryStore store, string targetDir, bool force, bool acceptNewSource,
        TextWriter output, TextWriter error, Action? afterInitialVerify, Action<string, string>? afterAsideMove)
    {
        targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
        var parent = Path.GetDirectoryName(targetDir)
            ?? throw new ArgumentException("targetDir must not be a filesystem root.", nameof(targetDir));
        var targetName = Path.GetFileName(targetDir);
        var previousPlain = targetDir + PreviousSuffix;

        // Report, up front, whatever an earlier (possibly interrupted, possibly concurrent) run left
        // beside the target — a half-built stage, or a previous-export directory this run did not
        // itself create. Neither is touched here; a staging directory is never deleted automatically,
        // and a previous-export directory is either the reusable plain slot (cleared safely in step 3
        // below, because it holds only reproducible bytes) or a timestamped one this run leaves alone.
        var otherStagingDirs = new List<string>();
        if (Directory.Exists(parent))
        {
            foreach (var d in Directory.EnumerateDirectories(parent, targetName + TmpSuffix + "*"))
            {
                otherStagingDirs.Add(d);
                output.WriteLine($"found an existing staging directory from another run (left alone): '{d}'");
            }
            foreach (var d in Directory.EnumerateDirectories(parent, targetName + PreviousSuffix + "*"))
                output.WriteLine($"found an existing previous-export directory: '{d}'");
        }

        // Step 1: render. Nothing has looked at the target yet.
        ExportPlan plan;
        try
        {
            plan = MemoryExport.Render(store);
        }
        catch (ExportRefusedException ex)
        {
            error.WriteLine(ex.Message);
            return new ExportResult(6, targetDir, 0, null, otherStagingDirs);
        }

        // A target path occupied by a plain file (not a directory) is neither Absent nor any of
        // Verify's other states — it must refuse before Verify is even asked, or a superficial
        // Directory.Exists == false would misclassify it as Absent and try to create a directory
        // where a file already sits.
        if (File.Exists(targetDir))
        {
            error.WriteLine($"refusing: '{targetDir}' exists and is a file, not a directory.");
            return new ExportResult(3, targetDir, 0, null, otherStagingDirs);
        }

        // Step 2: verify.
        var manifest = ExportManifest.TryRead(targetDir);
        var verdict = ExportManifest.Verify(manifest, targetDir, store);

        if (!Decide(verdict, store, force, acceptNewSource, output, error, out var refusalExitCode))
            return new ExportResult(refusalExitCode, targetDir, 0, null, otherStagingDirs);

        afterInitialVerify?.Invoke();

        // Step 3: clear the reusable previous slot before anything is moved (claim 22, pass 2 B1).
        if (Directory.Exists(previousPlain))
        {
            Directory.Delete(previousPlain, recursive: true);
            output.WriteLine($"cleared previous export directory: '{previousPlain}'");
        }

        // Step 4: stage.
        var stageDir = targetDir + TmpSuffix + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stageDir);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in plan.Files)
        {
            var path = Path.Combine(stageDir, f.FileName);
            File.WriteAllText(path, f.Text);
            files[f.FileName] = ExportManifest.HashFile(path);
        }
        var indexPath = Path.Combine(stageDir, MemoryStore.CoreFileName);
        File.WriteAllText(indexPath, plan.Index);
        files[MemoryStore.CoreFileName] = ExportManifest.HashFile(indexPath);

        var fingerprint = ExportManifest.Fingerprint(store);
        ExportManifest.Write(stageDir, new ExportManifest(ExportManifest.CurrentVersion, store.Root, fingerprint, DateTime.UtcNow.ToString("O"), files));

        // Step 5: re-verify immediately before touching the target (pass 2 M6).
        var reManifest = ExportManifest.TryRead(targetDir);
        var reVerdict = ExportManifest.Verify(reManifest, targetDir, store);
        if (!SameVerdict(verdict, reVerdict))
        {
            Directory.Delete(stageDir, recursive: true);   // ours, never exposed to the target: safe to drop
            error.WriteLine($"aborting: target changed between verification and swap (was {verdict.State}, now {reVerdict.State}). Nothing was moved.");
            return new ExportResult(6, targetDir, 0, null, otherStagingDirs);
        }

        var targetExists = Directory.Exists(targetDir);
        string? previousDirResult = null;

        if (targetExists)
        {
            // D8, pass 2 B3: the superset test over the two manifests, never `state == Clean` alone.
            var reusable = reVerdict.State == TargetState.Absent
                || (reVerdict.State == TargetState.Clean && IsSuperset(reManifest, files));
            var asideDir = reusable ? previousPlain : UniqueTimestampedPrevious(targetDir);

            try
            {
                Directory.Move(targetDir, asideDir);
            }
            catch (Exception ex)
            {
                Directory.Delete(stageDir, recursive: true);
                error.WriteLine($"swap failed while moving the target aside: {ex.Message}. Target left untouched.");
                return new ExportResult(3, targetDir, 0, null, otherStagingDirs);
            }

            afterAsideMove?.Invoke(stageDir, asideDir);

            try
            {
                Directory.Move(stageDir, targetDir);
            }
            catch (Exception ex)
            {
                var restored = false;
                try { Directory.Move(asideDir, targetDir); restored = true; }
                catch { /* the thing that made the swap fail is often still there; report the fresh state below regardless */ }

                // Step 6 (pass 2 M14): the reported state is a FRESH existence check, not the state assumed.
                var existsNow = Directory.Exists(targetDir);
                error.WriteLine($"swap failed while moving the new export into place: {ex.Message}. " +
                    $"Target is now {(existsNow ? "present" : "absent")} (restoring the previous target {(restored ? "succeeded" : "failed")}).");
                var survivingPrevious = Directory.Exists(asideDir) ? asideDir : null;
                return new ExportResult(3, targetDir, 0, survivingPrevious, otherStagingDirs);
            }

            previousDirResult = asideDir;
            output.WriteLine($"replaced target; previous export retained at '{asideDir}' ({(reusable ? "reusable" : "timestamped")}).");
        }
        else
        {
            try
            {
                Directory.Move(stageDir, targetDir);
            }
            catch (Exception ex)
            {
                var existsNow = Directory.Exists(targetDir);
                error.WriteLine($"swap failed while moving the new export into place: {ex.Message}. Target is now {(existsNow ? "present" : "absent")}.");
                return new ExportResult(3, targetDir, 0, null, otherStagingDirs);
            }
        }

        var storeChangedSinceExport = manifest is not null
            ? !string.Equals(fingerprint, manifest.SourceFingerprint, StringComparison.Ordinal)
            : (bool?)null;
        var resultLine = JsonSerializer.Serialize(new
        {
            target = targetDir,
            exported = plan.EntryCount,
            previous_dir = previousDirResult,
            other_staging_dirs = otherStagingDirs,
            store_changed_since_export = storeChangedSinceExport,
        });
        output.WriteLine("EXPORT_RESULT: " + resultLine);

        return new ExportResult(0, targetDir, plan.EntryCount, previousDirResult, otherStagingDirs);
    }

    /// <summary>D2, AC6: the single formatter both the refusal and the matching override call, so the
    /// two can never diverge — a test captures both real outputs and asserts them byte-identical.</summary>
    internal static string FormatAffectedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "affected paths: (none)";
        var sb = new StringBuilder("affected paths:");
        foreach (var p in paths) sb.Append('\n').Append("  ").Append(p);
        return sb.ToString();
    }

    /// <summary>Step 2's refusal/override decision. Returns <see langword="true"/> to proceed;
    /// <see langword="false"/> means refused, with <paramref name="refusalExitCode"/> set to 6 and the
    /// refusal (naming every affected path) already written to <paramref name="error"/>. Every
    /// override prints <see cref="FormatAffectedPaths"/> over the SAME <see cref="ManifestVerdict.Paths"/>
    /// the refusal would have printed (D2).</summary>
    private static bool Decide(ManifestVerdict verdict, MemoryStore store, bool force, bool acceptNewSource,
        TextWriter output, TextWriter error, out int refusalExitCode)
    {
        refusalExitCode = 0;
        switch (verdict.State)
        {
            case TargetState.Absent:
            case TargetState.Clean:
                return true;

            case TargetState.DifferentSource:
                if (!acceptNewSource)
                {
                    error.WriteLine($"refusing: the target's manifest names a different source ('{verdict.ManifestRoot}') " +
                        $"than this store ('{store.Root}'). --force cannot override this (D9) — use --accept-new-source if the data directory legitimately moved.");
                    error.WriteLine(FormatAffectedPaths(verdict.Paths));
                    refusalExitCode = 6;
                    return false;
                }
                output.WriteLine($"--accept-new-source: proceeding even though the target's manifest names a different source " +
                    $"('{verdict.ManifestRoot}') than this store ('{store.Root}').");
                output.WriteLine(FormatAffectedPaths(verdict.Paths));
                return true;

            case TargetState.Foreign:
            case TargetState.Unreadable:
            case TargetState.Drifted:
                if (!force)
                {
                    error.WriteLine($"refusing: target is {Describe(verdict.State)}. Use --force to override.");
                    error.WriteLine(FormatAffectedPaths(verdict.Paths));
                    refusalExitCode = 6;
                    return false;
                }
                output.WriteLine($"--force: proceeding even though target is {Describe(verdict.State)}.");
                output.WriteLine(FormatAffectedPaths(verdict.Paths));
                return true;

            default:
                throw new InvalidOperationException($"unhandled verdict state '{verdict.State}'.");
        }
    }

    private static string Describe(TargetState state) => state switch
    {
        TargetState.Foreign => "populated with no export manifest",
        TargetState.Unreadable => "populated with a manifest that cannot be parsed",
        TargetState.Drifted => "drifted from its recorded manifest",
        _ => state.ToString(),
    };

    private static bool SameVerdict(ManifestVerdict a, ManifestVerdict b) =>
        a.State == b.State && a.ManifestRoot == b.ManifestRoot && a.Paths.SequenceEqual(b.Paths, StringComparer.Ordinal);

    /// <summary>D8's superset test: every path the replaced manifest names is also produced by this
    /// export. A <see langword="null"/> previous manifest (nothing recorded) is vacuously a subset.</summary>
    private static bool IsSuperset(ExportManifest? previous, IReadOnlyDictionary<string, string> newFiles) =>
        previous is null || previous.Files.Keys.All(newFiles.ContainsKey);

    /// <summary>The timestamped retention name is only second-granularity, so two independent
    /// non-reusable replacements of the same target within one UTC second (a <c>--force</c> over
    /// drift, then an <c>--accept-new-source</c> over a different source, say) would otherwise collide
    /// and make <c>Directory.Move</c> throw onto an existing destination. Disambiguated with the same
    /// idiom <see cref="MemoryExport.Render"/> uses for a colliding file name: append <c>-2</c>,
    /// <c>-3</c>, … after the (still-readable) timestamp until the name is free. D8's timestamped
    /// previous is never auto-deleted, so the collision cannot be resolved by deleting it — only by
    /// not colliding in the first place.</summary>
    internal static string UniqueTimestampedPrevious(string targetDir)
    {
        var baseName = targetDir + PreviousSuffix + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var candidate = baseName;
        for (var n = 2; Directory.Exists(candidate); n++)
            candidate = $"{baseName}-{n}";
        return candidate;
    }
}
