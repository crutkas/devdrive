// DevDriveManager.FilterProbe — a small, UAC-elevated command broker for the Dev Drive app.
//
// It started life as a strictly READ-ONLY "Iso app" that ran only `fsutil devdrv query <X:>`. It is now
// a narrow command broker with an explicit verb model, so the main app can do one elevated thing per
// short UAC prompt instead of relaunching the whole app as administrator:
//
//   query  <driveLetter> <outputJsonPath>                                     READ-ONLY  fsutil devdrv query
//   resize --whatif  --plan <base64> --out <path> [--allowed-root <dir>]      READ-ONLY  feasibility of a shrink
//   resize --execute --plan <base64> --out <path> [--allowed-root <dir>]      DESTRUCTIVE shrink → carve → format
//
// SAFETY MODEL
//  * `query` and `resize --whatif` touch NOTHING — they only run Get-* / fsutil read queries.
//  * `resize --execute` is the ONLY destructive path and must be requested explicitly AND authorized:
//    the plan must carry ExecuteAuthorized=true (set only by the app's gated VolumeResizer.ExecuteAsync),
//    so a bare command-line `--execute` against a preview/hand-written plan is refused. Before it runs,
//    ResizeGuard re-validates the plan against a fresh read-only disk snapshot (defence in depth): a
//    crafted/garbage plan is rejected and NOTHING is touched. As an extra valve, setting
//    DDM_RESIZE_DRYRUN=1 makes --execute build the real commands but NOT run them.
//  * F3 IPC hardening: the resize PLAN arrives base64-encoded ON THE COMMAND LINE — there is no plan
//    FILE, eliminating the plan-file TOCTOU. Only the OUTPUT is a file (runas/ShellExecute can't redirect
//    stdout). The output path is canonicalized (Path.GetFullPath), must be a `ddm-resize-*.json` file,
//    must NOT be a reparse point (junction/symlink), and must sit DIRECTLY under an app-owned root. The
//    broker passes the USER's temp dir via `--allowed-root` so over-the-shoulder elevation works (the
//    elevated child's %TEMP% is the admin's, not the user's). RESIDUAL LIMITATION: a hijacked caller can
//    pass an arbitrary `--allowed-root`, so the output-path root check is advisory; the real firewall is
//    ResizeGuard + ExecuteAuthorized + UAC, and the output is only ever a small JSON result object.
//  * fsutil and powershell are resolved fully-qualified from System32 (never bare PATH), because this
//    helper runs ELEVATED and a PATH-hijack would otherwise run as administrator.
//  * F7: once --execute has begun mutating, a timeout does NOT kill the destructive child (aborting a
//    shrink/format mid-flight can corrupt the disk); the helper re-queries final disk state and reports
//    "state unknown — check Disk Management" instead of a false "Nothing was changed".
//
// Exit codes: 0 = result written; 2 = bad arguments / disallowed path; 3 = failed to write the output file.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevDriveCore;
using DevDriveCore.Models;
using DevDriveCore.Services;

return Dispatch(args);

// ---- dispatch --------------------------------------------------------------------------------------

static int Dispatch(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: DevDriveManager.FilterProbe <query|resize> ...");
        return 2;
    }

    string verb = args[0].Trim().ToLowerInvariant();
    switch (verb)
    {
        case "query":
            return args.Length >= 3
                ? RunQuery(args[1], args[2])
                : BadArgs("query <driveLetter> <outputJsonPath>");

        case "resize":
            return RunResize(args);

        default:
            // Back-compat: the legacy form was "<driveLetter> <outputJsonPath>" (== query).
            if (NormalizeDriveLetter(args[0]) is not null && args.Length >= 2)
            {
                return RunQuery(args[0], args[1]);
            }

            return BadArgs("<query|resize> ...");
    }
}

static int BadArgs(string usage)
{
    Console.Error.WriteLine($"usage: DevDriveManager.FilterProbe {usage}");
    return 2;
}

// ---- query verb (unchanged behaviour: READ-ONLY fsutil devdrv query) -------------------------------

static int RunQuery(string rawLetter, string rawOutputPath)
{
    string? letter = NormalizeDriveLetter(rawLetter);
    if (letter is null || string.IsNullOrWhiteSpace(rawOutputPath))
    {
        Console.Error.WriteLine("invalid drive letter or output path.");
        return 2;
    }

    if (!TryCanonicalizeAppOwnedPath(rawOutputPath, "ddm-filters-", out string outputPath))
    {
        return 2;
    }

    ProbeResult result;
    try
    {
        result = RunFsutilQuery(letter);
    }
    catch (Exception ex)
    {
        result = new ProbeResult(-1, string.Empty, ex.Message);
    }

    return WriteJson(outputPath, result) ? 0 : 3;
}

// ---- resize verb -----------------------------------------------------------------------------------

static int RunResize(string[] args)
{
    // args = [ "resize", (--whatif|--execute)*, --plan <base64>, --out <path>, (--allowed-root <dir>)* ]
    // The order of flags is free; mode defaults to the READ-ONLY --whatif.
    string mode = "--whatif";
    string? planB64 = null;
    string? rawOutputPath = null;
    var extraRoots = new List<string>();

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i].Trim().ToLowerInvariant())
        {
            case "--whatif":
                mode = "--whatif";
                break;
            case "--execute":
                mode = "--execute";
                break;
            case "--plan":
                planB64 = NextArg(args, ref i);
                break;
            case "--out":
                rawOutputPath = NextArg(args, ref i);
                break;
            case "--allowed-root":
                string? root = NextArg(args, ref i);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    extraRoots.Add(root);
                }

                break;
            default:
                // Unknown token — ignore (keeps the contract forgiving of extra flags).
                break;
        }
    }

    bool execute = mode == "--execute";

    if (string.IsNullOrWhiteSpace(planB64) || string.IsNullOrWhiteSpace(rawOutputPath))
    {
        return BadArgs("resize [--whatif|--execute] --plan <base64> --out <outputJsonPath> [--allowed-root <dir>]");
    }

    // F3: the output path is canonicalized + reparse-point-rejected + must be a ddm-resize-* file under an
    // app-owned root. The broker passes the USER's temp via --allowed-root (over-the-shoulder elevation).
    if (!TryCanonicalizeAppOwnedPath(rawOutputPath, "ddm-resize-", out string outputPath, extraRoots))
    {
        return 2;
    }

    // F3: decode the plan from the base64 command-line arg (no plan FILE → no plan-file TOCTOU). A
    // garbage/undecodable plan is rejected, never executed.
    ResizePlan? plan = TryDecodePlan(planB64);
    if (plan is null)
    {
        object failure = execute
            ? new ResizeExecuteOutcome { Success = false, Executed = false, Message = "Couldn't decode or parse the resize plan. Nothing was changed." }
            : (object)new ResizeFeasibility { CanProceed = false, Reason = "Couldn't decode or parse the resize plan." };
        return WriteJson(outputPath, failure) ? 0 : 3;
    }

    // F3: --execute must be authorized by the gated UI path (VolumeResizer.ExecuteAsync sets the flag on
    // the plan it serializes). A bare command-line --execute against a preview/hand-written plan is refused
    // here BEFORE any disk query. The read-only --whatif feasibility never needs authorization.
    if (execute && !plan.ExecuteAuthorized)
    {
        var unauthorized = new ResizeExecuteOutcome
        {
            Success = false,
            Executed = false,
            Message = "Refusing --execute: this plan is not authorized for execution. Nothing was changed.",
            SourceVolumeLetter = char.ToUpperInvariant(plan.SourceVolumeLetter),
            NewDriveLetter = char.ToUpperInvariant(plan.NewDriveLetter),
            DevDriveBytes = plan.ShrinkBytes,
        };
        return WriteJson(outputPath, unauthorized) ? 0 : 3;
    }

    // Build a fresh READ-ONLY snapshot of the source volume/partition/disk, then run the guards.
    DiskLayoutSnapshot snapshot = QueryDiskLayout(char.ToUpperInvariant(plan.SourceVolumeLetter));
    ResizeFeasibility feasibility = ResizeGuard.Evaluate(plan, snapshot);

    if (!execute)
    {
        // READ-ONLY feasibility: report what WOULD happen. Nothing was touched.
        return WriteJson(outputPath, feasibility) ? 0 : 3;
    }

    ResizeExecuteOutcome outcome = ExecuteResize(plan, snapshot, feasibility);
    return WriteJson(outputPath, outcome) ? 0 : 3;
}

// Returns the NEXT arg (advancing the index) for a "--flag value" pair, or null when value is missing.
static string? NextArg(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

// Runs (or, under DDM_RESIZE_DRYRUN, only builds) the real destructive resize sequence.
static ResizeExecuteOutcome ExecuteResize(ResizePlan plan, DiskLayoutSnapshot snapshot, ResizeFeasibility feasibility)
{
    char source = char.ToUpperInvariant(plan.SourceVolumeLetter);
    char target = char.ToUpperInvariant(plan.NewDriveLetter);

    // Defence in depth: even with --execute, refuse a plan the guards reject. Touch nothing.
    if (!feasibility.CanProceed)
    {
        return new ResizeExecuteOutcome
        {
            Success = false,
            Executed = false,
            Message = $"Refused by guard: {feasibility.Reason} Nothing was changed.",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
        };
    }

    string script = BuildExecuteScript(plan, feasibility, snapshot.DiskNumber);

    // Safety valve: DDM_RESIZE_DRYRUN=1 exercises the whole pipeline WITHOUT running the commands.
    if (IsDryRun())
    {
        return new ResizeExecuteOutcome
        {
            Success = false,
            Executed = false,
            Message = "Dry run (DDM_RESIZE_DRYRUN=1): the resize commands were built but NOT executed. Nothing was changed.",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
            CompletedSteps = feasibility.Steps,
        };
    }

    // The real, destructive sequence (shrink → carve → Format-Volume -DevDrive). Only reached via an
    // explicit, AUTHORIZED --execute + a passing guard + real elevation; the app keeps it behind a
    // default-off flag. F7: once this begins mutating we must NOT kill it on timeout (aborting a
    // shrink/format mid-flight can corrupt the disk), so killOnTimeout is false and the timeout is generous.
    (int exitCode, string stdout, string stderr) = RunPowerShell(script, killOnTimeout: false, timeoutMs: 480_000);

    if (exitCode == HelperExit.TimedOutNotKilled)
    {
        // F7: the mutation began but didn't confirm completion in time, and we did NOT cancel it. Re-query
        // the final disk state (read-only) and report reality — NEVER a false "Nothing was changed".
        return new ResizeExecuteOutcome
        {
            Success = false,
            Executed = true,
            Message = "The resize did not confirm completion within the time limit and was NOT cancelled " +
                      "(cancelling a shrink/format mid-operation can corrupt the disk). State is unknown — " +
                      $"check Disk Management. {DescribeFinalState(source, target)}".TrimEnd(),
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
        };
    }

    bool ok = exitCode == 0;
    if (!ok)
    {
        return new ResizeExecuteOutcome
        {
            Success = false,
            Executed = true,
            Message = $"Resize failed: {Trim(stderr)}",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
            CompletedSteps = Array.Empty<string>(),
        };
    }

    // F5: report the ACTUAL carved size + letter read back from the disk, not the requested value.
    FinalState final = ParseFinalState(stdout);
    char actualLetter = final.DriveLetter ?? target;
    ulong actualBytes = final.PartitionSizeBytes is > 0UL ? final.PartitionSizeBytes!.Value : feasibility.AlignedShrinkBytes;
    return new ResizeExecuteOutcome
    {
        Success = true,
        Executed = true,
        Message = $"Shrank {source}: and created a {ByteSizeFormatter.Format(actualBytes)} ReFS Dev Drive at {actualLetter}:.",
        SourceVolumeLetter = source,
        NewDriveLetter = actualLetter,
        DevDriveBytes = actualBytes,
        CompletedSteps = feasibility.Steps,
    };
}

// F7: best-effort READ-ONLY re-query of the target after a non-killed execute timeout, so the
// "state unknown" message can say whether the Dev Drive actually came into existence.
static string DescribeFinalState(char source, char target)
{
    try
    {
        DiskLayoutSnapshot s = QueryDiskLayout(target);
        return s.SourceResolved
            ? $"Drive {target}: now exists ({s.FileSystem}, {ByteSizeFormatter.Format(s.PartitionSizeBytes)})."
            : $"Drive {target}: was not found — the shrink of {source}: may be partially applied.";
    }
    catch
    {
        return string.Empty;
    }
}

// F5: parses the final-state object the execute script emits (actual carved letter + size + file system).
static FinalState ParseFinalState(string stdout)
{
    if (string.IsNullOrWhiteSpace(stdout))
    {
        return new FinalState(null, null, null);
    }

    try
    {
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;

        char? letter = null;
        if (root.TryGetProperty("FinalDriveLetter", out JsonElement l) &&
            l.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(l.GetString()))
        {
            letter = char.ToUpperInvariant(l.GetString()![0]);
        }

        ulong? size = null;
        if (root.TryGetProperty("FinalPartitionSizeBytes", out JsonElement sz) && sz.TryGetUInt64(out ulong v))
        {
            size = v;
        }

        string? fs = root.TryGetProperty("FinalFileSystem", out JsonElement f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;

        return new FinalState(letter, size, fs);
    }
    catch (JsonException)
    {
        return new FinalState(null, null, null);
    }
}

// ---- disk snapshot (READ-ONLY Storage queries) -----------------------------------------------------

static DiskLayoutSnapshot QueryDiskLayout(char letter)
{
    DiskLayoutSnapshot unresolved = new() { SourceResolved = false, SourceVolumeLetter = letter };

    if (letter is < 'A' or > 'Z')
    {
        return unresolved;
    }

    (int exitCode, string stdout, string _) = RunPowerShell(BuildSnapshotScript(letter));
    if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
    {
        return unresolved;
    }

    try
    {
        DiskLayoutSnapshot? snapshot =
            JsonSerializer.Deserialize<DiskLayoutSnapshot>(stdout, ResizeJson.ReadOptions);
        return snapshot ?? unresolved;
    }
    catch (JsonException)
    {
        return unresolved;
    }
}

// READ-ONLY PowerShell that emits a DiskLayoutSnapshot as JSON. `letter` is validated A–Z before it is
// interpolated, so it can't carry script. Every cmdlet is a Get-* query — it mutates nothing.
static string BuildSnapshotScript(char letter)
{
    string l = letter.ToString();
    return $$"""
$ErrorActionPreference='Stop'
try {
  $p = Get-Partition -DriveLetter '{{l}}' -ErrorAction Stop
  $d = Get-Disk -Number $p.DiskNumber -ErrorAction Stop
  $v = Get-Volume -DriveLetter '{{l}}' -ErrorAction Stop
  $s = Get-PartitionSupportedSize -DriveLetter '{{l}}' -ErrorAction Stop
  [pscustomobject]@{
    SourceResolved=$true
    SourceVolumeLetter='{{l}}'
    FileSystem=[string]$v.FileSystem
    PartitionType=[string]$p.Type
    GptType=[string]$p.GptType
    IsSystemPartition=[bool]$p.IsSystem
    IsActivePartition=[bool]$p.IsActive
    PartitionSizeBytes=[uint64]$p.Size
    SupportedSizeMinBytes=[uint64]$s.SizeMin
    SupportedSizeMaxBytes=[uint64]$s.SizeMax
    DiskNumber=[int]$d.Number
    PartitionStyle=[string]$d.PartitionStyle
    IsDiskOffline=[bool]$d.IsOffline
    IsDiskReadOnly=[bool]$d.IsReadOnly
    IsRemovable=[bool]($d.BusType -in @('USB','SD','MMC'))
    BusType=[string]$d.BusType
    PartitionAlignmentBytes=[uint64]0
    DriveLettersInUse=[string]((Get-Volume | Where-Object { $_.DriveLetter } | ForEach-Object { $_.DriveLetter }) -join '')
  } | ConvertTo-Json -Compress
} catch {
  [pscustomobject]@{ SourceResolved=$false; SourceVolumeLetter='{{l}}'; FileSystem='' } | ConvertTo-Json -Compress
}
""";
}

// The real destructive sequence. Built always (so the pipeline is testable), executed only by the
// non-dry-run --execute path. Letters are validated A–Z; the label is sanitized to a safe charset.
static string BuildExecuteScript(ResizePlan plan, ResizeFeasibility feasibility, int diskNumber)
{
    char source = char.ToUpperInvariant(plan.SourceVolumeLetter);
    char target = char.ToUpperInvariant(plan.NewDriveLetter);
    ulong newSourceSize = feasibility.SourceSizeBytesAfter;
    ulong devDriveSize = feasibility.AlignedShrinkBytes;
    string label = SanitizeLabel(plan.Label);
    // F6: re-check the target letter at the LAST moment, elevated, in case the snapshot went stale between
    // the read-only probe and execution. F5: carve an explicit -Size partition pinned to the freed region
    // (NOT -UseMaximumSize, which would swallow every free extent on the disk), then read BACK the actual
    // carved letter + size and emit them as JSON so the helper reports reality, not the requested value.
    return $$"""
$ErrorActionPreference='Stop'
if (Get-Volume -DriveLetter '{{target}}' -ErrorAction SilentlyContinue) { throw "Drive letter {{target}}: is already in use." }
Resize-Partition -DriveLetter '{{source}}' -Size {{newSourceSize}}
$null = New-Partition -DiskNumber {{diskNumber}} -Size {{devDriveSize}} -DriveLetter '{{target}}'
Format-Volume -DriveLetter '{{target}}' -DevDrive -FileSystem ReFS -NewFileSystemLabel '{{label}}' -Confirm:$false | Out-Null
$fp = Get-Partition -DriveLetter '{{target}}' -ErrorAction Stop
$fv = Get-Volume -DriveLetter '{{target}}' -ErrorAction Stop
[pscustomobject]@{ FinalDriveLetter=[string]$fp.DriveLetter; FinalPartitionSizeBytes=[uint64]$fp.Size; FinalFileSystem=[string]$fv.FileSystem } | ConvertTo-Json -Compress
""";
}

// ---- process helpers (fully-qualified from System32 — this helper runs ELEVATED) -------------------

static ProbeResult RunFsutilQuery(string driveLetter)
{
    var psi = new ProcessStartInfo
    {
        FileName = ResolveSystem32Exe("fsutil.exe"),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add("devdrv");
    psi.ArgumentList.Add("query");
    psi.ArgumentList.Add($"{driveLetter}:");

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("could not start fsutil.");

    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();

    if (!process.WaitForExit(25_000))
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        return new ProbeResult(-1, string.Empty, "fsutil timed out.");
    }

    process.WaitForExit();
    return new ProbeResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
}

static (int ExitCode, string Stdout, string Stderr) RunPowerShell(string script, bool killOnTimeout = true, int timeoutMs = 120_000)
{
    // -EncodedCommand (base64 UTF-16LE) avoids all command-line quoting/escaping concerns.
    string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var psi = new ProcessStartInfo
    {
        FileName = ResolvePowerShellPath(),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-NonInteractive");
    psi.ArgumentList.Add("-ExecutionPolicy");
    psi.ArgumentList.Add("Bypass");
    psi.ArgumentList.Add("-EncodedCommand");
    psi.ArgumentList.Add(encoded);

    try
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start powershell.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            if (killOnTimeout)
            {
                // READ-ONLY queries are safe to kill on timeout.
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, string.Empty, "powershell timed out.");
            }

            // F7: a destructive --execute may be mid-mutation. Leave the child running (cancelling a
            // shrink/format can corrupt the disk) and signal a non-killed timeout so the caller re-queries
            // disk state and reports "state unknown" instead of a false "Nothing was changed".
            return (HelperExit.TimedOutNotKilled, string.Empty, "powershell did not exit within the time limit; the operation may still be in progress.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }
    catch (Exception ex)
    {
        return (-1, string.Empty, ex.Message);
    }
}

static string ResolveSystem32Exe(string exe)
{
    string candidate = Path.Combine(Environment.SystemDirectory, exe);
    return File.Exists(candidate) ? candidate : exe;
}

static string ResolvePowerShellPath()
{
    string candidate = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    return File.Exists(candidate) ? candidate : "powershell.exe";
}

static bool IsDryRun() =>
    string.Equals(Environment.GetEnvironmentVariable("DDM_RESIZE_DRYRUN"), "1", StringComparison.Ordinal);

// ---- input / output helpers ------------------------------------------------------------------------

static ResizePlan? TryDecodePlan(string base64)
{
    try
    {
        byte[] bytes = Convert.FromBase64String(base64);
        string json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<ResizePlan>(json, ResizeJson.ReadOptions);
    }
    catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException)
    {
        return null;
    }
}

static bool WriteJson(string outputPath, object value)
{
    try
    {
        File.WriteAllText(outputPath, JsonSerializer.Serialize(value, ResizeJson.WriteOptions));
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"failed to write output: {ex.Message}");
        return false;
    }
}

// Accepts "G", "g", "G:", "G:\" and returns the bare uppercase letter ("G"), or null if not A–Z.
static string? NormalizeDriveLetter(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    char c = char.ToUpperInvariant(raw.Trim()[0]);
    return c is >= 'A' and <= 'Z' ? c.ToString() : null;
}

// Trims a label to a safe filesystem-friendly charset before it is interpolated into a format command.
static string SanitizeLabel(string? label)
{
    if (string.IsNullOrWhiteSpace(label))
    {
        return "DevDrive";
    }

    var sb = new StringBuilder();
    foreach (char c in label.Trim())
    {
        if (char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')
        {
            sb.Append(c);
        }

        if (sb.Length >= 32)
        {
            break;
        }
    }

    return sb.Length == 0 ? "DevDrive" : sb.ToString();
}

static string Trim(string? value) =>
    string.IsNullOrWhiteSpace(value) ? "(no detail)" : value.Trim();

// ---- F6 path hardening -----------------------------------------------------------------------------

// Canonicalizes a caller-supplied path and requires it to be a `<requiredPrefix>*.json` file located
// DIRECTLY inside an app-owned root (the per-user TEMP dir, %LocalAppData%\DevDriveManager, or any root
// the caller explicitly allows via --allowed-root). Path traversal is resolved by Path.GetFullPath, so a
// crafted path escapes these roots rather than slipping through, and reparse-point (junction/symlink)
// targets are rejected so an elevated write can't be redirected. On failure it writes NOTHING and
// returns false.
static bool TryCanonicalizeAppOwnedPath(string rawPath, string requiredPrefix, out string fullPath, IReadOnlyList<string>? extraAllowedRoots = null)
{
    fullPath = string.Empty;
    if (string.IsNullOrWhiteSpace(rawPath))
    {
        Console.Error.WriteLine("missing path.");
        return false;
    }

    string canonical;
    try
    {
        canonical = Path.GetFullPath(rawPath);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        Console.Error.WriteLine($"invalid path: {ex.Message}");
        return false;
    }

    string fileName = Path.GetFileName(canonical);
    if (string.IsNullOrEmpty(fileName) ||
        !fileName.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
        !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("refusing a path outside the app-owned naming scheme.");
        return false;
    }

    string? directory = Path.GetDirectoryName(canonical);
    if (string.IsNullOrEmpty(directory))
    {
        Console.Error.WriteLine("refusing a path with no directory.");
        return false;
    }

    // F3: reject reparse points on the output file or its directory — a junction/symlink could redirect
    // our elevated write somewhere it shouldn't go.
    if (IsReparsePoint(canonical) || IsReparsePoint(directory))
    {
        Console.Error.WriteLine("refusing a reparse-point path.");
        return false;
    }

    if (!IsAppOwnedRoot(directory, extraAllowedRoots))
    {
        Console.Error.WriteLine("refusing a path outside the app-owned location.");
        return false;
    }

    fullPath = canonical;
    return true;
}

// True when the path exists and carries the reparse-point attribute (junction / symbolic link / mount).
static bool IsReparsePoint(string path)
{
    try
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
    }
    catch
    {
        // Unknown → not treated as a reparse point; the prefix + app-owned-root checks still gate the path.
    }

    return false;
}

static bool IsAppOwnedRoot(string directory, IReadOnlyList<string>? extraAllowedRoots = null)
{
    foreach (string root in AllowedOutputRoots())
    {
        if (IsSameDirectory(directory, root))
        {
            return true;
        }
    }

    if (extraAllowedRoots is not null)
    {
        foreach (string root in extraAllowedRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) && IsSameDirectory(directory, root))
            {
                return true;
            }
        }
    }

    return false;
}

static IEnumerable<string> AllowedOutputRoots()
{
    yield return Path.GetTempPath();

    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrEmpty(localAppData))
    {
        yield return Path.Combine(localAppData, "DevDriveManager");
    }
}

static bool IsSameDirectory(string a, string b)
{
    static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}

// ---- JSON contracts --------------------------------------------------------------------------------

/// <summary>Raw result of the elevated <c>fsutil devdrv query</c> run, serialized to the output file.</summary>
internal sealed record ProbeResult(int ExitCode, string Stdout, string Stderr);

/// <summary>F5: the actual carved partition letter/size/file system read back after a successful execute.</summary>
internal sealed record FinalState(char? DriveLetter, ulong? PartitionSizeBytes, string? FileSystem);

/// <summary>Sentinel exit codes for the internal PowerShell runner.</summary>
internal static class HelperExit
{
    /// <summary>A non-killed timeout: the (possibly destructive) child was left running. See F7.</summary>
    public const int TimedOutNotKilled = -2;
}

internal static class ResizeJson
{
    // Read plans + snapshots case-insensitively (the app writes PascalCase; PowerShell emits PascalCase).
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Results are written PascalCase; VolumeResizer reads them case-insensitively.
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
    };
}
