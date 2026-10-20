namespace AtlasDesk.Harness.Tools;

public enum ArgType { String, Integer }

/// <summary>Schema for one tool argument.</summary>
public sealed record ArgSpec(
    string Name,
    ArgType Type,
    bool Required = true,
    int MaxLength = 2000,
    string? Pattern = null,
    long Min = long.MinValue,
    long Max = long.MaxValue);

/// <summary>Per-tool policy. MaxRetries = extra attempts after the first, for transient failures only.</summary>
public sealed record ToolPolicy(
    bool RequiresApproval = false,
    bool HasSideEffects = false,
    int MaxRetries = 2);

public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<ArgSpec> Args,
    ToolPolicy Policy);

public sealed record ToolInvocation(string RunId, ToolArguments Args, string IdempotencyKey);

public sealed record ToolResult(string Content);

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Signals a failure that is worth retrying (timeouts, 503s). Everything else is not retried.</summary>
public sealed class TransientToolException(string message) : Exception(message);
