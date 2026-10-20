using System.Diagnostics;
using AtlasDesk.Harness.Telemetry;
using AtlasDesk.Sample;
using AtlasDesk.ScriptedModel;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Telemetry.Tests;

internal sealed class CapturingLogger : ILogger
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception is null ? "" : "|" + exception));
}

public class RedactorTests
{
    [Theory]
    [InlineData("contact dana.reyes@example.test please", "dana.reyes@example.test")]
    [InlineData("Authorization: Bearer abcdef1234567890", "abcdef1234567890")]
    [InlineData("key sk-live_ABCDEF1234567890", "ABCDEF1234567890")]
    [InlineData("card 4111 1111 1111 1111", "4111")]
    [InlineData("call +1 555 010 4477 now", "555 010 4477")]
    [InlineData("password=hunter2hunter2", "hunter2")]
    [InlineData("api_key: 9f8e7d6c5b4a", "9f8e7d6c5b4a")]
    public void Redactor_masks_secret_and_pii_shapes(string input, string mustNotRemain) =>
        Assert.DoesNotContain(mustNotRemain, Redactor.Redact(input));

    [Fact]
    public void Redactor_leaves_ordinary_diagnostic_text_alone()
    {
        const string text = "run r-17 step 3: tool send_customer_email attempt 2 failed (timeout)";
        Assert.Equal(text, Redactor.Redact(text));
    }

    [Fact]
    public void Redactor_does_not_mangle_action_hashes()
    {
        var hash = new string('a', 20) + "0123456789012345" + new string('b', 28);
        Assert.Equal(hash, Redactor.Redact(hash));
    }
}

public class TelemetryTests
{
    private static readonly string[] Sentinels =
    [
        "dana.reyes@example.test", "dana.reyes", "+1 555 010 4477", "555 010 4477",
        "4111 1111 1111 1111", "4111", "SECRETSECRET123456", "hunter2hunter2", "Bearer abcdef1234567890",
    ];

    private const string EmailBody = "Hi dana.reyes@example.test, card 4111 1111 1111 1111 was refunded. password=hunter2hunter2";

    private static async Task<(List<Activity> Spans, CapturingLogger Log, string RunId)> RunScenarioAsync()
    {
        var runId = $"tel-{Guid.NewGuid():N}";
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var emailArgs = $$"""{"ticket_id":"T-1001","body":"{{EmailBody}}"}""";
        var sample = new SampleHarness();
        var model = new ScriptedModel([
            ScriptStep.ToolCall("sk-SECRETSECRET123456", "{}"),
            new ScriptStep("Bearer abcdef1234567890 not json"),
            ScriptStep.ToolCall("get_account_history", """{"ticket_id":"T-1001"}"""),
            ScriptStep.ToolCall("send_customer_email", emailArgs),
            ScriptStep.Finish("Sent to dana.reyes@example.test"),
        ]);
        var log = new CapturingLogger();
        var approver = ScriptedApprover.ApproveAll();
        await sample.CreateRunner(model, approver, logger: log).RunAsync(runId, "T-1001");

        // Spans of this run only: the run span carries the id; children share its trace.
        var runSpan = spans.Single(s => s.OperationName == "harness.run" && (string?)s.GetTagItem("harness.run.id") == runId);
        var mine = spans.Where(s => s.TraceId == runSpan.TraceId).ToList();
        return (mine, log, runId);
    }

    private static IEnumerable<string> AllText(IEnumerable<Activity> spans) =>
        spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))
            .Append(s.DisplayName).Append(s.StatusDescription ?? ""));

    [Fact]
    public async Task Run_spans_never_contain_secrets_or_pii()
    {
        var (spans, _, _) = await RunScenarioAsync();
        var text = string.Join("\n", AllText(spans));
        foreach (var s in Sentinels) Assert.DoesNotContain(s, text);
    }

    [Fact]
    public async Task Logs_never_contain_secrets_or_pii()
    {
        var (_, log, _) = await RunScenarioAsync();
        Assert.NotEmpty(log.Lines);
        var text = string.Join("\n", log.Lines);
        foreach (var s in Sentinels) Assert.DoesNotContain(s, text);
    }

    [Fact]
    public async Task Run_spans_contain_the_diagnostic_fields_operators_need()
    {
        var (spans, _, _) = await RunScenarioAsync();
        var run = spans.Single(s => s.OperationName == "harness.run");
        Assert.Equal("completed", run.GetTagItem("harness.status"));

        var tools = spans.Where(s => s.OperationName == "harness.tool").ToList();
        Assert.Contains(tools, t => (string?)t.GetTagItem("tool.name") == "send_customer_email"
            && (string?)t.GetTagItem("harness.policy.decision") == "allow:approved"
            && t.GetTagItem("tool.latency_ms") is not null
            && (int?)t.GetTagItem("tool.attempt") == 1);

        var steps = spans.Where(s => s.OperationName == "harness.step").ToList();
        Assert.Contains(steps, s => (string?)s.GetTagItem("harness.validation.result") == "unknown_tool");
        Assert.Contains(steps, s => (string?)s.GetTagItem("harness.validation.result") == "malformed_json");
    }

    [Fact]
    public async Task Unknown_tool_name_proposed_by_the_model_is_not_recorded_in_spans()
    {
        var (spans, _, _) = await RunScenarioAsync();
        Assert.DoesNotContain("sk-", string.Join("\n", AllText(spans)));
    }

    [Fact]
    public void Redacting_processor_scrubs_leaky_tags_in_an_opentelemetry_pipeline()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Test.Leaky")
            .AddProcessor(new RedactingActivityProcessor())
            .AddInMemoryExporter(exported)
            .Build();
        using var source = new ActivitySource("Test.Leaky");
        using (var a = source.StartActivity("leaky"))
        {
            a!.SetTag("note", "contact dana.reyes@example.test");
            a.SetTag("customer.name", "Dana Reyes");
            a.SetTag("tool.args", """{"body":"secret"}""");
            a.SetTag("harness.step", 3);
            a.SetStatus(ActivityStatusCode.Error, "failed for dana.reyes@example.test");
        }
        provider.ForceFlush();

        var span = Assert.Single(exported);
        Assert.DoesNotContain("dana.reyes", (string)span.GetTagItem("note")!);
        Assert.Null(span.GetTagItem("customer.name"));
        Assert.Null(span.GetTagItem("tool.args"));
        Assert.Equal(3, span.GetTagItem("harness.step"));
        Assert.DoesNotContain("dana.reyes", span.StatusDescription);
    }
}
