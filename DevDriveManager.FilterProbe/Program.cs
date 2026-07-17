// DevDriveManager.FilterProbe — a small, UAC-elevated command broker for the Dev Drive app.
//
// It started life as a strictly READ-ONLY "Iso app" that ran only `fsutil devdrv query <X:>`. It is now
// a narrow command broker with an explicit verb model, so the main app can do one elevated thing per
// short UAC prompt instead of relaunching the whole app as administrator:
//
//   query  <driveLetter> <outputJsonPath>                                     READ-ONLY  fsutil devdrv query
//   resize --whatif  --plan <base64> --out <path> [--allowed-root <dir>]      READ-ONLY  feasibility of a shrink
//   resize --execute --plan <base64> --out <path> [--allowed-root <dir>]      DESTRUCTIVE shrink → carve → format
//   vhd --create|--revert --plan <base64> --out <path> [--allowed-root <dir>] PRIVILEGED VHDX transaction
//
// SAFETY MODEL
//  * `query` and `resize --whatif` touch NOTHING — they only run Get-* / fsutil read queries.
//  * `resize --execute` is the existing-volume destructive path and must be requested explicitly and authorized:
//    the plan must carry ExecuteAuthorized=true (set only by VolumeResizer.ExecuteAsync after confirmation),
//    so a bare command-line `--execute` against a preview/hand-written plan is refused. Before it runs,
//    ResizeGuard validates a fresh read-only disk snapshot, binds that exact identity inside this elevated
//    process, and revalidates it immediately before mutation (defence in depth): a crafted/garbage plan is
//    rejected and NOTHING is touched. As an extra valve, setting
//    DDM_RESIZE_DRYRUN=1 makes --execute build the real commands but NOT run them.
//  * `vhd --create` also requires authorization. It refuses existing/reparse-point paths, creates and
//    attaches only that new VHDX, then binds Get-DiskImage to the native physical disk number before
//    requiring RAW/empty/online/writable/expected-size state. A confirmed failure detaches and deletes
//    only the new VHDX; uncertain state is reported rather than retried or hidden.
//  * F3 IPC hardening: mutation plans arrive base64-encoded ON THE COMMAND LINE — there is no plan
//    FILE, eliminating the plan-file TOCTOU. Only the OUTPUT is a file (runas/ShellExecute can't redirect
//    stdout). The output path is canonicalized (Path.GetFullPath), must use the verb-specific app prefix,
//    must NOT be a reparse point (junction/symlink), and must sit DIRECTLY under an app-owned root. The
//    broker passes the USER's temp dir via `--allowed-root` so over-the-shoulder elevation works (the
//    elevated child's %TEMP% is the admin's, not the user's). RESIDUAL LIMITATION: a hijacked caller can
//    pass an arbitrary `--allowed-root`, so the output-path root check is advisory; the real firewall is
//    live guards + ExecuteAuthorized + UAC, and the output is only ever a small JSON result object.
//  * fsutil and powershell resolve fully-qualified from System32. Generated scripts import the inbox
//    Storage module from its absolute System32 path and use module-qualified cmdlets.
//  * F7: once storage mutation begins, a timeout does NOT kill the child (aborting a shrink/format
//    mid-flight can corrupt state); the helper reports "state unknown — check Disk Management" instead
//    of a false "Nothing was changed".
//
// Exit codes: 0 = result written; 2 = bad arguments / disallowed path; 3 = failed to write the output file.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevDriveCore;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

return Dispatch(args);

// ---- dispatch --------------------------------------------------------------------------------------

static int Dispatch(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: DevDriveManager.FilterProbe <query|resize|vhd> ...");
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

        case "vhd":
            return RunVhd(args);

        default:
            // Back-compat: the legacy form was "<driveLetter> <outputJsonPath>" (== query).
            if (NormalizeDriveLetter(args[0]) is not null && args.Length >= 2)
            {
                return RunQuery(args[0], args[1]);
            }

            return BadArgs("<query|resize|vhd> ...");
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

    // F3: --execute must be authorized by the gated UI path (VolumeResizer sets the flag on
    // the plan it serializes). A bare command-line --execute against a hand-written plan is refused
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
    bool hasBoundIdentity = ResizeGuard.HasExpectedPreviewIdentity(plan);
    ResizePlan verificationPlan = execute && !hasBoundIdentity
        ? plan with { ExecuteAuthorized = false }
        : plan;
    ResizeFeasibility feasibility = ResizeGuard.Evaluate(verificationPlan, snapshot);

    if (!execute)
    {
        // READ-ONLY feasibility: report what WOULD happen. Nothing was touched.
        return WriteJson(outputPath, feasibility) ? 0 : 3;
    }

    ResizePlan executionPlan = plan;
    if (feasibility.CanProceed && !hasBoundIdentity)
    {
        // The normal one-confirmation path arrives without a separate preview identity. Bind the exact
        // live snapshot here, inside the same elevated helper, then run the authorized guard again.
        executionPlan = ResizeGuard.BindForExecution(plan, feasibility);
        feasibility = ResizeGuard.Evaluate(executionPlan, snapshot);
    }

    ResizeExecuteOutcome outcome = ExecuteResize(executionPlan, snapshot, feasibility);
    return WriteJson(outputPath, outcome) ? 0 : 3;
}

// Returns the NEXT arg (advancing the index) for a "--flag value" pair, or null when value is missing.
static string? NextArg(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

// ---- VHDX verb -------------------------------------------------------------------------------------

static int RunVhd(string[] args)
{
    string? mode = null;
    string? planB64 = null;
    string? rawOutputPath = null;
    var extraRoots = new List<string>();

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i].Trim().ToLowerInvariant())
        {
            case "--create":
                mode = "--create";
                break;
            case "--revert":
                mode = "--revert";
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
        }
    }

    if (mode is null || string.IsNullOrWhiteSpace(planB64) || string.IsNullOrWhiteSpace(rawOutputPath))
    {
        return BadArgs("vhd <--create|--revert> --plan <base64> --out <outputJsonPath> [--allowed-root <dir>]");
    }

    if (!TryCanonicalizeAppOwnedPath(rawOutputPath, "ddm-vhd-", out string outputPath, extraRoots))
    {
        return 2;
    }

    VhdProvisionResult outcome;
    if (mode == "--create")
    {
        VhdProvisionPlan? plan = TryDecodeJson<VhdProvisionPlan>(planB64);
        outcome = plan is null
            ? VhdFailure("Couldn't decode or parse the VHDX creation plan. Nothing was changed.")
            : ExecuteVhdCreate(plan);
    }
    else
    {
        VhdRevertPlan? plan = TryDecodeJson<VhdRevertPlan>(planB64);
        outcome = plan is null
            ? VhdFailure("Couldn't decode or parse the VHDX recovery plan. Nothing was changed.")
            : ExecuteVhdRevert(plan);
    }

    return WriteJson(outputPath, outcome) ? 0 : 3;
}

static VhdProvisionResult ExecuteVhdCreate(VhdProvisionPlan plan)
{
    if (!plan.ExecuteAuthorized)
    {
        return VhdFailure(
            "Refusing VHDX creation: this plan is not authorized for execution. Nothing was changed.",
            plan.FilePath,
            plan.DriveLetter);
    }

    if (!TryValidateVhdCreatePlan(plan, out string fullPath, out string validationError))
    {
        return VhdFailure($"{validationError} Nothing was changed.", plan.FilePath, plan.DriveLetter);
    }

    plan = plan with
    {
        FilePath = fullPath,
        DriveLetter = char.ToUpperInvariant(plan.DriveLetter),
    };

    if (IsVhdDryRun())
    {
        string dryRunScript = VhdPowerShellScript.BuildFinalize(plan, expectedDiskNumber: 0);
        (int parseExitCode, string _, string parseError) = ValidatePowerShellSyntax(dryRunScript);
        if (parseExitCode != 0)
        {
            return VhdFailure(
                $"Dry run could not parse the generated VHDX command sequence: {Trim(parseError)} Nothing was changed.",
                fullPath,
                plan.DriveLetter);
        }

        return VhdFailure(
            "Dry run (DDM_VHD_DRYRUN=1): the complete VHDX command sequence was built but not executed. Nothing was changed.",
            fullPath,
            plan.DriveLetter);
    }

    (int capabilityExitCode, string capabilityOutput, string capabilityError) =
        RunPowerShell(VhdPowerShellScript.BuildCapabilityProbe());
    if (capabilityExitCode != 0)
    {
        return VhdFailure(
            $"Couldn't verify Dev Drive formatting support: {Trim(capabilityError)} Nothing was changed.",
            fullPath,
            plan.DriveLetter);
    }

    if (!VhdPowerShellScript.TryParseCapability(capabilityOutput, out VhdCapabilityState capability))
    {
        return VhdFailure(
            "Dev Drive formatting support returned an invalid response. Nothing was changed.",
            fullPath,
            plan.DriveLetter);
    }

    if (!capability.Supported)
    {
        return VhdFailure(
            $"This Windows installation cannot format Dev Drives (build {capability.Build}.{capability.Revision}, DevDrive parameter present: {capability.HasDevDriveParameter}). Nothing was changed.",
            fullPath,
            plan.DriveLetter);
    }

    var provisioner = new VhdProvisioner(
        new NativeVhdApi(),
        new SystemFileSystem(),
        new InMemoryReversibilityStore());

    VhdProvisionResult surfaced;
    try
    {
        surfaced = provisioner.ProvisionAsync(plan).GetAwaiter().GetResult();
    }
    catch (VhdProvisioningException ex)
    {
        if (ex.Stage == VhdProvisioningStage.Create)
        {
            return new VhdProvisionResult
            {
                Success = false,
                Executed = !ex.RollbackConfirmed,
                StateUnknown = !ex.RollbackConfirmed,
                RolledBack = ex.RollbackConfirmed,
                Message = ex.RollbackConfirmed
                    ? $"{ex.Message} Nothing was attached."
                    : $"{ex.Message} Cleanup could not be confirmed. Check the VHDX path and Disk Management before retrying.",
                FilePath = fullPath,
                DriveLetter = plan.DriveLetter,
            };
        }

        return new VhdProvisionResult
        {
            Success = false,
            Executed = !ex.RollbackConfirmed,
            StateUnknown = !ex.RollbackConfirmed,
            RolledBack = ex.RollbackConfirmed,
            Message = ex.RollbackConfirmed
                ? ex.Message
                : $"{ex.Message} Check Disk Management before retrying.",
            FilePath = fullPath,
            DriveLetter = plan.DriveLetter,
        };
    }
    catch (Exception ex)
    {
        bool remains = File.Exists(fullPath);
        return new VhdProvisionResult
        {
            Success = false,
            Executed = remains,
            StateUnknown = remains,
            RolledBack = !remains,
            Message = remains
                ? $"VHDX creation failed and cleanup could not be confirmed ({ex.Message}). Check Disk Management before retrying."
                : $"VHDX creation failed and no backing file remains: {ex.Message}",
            FilePath = fullPath,
            DriveLetter = plan.DriveLetter,
        };
    }

    if (surfaced.DiskNumber is not int diskNumber)
    {
        return RollBackVhd(
            provisioner,
            surfaced,
            "Windows attached the VHDX but did not return a usable physical disk number.");
    }

    string script;
    try
    {
        script = VhdPowerShellScript.BuildFinalize(plan, diskNumber);
    }
    catch (Exception ex)
    {
        return RollBackVhd(provisioner, surfaced, $"The format plan could not be built: {ex.Message}");
    }

    (int exitCode, string stdout, string stderr) =
        RunPowerShell(script, killOnTimeout: false, timeoutMs: 600_000);

    if (exitCode == HelperExit.TimedOutNotKilled)
    {
        return surfaced with
        {
            Success = false,
            Executed = true,
            StateUnknown = true,
            Message =
                "VHDX formatting did not confirm completion and was not cancelled. " +
                "Check Disk Management before retrying.",
            DriveLetter = plan.DriveLetter,
        };
    }

    if (exitCode != 0)
    {
        string detail = Trim(VhdPowerShellScript.RemoveMutationMarker(stderr));
        bool mutationStarted = VhdPowerShellScript.MutationMayHaveStarted(exitCode, stderr);
        return RollBackVhd(
            provisioner,
            surfaced,
            mutationStarted
                ? $"VHDX initialization or formatting failed after disk changes began: {detail}"
                : $"The live VHDX preflight refused the operation: {detail}");
    }

    if (!VhdPowerShellScript.TryParseFinalState(stdout, out VhdFinalState finalState) ||
        finalState.DriveLetter != plan.DriveLetter)
    {
        return surfaced with
        {
            Success = false,
            Executed = true,
            StateUnknown = true,
            Message =
                "Windows completed the VHDX command sequence without a trustworthy matching final-state readback. " +
                "Check Disk Management before retrying.",
            DriveLetter = plan.DriveLetter,
        };
    }

    return surfaced with
    {
        Success = true,
        Executed = true,
        StateUnknown = false,
        RolledBack = false,
        Message =
            $"Created and attached the VHDX, then initialized and formatted {finalState.DriveLetter}: " +
            $"as a {ByteSizeFormatter.Format(finalState.SizeBytes)} ReFS Dev Drive.",
        DriveLetter = finalState.DriveLetter,
        SizeBytes = finalState.SizeBytes,
        FileSystem = finalState.FileSystem,
    };
}

static VhdProvisionResult ExecuteVhdRevert(VhdRevertPlan plan)
{
    if (!plan.ExecuteAuthorized)
    {
        return VhdFailure(
            "Refusing VHDX recovery: this plan is not authorized for execution. Nothing was changed.",
            plan.FilePath);
    }

    if (!TryNormalizeVhdPath(plan.FilePath, out string fullPath, out string validationError))
    {
        return VhdFailure($"{validationError} Nothing was changed.", plan.FilePath);
    }

    if (!File.Exists(fullPath))
    {
        return new VhdProvisionResult
        {
            Success = true,
            Executed = false,
            Message = "The recorded VHDX is already absent.",
            FilePath = fullPath,
        };
    }

    var store = new InMemoryReversibilityStore();
    var provisioner = new VhdProvisioner(new NativeVhdApi(), new SystemFileSystem(), store);
    var entry = new ReversibilityEntry
    {
        Id = VhdProvisioner.ReversibilityId(fullPath),
        Kind = ReversibilityKinds.VhdProvision,
        TargetPath = fullPath,
    };
    store.Save(entry);

    try
    {
        provisioner.RevertAsync(entry).GetAwaiter().GetResult();
        return new VhdProvisionResult
        {
            Success = true,
            Executed = true,
            Message = "Detached and deleted the recorded VHDX.",
            FilePath = fullPath,
        };
    }
    catch (Exception ex)
    {
        return new VhdProvisionResult
        {
            Success = false,
            Executed = true,
            StateUnknown = true,
            Message =
                $"The VHDX could not be fully detached and deleted ({ex.Message}). " +
                "Check Disk Management before retrying.",
            FilePath = fullPath,
        };
    }
}

static VhdProvisionResult RollBackVhd(
    VhdProvisioner provisioner,
    VhdProvisionResult surfaced,
    string reason)
{
    try
    {
        provisioner.RevertAsync(surfaced.ReversibilityId).GetAwaiter().GetResult();
        bool remains = File.Exists(surfaced.FilePath);
        if (!remains)
        {
            return surfaced with
            {
                Success = false,
                Executed = false,
                StateUnknown = false,
                RolledBack = true,
                Message = $"{reason} The new VHDX was detached and deleted.",
            };
        }
    }
    catch (Exception ex)
    {
        reason = $"{reason} Automatic rollback also failed: {ex.Message}";
    }

    return surfaced with
    {
        Success = false,
        Executed = true,
        StateUnknown = true,
        RolledBack = false,
        Message = $"{reason} Check Disk Management before retrying.",
    };
}

static VhdProvisionResult VhdFailure(string message, string filePath = "", char? driveLetter = null) =>
    new()
    {
        Success = false,
        Executed = false,
        Message = message,
        FilePath = filePath,
        DriveLetter = driveLetter is char letter ? char.ToUpperInvariant(letter) : null,
    };

static bool TryValidateVhdCreatePlan(
    VhdProvisionPlan plan,
    out string fullPath,
    out string error)
{
    fullPath = string.Empty;
    error = string.Empty;

    if (!TryNormalizeVhdPath(plan.FilePath, out fullPath, out error))
    {
        return false;
    }

    if (File.Exists(fullPath))
    {
        error = $"A file already exists at '{fullPath}'. Refusing to overwrite or delete it.";
        return false;
    }

    if (plan.VolumeSizeBytes < DevDriveSizeMath.MinimumSizeBytesExact)
    {
        error = "A Dev Drive must be at least 50 GiB.";
        return false;
    }

    if (plan.MaximumSizeBytes <= plan.VolumeSizeBytes)
    {
        error = "The VHD container does not include space for partition metadata.";
        return false;
    }

    char letter = char.ToUpperInvariant(plan.DriveLetter);
    if (letter is < 'D' or > 'Z')
    {
        error = "The target drive letter must be between D and Z.";
        return false;
    }

    return true;
}

static bool TryNormalizeVhdPath(string rawPath, out string fullPath, out string error)
{
    fullPath = string.Empty;
    error = string.Empty;
    if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath))
    {
        error = "The VHDX path must be fully qualified.";
        return false;
    }

    try
    {
        fullPath = Path.GetFullPath(rawPath);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        error = $"The VHDX path is invalid: {ex.Message}";
        return false;
    }

    if (!Path.GetExtension(fullPath).Equals(".vhdx", StringComparison.OrdinalIgnoreCase))
    {
        error = "The backing file must use the .vhdx extension.";
        return false;
    }

    string? parent = Path.GetDirectoryName(fullPath);
    for (string? current = parent; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
    {
        if (IsReparsePoint(current))
        {
            error = "Refusing a VHDX path below a junction or symbolic link.";
            return false;
        }
    }

    if (IsReparsePoint(fullPath))
    {
        error = "Refusing a VHDX path that is a junction or symbolic link.";
        return false;
    }

    return true;
}

static bool IsVhdDryRun() =>
    string.Equals(Environment.GetEnvironmentVariable("DDM_VHD_DRYRUN"), "1", StringComparison.Ordinal);

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

    string script = ResizePowerShellScript.BuildExecute(plan, feasibility, snapshot.DiskNumber);

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
    // explicit, AUTHORIZED --execute + one user confirmation + real elevation.
    // F7: once this begins mutating we must NOT kill it on timeout (aborting a
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
            StateUnknown = true,
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
        bool mutationMayHaveStarted = ResizePowerShellScript.MutationMayHaveStarted(exitCode, stderr);
        string detail = Trim(ResizePowerShellScript.RemoveMutationMarker(stderr));
        return new ResizeExecuteOutcome
        {
            Success = false,
            Executed = mutationMayHaveStarted,
            StateUnknown = mutationMayHaveStarted,
            Message = mutationMayHaveStarted
                ? $"Resize failed after disk changes may have begun: {detail}"
                : $"The live resize preflight failed: {detail} Nothing was changed.",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
            CompletedSteps = Array.Empty<string>(),
        };
    }

    if (!ResizePowerShellScript.TryParseFinalState(stdout, plan, out ResizeFinalState final))
    {
        return new ResizeExecuteOutcome
        {
            Success = false,
            Executed = true,
            StateUnknown = true,
            Message = "The resize command completed, but its final Dev Drive state could not be verified. Inspect Disk Management before retrying.",
            SourceVolumeLetter = source,
            NewDriveLetter = target,
            DevDriveBytes = feasibility.AlignedShrinkBytes,
            CompletedSteps = Array.Empty<string>(),
        };
    }

    return new ResizeExecuteOutcome
    {
        Success = true,
        Executed = true,
        StateUnknown = false,
        Message = $"Shrank {source}: and created a {ByteSizeFormatter.Format(final.FinalPartitionSizeBytes)} ReFS Dev Drive at {final.FinalDriveLetter}:.",
        SourceVolumeLetter = source,
        NewDriveLetter = final.FinalDriveLetter,
        DevDriveBytes = final.FinalPartitionSizeBytes,
        CompletedSteps = feasibility.Steps,
        DiskNumber = final.FinalDiskNumber,
        PartitionNumber = final.FinalPartitionNumber,
        FileSystem = final.FinalFileSystem,
        IsDevDrive = final.IsDevDrive,
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

// ---- disk snapshot (READ-ONLY Storage queries) -----------------------------------------------------

static DiskLayoutSnapshot QueryDiskLayout(char letter)
{
    DiskLayoutSnapshot unresolved = new() { SourceResolved = false, SourceVolumeLetter = letter };

    if (letter is < 'A' or > 'Z')
    {
        return unresolved;
    }

    (int exitCode, string stdout, string _) = RunPowerShell(ResizePowerShellScript.BuildSnapshot(letter));
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

static (int ExitCode, string Stdout, string Stderr) ValidatePowerShellSyntax(string script)
{
    string scriptBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    string parserScript = $$"""
$ErrorActionPreference='Stop'
$code=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{scriptBase64}}'))
$tokens=$null
$errors=$null
[void][System.Management.Automation.Language.Parser]::ParseInput($code,[ref]$tokens,[ref]$errors)
if ($errors.Count -gt 0) { throw (($errors | ForEach-Object { $_.Message }) -join '; ') }
""";
    return RunPowerShell(parserScript, killOnTimeout: true, timeoutMs: 30_000);
}

static string ResolveSystem32Exe(string exe)
{
    string candidate = Path.Combine(Environment.SystemDirectory, exe);
    return File.Exists(candidate)
        ? candidate
        : throw new FileNotFoundException($"Required system executable was not found: {candidate}", candidate);
}

static string ResolvePowerShellPath()
{
    string candidate = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    return File.Exists(candidate)
        ? candidate
        : throw new FileNotFoundException($"Windows PowerShell was not found: {candidate}", candidate);
}

static bool IsDryRun() =>
    string.Equals(Environment.GetEnvironmentVariable("DDM_RESIZE_DRYRUN"), "1", StringComparison.Ordinal);

// ---- input / output helpers ------------------------------------------------------------------------

static ResizePlan? TryDecodePlan(string base64) => TryDecodeJson<ResizePlan>(base64);

static T? TryDecodeJson<T>(string base64)
{
    try
    {
        byte[] bytes = Convert.FromBase64String(base64);
        string json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<T>(json, ResizeJson.ReadOptions);
    }
    catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException)
    {
        return default;
    }
}

static bool WriteJson(string outputPath, object value)
{
    try
    {
        string json = JsonSerializer.Serialize(value, ResizeJson.WriteOptions);
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(json);
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
        // Fail closed when an existing path cannot be inspected.
        return true;
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
