using System.Diagnostics;
using OpenTelemetry;

namespace AtlasDesk.Harness.Telemetry;

public static class HarnessTelemetry
{
    public const string SourceName = "AtlasDesk.Harness";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    /// <summary>Sets a tag after redaction. Every harness tag goes through here.</summary>
    public static void Tag(this Activity? activity, string key, object? value)
    {
        if (activity is null || value is null) return;
        activity.SetTag(key, value is string s ? Redactor.Redact(s) : value);
    }
}

/// <summary>
/// Defense in depth for any OpenTelemetry pipeline: scrubs string tags and drops tag keys that
/// are known to carry customer content, even if some other code path set them.
/// </summary>
public sealed class RedactingActivityProcessor : BaseProcessor<Activity>
{
    private static readonly string[] DeniedPrefixes = ["customer.", "tool.args", "tool.result", "email.", "ticket.body"];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in activity.TagObjects.ToList())
        {
            if (DeniedPrefixes.Any(p => tag.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                activity.SetTag(tag.Key, null);
            else if (tag.Value is string s)
            {
                var redacted = Redactor.Redact(s);
                if (!ReferenceEquals(redacted, s) && redacted != s) activity.SetTag(tag.Key, redacted);
            }
        }
        if (activity.StatusDescription is { } d) activity.SetStatus(activity.Status, Redactor.Redact(d));
    }
}
