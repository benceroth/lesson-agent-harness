using AtlasDesk.Harness.Tools;
using AtlasDesk.Harness.Validation;
using AtlasDesk.Harness.State;
using AtlasDesk.Sample;
using AtlasDesk.Sample.Tools;
using AtlasDesk.ScriptedModel;

namespace Validation.Tests;

public class ActionValidatorTests
{
    private static readonly ToolRegistry Registry = new SampleHarness().Registry;

    private static ValidationResult V(string raw, int max = ActionValidator.DefaultMaxBytes) => ActionValidator.Validate(raw, Registry, max);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":")]
    [InlineData("Sure! {type: tool_call}")]
    public void Rejects_malformed_json(string raw)
    {
        var r = V(raw);
        Assert.False(r.IsValid);
        Assert.Equal("malformed_json", r.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"finish\"")]
    [InlineData("42")]
    public void Rejects_non_object_root(string raw) => Assert.Equal("not_object", V(raw).Code);

    [Fact]
    public void Rejects_unknown_top_level_field() =>
        Assert.Equal("unknown_field", V("""{"type":"finish","message":"x","admin":true}""").Code);

    [Fact]
    public void Rejects_duplicate_fields() =>
        Assert.Equal("duplicate_field", V("""{"type":"finish","type":"finish","message":"x"}""").Code);

    [Fact]
    public void Rejects_duplicate_argument_keys() =>
        Assert.Equal("duplicate_field", V("""{"type":"tool_call","tool":"issue_refund","args":{"ticket_id":"T-1001","amount_cents":1,"amount_cents":50000}}""").Code);

    [Fact]
    public void Rejects_unknown_action_type() =>
        Assert.Equal("unknown_action_type", V("""{"type":"run_shell","message":"x"}""").Code);

    [Fact]
    public void Rejects_unknown_tool_without_guessing()
    {
        var r = V("""{"type":"tool_call","tool":"email_customer","args":{"ticket_id":"T-1001","body":"hi"}}""");
        Assert.False(r.IsValid);
        Assert.Equal("unknown_tool", r.Code);
        Assert.Null(r.Action);
    }

    [Theory]
    [InlineData("Send_Customer_Email")]
    [InlineData("send_customer_email ")]
    public void Tool_lookup_is_exact(string name) =>
        Assert.Equal("unknown_tool", V($$$"""{"type":"tool_call","tool":"{{{name}}}","args":{}}""").Code);

    [Fact]
    public void Rejects_tool_that_is_registered_but_not_allow_listed()
    {
        var narrow = new SampleHarness(allowList: [SampleToolNames.GetAccountHistory]).Registry;
        var r = ActionValidator.Validate("""{"type":"tool_call","tool":"issue_refund","args":{"ticket_id":"T-1001","amount_cents":5}}""", narrow);
        Assert.Equal("unknown_tool", r.Code);
    }

    [Fact]
    public void Rejects_missing_required_argument() =>
        Assert.Equal("missing_field", V("""{"type":"tool_call","tool":"issue_refund","args":{"ticket_id":"T-1001"}}""").Code);

    [Theory]
    [InlineData("""{"ticket_id":"T-1001","amount_cents":"500"}""")]
    [InlineData("""{"ticket_id":"T-1001","amount_cents":5.5}""")]
    [InlineData("""{"ticket_id":"T-1001","amount_cents":true}""")]
    [InlineData("""{"ticket_id":1001,"amount_cents":5}""")]
    public void Rejects_wrong_argument_type(string args) =>
        Assert.Equal("wrong_type", V($$"""{"type":"tool_call","tool":"issue_refund","args":{{args}}}""").Code);

    [Fact]
    public void Rejects_extra_argument() =>
        Assert.Equal("unknown_field", V("""{"type":"tool_call","tool":"get_account_history","args":{"ticket_id":"T-1001","include_internal":true}}""").Code);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(50_001)]
    public void Rejects_out_of_range_integer(long amount) =>
        Assert.Equal("invalid_argument", V($$$"""{"type":"tool_call","tool":"issue_refund","args":{"ticket_id":"T-1001","amount_cents":{{{amount}}}}}""").Code);

    [Theory]
    [InlineData("T-1001'; DROP TABLE tickets;--")]
    [InlineData("T-1001\\n")]
    [InlineData("../../etc/passwd")]
    public void Rejects_argument_not_matching_pattern(string id) =>
        Assert.Equal("invalid_argument", V($$$"""{"type":"tool_call","tool":"get_account_history","args":{"ticket_id":"{{{id}}}"}}""").Code);

    [Fact]
    public void Rejects_oversized_output()
    {
        var raw = $$"""{"type":"finish","message":"{{new string('a', 3000)}}"}""";
        Assert.Equal("oversized", V(raw, max: 1024).Code);
    }

    [Fact]
    public void Rejects_excessively_nested_json()
    {
        var raw = string.Concat(Enumerable.Repeat("{\"a\":", 20)) + "1" + new string('}', 20);
        Assert.Equal("malformed_json", V(raw).Code);
    }

    [Fact]
    public void Rejects_over_long_message() =>
        Assert.Equal("invalid_argument", V($$"""{"type":"finish","message":"{{new string('a', 4001)}}"}""").Code);

    [Fact]
    public void Accepts_valid_tool_call()
    {
        var r = V("""{"type":"tool_call","tool":"issue_refund","args":{"amount_cents":1000,"ticket_id":"T-1001"}}""");
        Assert.True(r.IsValid);
        var call = Assert.IsType<ToolCallAction>(r.Action);
        Assert.Equal("issue_refund", call.Tool);
        Assert.Equal(1000, call.Args.GetLong("amount_cents"));
    }

    [Fact]
    public void Accepts_finish_and_clarify()
    {
        Assert.IsType<FinishAction>(V("""{"type":"finish","message":"done"}""").Action);
        Assert.IsType<ClarifyAction>(V("""{"type":"clarify","message":"which order?"}""").Action);
    }

    [Fact]
    public void Error_messages_do_not_echo_model_content()
    {
        const string secret = "sk-SECRETSECRET123456";
        var inputs = new[]
        {
            $$$"""{"type":"tool_call","tool":"{{{secret}}}","args":{}}""",
            $$$"""{"type":"tool_call","tool":"issue_refund","args":{"ticket_id":"{{{secret}}}","amount_cents":1}}""",
            $$"""{"type":"{{secret}}"}""",
            $$"""{"type":"finish","message":"x","{{secret}}":1}""",
            $$$"""{"type":"tool_call","tool":"issue_refund","args":{"{{{secret}}}":1}}""",
            secret,
        };
        foreach (var raw in inputs)
        {
            var r = V(raw);
            Assert.False(r.IsValid);
            Assert.DoesNotContain(secret, r.Message);
        }
    }
}

public class RunnerValidationTests
{
    [Fact]
    public async Task Unknown_tool_is_rejected_before_any_external_call()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([
            ScriptStep.ToolCall("email_customer", """{"ticket_id":"T-1001","body":"hi"}"""),
            ScriptStep.Finish(),
        ]);
        var result = await sample.CreateRunner(model, ScriptedApprover.ApproveAll()).RunAsync("r1", "T-1001");

        Assert.Empty(sample.Executed);
        Assert.Empty(sample.Ledger.Effects);
        Assert.Equal("unknown_tool", result.State.History[0].Outcome);
        Assert.Equal("validation_error", model.Requests[1].LastObservation!.Kind);
    }

    [Fact]
    public async Task Malformed_arguments_never_reach_a_tool()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([
            ScriptStep.ToolCall("issue_refund", """{"ticket_id":"T-1001","amount_cents":"49900"}"""),
            ScriptStep.ToolCall("issue_refund", """{"ticket_id":"T-1001","amount_cents":-1}"""),
            ScriptStep.Finish(),
        ]);
        var result = await sample.CreateRunner(model, ScriptedApprover.ApproveAll()).RunAsync("r2", "T-1001");

        Assert.Empty(sample.Executed);
        Assert.Equal(2, result.State.InvalidOutputs);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Invalid_output_is_rejected_and_the_model_is_told_why_without_echo()
    {
        var sample = new SampleHarness();
        var model = new ScriptedModel([new ScriptStep("garbage sk-LEAKYLEAKY123456"), ScriptStep.Finish()]);
        await sample.CreateRunner(model, ScriptedApprover.ApproveAll()).RunAsync("r3", "T-1001");

        var obs = model.Requests[1].LastObservation!;
        Assert.Equal("validation_error", obs.Kind);
        Assert.False(obs.Untrusted);
        Assert.DoesNotContain("LEAKY", obs.Content);
    }
}
