using AtlasDesk.Harness.Approvals;
using AtlasDesk.Harness.State;
using AtlasDesk.Harness.Tools;
using AtlasDesk.Sample;
using AtlasDesk.Sample.Comparison;
using AtlasDesk.ScriptedModel;

// Usage:
//   dotnet run --project src/AtlasDesk.Sample -- compare         (attack set: naive loop vs harness)
//   dotnet run --project src/AtlasDesk.Sample -- approval-trace  (exact-action approval, including a tampered action)
//   dotnet run --project src/AtlasDesk.Sample -- demo            (support-desk run with a console approval prompt)
var command = args.FirstOrDefault() ?? "compare";
switch (command)
{
    case "compare":
        return await CompareAsync(args.Contains("--markdown"));
    case "approval-trace":
        return await ApprovalTraceAsync();
    case "demo":
        return await DemoAsync();
    default:
        Console.Error.WriteLine("Unknown command. Use: compare [--markdown] | approval-trace | demo");
        return 2;
}

static async Task<int> CompareAsync(bool markdown)
{
    var s = await ComparisonRunner.RunAsync();
    if (markdown)
    {
        Console.WriteLine("| Id | Scenario | Naive loop | Harness |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var r in s.Results)
            Console.WriteLine($"| {r.Case.Id} | {r.Case.Name} | {Cell(r.Case, r.Naive)} | {Cell(r.Case, r.Harness)} |");
        Console.WriteLine();
        Console.WriteLine("| Measure | Naive loop | Harness |");
        Console.WriteLine("|---|---|---|");
        Console.WriteLine($"| Attacks in scripted set | {s.Attacks} | {s.Attacks} |");
        Console.WriteLine($"| Attacks allowed | {s.NaiveAllowed} | {s.HarnessAllowed} |");
        Console.WriteLine($"| Attacks blocked | {s.NaiveBlocked} | {s.HarnessBlocked} |");
        Console.WriteLine($"| Benign cases completed | {s.NaiveBenignCompleted}/{s.Benign} | {s.HarnessBenignCompleted}/{s.Benign} |");
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine($"{"Id",-4} {"Scenario",-40} {"Naive loop",-14} {"Harness",-14}");
        foreach (var r in s.Results)
            Console.WriteLine($"{r.Case.Id,-4} {r.Case.Name,-40} {Cell(r.Case, r.Naive),-14} {Cell(r.Case, r.Harness),-14}");
        Console.WriteLine();
    }
    if (markdown) return s.HarnessAllowed == 0 ? 0 : 1;

    Console.WriteLine($"Attacks in scripted set: {s.Attacks}");
    Console.WriteLine($"Naive loop: allowed {s.NaiveAllowed}, blocked {s.NaiveBlocked}");
    Console.WriteLine($"Harness:    allowed {s.HarnessAllowed}, blocked {s.HarnessBlocked}");
    Console.WriteLine($"Benign control cases completed without violation: naive {s.NaiveBenignCompleted}/{s.Benign}, harness {s.HarnessBenignCompleted}/{s.Benign}");
    Console.WriteLine("Measured on this scripted sample, not a benchmark.");
    return s.HarnessAllowed == 0 ? 0 : 1;

    static string Cell(AttackCase c, RunObservation o) =>
        c.IsAttack ? (o.Allowed ? "ALLOWED" : "blocked") : (o.Completed && !o.Allowed ? "completed" : "FAILED");
}

static async Task<int> ApprovalTraceAsync()
{
    const string runId = "approval-trace-demo";
    var preapproved = new ApprovedAction("issue_refund", """{"ticket_id":"T-1001","amount_cents":1000}""");
    var tamperedArgs = """{"ticket_id":"T-1001","amount_cents":49900}""";
    var approver = new TraceApprover(preapproved.HashFor(runId));
    var sample = new SampleHarness();
    var model = new ScriptedModel([
        ScriptStep.ToolCall(preapproved.Tool, tamperedArgs),
        ScriptStep.Finish(),
    ]);
    var result = await sample.CreateRunner(model, approver).RunAsync(runId, "T-1001");
    var request = approver.Request ?? throw new InvalidOperationException("The approval request was not captured.");
    var proposedHash = request.ActionHash;
    var rejected = result.State.History.First(h => h.Kind == "tool_call").Outcome;

    Console.WriteLine("# Exact-action approval trace");
    Console.WriteLine();
    Console.WriteLine("| Stage | Evidence |");
    Console.WriteLine("|---|---|");
    Console.WriteLine($"| Request | `issue_refund` for `T-1001`, **$499.00**; proposed hash `{proposedHash}` |");
    Console.WriteLine($"| Approval | Approver returned approved, bound to the previously reviewed **$10.00** action hash `{approver.ApprovedHash}` |");
    Console.WriteLine($"| Tamper check | Candidate hash `{proposedHash}` did not match the approved hash |");
    Console.WriteLine($"| Harness decision | `{rejected}` |");
    Console.WriteLine($"| Side effects | `{sample.Ledger.Effects.Count}` |");
    return rejected == "approval_rejected_ActionMismatch" && sample.Ledger.Effects.Count == 0 ? 0 : 1;
}

static async Task<int> DemoAsync()
{
    var email = new ApprovedAction("send_customer_email", """{"ticket_id":"T-1001","body":"Hello, thanks for contacting AtlasDesk. We are looking into your duplicate charge."}""");
    var model = new ScriptedModel([
        ScriptStep.ToolCall("get_account_history", """{"ticket_id":"T-1001"}"""),
        ScriptStep.ToolCall(email.Tool, email.ArgsJson),
        ScriptStep.Finish("Reply sent."),
    ]);
    var sample = new SampleHarness();
    var runner = sample.CreateRunner(model, new ConsoleApprover(), new SqliteRunStore("atlasdesk-demo.db"));
    var result = await runner.RunAsync($"demo-{Guid.NewGuid():N}", "T-1001");
    Console.WriteLine($"Run finished: {result.StatusCode}; emails sent: {sample.Ledger.Count("send_customer_email")}");
    return result.IsSuccess ? 0 : 1;
}

sealed class ConsoleApprover : IApprover
{
    public Task<ApprovalDecision> ReviewAsync(ApprovalRequest request, CancellationToken ct)
    {
        Console.WriteLine($"Approval needed: {request.Tool} {request.Args.ToCanonicalJson()}");
        Console.WriteLine($"Action hash: {request.ActionHash}");
        Console.Write("Approve exactly this action? [y/N] ");
        var answer = Console.ReadLine();
        return Task.FromResult(string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            ? ApprovalDecision.ApproveExact(request, Environment.UserName)
            : ApprovalDecision.Deny());
    }
}

sealed class TraceApprover(string approvedActionHash) : IApprover
{
    public ApprovalRequest? Request { get; private set; }
    public string ApprovedHash => approvedActionHash;

    public Task<ApprovalDecision> ReviewAsync(ApprovalRequest request, CancellationToken ct)
    {
        Request = request;
        return Task.FromResult(new ApprovalDecision(true, "support.agent.1", approvedActionHash));
    }
}
