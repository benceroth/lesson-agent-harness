namespace AtlasDesk.Harness.Tools;

/// <summary>
/// Allow-list of tools the model may name. Lookup is exact and case-sensitive; nothing is guessed.
/// A tool that is registered but not allow-listed is invisible to the model.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<ITool> tools, IEnumerable<string>? allowList = null)
    {
        var allowed = allowList is null ? null : new HashSet<string>(allowList, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (allowed is not null && !allowed.Contains(tool.Definition.Name)) continue;
            if (!_tools.TryAdd(tool.Definition.Name, tool))
                throw new ArgumentException($"Duplicate tool '{tool.Definition.Name}'.");
        }
    }

    public bool TryResolve(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    public IReadOnlyCollection<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToList();

    public IReadOnlyCollection<string> Names => _tools.Keys;
}
