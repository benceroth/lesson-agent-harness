using AtlasDesk.Harness.Approvals;
using AtlasDesk.Harness.Limits;
using AtlasDesk.Harness.Model;
using AtlasDesk.Harness.Runtime;
using AtlasDesk.Harness.State;
using AtlasDesk.Harness.Tools;
using AtlasDesk.Sample.Tools;
using Microsoft.Extensions.Logging;

namespace AtlasDesk.Sample;

/// <summary>Wires the sample support-desk agent: tools, registry, approvals, store, runner.</summary>
public sealed class SampleHarness
{
    public SideEffectLedger Ledger { get; } = new();
    public List<ExecutedCall> Executed { get; } = [];
    public IReadOnlyList<ITool> Tools { get; }
    public ToolRegistry Registry { get; }

    public SampleHarness(IEnumerable<ITool>? extraTools = null, IEnumerable<string>? allowList = null)
    {
        var tools = new List<ITool>
        {
            new GetAccountHistoryTool(),
            new DraftCustomerReplyTool(),
            new SendCustomerEmailTool(Ledger),
            new IssueRefundTool(Ledger),
        };
        if (extraTools is not null) tools.AddRange(extraTools);
        Tools = tools.Select(t => (ITool)new RecordingTool(t, Executed)).ToList();
        Registry = new ToolRegistry(Tools, allowList);
    }

    public HarnessRunner CreateRunner(
        IModelClient model,
        IApprover approver,
        IRunStore? store = null,
        RunBudget? budget = null,
        TimeProvider? clock = null,
        ILogger? logger = null,
        ApprovalService? approvals = null)
    {
        clock ??= TimeProvider.System;
        approvals ??= new ApprovalService(new InMemoryApprovalStore(), clock);
        return new HarnessRunner(Registry, model, approver, approvals, store ?? new InMemoryRunStore(), budget ?? new RunBudget(), clock, logger);
    }
}
