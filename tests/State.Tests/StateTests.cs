using AtlasDesk.Harness.Approvals;
using AtlasDesk.Harness.State;
using AtlasDesk.Sample;
using AtlasDesk.ScriptedModel;
using Microsoft.Extensions.Time.Testing;

namespace State.Tests;

public sealed class SqliteDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, $"state-tests-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { Path, Path + "-journal", Path + "-wal", Path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

internal sealed class CrashException : Exception;

/// <summary>Fails the Nth save once, simulating a process dying between a tool effect and persistence.</summary>
internal sealed class FailingStore(IRunStore inner, int failOnSave) : IRunStore
{
    private int _saves;
    public Task<RunState?> LoadAsync(string runId, CancellationToken ct = default) => inner.LoadAsync(runId, ct);

    public Task SaveAsync(RunState state, CancellationToken ct = default)
    {
        if (++_saves == failOnSave) throw new CrashException();
        return inner.SaveAsync(state, ct);
    }
}

public class CrashResumeTests
{
    private const string Body = "We refunded the duplicate charge.";
    private static readonly string EmailArgs = $$"""{"ticket_id":"T-1001","body":"{{Body}}"}""";
    private static readonly ApprovedAction Email = new("send_customer_email", EmailArgs);

    private static ScriptStep[] Script(params ScriptStep[] tail) =>
        [ScriptStep.ToolCall("get_account_history", """{"ticket_id":"T-1001"}"""), ScriptStep.ToolCall(Email.Tool, Email.ArgsJson), .. tail];

    [Fact]
    public async Task Crash_mid_run_resumes_from_the_sqlite_store()
    {
        using var db = new SqliteDb();
        var sample = new SampleHarness();

        var crashing = new ScriptedModel(Script(new ScriptStep("", Throw: new CrashException())));
        await Assert.ThrowsAsync<CrashException>(() =>
            sample.CreateRunner(crashing, ScriptedApprover.ApproveOnly(Email), new SqliteRunStore(db.Path)).RunAsync("crash-1", "T-1001"));

        var saved = await new SqliteRunStore(db.Path).LoadAsync("crash-1");
        Assert.NotNull(saved);
        Assert.Equal(RunStatus.Running, saved.Status);
        Assert.Equal(2, saved.Steps);

        var resumed = new ScriptedModel(Script(ScriptStep.Finish("Reply sent.")), startAt: 2);
        var result = await sample.CreateRunner(resumed, ScriptedApprover.ApproveOnly(Email), new SqliteRunStore(db.Path)).RunAsync("crash-1", "T-1001");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, result.State.Steps);
        Assert.Equal(1, resumed.Calls);
        Assert.Equal(1, sample.Ledger.Count("send_customer_email"));
    }

    [Fact]
    public async Task Resume_does_not_resend_a_completed_email()
    {
        using var db = new SqliteDb();
        var sample = new SampleHarness();
        var crashing = new ScriptedModel(Script(new ScriptStep("", Throw: new CrashException())));
        await Assert.ThrowsAsync<CrashException>(() =>
            sample.CreateRunner(crashing, ScriptedApprover.ApproveOnly(Email), new SqliteRunStore(db.Path)).RunAsync("crash-2", "T-1001"));

        // After restart the model (e.g. confused by a reset conversation) proposes the same email again.
        var approver = ScriptedApprover.ApproveOnly(Email);
        var resumed = new ScriptedModel([ScriptStep.ToolCall(Email.Tool, Email.ArgsJson), ScriptStep.Finish()]);
        var result = await sample.CreateRunner(resumed, approver, new SqliteRunStore(db.Path)).RunAsync("crash-2", "T-1001");

        Assert.Equal(1, sample.Ledger.Count("send_customer_email"));
        Assert.Equal(0, approver.Reviews);
        Assert.Equal("already_done", result.State.History.Last(h => h.Kind == "tool_call").Outcome);
    }

    [Fact]
    public async Task Crash_after_a_tool_effect_but_before_saving_does_not_duplicate_the_side_effect()
    {
        using var db = new SqliteDb();
        var sample = new SampleHarness();
        // Saves: 1 = initial, 2 = after step 1, 3 = after the email step -> crash here, after the email went out.
        var store = new FailingStore(new SqliteRunStore(db.Path), failOnSave: 3);
        var model = new ScriptedModel(Script(ScriptStep.Finish()));
        await Assert.ThrowsAsync<CrashException>(() =>
            sample.CreateRunner(model, ScriptedApprover.ApproveOnly(Email), store).RunAsync("crash-3", "T-1001"));
        Assert.Equal(1, sample.Ledger.Count("send_customer_email"));

        var saved = await new SqliteRunStore(db.Path).LoadAsync("crash-3");
        Assert.Empty(saved!.CompletedEffects);

        var resumed = new ScriptedModel(Script(ScriptStep.Finish()), startAt: 1);
        var result = await sample.CreateRunner(resumed, ScriptedApprover.ApproveOnly(Email), new SqliteRunStore(db.Path)).RunAsync("crash-3", "T-1001");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, sample.Ledger.Count("send_customer_email"));
    }

    [Fact]
    public async Task Conversation_reset_does_not_lose_state()
    {
        using var db = new SqliteDb();
        var sample = new SampleHarness();
        var crashing = new ScriptedModel(Script(new ScriptStep("", Throw: new CrashException())));
        await Assert.ThrowsAsync<CrashException>(() =>
            sample.CreateRunner(crashing, ScriptedApprover.ApproveOnly(Email), new SqliteRunStore(db.Path)).RunAsync("reset-1", "T-1001"));

        var freshModel = new ScriptedModel([ScriptStep.Finish()]);
        await sample.CreateRunner(freshModel, ScriptedApprover.Deny(), new SqliteRunStore(db.Path)).RunAsync("reset-1", "T-1001");

        var request = freshModel.Requests.Single();
        Assert.Equal("T-1001", request.TicketId);
        Assert.Equal(3, request.Step);
        Assert.Equal(2, request.CompletedSteps.Count);
        Assert.Contains(request.CompletedSteps, s => s.Contains("send_customer_email") && s.EndsWith(":ok"));
        Assert.Equal("send_customer_email", request.LastObservation!.Tool);
    }

    [Fact]
    public async Task Model_requests_carry_structured_state_not_a_transcript()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel(Script(ScriptStep.Finish()));
        await sample.CreateRunner(model, ScriptedApprover.ApproveOnly(Email)).RunAsync("struct-1", "T-1001");

        var last = model.Requests[^1];
        Assert.Equal(2, last.CompletedSteps.Count);
        Assert.Equal("send_customer_email", last.LastObservation!.Tool);
        Assert.DoesNotContain("duplicate charge", string.Join("|", last.CompletedSteps));
    }

    [Fact]
    public async Task Finished_run_cannot_be_resumed_or_extended()
    {
        var store = new InMemoryRunStore();
        var sample = new SampleHarness();
        var first = await sample.CreateRunner(new ScriptedModel([ScriptStep.Finish()]), ScriptedApprover.Deny(), store).RunAsync("done-1", "T-1001");
        Assert.Equal(RunStatus.Completed, first.Status);

        var model = new ScriptedModel([ScriptStep.ToolCall(Email.Tool, Email.ArgsJson)]);
        var again = await sample.CreateRunner(model, ScriptedApprover.ApproveAll(), store).RunAsync("done-1", "T-1001");

        Assert.Equal(RunStatus.Completed, again.Status);
        Assert.Equal(0, model.Calls);
        Assert.Empty(sample.Ledger.Effects);
    }

    [Fact]
    public async Task Resumed_run_past_its_deadline_ends_as_deadline_exceeded()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryRunStore();
        var sample = new SampleHarness();
        var crashing = new ScriptedModel(Script(new ScriptStep("", Throw: new CrashException())));
        await Assert.ThrowsAsync<CrashException>(() =>
            sample.CreateRunner(crashing, ScriptedApprover.ApproveOnly(Email), store, clock: clock).RunAsync("late-1", "T-1001"));

        clock.Advance(TimeSpan.FromMinutes(10));
        var resumed = new ScriptedModel([ScriptStep.Finish()]);
        var result = await sample.CreateRunner(resumed, ScriptedApprover.Deny(), store, clock: clock).RunAsync("late-1", "T-1001");

        Assert.Equal(RunStatus.DeadlineExceeded, result.Status);
        Assert.Equal(0, resumed.Calls);
    }
}

public class RunStoreTests
{
    private static RunState Sample() => new()
    {
        RunId = "rt-1",
        TicketId = "T-1001",
        StartedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        DeadlineUtc = DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
        Steps = 3,
        TokensUsed = 450,
        CostUsd = 0.006m,
        History = [new StepRecord(1, "tool_call", "get_account_history", "abc", "ok")],
        CompletedEffects = new() { ["abc"] = "Email sent." },
        LastObservation = new Observation("tool_result", "get_account_history", "text", true),
    };

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public async Task Store_roundtrips_run_state(string kind)
    {
        using var db = new SqliteDb();
        IRunStore store = kind == "memory" ? new InMemoryRunStore() : new SqliteRunStore(db.Path);
        var state = Sample();
        state.Terminate(RunStatus.StepLimit, "max steps 8");
        await store.SaveAsync(state);

        var loaded = await store.LoadAsync("rt-1");
        Assert.NotNull(loaded);
        Assert.Equal(RunStatus.StepLimit, loaded.Status);
        Assert.Equal("max steps 8", loaded.StopDetail);
        Assert.Equal(3, loaded.Steps);
        Assert.Equal(450, loaded.TokensUsed);
        Assert.Equal(0.006m, loaded.CostUsd);
        Assert.Equal("Email sent.", loaded.CompletedEffects["abc"]);
        Assert.True(loaded.LastObservation!.Untrusted);
        Assert.Equal(state.DeadlineUtc, loaded.DeadlineUtc);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public async Task Unknown_run_loads_as_null(string kind)
    {
        using var db = new SqliteDb();
        IRunStore store = kind == "memory" ? new InMemoryRunStore() : new SqliteRunStore(db.Path);
        Assert.Null(await store.LoadAsync("missing"));
    }

    [Fact]
    public async Task Sqlite_store_updates_an_existing_run()
    {
        using var db = new SqliteDb();
        var store = new SqliteRunStore(db.Path);
        var state = Sample();
        await store.SaveAsync(state);
        state.Steps = 7;
        await store.SaveAsync(state);
        Assert.Equal(7, (await store.LoadAsync("rt-1"))!.Steps);
    }

    [Fact]
    public async Task In_memory_store_does_not_alias_caller_state()
    {
        var store = new InMemoryRunStore();
        var state = Sample();
        await store.SaveAsync(state);
        state.Steps = 99;
        Assert.Equal(3, (await store.LoadAsync("rt-1"))!.Steps);
    }

    [Fact]
    public void Terminal_status_is_immutable()
    {
        var state = Sample();
        state.Terminate(RunStatus.Completed);
        state.Terminate(RunStatus.Failed, "late failure");
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Throws<ArgumentException>(() => state.Terminate(RunStatus.Running));
    }

    [Fact]
    public void Only_completed_counts_as_success_and_codes_are_machine_readable()
    {
        foreach (var s in Enum.GetValues<RunStatus>())
            Assert.Equal(s == RunStatus.Completed, s.IsSuccess());
        Assert.Equal("deadline_exceeded", RunStatus.DeadlineExceeded.ToCode());
        Assert.Equal("turn_limit", RunStatus.StepLimit.ToCode());
    }
}
