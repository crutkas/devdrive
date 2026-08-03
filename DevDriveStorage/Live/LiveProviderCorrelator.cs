namespace DevDriveStorage.Live;

/// <summary>
/// Infers a <see cref="StorageProviderContext"/> from a node's name and path. This is
/// deliberately best-effort correlation: it fills the Details pane's provider card by
/// recognising well-known developer artifacts, and marks its findings <see
/// cref="ProviderAvailability.Stale"/> because they are inferred from the path shape
/// rather than confirmed by the owning tool.
/// </summary>
internal static class LiveProviderCorrelator
{
    public static StorageProviderContext? Correlate(
        string name,
        string physicalPath,
        StorageNodeKind kind,
        DateTimeOffset observedAtUtc)
    {
        if (kind == StorageNodeKind.File)
        {
            if (name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
            {
                bool wsl = physicalPath.Contains("wsl", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("ext4", StringComparison.OrdinalIgnoreCase);
                return new StorageProviderContext(
                    wsl ? "WSL2 distribution" : "Virtual disk",
                    name,
                    wsl
                        ? "Inferred from a WSL2 .vhdx virtual disk."
                        : "Inferred from a .vhdx / .vhd virtual disk file.",
                    ProviderAvailability.Stale,
                    observedAtUtc);
            }

            return null;
        }

        string[] segments = physicalPath.Split(
            ['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        foreach ((string ProviderName, string Evidence) match in MatchFolder(name, segments))
        {
            return new StorageProviderContext(
                match.ProviderName,
                name,
                match.Evidence,
                ProviderAvailability.Stale,
                observedAtUtc);
        }

        return null;
    }

    private static IEnumerable<(string ProviderName, string Evidence)> MatchFolder(
        string name,
        string[] segments)
    {
        if (Equals(name, "node_modules"))
        {
            yield return ("npm", "Inferred from a node_modules dependency tree.");
            yield break;
        }

        if (Equals(name, ".cargo") || HasSegmentPair(segments, ".cargo", "registry"))
        {
            yield return ("Cargo", "Inferred from a Cargo registry / cache directory.");
            yield break;
        }

        if (HasSegmentPair(segments, ".nuget", "packages"))
        {
            yield return ("NuGet", "Inferred from the .nuget\\packages global cache.");
            yield break;
        }

        if (HasSegmentPair(segments, "pip", "Cache") || HasSegmentPair(segments, "pip", "cache"))
        {
            yield return ("pip", "Inferred from a pip download cache.");
            yield break;
        }

        if (HasSegmentPair(segments, "vcpkg", "archives"))
        {
            yield return ("vcpkg", "Inferred from a vcpkg binary archive cache.");
            yield break;
        }

        if (Equals(name, "target") && HasSegment(segments, "cargo") is false &&
            LooksLikeBuildOutput(segments, "target"))
        {
            yield return ("Cargo build output", "Inferred from a Rust target\\ directory.");
            yield break;
        }

        if (Equals(name, "obj") || Equals(name, "bin"))
        {
            yield return (".NET build output", $"Inferred from a {name}\\ build directory.");
            yield break;
        }

        if (Equals(name, "target"))
        {
            yield return ("Build output", "Inferred from a target\\ build directory.");
        }
    }

    private static bool LooksLikeBuildOutput(string[] segments, string leaf) =>
        segments.Length >= 2 &&
        string.Equals(segments[^1], leaf, StringComparison.OrdinalIgnoreCase);

    private static bool Equals(string value, string other) =>
        string.Equals(value, other, StringComparison.OrdinalIgnoreCase);

    private static bool HasSegment(string[] segments, string segment) =>
        segments.Any(s => string.Equals(s, segment, StringComparison.OrdinalIgnoreCase));

    private static bool HasSegmentPair(string[] segments, string first, string second)
    {
        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (string.Equals(segments[i], first, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[i + 1], second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
