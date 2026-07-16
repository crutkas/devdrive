using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Default <see cref="IPackageCacheService"/>. Composes an <see cref="IEnvironmentProvider"/> and an
/// <see cref="IFileSystemProbe"/> so all detection/classification/sizing logic is unit-testable
/// against an in-memory environment and filesystem. No mutation occurs anywhere in this type.
/// </summary>
public sealed class PackageCacheService : IPackageCacheService
{
    private readonly IEnvironmentProvider _environment;
    private readonly IFileSystemProbe _fileSystem;
    private readonly IReadOnlyList<PackageCacheDefinition> _catalog;
    private readonly NpmCacheLocator? _npmLocator;

    /// <summary>Creates a service over the supplied environment/filesystem and (optional) catalogue.</summary>
    /// <param name="environment">Read-only environment-variable provider.</param>
    /// <param name="fileSystem">Read-only filesystem probe (directory existence/size).</param>
    /// <param name="catalog">Optional catalogue override; defaults to <see cref="PackageCacheCatalog.Default"/>.</param>
    /// <param name="npmProcessRunner">
    /// Optional process-runner seam. When supplied, npm detection additionally consults
    /// <c>npm config get cache</c> (read-only) so a shell/process-level <c>npm_config_cache</c> recorded
    /// in npmrc is honoured. When <c>null</c>, npm falls back to env-var → default only (no process is
    /// launched — keeps unit tests hermetic).
    /// </param>
    public PackageCacheService(
        IEnvironmentProvider environment,
        IFileSystemProbe fileSystem,
        IReadOnlyList<PackageCacheDefinition>? catalog = null,
        IProcessRunner? npmProcessRunner = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _catalog = catalog ?? PackageCacheCatalog.Default;
        _npmLocator = npmProcessRunner is null ? null : new NpmCacheLocator(_environment, npmProcessRunner);
    }

    /// <summary>
    /// Convenience factory wiring the real environment + filesystem probes, plus a short-timeout
    /// process runner so npm's cache can be resolved authoritatively via <c>npm config get cache</c>.
    /// Because that probe launches a process, <see cref="GetPackageCaches"/> must be called off the UI
    /// thread (callers use <c>Task.Run</c>).
    /// </summary>
    public static PackageCacheService CreateDefault() =>
        new(new SystemEnvironmentProvider(), new FileSystemProbe(), catalog: null, npmProcessRunner: new ProcessRunner(6_000));

    /// <inheritdoc />
    public IReadOnlyList<PackageCacheInfo> GetPackageCaches(char? devDriveLetter)
    {
        char? dev = devDriveLetter is char letter ? char.ToUpperInvariant(letter) : null;
        var result = new List<PackageCacheInfo>(_catalog.Count);
        foreach (PackageCacheDefinition definition in _catalog)
        {
            result.Add(Resolve(definition, dev));
        }

        return result;
    }

    /// <inheritdoc />
    public Task<ulong> CalculateSizeAsync(PackageCacheInfo cache, TimeSpan timeBudget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (!cache.Detected || string.IsNullOrWhiteSpace(cache.ResolvedPath))
        {
            return Task.FromResult(0UL);
        }

        return _fileSystem.GetDirectorySizeAsync(cache.ResolvedPath, timeBudget, cancellationToken);
    }

    /// <inheritdoc />
    public PackageCacheMovePlan BuildMovePlan(PackageCacheInfo cache, char devDriveLetter)
    {
        ArgumentNullException.ThrowIfNull(cache);
        char dev = char.ToUpperInvariant(devDriveLetter);
        string target = $@"{dev}:\packages\{FolderToken(cache.Name)}";

        return new PackageCacheMovePlan
        {
            ToolName = cache.Name,
            SourcePath = cache.ResolvedPath,
            TargetPath = target,
            EnvironmentVariable = cache.EnvironmentVariable,
        };
    }

    private PackageCacheInfo Resolve(PackageCacheDefinition definition, char? dev)
    {
        string rawPath;
        string? environmentValue;

        // npm gets authoritative 3-step resolution (env -> `npm config get cache` -> default) when a
        // process runner is wired; every other tool uses the generic env-var -> default-template path.
        if (_npmLocator is not null && IsNpm(definition))
        {
            NpmCacheResolution npm = _npmLocator.Resolve();
            rawPath = npm.RawPath;
            environmentValue = npm.Source == NpmCacheSource.EnvironmentVariable ? npm.RawPath : null;
        }
        else
        {
            string? rawEnvValue = _environment.GetEnvironmentVariable(definition.EnvironmentVariable);
            bool envSet = !string.IsNullOrWhiteSpace(rawEnvValue);
            rawPath = envSet ? rawEnvValue! : definition.DefaultPathTemplate;
            environmentValue = envSet ? rawEnvValue : null;
        }

        string expanded = _environment.ExpandEnvironmentVariables(rawPath);
        string resolved = PathHelpers.NormalizeFullPath(expanded);
        char? drive = PathHelpers.DriveLetterOf(resolved);
        bool detected = _fileSystem.DirectoryExists(resolved);
        bool onDev = dev is char devLetter
            && drive is char driveLetter
            && char.ToUpperInvariant(driveLetter) == devLetter;

        return new PackageCacheInfo
        {
            Name = definition.Name,
            PathTemplate = definition.DefaultPathTemplate,
            EnvironmentVariable = definition.EnvironmentVariable,
            EnvironmentValue = environmentValue,
            ResolvedPath = resolved,
            Detected = detected,
            DriveLetter = drive,
            OnDevDrive = onDev,
        };
    }

    private static bool IsNpm(PackageCacheDefinition definition) =>
        string.Equals(definition.EnvironmentVariable, NpmCacheLocator.EnvironmentVariable, StringComparison.OrdinalIgnoreCase);

    private static string FolderToken(string toolName)
    {
        var builder = new System.Text.StringBuilder(toolName.Length);
        foreach (char c in toolName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (c is ' ' or '-' or '_' && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string token = builder.ToString().Trim('-');
        return token.Length == 0 ? "cache" : token;
    }
}
