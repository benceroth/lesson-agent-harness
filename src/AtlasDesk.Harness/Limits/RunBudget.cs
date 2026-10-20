using AtlasDesk.Harness.State;

namespace AtlasDesk.Harness.Limits;

/// <summary>Budgets owned by the harness, not the prompt. Defaults mirror the AtlasDesk example in the post.</summary>
public sealed record RunBudget
{
    public int MaxSteps { get; init; } = 8;
    public int MaxToolAttempts { get; init; } = 12;
    public long MaxTokens { get; init; } = 20_000;
    public decimal MaxCostUsd { get; init; } = 0.50m;
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxInvalidOutputs { get; init; } = 2;
    public int MaxOutputBytes { get; init; } = 16 * 1024;
    public int MaxToolResultChars { get; init; } = 8_000;
}

public static class BudgetGuard
{
    /// <summary>Returns the terminal status to apply if a budget is already exhausted, otherwise null.</summary>
    public static (RunStatus Status, string Detail)? CheckBeforeModelCall(RunState s, RunBudget b, DateTimeOffset now)
    {
        if (now >= s.DeadlineUtc) return (RunStatus.DeadlineExceeded, "run deadline reached");
        if (s.Steps >= b.MaxSteps) return (RunStatus.StepLimit, $"max steps {b.MaxSteps}");
        return CheckUsage(s, b);
    }

    public static (RunStatus Status, string Detail)? CheckUsage(RunState s, RunBudget b)
    {
        if (s.TokensUsed > b.MaxTokens) return (RunStatus.TokenLimit, $"max tokens {b.MaxTokens}");
        if (s.CostUsd > b.MaxCostUsd) return (RunStatus.CostLimit, $"max cost {b.MaxCostUsd}");
        return null;
    }

    public static (RunStatus Status, string Detail)? CheckBeforeToolAttempt(RunState s, RunBudget b, DateTimeOffset now)
    {
        if (now >= s.DeadlineUtc) return (RunStatus.DeadlineExceeded, "run deadline reached");
        if (s.ToolAttempts >= b.MaxToolAttempts) return (RunStatus.ToolCallLimit, $"max tool attempts {b.MaxToolAttempts}");
        return null;
    }
}
