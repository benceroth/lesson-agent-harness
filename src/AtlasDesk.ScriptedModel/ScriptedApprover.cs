using System.Text.Json;
using AtlasDesk.Harness.Approvals;
using AtlasDesk.Harness.Tools;

namespace AtlasDesk.ScriptedModel;

/// <summary>An action a human is scripted to have approved (tool + exact arguments).</summary>
public sealed record ApprovedAction(string Tool, string ArgsJson)
{
    public ToolArguments ToArguments()
    {
        using var doc = JsonDocument.Parse(ArgsJson);
        return new ToolArguments(doc.RootElement.EnumerateObject().Select(p =>
            new KeyValuePair<string, object>(p.Name, p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : p.Value.GetString()!)));
    }

    public string HashFor(string runId) => ActionHash.Compute(runId, Tool, ToArguments());
}

public enum ApproverMode
{
    /// <summary>Approves a proposal only if it exactly matches a listed action.</summary>
    ApproveListed,
    /// <summary>Always answers "approved" with the hash of the listed (stale) action, whatever was proposed.</summary>
    ReplayStale,
    DenyAll,
    /// <summary>Approves exactly what is shown. Test convenience only.</summary>
    ApproveAll,
}

/// <summary>Scripted stand-in for the human approval UI.</summary>
public sealed class ScriptedApprover : IApprover
{
    private readonly ApproverMode _mode;
    private readonly IReadOnlyList<ApprovedAction> _listed;

    private ScriptedApprover(ApproverMode mode, IEnumerable<ApprovedAction> listed)
    {
        _mode = mode;
        _listed = listed.ToList();
    }

    public int Reviews { get; private set; }

    public IReadOnlyList<ApprovedAction> Listed => _listed;

    public static ScriptedApprover Deny() => new(ApproverMode.DenyAll, []);
    public static ScriptedApprover ApproveAll() => new(ApproverMode.ApproveAll, []);
    public static ScriptedApprover ApproveOnly(params ApprovedAction[] listed) => new(ApproverMode.ApproveListed, listed);
    public static ScriptedApprover ReplayStale(ApprovedAction stale) => new(ApproverMode.ReplayStale, [stale]);

    public Task<ApprovalDecision> ReviewAsync(ApprovalRequest request, CancellationToken ct)
    {
        Reviews++;
        const string who = "support.agent.1";
        var decision = _mode switch
        {
            ApproverMode.ApproveAll => ApprovalDecision.ApproveExact(request, who),
            ApproverMode.ApproveListed when _listed.Any(a => a.HashFor(request.RunId) == request.ActionHash)
                => ApprovalDecision.ApproveExact(request, who),
            ApproverMode.ReplayStale => new ApprovalDecision(true, who, _listed[0].HashFor(request.RunId)),
            _ => ApprovalDecision.Deny(),
        };
        return Task.FromResult(decision);
    }
}
