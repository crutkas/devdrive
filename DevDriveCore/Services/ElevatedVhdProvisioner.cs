using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Persists a user-side recovery receipt and delegates the complete VHDX transaction to the bundled
/// elevated helper.
/// </summary>
public sealed class ElevatedVhdProvisioner : IVhdProvisioner
{
    private readonly IElevatedVhdBroker _broker;
    private readonly IFileSystem _fileSystem;
    private readonly IReversibilityStore _reversibility;

    public ElevatedVhdProvisioner(
        IElevatedVhdBroker broker,
        IFileSystem fileSystem,
        IReversibilityStore reversibility)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _reversibility = reversibility ?? throw new ArgumentNullException(nameof(reversibility));
    }

    public static ElevatedVhdProvisioner CreateDefault() =>
        new(
            new ElevatedVhdBroker(),
            new SystemFileSystem(),
            new JsonFileReversibilityStore(JsonFileReversibilityStore.DefaultPath));

    /// <inheritdoc />
    public async Task<VhdProvisionResult> ProvisionAsync(
        VhdProvisionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ValidateCreatePlan(plan);
        string fullPath = Path.GetFullPath(plan.FilePath);
        if (_fileSystem.FileExists(fullPath))
        {
            throw new IOException(
                $"A file already exists at '{fullPath}'. Refusing to overwrite or delete it.");
        }

        VhdProvisionPlan authorizedPlan = plan with
        {
            FilePath = fullPath,
            DriveLetter = char.ToUpperInvariant(plan.DriveLetter),
            Label = string.IsNullOrWhiteSpace(plan.Label) ? "DevDrive" : plan.Label.Trim(),
            ExecuteAuthorized = true,
        };
        string planJson = JsonSerializer.Serialize(authorizedPlan, JsonOptions);
        var entry = new ReversibilityEntry
        {
            Id = VhdProvisioner.ReversibilityId(fullPath),
            Kind = ReversibilityKinds.VhdProvision,
            TimestampUtc = DateTimeOffset.UtcNow,
            TargetPath = fullPath,
        };

        cancellationToken.ThrowIfCancellationRequested();
        _reversibility.Save(entry);

        string? resultJson = await _broker.InvokeAsync(
            new VhdBrokerRequest { Mode = VhdBrokerMode.Create, PlanJson = planJson },
            cancellationToken).ConfigureAwait(false);

        if (resultJson is null)
        {
            _reversibility.Remove(entry.Id);
            return new VhdProvisionResult
            {
                Success = false,
                Executed = false,
                Message = "Administrator approval was declined or the bundled elevated helper is unavailable. Nothing was changed.",
                FilePath = fullPath,
                DriveLetter = authorizedPlan.DriveLetter,
            };
        }

        VhdProvisionResult result;
        try
        {
            result = JsonSerializer.Deserialize<VhdProvisionResult>(resultJson, JsonOptions)
                ?? throw new JsonException("The elevated helper returned an empty result.");
        }
        catch (JsonException ex)
        {
            return StateUnknown(
                authorizedPlan,
                entry.Id,
                $"The elevated helper returned an unreadable result ({ex.Message}). Check Disk Management before retrying.");
        }

        result = result with
        {
            FilePath = string.IsNullOrWhiteSpace(result.FilePath) ? fullPath : result.FilePath,
            ReversibilityId = entry.Id,
        };

        if (result.Success && !IsTrustworthySuccess(result, authorizedPlan))
        {
            return StateUnknown(
                authorizedPlan,
                entry.Id,
                "The elevated helper reported success without a complete matching final-state readback. " +
                "Check Disk Management before retrying.");
        }

        if (!result.Success && !result.StateUnknown && (!result.Executed || result.RolledBack))
        {
            _reversibility.Remove(entry.Id);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task RevertAsync(ReversibilityEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != ReversibilityKinds.VhdProvision || string.IsNullOrWhiteSpace(entry.TargetPath))
        {
            throw new ArgumentException("The recovery entry is not a VHDX provisioning receipt.", nameof(entry));
        }

        string fullPath = Path.GetFullPath(entry.TargetPath);
        if (!_fileSystem.FileExists(fullPath))
        {
            _reversibility.Remove(entry.Id);
            return;
        }

        string planJson = JsonSerializer.Serialize(
            new VhdRevertPlan { FilePath = fullPath, ExecuteAuthorized = true },
            JsonOptions);
        string? resultJson = await _broker.InvokeAsync(
            new VhdBrokerRequest { Mode = VhdBrokerMode.Revert, PlanJson = planJson },
            cancellationToken).ConfigureAwait(false);

        if (resultJson is null)
        {
            throw new InvalidOperationException(
                "Administrator approval was declined or the bundled elevated helper is unavailable. The VHDX was not reverted.");
        }

        VhdProvisionResult result = JsonSerializer.Deserialize<VhdProvisionResult>(resultJson, JsonOptions)
            ?? throw new InvalidOperationException("The elevated helper returned an empty VHDX revert result.");
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }

        _reversibility.Remove(entry.Id);
    }

    private static void ValidateCreatePlan(VhdProvisionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.FilePath);
        if (!Path.IsPathFullyQualified(plan.FilePath) ||
            !Path.GetExtension(plan.FilePath).Equals(".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The VHDX path must be a fully qualified .vhdx file path.", nameof(plan));
        }

        if (plan.VolumeSizeBytes < DevDriveSizeMath.MinimumSizeBytesExact)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "A Dev Drive must be at least 50 GiB.");
        }

        if (plan.MaximumSizeBytes <= plan.VolumeSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The VHD container must include partition metadata headroom.");
        }

        char letter = char.ToUpperInvariant(plan.DriveLetter);
        if (letter is < 'D' or > 'Z')
        {
            throw new ArgumentException("The target drive letter must be between D and Z.", nameof(plan));
        }
    }

    private static bool IsTrustworthySuccess(VhdProvisionResult result, VhdProvisionPlan plan) =>
        result.Executed &&
        !result.StateUnknown &&
        result.DiskNumber is >= 0 &&
        !string.IsNullOrWhiteSpace(result.PhysicalPath) &&
        result.DriveLetter == plan.DriveLetter &&
        SizesMatch(result.SizeBytes, plan.VolumeSizeBytes) &&
        result.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase) &&
        PathsEqual(result.FilePath, plan.FilePath);

    private static bool SizesMatch(ulong actual, ulong expected)
    {
        ulong delta = actual > expected ? actual - expected : expected - actual;
        return delta <= 1024UL * 1024UL;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static VhdProvisionResult StateUnknown(
        VhdProvisionPlan plan,
        string reversibilityId,
        string message) =>
        new()
        {
            Success = false,
            Executed = true,
            StateUnknown = true,
            Message = message,
            FilePath = plan.FilePath,
            DriveLetter = plan.DriveLetter,
            ReversibilityId = reversibilityId,
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
