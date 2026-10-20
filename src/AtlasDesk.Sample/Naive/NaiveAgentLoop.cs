using System.Text.Json.Nodes;
using AtlasDesk.Harness.Model;
using AtlasDesk.Harness.State;
using AtlasDesk.Harness.Tools;

namespace AtlasDesk.Sample.Naive;

public sealed record NaiveResult(int Steps, long Tokens, bool Completed, bool GuardTripped, string? FinalMessage);

/// <summary>
/// The "prompt-only" prototype, written the way notebook agents often are, for comparison only:
/// lenient JSON extraction, fuzzy tool-name matching, no argument validation, no approval gate,
/// no budgets. The demo guard exists only so the comparison program itself terminates.
/// </summary>
public sealed class NaiveAgentLoop(IEnumerable<ITool> tools, IModelClient model, int demoGuardIterations = 200, TimeSpan? demoGuardTime = null)
{
    private readonly List<ITool> _tools = tools.ToList();

    public async Task<NaiveResult> RunAsync(string runId, string ticketId, CancellationToken ct = default)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        guard.CancelAfter(demoGuardTime ?? TimeSpan.FromSeconds(1));
        var steps = 0;
        long tokens = 0;
        Observation? last = null;
        try
        {
            while (steps < demoGuardIterations)
            {
                var request = new ModelRequest(runId, ticketId, steps + 1, int.MaxValue, [], _tools.Select(t => t.Definition).ToList(), last);
                var response = await model.NextAsync(request, guard.Token);
                steps++;
                tokens += response.Tokens;

                var node = ExtractJson(response.RawOutput);
                var type = node?["type"]?.GetValue<string>();
                if (type == "finish") return new NaiveResult(steps, tokens, true, false, node?["message"]?.ToString());
                if (type != "tool_call") { last = new Observation("error", null, "bad action", false); continue; }

                var tool = FindTool(node!["tool"]?.ToString() ?? "");
                if (tool is null) { last = new Observation("error", null, "no such tool", false); continue; }
                var args = ToArgs(node["args"] as JsonObject);
                try
                {
                    var result = await tool.ExecuteAsync(new ToolInvocation(runId, args, Guid.NewGuid().ToString("N")), guard.Token);
                    last = new Observation("tool_result", tool.Definition.Name, result.Content, true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = new Observation("error", null, ex.Message, false); }
            }
            return new NaiveResult(steps, tokens, false, true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NaiveResult(steps, tokens, false, true, null);
        }
    }

    private static JsonObject? ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(raw[start..(end + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // Guesses: the best token overlap wins, so "email_customer" quietly becomes "send_customer_email".
    private ITool? FindTool(string requested)
    {
        var exact = _tools.FirstOrDefault(t => t.Definition.Name == requested);
        if (exact is not null) return exact;
        var wanted = requested.Split('_', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return _tools
            .Select(t => (Tool: t, Score: t.Definition.Name.Split('_').Count(wanted.Contains)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Tool)
            .FirstOrDefault();
    }

    private static ToolArguments ToArgs(JsonObject? args)
    {
        var values = new List<KeyValuePair<string, object>>();
        foreach (var (k, v) in args ?? [])
        {
            object value = v switch
            {
                JsonValue jv when jv.TryGetValue<long>(out var l) => l,
                JsonValue jv when jv.TryGetValue<double>(out var d) => (long)Math.Round(d),
                JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
                JsonValue jv => jv.ToString(),
                _ => v?.ToJsonString() ?? "",
            };
            values.Add(new(k, value));
        }
        return new ToolArguments(values);
    }
}
