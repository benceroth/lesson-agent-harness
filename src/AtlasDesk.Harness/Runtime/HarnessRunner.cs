using System.Diagnostics;
using AtlasDesk.Harness.Approvals;
using AtlasDesk.Harness.Limits;
using AtlasDesk.Harness.Model;
using AtlasDesk.Harness.State;
using AtlasDesk.Harness.Telemetry;
using AtlasDesk.Harness.Tools;
using AtlasDesk.Harness.Validation;
using Microsoft.Extensions.Logging;

namespace AtlasDesk.Harness.Runtime;

public sealed record RunResult(RunState State)
{
    public RunStatus Status => State.Status;
    public bool IsSuccess => State.Status.IsSuccess();
    public string StatusCode => State.Status.ToCode();
}

/// <summary>
/// The harness loop. The model proposes; this class validates, checks policy and approval,
/// executes with limits, records state and telemetry, and decides whether the model may continue.
/// </summary>
public sealed class HarnessRunner(
    ToolRegistry registry,
    IModelClient model,
    IApprover approver,
    ApprovalService approvals,
    IRunStore store,
    RunBudget budget,
    TimeProvider? clock = null,
    ILogger? logger = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<RunResult> RunAsync(string runId, string ticketId, CancellationToken ct = default)
    {
        var state = await store.LoadAsync(runId, ct);
        var resumed = state is not null;
        if (state is null)
        {
            var now = _clock.GetUtcNow();
            state = new RunState { RunId = runId, TicketId = ticketId, StartedAt = now, DeadlineUtc = now + budget.Deadline };
            await store.SaveAsync(state, ct);
        }

        using var run = HarnessTelemetry.Source.StartActivity("harness.run");
        run.Tag("harness.run.id", runId);
        run.Tag("harness.run.resumed", resumed);

        while (!state.Status.IsTerminal())
        {
            ct.ThrowIfCancellationRequested();
            if (BudgetGuard.CheckBeforeModelCall(state, budget, _clock.GetUtcNow()) is { } stop)
            {
                Stop(state, stop);
                break;
            }

            using var step = HarnessTelemetry.Source.StartActivity("harness.step");
            step.Tag("harness.step", state.Steps + 1);

            var request = BuildRequest(state);
            ModelResponse response;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(state.DeadlineUtc - _clock.GetUtcNow());
                try { response = await model.NextAsync(request, cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Stop(state, (RunStatus.DeadlineExceeded, "model call exceeded deadline"));
                    break;
                }
            }

            state.Steps++;
            state.TokensUsed += response.Tokens;
            state.CostUsd += response.CostUsd;
            step.Tag("harness.tokens", response.Tokens);

            if (BudgetGuard.CheckUsage(state, budget) is { } overUsage)
            {
                // The proposed action is discarded: an over-budget run must not take further side effects.
                Stop(state, overUsage);
                break;
            }

            var validation = ActionValidator.Validate(response.RawOutput, registry, budget.MaxOutputBytes);
            step.Tag("harness.validation.result", validation.Code);
            if (!validation.IsValid)
            {
                state.InvalidOutputs++;
                state.History.Add(new StepRecord(state.Steps, "invalid_output", null, null, validation.Code));
                state.LastObservation = new Observation("validation_error", null, $"{validation.Code}: {validation.Message}", false);
                Log(LogLevel.Warning, $"run {runId} step {state.Steps}: output rejected ({validation.Code})");
                if (state.InvalidOutputs > budget.MaxInvalidOutputs)
                    Stop(state, (RunStatus.InvalidOutputLimit, "too many invalid outputs"));
                await store.SaveAsync(state, ct);
                continue;
            }

            switch (validation.Action)
            {
                case FinishAction f:
                    step.Tag("harness.action.type", "finish");
                    state.FinalMessage = f.Message;
                    state.History.Add(new StepRecord(state.Steps, "finish", null, null, "completed"));
                    state.Terminate(RunStatus.Completed);
                    break;
                case ClarifyAction c:
                    step.Tag("harness.action.type", "clarify");
                    state.FinalMessage = c.Message;
                    state.History.Add(new StepRecord(state.Steps, "clarify", null, null, "needs_clarification"));
                    state.Terminate(RunStatus.NeedsClarification);
                    break;
                case ToolCallAction call:
                    step.Tag("harness.action.type", "tool_call");
                    await ExecuteToolCallAsync(state, call, ct);
                    break;
            }

            await store.SaveAsync(state, ct);
        }

        await store.SaveAsync(state, ct);
        run.Tag("harness.status", state.Status.ToCode());
        run.Tag("harness.steps", state.Steps);
        run.Tag("harness.tokens.total", state.TokensUsed);
        run?.SetStatus(state.Status.IsSuccess() ? ActivityStatusCode.Ok : ActivityStatusCode.Error, state.Status.ToCode());
        Log(LogLevel.Information, $"run {runId} finished: {state.Status.ToCode()}");
        return new RunResult(state);
    }

    private ModelRequest BuildRequest(RunState state) => new(
        state.RunId,
        state.TicketId,
        state.Steps + 1,
        budget.MaxSteps - state.Steps,
        state.History.Select(h => $"{h.Step}:{h.Kind}:{h.Tool ?? "-"}:{h.Outcome}").ToList(),
        registry.Definitions.ToList(),
        state.LastObservation);

    private void Stop(RunState state, (RunStatus Status, string Detail) stop)
    {
        state.Terminate(stop.Status, stop.Detail);
        Log(LogLevel.Warning, $"run {state.RunId} stopped: {stop.Status.ToCode()}");
    }

    private async Task ExecuteToolCallAsync(RunState state, ToolCallAction call, CancellationToken ct)
    {
        if (!registry.TryResolve(call.Tool, out var tool))
        {
            state.History.Add(new StepRecord(state.Steps, "tool_call", null, null, "unregistered"));
            state.LastObservation = new Observation("policy_denied", null, "Tool is not registered.", false);
            return;
        }
        var def = tool.Definition;
        var hash = ActionHash.Compute(state.RunId, def.Name, call.Args);

        void Record(string outcome, Observation obs)
        {
            state.History.Add(new StepRecord(state.Steps, "tool_call", def.Name, hash, outcome));
            state.LastObservation = obs;
        }

        // Idempotency: an identical side effect already completed in this run is never repeated.
        if (def.Policy.HasSideEffects && state.CompletedEffects.TryGetValue(hash, out var prior))
        {
            Record("already_done", new Observation("harness_notice", def.Name, "This exact action was already completed. Result: " + prior, false));
            return;
        }

        if (def.Policy.RequiresApproval)
        {
            var request = new ApprovalRequest(state.RunId, def.Name, call.Args, hash);
            var decision = await approver.ReviewAsync(request, ct);
            if (!decision.Approved || decision.Approver is null || decision.ApprovedActionHash is null)
            {
                Log(LogLevel.Information, $"run {state.RunId}: approval denied for {def.Name}");
                Record("approval_denied", new Observation("policy_denied", def.Name, "A human approver did not approve this action.", false));
                return;
            }
            var grant = approvals.Issue(decision.ApprovedActionHash, decision.Approver);
            var outcome = approvals.Consume(grant.Id, hash);
            if (outcome != ApprovalOutcome.Approved)
            {
                Log(LogLevel.Warning, $"run {state.RunId}: approval rejected for {def.Name} ({outcome})");
                Record("approval_rejected_" + outcome, new Observation("policy_denied", def.Name, "The approval does not apply to this exact action.", false));
                return;
            }
        }

        for (var attempt = 1; attempt <= 1 + def.Policy.MaxRetries; attempt++)
        {
            if (BudgetGuard.CheckBeforeToolAttempt(state, budget, _clock.GetUtcNow()) is { } stop)
            {
                state.History.Add(new StepRecord(state.Steps, "tool_call", def.Name, hash, "budget_stop"));
                Stop(state, stop);
                return;
            }
            state.ToolAttempts++;

            using var span = HarnessTelemetry.Source.StartActivity("harness.tool");
            span.Tag("tool.name", def.Name);
            span.Tag("tool.attempt", attempt);
            span.Tag("harness.policy.decision", def.Policy.RequiresApproval ? "allow:approved" : "allow");
            var started = Stopwatch.GetTimestamp();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string? failure = null;
            var transient = false;
            try
            {
                var invocation = new ToolInvocation(state.RunId, call.Args, hash);
                var result = await tool.ExecuteAsync(invocation, cts.Token).WaitAsync(budget.ToolTimeout, ct);
                var content = result.Content.Length > budget.MaxToolResultChars
                    ? result.Content[..budget.MaxToolResultChars] + "…[truncated]"
                    : result.Content;
                if (def.Policy.HasSideEffects) state.CompletedEffects[hash] = content;
                span.Tag("tool.outcome", "ok");
                Record("ok", new Observation("tool_result", def.Name, content, true));
                return;
            }
            catch (TimeoutException)
            {
                // WaitAsync only stops waiting; cancel so cooperative tools stop too. See docs on non-cooperative tools.
                await cts.CancelAsync();
                failure = "timeout"; transient = true;
            }
            catch (TransientToolException)
            {
                failure = "transient_error"; transient = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Generic on purpose: no stack traces or exception text reach the model or telemetry.
                failure = "tool_error";
            }
            finally
            {
                span.Tag("tool.latency_ms", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            span.Tag("tool.outcome", failure);
            Log(LogLevel.Warning, $"run {state.RunId}: tool {def.Name} attempt {attempt} failed ({failure})");
            if (!transient || attempt > def.Policy.MaxRetries)
            {
                Record("failed_" + failure, new Observation("tool_error", def.Name, $"Tool failed: {failure}.", false));
                return;
            }
        }
    }

    private void Log(LogLevel level, string message) =>
        logger?.Log(level, "{Message}", Redactor.Redact(message));
}
