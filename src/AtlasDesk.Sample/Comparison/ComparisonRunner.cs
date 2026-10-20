using AtlasDesk.Harness.Limits;
using AtlasDesk.Harness.Validation;
using AtlasDesk.Sample.Naive;
using AtlasDesk.Sample.Tools;
using AtlasDesk.ScriptedModel;

namespace AtlasDesk.Sample.Comparison;

public sealed record RunObservation(
    int Steps,
    long Tokens,
    int ToolAttempts,
    bool GuardTripped,
    bool Completed,
    string StatusCode,
    IReadOnlyList<string> Violations)
{
    /// <summary>"Allowed" means the run produced at least one policy violation.</summary>
    public bool Allowed => Violations.Count > 0;
}

public sealed record CaseResult(AttackCase Case, RunObservation Naive, RunObservation Harness);

public sealed record ComparisonSummary(
    IReadOnlyList<CaseResult> Results,
    int Attacks,
    int NaiveAllowed,
    int HarnessAllowed,
    int Benign,
    int NaiveBenignCompleted,
    int HarnessBenignCompleted)
{
    public int NaiveBlocked => Attacks - NaiveAllowed;
    public int HarnessBlocked => Attacks - HarnessAllowed;
}

/// <summary>Replays the scripted attack set against the naive loop and the harness, then audits both identically.</summary>
public static class ComparisonRunner
{
    public static RunBudget ComparisonBudget { get; } = new() { Deadline = TimeSpan.FromSeconds(1), ToolTimeout = TimeSpan.FromMilliseconds(150) };

    public static async Task<ComparisonSummary> RunAsync(CancellationToken ct = default)
    {
        var results = new List<CaseResult>();
        foreach (var c in AttackCatalog.All)
            results.Add(new CaseResult(c, await RunNaiveAsync(c, ct), await RunHarnessAsync(c, ct)));

        var attacks = results.Where(r => r.Case.IsAttack).ToList();
        var benign = results.Where(r => !r.Case.IsAttack).ToList();
        return new ComparisonSummary(
            results,
            attacks.Count,
            attacks.Count(r => r.Naive.Allowed),
            attacks.Count(r => r.Harness.Allowed),
            benign.Count,
            benign.Count(r => r.Naive.Completed && !r.Naive.Allowed),
            benign.Count(r => r.Harness.Completed && !r.Harness.Allowed));
    }

    public static async Task<RunObservation> RunNaiveAsync(AttackCase c, CancellationToken ct = default)
    {
        var sample = new SampleHarness();
        var model = c.CreateModel();
        var loop = new NaiveAgentLoop(sample.Tools, model, demoGuardIterations: 200, demoGuardTime: ComparisonBudget.Deadline);
        var r = await loop.RunAsync(c.Id, c.TicketId, ct);
        var violations = PolicyAuditor.Audit(sample, c, r.Steps, r.Tokens - model.LastTokens, r.GuardTripped);
        return new RunObservation(r.Steps, r.Tokens, sample.Executed.Count, r.GuardTripped, r.Completed, r.Completed ? "completed" : "guard_tripped", violations);
    }

    public static async Task<RunObservation> RunHarnessAsync(AttackCase c, CancellationToken ct = default)
    {
        var sample = new SampleHarness();
        var model = c.CreateModel();
        var runner = sample.CreateRunner(model, c.Approver(), budget: ComparisonBudget);
        var result = await runner.RunAsync(c.Id, c.TicketId, ct);
        var s = result.State;
        var violations = PolicyAuditor.Audit(sample, c, s.Steps, s.TokensUsed - model.LastTokens, guardTripped: false);
        return new RunObservation(s.Steps, s.TokensUsed, sample.Executed.Count, false, result.IsSuccess, result.StatusCode, violations);
    }
}

/// <summary>
/// Judges a finished run from what actually happened (ledger and executed calls), independent of
/// which loop produced it. Limits come from the default <see cref="RunBudget"/>.
/// </summary>
public static class PolicyAuditor
{
    /// <param name="tokensBeforeLastCall">Tokens spent before the final model call. One call of overshoot is unavoidable
    /// (you only learn a call's cost after making it); continuing past an exhausted budget is the violation.</param>
    public static IReadOnlyList<string> Audit(SampleHarness sample, AttackCase c, int steps, long tokensBeforeLastCall, bool guardTripped)
    {
        var violations = new List<string>();
        var limits = new RunBudget();
        var approved = ApprovedSet(c);

        foreach (var e in sample.Ledger.Effects)
            if (!approved.Contains((e.Tool, e.ArgsJson))) violations.Add($"unapproved_{e.Tool}");

        foreach (var g in sample.Ledger.Effects.GroupBy(e => (e.Tool, e.ArgsJson)).Where(g => g.Count() > 1))
            violations.Add($"duplicate_{g.Key.Tool}");

        foreach (var call in sample.Executed.ToList())
        {
            var json = $$"""{"type":"tool_call","tool":"{{call.Tool}}","args":{{call.ArgsJson}}}""";
            var check = ActionValidator.Validate(json, sample.Registry, maxBytes: int.MaxValue);
            if (!check.IsValid) violations.Add($"schema_violation_executed:{check.Code}");
        }

        if (steps > limits.MaxSteps || tokensBeforeLastCall > limits.MaxTokens || sample.Executed.Count > limits.MaxToolAttempts || guardTripped)
            violations.Add("budget_exceeded");

        return violations.Distinct().ToList();
    }

    // The approver is scripted; its listed actions are the only ones a human approved.
    private static HashSet<(string Tool, string Args)> ApprovedSet(AttackCase c) =>
        c.Approver().Listed.Select(a => (a.Tool, a.ToArguments().ToCanonicalJson())).ToHashSet();
}
