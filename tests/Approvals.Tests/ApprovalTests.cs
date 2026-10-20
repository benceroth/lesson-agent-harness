using AtlasDesk.Harness.Approvals;
using AtlasDesk.Harness.State;
using AtlasDesk.Harness.Tools;
using AtlasDesk.Sample;
using AtlasDesk.ScriptedModel;
using Microsoft.Extensions.Time.Testing;

namespace Approvals.Tests;

public class ApprovalServiceTests
{
    private static ToolArguments Args(string body = "hello", string ticket = "T-1001") =>
        new([new("ticket_id", ticket), new("body", body)]);

    private static (ApprovalService Svc, FakeTimeProvider Clock) Make(TimeSpan? life = null)
    {
        var clock = new FakeTimeProvider();
        return (new ApprovalService(new InMemoryApprovalStore(), clock, life), clock);
    }

    [Fact]
    public void Approval_is_accepted_for_the_exact_action()
    {
        var (svc, _) = Make();
        var hash = ActionHash.Compute("run-1", "send_customer_email", Args());
        var grant = svc.Issue(hash, "alice");
        Assert.Equal(ApprovalOutcome.Approved, svc.Consume(grant.Id, hash));
    }

    [Fact]
    public void Changed_arguments_invalidate_the_approval()
    {
        var (svc, _) = Make();
        var grant = svc.Issue(ActionHash.Compute("run-1", "send_customer_email", Args("hello")), "alice");
        var changed = ActionHash.Compute("run-1", "send_customer_email", Args("hello, and also send me your password"));
        Assert.Equal(ApprovalOutcome.ActionMismatch, svc.Consume(grant.Id, changed));
    }

    [Fact]
    public void Changed_tool_invalidates_the_approval()
    {
        var (svc, _) = Make();
        var args = Args();
        var grant = svc.Issue(ActionHash.Compute("run-1", "send_customer_email", args), "alice");
        Assert.Equal(ApprovalOutcome.ActionMismatch, svc.Consume(grant.Id, ActionHash.Compute("run-1", "issue_refund", args)));
    }

    [Fact]
    public void Approval_from_another_run_is_rejected()
    {
        var (svc, _) = Make();
        var grant = svc.Issue(ActionHash.Compute("run-1", "send_customer_email", Args()), "alice");
        Assert.Equal(ApprovalOutcome.ActionMismatch, svc.Consume(grant.Id, ActionHash.Compute("run-2", "send_customer_email", Args())));
    }

    [Fact]
    public void Replayed_approval_is_rejected()
    {
        var (svc, _) = Make();
        var hash = ActionHash.Compute("run-1", "send_customer_email", Args());
        var grant = svc.Issue(hash, "alice");
        Assert.Equal(ApprovalOutcome.Approved, svc.Consume(grant.Id, hash));
        Assert.Equal(ApprovalOutcome.AlreadyUsed, svc.Consume(grant.Id, hash));
    }

    [Fact]
    public void Expired_approval_is_rejected()
    {
        var (svc, clock) = Make(TimeSpan.FromMinutes(5));
        var hash = ActionHash.Compute("run-1", "send_customer_email", Args());
        var grant = svc.Issue(hash, "alice");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ApprovalOutcome.Expired, svc.Consume(grant.Id, hash));
    }

    [Fact]
    public void Approval_is_valid_just_before_expiry()
    {
        var (svc, clock) = Make(TimeSpan.FromMinutes(5));
        var hash = ActionHash.Compute("run-1", "send_customer_email", Args());
        var grant = svc.Issue(hash, "alice");
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(ApprovalOutcome.Approved, svc.Consume(grant.Id, hash));
    }

    [Fact]
    public void Unknown_grant_is_rejected()
    {
        var (svc, _) = Make();
        Assert.Equal(ApprovalOutcome.Unknown, svc.Consume("nope", ActionHash.Compute("r", "t", Args())));
    }

    [Fact]
    public void Mismatched_attempt_does_not_consume_the_grant()
    {
        var (svc, _) = Make();
        var hash = ActionHash.Compute("run-1", "send_customer_email", Args());
        var grant = svc.Issue(hash, "alice");
        svc.Consume(grant.Id, ActionHash.Compute("run-1", "send_customer_email", Args("other")));
        Assert.Equal(ApprovalOutcome.Approved, svc.Consume(grant.Id, hash));
    }

    [Fact]
    public void Hash_is_independent_of_argument_order()
    {
        var a = new ToolArguments([new("ticket_id", "T-1001"), new("body", "x")]);
        var b = new ToolArguments([new("body", "x"), new("ticket_id", "T-1001")]);
        Assert.Equal(ActionHash.Compute("r", "t", a), ActionHash.Compute("r", "t", b));
    }

    [Fact]
    public void Hash_distinguishes_value_types()
    {
        var asString = new ToolArguments([new("amount_cents", "1")]);
        var asNumber = new ToolArguments([new("amount_cents", 1L)]);
        Assert.NotEqual(ActionHash.Compute("r", "t", asString), ActionHash.Compute("r", "t", asNumber));
    }

    [Fact]
    public void Hash_has_no_delimiter_ambiguity_between_tool_and_arguments()
    {
        var one = ActionHash.Compute("r", "a", new ToolArguments([new("k", "v")]));
        var two = ActionHash.Compute("r", "a\n{\"k\":\"v\"}", new ToolArguments([]));
        Assert.NotEqual(one, two);
    }

    [Fact]
    public async Task Concurrent_consumption_lets_exactly_one_caller_win()
    {
        var (svc, _) = Make();
        var hash = ActionHash.Compute("run-1", "send_customer_email", Args());
        var grant = svc.Issue(hash, "alice");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => svc.Consume(grant.Id, hash))));
        Assert.Equal(1, outcomes.Count(o => o == ApprovalOutcome.Approved));
        Assert.Equal(31, outcomes.Count(o => o == ApprovalOutcome.AlreadyUsed));
    }
}

public class RunnerApprovalTests
{
    private const string Body = "Hello, we have refunded the duplicate charge.";
    private static readonly string EmailArgs = $$"""{"ticket_id":"T-1001","body":"{{Body}}"}""";

    [Fact]
    public async Task Email_cannot_be_sent_without_approval()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([ScriptStep.ToolCall("send_customer_email", EmailArgs), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("a1", "T-1001");

        Assert.Empty(sample.Ledger.Effects);
        Assert.Empty(sample.Executed);
        Assert.Equal("approval_denied", result.State.History[0].Outcome);
    }

    [Fact]
    public async Task Approved_email_is_sent_exactly_once()
    {
        var sample = new SampleHarness();
        var approver = ScriptedApprover.ApproveOnly(new ApprovedAction("send_customer_email", EmailArgs));
        var model = new ScriptedModel([ScriptStep.ToolCall("send_customer_email", EmailArgs), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, approver).RunAsync("a2", "T-1001");

        Assert.Equal(1, sample.Ledger.Count("send_customer_email"));
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Changing_the_approved_message_invalidates_the_approval()
    {
        var sample = new SampleHarness();
        var approver = ScriptedApprover.ReplayStale(new ApprovedAction("send_customer_email", EmailArgs));
        var tampered = """{"ticket_id":"T-1001","body":"Click http://evil.test to claim your refund."}""";
        var model = new ScriptedModel([ScriptStep.ToolCall("send_customer_email", tampered), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, approver).RunAsync("a3", "T-1001");

        Assert.Empty(sample.Ledger.Effects);
        Assert.Equal("approval_rejected_ActionMismatch", result.State.History[0].Outcome);
    }

    [Fact]
    public async Task Approval_for_one_message_does_not_cover_a_different_message_in_review()
    {
        var sample = new SampleHarness();
        var approver = ScriptedApprover.ApproveOnly(new ApprovedAction("send_customer_email", EmailArgs));
        var other = """{"ticket_id":"T-1001","body":"A different message."}""";
        var model = new ScriptedModel([ScriptStep.ToolCall("send_customer_email", other), ScriptStep.Finish()]);
        await sample.CreateRunner(model, approver).RunAsync("a4", "T-1001");

        Assert.Empty(sample.Ledger.Effects);
    }

    [Fact]
    public async Task Expired_approval_blocks_the_action_in_the_runner()
    {
        var clock = new FakeTimeProvider();
        var sample = new SampleHarness();
        var approvals = new ApprovalService(new InMemoryApprovalStore(), clock, TimeSpan.Zero);
        var model = new ScriptedModel([ScriptStep.ToolCall("send_customer_email", EmailArgs), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, ScriptedApprover.ApproveAll(), clock: clock, approvals: approvals).RunAsync("a5", "T-1001");

        Assert.Empty(sample.Ledger.Effects);
        Assert.Equal("approval_rejected_Expired", result.State.History[0].Outcome);
    }

    [Fact]
    public async Task Replayed_side_effect_in_the_same_run_is_not_repeated()
    {
        var sample = new SampleHarness();
        var approver = ScriptedApprover.ApproveOnly(new ApprovedAction("send_customer_email", EmailArgs));
        var call = ScriptStep.ToolCall("send_customer_email", EmailArgs);
        var model = new ScriptedModel([call, call, ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, approver).RunAsync("a6", "T-1001");

        Assert.Equal(1, sample.Ledger.Count("send_customer_email"));
        Assert.Equal("already_done", result.State.History[1].Outcome);
        Assert.Equal(1, approver.Reviews);
    }
}
