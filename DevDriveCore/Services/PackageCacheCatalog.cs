namespace DevDriveCore.Services;

/// <summary>
/// Static definition of a developer tool's package cache: its display name, the environment variable
/// that relocates the cache, and the unexpanded default path template used when that variable is unset.
/// </summary>
/// <param name="Name">Display name, e.g. "npm".</param>
/// <param name="EnvironmentVariable">Variable that points the tool at its cache, e.g. <c>npm_config_cache</c>.</param>
/// <param name="DefaultPathTemplate">Unexpanded default path, e.g. <c>%AppData%\npm-cache</c>.</param>
public sealed record PackageCacheDefinition(string Name, string EnvironmentVariable, string DefaultPathTemplate);

/// <summary>
/// The built-in catalogue of package caches the app understands. Environment-variable names and
/// default locations are the documented, real ones for each tool.
/// </summary>
public static class PackageCacheCatalog
{
    /// <summary>
    /// The default catalogue. The headline tools are the package managers most developers use
    /// (npm, NuGet, pip, uv, Cargo, vcpkg); Gradle/Go/Yarn are the "optional if easy" additions
    /// (each has a clean relocation environment variable).
    /// </summary>
    /// <remarks>
    /// "NuGet global packages" (<c>NUGET_PACKAGES</c>, <c>%UserProfile%\.nuget\packages</c>) is the single
    /// folder shared by NuGet, dotnet, MSBuild, and Visual Studio — so it is listed once, not duplicated
    /// per consumer.
    /// </remarks>
    public static IReadOnlyList<PackageCacheDefinition> Default { get; } = new[]
    {
        // npm >= 5 defaults its cache to LOCAL AppData (not Roaming). The authoritative path is
        // resolved at runtime by NpmCacheLocator (npm_config_cache -> `npm config get cache` -> this
        // default); this template is the last-resort fallback only.
        new PackageCacheDefinition("npm", "npm_config_cache", @"%LocalAppData%\npm-cache"),
        new PackageCacheDefinition("NuGet global packages", "NUGET_PACKAGES", @"%UserProfile%\.nuget\packages"),
        new PackageCacheDefinition("pip", "PIP_CACHE_DIR", @"%LocalAppData%\pip\Cache"),
        // uv (astral-sh/uv): the fast Python package manager. Cache relocates via UV_CACHE_DIR; default
        // on Windows is %LocalAppData%\uv\cache.
        new PackageCacheDefinition("uv", "UV_CACHE_DIR", @"%LocalAppData%\uv\cache"),
        // Poetry: dominant Python packaging tool. POETRY_CACHE_DIR relocates the wheel/sdist cache.
        new PackageCacheDefinition("Poetry", "POETRY_CACHE_DIR", @"%LocalAppData%\pypoetry\Cache"),
        new PackageCacheDefinition("Cargo", "CARGO_HOME", @"%UserProfile%\.cargo"),
        new PackageCacheDefinition("vcpkg", "VCPKG_DEFAULT_BINARY_CACHE", @"%LocalAppData%\vcpkg\archives"),
        new PackageCacheDefinition("Gradle", "GRADLE_USER_HOME", @"%UserProfile%\.gradle"),
        new PackageCacheDefinition("Go modules", "GOMODCACHE", @"%UserProfile%\go\pkg\mod"),
        new PackageCacheDefinition("Yarn", "YARN_CACHE_FOLDER", @"%LocalAppData%\Yarn\Cache"),
        // pnpm: the content-addressable store is relocated via the `store-dir` setting, which pnpm reads
        // from the PNPM_CONFIG_STORE_DIR environment variable (NOT PNPM_HOME — that only moves the global
        // bin/home, not the package store). pnpm keeps a SEPARATE store per drive for hardlinking, so a
        // moved store most benefits projects on that same drive. Default store on Windows: %LocalAppData%\pnpm\store.
        new PackageCacheDefinition("pnpm", "PNPM_CONFIG_STORE_DIR", @"%LocalAppData%\pnpm\store"),
        // Bun: BUN_INSTALL_CACHE_DIR points directly at the global install cache.
        new PackageCacheDefinition("Bun", "BUN_INSTALL_CACHE_DIR", @"%UserProfile%\.bun\install\cache"),
        // Deno: DENO_DIR is the cache root (downloaded modules + npm interop cache).
        new PackageCacheDefinition("Deno", "DENO_DIR", @"%LocalAppData%\deno"),
        // Pub (Dart/Flutter): PUB_CACHE is the global package cache root.
        new PackageCacheDefinition("Pub (Dart/Flutter)", "PUB_CACHE", @"%LocalAppData%\Pub\Cache"),
    };
}
