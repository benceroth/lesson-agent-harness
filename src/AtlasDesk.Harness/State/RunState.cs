using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlasDesk.Harness.State;

public enum RunStatus
{
    Running,
    Completed,
    NeedsClarification,
    StepLimit,
    DeadlineExceeded,
    TokenLimit,
    CostLimit,
    ToolCallLimit,
    InvalidOutputLimit,
    Failed,
}

public static class RunStatusExtensions
{
    public static bool IsTerminal(this RunStatus s) => s != RunStatus.Running;

    /// <summary>Only a model-issued, validated "finish" counts as success.</summary>
    public static bool IsSuccess(this RunStatus s) => s == RunStatus.Completed;

    public static string ToCode(this RunStatus s) => s switch
    {
        RunStatus.Running => "running",
        RunStatus.Completed => "completed",
        RunStatus.NeedsClarification => "needs_clarification",
        RunStatus.StepLimit => "turn_limit",
        RunStatus.DeadlineExceeded => "deadline_exceeded",
        RunStatus.TokenLimit => "token_limit",
        RunStatus.CostLimit => "cost_limit",
        RunStatus.ToolCallLimit => "tool_call_limit",
        RunStatus.InvalidOutputLimit => "invalid_output_limit",
        _ => "failed",
    };
}

/// <summary>What the model is told about the previous step. Tool output is always marked untrusted.</summary>
public sealed record Observation(string Kind, string? Tool, string Content, bool Untrusted);

public sealed record StepRecord(int Step, string Kind, string? Tool, string? ActionHash, string Outcome);

/// <summary>
/// Durable, structured run state. Security-relevant facts (approvals, completed side effects)
/// live here, not in any prompt or summary.
/// </summary>
public sealed class RunState
{
    public string RunId { get; set; } = "";
    public string TicketId { get; set; } = "";
    [JsonInclude] public RunStatus Status { get; private set; } = RunStatus.Running;
    [JsonInclude] public string? StopDetail { get; private set; }
    public string? FinalMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public int Steps { get; set; }
    public int ToolAttempts { get; set; }
    public int InvalidOutputs { get; set; }
    public long TokensUsed { get; set; }
    public decimal CostUsd { get; set; }
    public List<StepRecord> History { get; set; } = [];
    public Dictionary<string, string> CompletedEffects { get; set; } = [];
    public Observation? LastObservation { get; set; }

    /// <summary>Terminal states are final: once set they cannot change.</summary>
    public void Terminate(RunStatus status, string? detail = null)
    {
        if (status == RunStatus.Running) throw new ArgumentException("Terminate requires a terminal status.", nameof(status));
        if (Status.IsTerminal()) return;
        Status = status;
        StopDetail = detail;
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static RunState FromJson(string json) => JsonSerializer.Deserialize<RunState>(json, Json)!;
}
