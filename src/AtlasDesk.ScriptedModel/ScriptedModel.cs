using AtlasDesk.Harness.Model;

namespace AtlasDesk.ScriptedModel;

/// <summary>One scripted model turn: what it returns, what it "costs", and how it misbehaves.</summary>
public sealed record ScriptStep(
    string Output,
    int Tokens = 150,
    decimal CostUsd = 0.002m,
    TimeSpan? Delay = null,
    bool Hang = false,
    Exception? Throw = null)
{
    public static ScriptStep ToolCall(string tool, string argsJson, int tokens = 150) =>
        new($$"""{"type":"tool_call","tool":"{{tool}}","args":{{argsJson}}}""", tokens);

    public static ScriptStep Finish(string message = "Done.") =>
        new($$"""{"type":"finish","message":"{{message}}"}""");
}

/// <summary>
/// Fake model that replays scripted outputs (valid, malicious or broken). No network, no API key.
/// Records every request it receives so tests can assert exactly what the harness exposed.
/// </summary>
public sealed class ScriptedModel : IModelClient
{
    private readonly IReadOnlyList<ScriptStep> _steps;
    private readonly bool _repeatLast;
    private int _index;

    public ScriptedModel(IEnumerable<ScriptStep> steps, bool repeatLast = false, int startAt = 0)
    {
        _steps = steps.ToList();
        _repeatLast = repeatLast;
        _index = startAt;
    }

    public List<ModelRequest> Requests { get; } = [];
    public int Calls => Requests.Count;
    public int LastTokens { get; private set; }

    public async Task<ModelResponse> NextAsync(ModelRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        ScriptStep step;
        if (_index < _steps.Count) step = _steps[_index++];
        else if (_repeatLast && _steps.Count > 0) step = _steps[^1];
        else throw new InvalidOperationException("Script exhausted.");

        if (step.Throw is not null) throw step.Throw;
        if (step.Hang) await Task.Delay(Timeout.Infinite, ct);
        if (step.Delay is { } d) await Task.Delay(d, ct);
        LastTokens = step.Tokens;
        return new ModelResponse(step.Output, step.Tokens, step.CostUsd);
    }
}
