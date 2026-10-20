using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtlasDesk.Harness.Tools;

namespace AtlasDesk.Harness.Validation;

public abstract record ModelAction;
public sealed record ToolCallAction(string Tool, ToolArguments Args) : ModelAction;
public sealed record FinishAction(string Message) : ModelAction;
public sealed record ClarifyAction(string Message) : ModelAction;

public sealed record ValidationResult(bool IsValid, ModelAction? Action, string Code, string Message)
{
    public static ValidationResult Ok(ModelAction action) => new(true, action, "ok", "ok");
    public static ValidationResult Fail(string code, string message) => new(false, null, code, message);
}

/// <summary>
/// Treats raw model output as untrusted input. Strict by design: unknown fields, duplicate keys,
/// wrong types, unknown tools and oversized payloads are rejected. Messages never echo model content.
/// </summary>
public static class ActionValidator
{
    public const int DefaultMaxBytes = 16 * 1024;
    private const int MaxMessageChars = 4000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static ValidationResult Validate(string? raw, ToolRegistry registry, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ValidationResult.Fail("malformed_json", "Output was empty.");
        if (Encoding.UTF8.GetByteCount(raw) > maxBytes) return ValidationResult.Fail("oversized", "Output exceeds the size limit.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { return ValidationResult.Fail("malformed_json", "Output is not valid JSON (or is nested too deeply)."); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ValidationResult.Fail("not_object", "Output must be a JSON object.");

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (!fields.TryAdd(p.Name, p.Value)) return ValidationResult.Fail("duplicate_field", "Duplicate field in output.");
            }

            if (!fields.TryGetValue("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return ValidationResult.Fail("missing_field", "Field 'type' is required and must be a string.");

            return typeEl.GetString() switch
            {
                "finish" => ValidateMessage(fields, "finish", m => new FinishAction(m)),
                "clarify" => ValidateMessage(fields, "clarify", m => new ClarifyAction(m)),
                "tool_call" => ValidateToolCall(fields, registry),
                _ => ValidationResult.Fail("unknown_action_type", "Action type is not one of: tool_call, finish, clarify."),
            };
        }
    }

    private static ValidationResult ValidateMessage(Dictionary<string, JsonElement> fields, string type, Func<string, ModelAction> make)
    {
        foreach (var k in fields.Keys)
            if (k is not ("type" or "message")) return ValidationResult.Fail("unknown_field", $"Field not allowed for '{type}'.");
        if (!fields.TryGetValue("message", out var m)) return ValidationResult.Fail("missing_field", "Field 'message' is required.");
        if (m.ValueKind != JsonValueKind.String) return ValidationResult.Fail("wrong_type", "Field 'message' must be a string.");
        var text = m.GetString()!;
        if (text.Length > MaxMessageChars) return ValidationResult.Fail("invalid_argument", "Field 'message' is too long.");
        return ValidationResult.Ok(make(text));
    }

    private static ValidationResult ValidateToolCall(Dictionary<string, JsonElement> fields, ToolRegistry registry)
    {
        foreach (var k in fields.Keys)
            if (k is not ("type" or "tool" or "args")) return ValidationResult.Fail("unknown_field", "Field not allowed for 'tool_call'.");
        if (!fields.TryGetValue("tool", out var toolEl)) return ValidationResult.Fail("missing_field", "Field 'tool' is required.");
        if (toolEl.ValueKind != JsonValueKind.String) return ValidationResult.Fail("wrong_type", "Field 'tool' must be a string.");
        if (!fields.TryGetValue("args", out var argsEl)) return ValidationResult.Fail("missing_field", "Field 'args' is required.");
        if (argsEl.ValueKind != JsonValueKind.Object) return ValidationResult.Fail("wrong_type", "Field 'args' must be an object.");

        // Exact match only. "email_customer" is never mapped to "send_customer_email".
        if (!registry.TryResolve(toolEl.GetString()!, out var tool))
            return ValidationResult.Fail("unknown_tool", "Tool is not registered. Use one of the listed tool names exactly.");

        var specs = tool.Definition.Args.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in argsEl.EnumerateObject())
        {
            if (values.ContainsKey(p.Name)) return ValidationResult.Fail("duplicate_field", "Duplicate argument.");
            if (!specs.TryGetValue(p.Name, out var spec)) return ValidationResult.Fail("unknown_field", "Argument is not defined for this tool.");
            var checkedValue = CheckArg(spec, p.Value);
            if (checkedValue.Error is not null) return checkedValue.Error;
            values[p.Name] = checkedValue.Value!;
        }
        foreach (var spec in specs.Values)
            if (spec.Required && !values.ContainsKey(spec.Name))
                return ValidationResult.Fail("missing_field", $"Argument '{spec.Name}' is required.");

        return ValidationResult.Ok(new ToolCallAction(tool.Definition.Name, new ToolArguments(values)));
    }

    private static (object? Value, ValidationResult? Error) CheckArg(ArgSpec spec, JsonElement el)
    {
        switch (spec.Type)
        {
            case ArgType.String:
                if (el.ValueKind != JsonValueKind.String) return (null, ValidationResult.Fail("wrong_type", $"Argument '{spec.Name}' must be a string."));
                var s = el.GetString()!;
                if (s.Length > spec.MaxLength) return (null, ValidationResult.Fail("invalid_argument", $"Argument '{spec.Name}' is too long."));
                if (spec.Pattern is not null && !SafeMatch(spec.Pattern, s))
                    return (null, ValidationResult.Fail("invalid_argument", $"Argument '{spec.Name}' has an invalid format."));
                return (s, null);
            case ArgType.Integer:
                if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var n))
                    return (null, ValidationResult.Fail("wrong_type", $"Argument '{spec.Name}' must be an integer."));
                if (n < spec.Min || n > spec.Max) return (null, ValidationResult.Fail("invalid_argument", $"Argument '{spec.Name}' is out of range."));
                return (n, null);
            default:
                return (null, ValidationResult.Fail("invalid_argument", "Unsupported argument type."));
        }
    }

    private static bool SafeMatch(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant, RegexTimeout); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
