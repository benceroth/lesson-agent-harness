using AtlasDesk.Harness.Limits;
using AtlasDesk.Harness.Model;
using AtlasDesk.Harness.State;
using AtlasDesk.Harness.Tools;
using AtlasDesk.Sample;
using AtlasDesk.ScriptedModel;
using Microsoft.Extensions.Time.Testing;

namespace Limits.Tests;

internal sealed class DelegateTool(string name, Func<int, CancellationToken, Task<ToolResult>> behavior) : ITool
{
    private int _calls;
    public int Calls => _calls;
    public ToolDefinition Definition { get; } = new(name, "test tool", [], new ToolPolicy());
    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct) =>
        behavior(Interlocked.Increment(ref _calls), ct);
}

internal sealed class ClockAdvancingModel(FakeTimeProvider clock, TimeSpan advance) : IModelClient
{
    public Task<ModelResponse> NextAsync(ModelRequest request, CancellationToken ct)
    {
        clock.Advance(advance);
        return Task.FromResult(new ModelResponse("""{"type":"tool_call","tool":"get_account_history","args":{"ticket_id":"T-1001"}}"""));
    }
}

public class RunLimitTests
{
    private const string History = """{"ticket_id":"T-1001"}""";
    private static string Call(string tool) => $$$"""{"type":"tool_call","tool":"{{{tool}}}","args":{}}""";

    [Fact]
    public void Budget_defaults_match_the_atlasdesk_example_in_the_post()
    {
        var b = new RunBudget();
        Assert.Equal(8, b.MaxSteps);
        Assert.Equal(TimeSpan.FromSeconds(120), b.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(15), b.ToolTimeout);
        Assert.Equal(2, new ToolPolicy().MaxRetries);
    }

    [Fact]
    public async Task Infinite_tool_loop_is_stopped_at_step_limit()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([ScriptStep.ToolCall("get_account_history", History)], repeatLast: true);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("loop", "T-1001");

        Assert.Equal(RunStatus.StepLimit, result.Status);
        Assert.Equal("turn_limit", result.StatusCode);
        Assert.Equal(8, model.Calls);
        Assert.Equal(8, sample.Executed.Count);
    }

    [Fact]
    public async Task Stopped_runs_are_never_reported_as_success()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([ScriptStep.ToolCall("get_account_history", History)], repeatLast: true);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("nosuccess", "T-1001");

        Assert.False(result.IsSuccess);
        Assert.NotEqual(RunStatus.Completed, result.Status);
        Assert.Null(result.State.FinalMessage);
    }

    [Fact]
    public async Task Token_budget_exceeded_stops_before_executing_the_action()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([ScriptStep.ToolCall("get_account_history", History, tokens: 30_000), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("tokens", "T-1001");

        Assert.Equal(RunStatus.TokenLimit, result.Status);
        Assert.Empty(sample.Executed);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Cost_budget_exceeded_stops_the_run()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([new ScriptStep(ScriptStep.Finish().Output, CostUsd: 0.75m)]);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("cost", "T-1001");

        Assert.Equal(RunStatus.CostLimit, result.Status);
        Assert.Equal("cost_limit", result.StatusCode);
    }

    [Fact]
    public async Task Tool_attempt_limit_stops_the_run()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([ScriptStep.ToolCall("get_account_history", History)], repeatLast: true);
        var budget = new RunBudget { MaxToolAttempts = 2 };
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny(), budget: budget).RunAsync("attempts", "T-1001");

        Assert.Equal(RunStatus.ToolCallLimit, result.Status);
        Assert.Equal(2, sample.Executed.Count);
    }

    [Fact]
    public async Task Deadline_exceeded_when_the_clock_passes_the_run_deadline()
    {
        var clock = new FakeTimeProvider();
        var sample = new SampleHarness();
        var model = new ClockAdvancingModel(clock, TimeSpan.FromSeconds(50));
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny(), clock: clock).RunAsync("deadline", "T-1001");

        Assert.Equal(RunStatus.DeadlineExceeded, result.Status);
        Assert.Equal("deadline_exceeded", result.StatusCode);
        Assert.True(result.State.Steps <= 3);
    }

    [Fact]
    public async Task Hanging_model_is_cancelled_at_the_deadline()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([new ScriptStep("", Hang: true)]);
        var budget = new RunBudget { Deadline = TimeSpan.FromMilliseconds(200) };
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny(), budget: budget).RunAsync("hang-model", "T-1001");

        Assert.Equal(RunStatus.DeadlineExceeded, result.Status);
    }

    [Fact]
    public async Task Hanging_tool_times_out_and_is_retried_at_most_twice()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([ScriptStep.ToolCall("get_account_history", """{"ticket_id":"T-9999"}"""), ScriptStep.Finish()]);
        var budget = new RunBudget { ToolTimeout = TimeSpan.FromMilliseconds(100) };
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny(), budget: budget).RunAsync("hang-tool", "T-1001");

        Assert.Equal(3, sample.Executed.Count);
        Assert.Equal("failed_timeout", result.State.History[0].Outcome);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Tool_that_ignores_cancellation_does_not_block_the_run()
    {
        var stubborn = new DelegateTool("stubborn_read", (_, _) => new TaskCompletionSource<ToolResult>().Task);
        var sample = new SampleHarness([stubborn]);
        var model = new ScriptedModel([new ScriptStep(Call("stubborn_read")), ScriptStep.Finish()]);
        var budget = new RunBudget { ToolTimeout = TimeSpan.FromMilliseconds(50) };
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny(), budget: budget).RunAsync("stubborn", "T-1001");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, stubborn.Calls);
    }

    [Fact]
    public async Task Non_transient_tool_failure_is_not_retried()
    {
        var broken = new DelegateTool("broken_read", (_, _) => throw new InvalidOperationException("boom at C:\\secret\\path"));
        var sample = new SampleHarness([broken]);
        var model = new ScriptedModel([new ScriptStep(Call("broken_read")), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("nonretry", "T-1001");

        Assert.Equal(1, broken.Calls);
        Assert.Equal("failed_tool_error", result.State.History[0].Outcome);
        Assert.DoesNotContain("secret", model.Requests[1].LastObservation!.Content);
    }

    [Fact]
    public async Task Transient_failure_recovers_within_the_retry_cap()
    {
        var flaky = new DelegateTool("flaky_read", (n, _) =>
            n < 3 ? throw new TransientToolException("503") : Task.FromResult(new ToolResult("ok")));
        var sample = new SampleHarness([flaky]);
        var model = new ScriptedModel([new ScriptStep(Call("flaky_read")), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("flaky", "T-1001");

        Assert.Equal(3, flaky.Calls);
        Assert.Equal("ok", result.State.History[0].Outcome);
    }

    [Fact]
    public async Task Persistent_transient_failure_stops_retrying_after_two_retries()
    {
        var down = new DelegateTool("down_read", (_, _) => throw new TransientToolException("503"));
        var sample = new SampleHarness([down]);
        var model = new ScriptedModel([new ScriptStep(Call("down_read")), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("down", "T-1001");

        Assert.Equal(3, down.Calls);
        Assert.Equal("failed_transient_error", result.State.History[0].Outcome);
    }

    [Fact]
    public async Task Repeated_invalid_output_stops_the_run()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([new ScriptStep("nonsense")], repeatLast: true);
        var result = await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("invalid", "T-1001");

        Assert.Equal(RunStatus.InvalidOutputLimit, result.Status);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task Tool_results_are_truncated_to_the_configured_size()
    {
        var big = new DelegateTool("big_read", (_, _) => Task.FromResult(new ToolResult(new string('x', 50_000))));
        var sample = new SampleHarness([big]);
        var model = new ScriptedModel([new ScriptStep(Call("big_read")), ScriptStep.Finish()]);
        await sample.CreateRunner(model, ScriptedApprover.Deny()).RunAsync("big", "T-1001");

        Assert.True(model.Requests[1].LastObservation!.Content.Length < 8_100);
    }
}
