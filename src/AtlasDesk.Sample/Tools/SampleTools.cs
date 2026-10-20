using AtlasDesk.Harness.Tools;

namespace AtlasDesk.Sample.Tools;

public static class SampleToolNames
{
    public const string GetAccountHistory = "get_account_history";
    public const string DraftCustomerReply = "draft_customer_reply";
    public const string SendCustomerEmail = "send_customer_email";
    public const string IssueRefund = "issue_refund";
}

internal static class Patterns
{
    public const string TicketId = @"^T-\d{4}\z";
}

/// <summary>Read-only. Narrow by design: one ticket id in, text out.</summary>
public sealed class GetAccountHistoryTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        SampleToolNames.GetAccountHistory,
        "Read the account history for a ticket (read-only).",
        [new ArgSpec("ticket_id", ArgType.String, Pattern: Patterns.TicketId, MaxLength: 6)],
        new ToolPolicy());

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var id = invocation.Args.GetString("ticket_id");
        if (id == TicketRepository.Hanging) await Task.Delay(Timeout.Infinite, ct);
        return new ToolResult(TicketRepository.Get(id) ?? "No history found.");
    }
}

/// <summary>Prepares a message. Does not send it.</summary>
public sealed class DraftCustomerReplyTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        SampleToolNames.DraftCustomerReply,
        "Draft a customer reply from the ticket history. Does not send.",
        [
            new ArgSpec("ticket_id", ArgType.String, Pattern: Patterns.TicketId, MaxLength: 6),
            new ArgSpec("history", ArgType.String, MaxLength: 4000),
        ],
        new ToolPolicy());

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var history = invocation.Args.GetString("history");
        var summary = history.Length > 60 ? history[..60] + "…" : history;
        return Task.FromResult(new ToolResult($"Draft for {invocation.Args.GetString("ticket_id")}: Hello, thanks for contacting AtlasDesk about \"{summary}\". We are looking into it."));
    }
}

/// <summary>Side effect. Requires approval of the exact message; idempotent per action hash.</summary>
public sealed class SendCustomerEmailTool(SideEffectLedger ledger) : ITool
{
    public ToolDefinition Definition { get; } = new(
        SampleToolNames.SendCustomerEmail,
        "Send an approved email to the customer on a ticket. Requires human approval.",
        [
            new ArgSpec("ticket_id", ArgType.String, Pattern: Patterns.TicketId, MaxLength: 6),
            new ArgSpec("body", ArgType.String, MaxLength: 2000),
        ],
        new ToolPolicy(RequiresApproval: true, HasSideEffects: true));

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var args = new ToolArguments(
        [
            new("ticket_id", invocation.Args.GetString("ticket_id")),
            new("body", invocation.Args.GetString("body")),
        ]);
        var sent = ledger.TryRecord(new EffectRecord(Definition.Name, args.ToCanonicalJson(), invocation.IdempotencyKey), deduplicate: true);
        return Task.FromResult(new ToolResult(sent ? "Email sent." : "Email already sent (duplicate suppressed)."));
    }
}

/// <summary>Side effect. Requires approval; amount is bounded by schema.</summary>
public sealed class IssueRefundTool(SideEffectLedger ledger) : ITool
{
    public ToolDefinition Definition { get; } = new(
        SampleToolNames.IssueRefund,
        "Issue a refund in cents for a ticket. Requires human approval.",
        [
            new ArgSpec("ticket_id", ArgType.String, Pattern: Patterns.TicketId, MaxLength: 6),
            new ArgSpec("amount_cents", ArgType.Integer, Min: 1, Max: 50_000),
        ],
        new ToolPolicy(RequiresApproval: true, HasSideEffects: true));

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        var args = new ToolArguments(
        [
            new("ticket_id", invocation.Args.GetString("ticket_id")),
            new("amount_cents", invocation.Args.GetLong("amount_cents")),
        ]);
        var done = ledger.TryRecord(new EffectRecord(Definition.Name, args.ToCanonicalJson(), invocation.IdempotencyKey), deduplicate: true);
        return Task.FromResult(new ToolResult(done ? "Refund issued." : "Refund already issued (duplicate suppressed)."));
    }
}

/// <summary>Decorator that records every real execution, so both loops are audited the same way.</summary>
public sealed class RecordingTool(ITool inner, List<ExecutedCall> log) : ITool
{
    public ToolDefinition Definition => inner.Definition;

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct)
    {
        lock (log) log.Add(new ExecutedCall(Definition.Name, invocation.Args.ToCanonicalJson()));
        return inner.ExecuteAsync(invocation, ct);
    }
}

public sealed record ExecutedCall(string Tool, string ArgsJson);
