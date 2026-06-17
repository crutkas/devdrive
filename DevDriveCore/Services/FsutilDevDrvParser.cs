using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Parses the textual output of <c>fsutil devdrv query &lt;X:&gt;</c> into a
/// <see cref="DevDriveTrustInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>fsutil devdrv query</c> requires elevation; unelevated it prints "Failed to open the
/// volume. / Error 5: Access is denied." and exits non-zero. That case (and any other case where
/// nothing could be determined) returns <c>null</c> so the UI can show "Run as admin to see
/// filters" instead of an error.
/// </para>
/// <para>
/// <b>Unverified:</b> the exact elevated label strings could not be captured on this (unelevated)
/// machine, and Microsoft Learn does not document a literal sample. The parser is therefore
/// intentionally keyword-tolerant: it keys off substrings ("trust", "attached", "allowed",
/// "antivirus") and the fsutil house "Key : Value" style, and also accepts sentence forms such as
/// "This is a trusted developer volume." This handles the realistic shapes; see the unit tests.
/// </para>
/// </remarks>
public static class FsutilDevDrvParser
{
    private static readonly char[] FilterSeparators = { ',', ';' };

    private static readonly HashSet<string> NonFilterTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "(all)", "all", "none", "(none)", "<none>", "n/a", "na", "-",
    };

    /// <summary>Parses fsutil output. Returns <c>null</c> when nothing could be determined (e.g. access denied).</summary>
    public static DevDriveTrustInfo? Parse(int exitCode, string? standardOutput, string? standardError)
    {
        string combined = standardOutput ?? string.Empty;
        if (!string.IsNullOrEmpty(standardError))
        {
            combined += "\n" + standardError;
        }

        if (IndicatesAccessDenied(combined))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(combined))
        {
            return null;
        }

        // Group-policy enforcement of the antivirus filter ("…protected by antivirus filter, by group
        // policy" on managed PCs) means Defender performance mode is controlled by the organization and
        // cannot be toggled locally. fsutil only emits this phrase in that context, so a substring match
        // is sufficient (and keeps the honest "controlled by your organization" framing).
        bool policyEnforced = combined.Contains("group policy", StringComparison.OrdinalIgnoreCase);

        var trust = DevDriveTrustState.Unknown;
        bool performanceMode = false;
        bool sawDevDriveSignal = false;
        var attached = new List<string>();
        var allowed = new List<string>();
        string? section = null; // "attached" | "allowed" for indented continuation lines

        foreach (string rawLine in combined.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                section = null;
                continue;
            }

            int colon = rawLine.IndexOf(':');
            bool indentedContinuation = colon < 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');

            if (indentedContinuation && section is not null)
            {
                AddFilters(section == "attached" ? attached : allowed, rawLine);
                continue;
            }

            string key = (colon >= 0 ? rawLine[..colon] : rawLine).Trim();
            string value = colon >= 0 ? rawLine[(colon + 1)..].Trim() : string.Empty;
            string keyLower = key.ToLowerInvariant();
            string valueLower = value.ToLowerInvariant();
            section = null;

            bool mentionsDevVolume = keyLower.Contains("developer volume") || keyLower.Contains("dev drive") || keyLower.Contains("dev volume");
            if (mentionsDevVolume)
            {
                sawDevDriveSignal = true;
            }

            // Antivirus / performance-mode signal (check before the generic "allowed" filter branch).
            if (keyLower.Contains("antivirus") || keyLower.Contains("performance"))
            {
                performanceMode = InterpretPerformanceMode(keyLower, valueLower) ?? performanceMode;
            }
            else if (keyLower.Contains("attach"))
            {
                AddFilters(attached, value);
                section = "attached";
            }
            else if (keyLower.Contains("allow") && keyLower.Contains("filter"))
            {
                AddFilters(allowed, value);
                section = "allowed";
            }

            // Trust state can come from an explicit "trusted" line or from the dev-volume line/value.
            if (keyLower.Contains("trust"))
            {
                ApplyTrust(ref trust, valueLower.Length > 0 ? valueLower : keyLower);
            }
            else if (colon < 0 && mentionsDevVolume)
            {
                ApplyTrust(ref trust, keyLower); // sentence form: "This is a trusted developer volume."
            }
            else if (mentionsDevVolume && (valueLower.Contains("trust")))
            {
                ApplyTrust(ref trust, valueLower);
            }
        }

        if (trust == DevDriveTrustState.Unknown && attached.Count == 0 && allowed.Count == 0 && !sawDevDriveSignal)
        {
            // Learned nothing AND the command failed -> treat as undeterminable.
            if (exitCode != 0)
            {
                return null;
            }
        }

        return new DevDriveTrustInfo
        {
            TrustState = trust,
            PerformanceModeOn = performanceMode,
            AntivirusPolicyEnforced = policyEnforced,
            AttachedFilters = attached,
            AllowedFilters = allowed,
        };
    }

    /// <summary>True when the output is the unelevated "Access is denied" failure.</summary>
    public static bool IndicatesAccessDenied(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Error 5", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Failed to open the volume", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyTrust(ref DevDriveTrustState trust, string text)
    {
        // "untrusted" / "not trusted" / "trusted : no" all mean Untrusted.
        if (text.Contains("untrust") || text.Contains("not trusted"))
        {
            trust = DevDriveTrustState.Untrusted;
            return;
        }

        // Explicit no for a trust line.
        if (StartsWithWord(text, "no") || text.Contains(": no"))
        {
            trust = DevDriveTrustState.Untrusted;
            return;
        }

        if (text.Contains("trusted") || StartsWithWord(text, "yes"))
        {
            if (trust != DevDriveTrustState.Untrusted)
            {
                trust = DevDriveTrustState.Trusted;
            }
        }
    }

    private static bool? InterpretPerformanceMode(string keyLower, string valueLower)
    {
        // "Performance mode : On/Off"
        if (keyLower.Contains("performance"))
        {
            if (valueLower.Contains("on") || valueLower.Contains("enabled")) return true;
            if (valueLower.Contains("off") || valueLower.Contains("disabled")) return false;
        }

        // "Antivirus filter allowed ... : No"  => AV not allowed to attach => performance mode ON.
        if (keyLower.Contains("antivirus"))
        {
            if (valueLower.Contains("not allowed") || valueLower.Contains("disallow") || StartsWithWord(valueLower, "no"))
            {
                return true;
            }

            if (valueLower.Contains("allowed") || StartsWithWord(valueLower, "yes"))
            {
                return false;
            }
        }

        return null;
    }

    private static void AddFilters(List<string> target, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string piece in text.Split(FilterSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Strip list markers like "[0]" or leading bullets.
            string token = piece.TrimStart('[', ']', '-', '*', '\t', ' ');
            int bracket = token.IndexOf(']');
            if (bracket >= 0 && bracket < token.Length - 1)
            {
                token = token[(bracket + 1)..].Trim();
            }

            // Drop a trailing parenthetical description, e.g. "luafv (Layered Driver)".
            int paren = token.IndexOf('(');
            if (paren > 0)
            {
                token = token[..paren].Trim();
            }

            if (token.Length == 0 || NonFilterTokens.Contains(token))
            {
                continue;
            }

            if (!target.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(token);
            }
        }
    }

    private static bool StartsWithWord(string text, string word)
    {
        text = text.TrimStart();
        if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Length == word.Length || !char.IsLetter(text[word.Length]);
    }
}
