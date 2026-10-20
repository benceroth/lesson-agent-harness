using System.Text.RegularExpressions;

namespace AtlasDesk.Harness.Telemetry;

/// <summary>Masks common secret and PII shapes. Pattern based: a safety net, not a substitute for not recording content.</summary>
public static partial class Redactor
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (BearerRegex(), "[REDACTED:token]"),
        (KeyValueSecretRegex(), "$1=[REDACTED:secret]"),
        (ApiKeyRegex(), "[REDACTED:key]"),
        (EmailRegex(), "[REDACTED:email]"),
        (CardRegex(), "[REDACTED:card]"),
        (PhoneRegex(), "[REDACTED:phone]"),
    ];

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var result = value;
        foreach (var (pattern, replacement) in Rules) result = pattern.Replace(result, replacement);
        return result;
    }

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.None, 200)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)\b(api[_-]?key|password|passwd|secret|token|authorization)\s*[=:]\s*\S+", RegexOptions.None, 200)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"\b(?:sk|pk|ghp|xox[a-z])[-_][A-Za-z0-9_-]{10,}", RegexOptions.None, 200)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.None, 200)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.None, 200)]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"(?<!\w)\+?\d[\d ().-]{8,}\d\b", RegexOptions.None, 200)]
    private static partial Regex PhoneRegex();
}
