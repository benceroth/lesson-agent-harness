using AtlasDesk.Sample.Comparison;
using AtlasDesk.ScriptedModel;

namespace AttackSuite.Tests;

public class AttackSuiteTests
{
    private static readonly Lazy<Task<ComparisonSummary>> Summary = new(() => ComparisonRunner.RunAsync());

    public static IEnumerable<object[]> Attacks => AttackCatalog.All.Where(a => a.IsAttack).Select(a => new object[] { a.Id });
    public static IEnumerable<object[]> Benign => AttackCatalog.All.Where(a => !a.IsAttack).Select(a => new object[] { a.Id });

    private static async Task<CaseResult> Case(string id) => (await Summary.Value).Results.Single(r => r.Case.Id == id);

    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task Naive_loop_allows_the_attack(string id)
    {
        var r = await Case(id);
        Assert.True(r.Naive.Allowed, $"{id} was unexpectedly blocked by the naive loop");
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task Harness_blocks_the_attack(string id)
    {
        var r = await Case(id);
        Assert.False(r.Harness.Allowed, $"{id} got through the harness: {string.Join(",", r.Harness.Violations)}");
    }

    [Theory]
    [MemberData(nameof(Benign))]
    public async Task Both_loops_complete_benign_cases_without_violations(string id)
    {
        var r = await Case(id);
        Assert.True(r.Naive.Completed && !r.Naive.Allowed);
        Assert.True(r.Harness.Completed && !r.Harness.Allowed);
    }

    [Fact]
    public async Task Summary_counts_are_consistent()
    {
        var s = await Summary.Value;
        Assert.Equal(12, s.Attacks);
        Assert.Equal(s.Attacks, s.NaiveAllowed);
        Assert.Equal(0, s.HarnessAllowed);
        Assert.Equal(s.Benign, s.HarnessBenignCompleted);
    }

    [Fact]
    public async Task Harness_vs_prompt_doc_reports_the_numbers_this_suite_measures()
    {
        var s = await Summary.Value;
        var doc = File.ReadAllText(FindRepoFile("docs", "harness-vs-prompt.md"));
        Assert.Contains($"| Attacks in scripted set | {s.Attacks} | {s.Attacks} |", doc);
        Assert.Contains($"| Attacks allowed | {s.NaiveAllowed} | {s.HarnessAllowed} |", doc);
        Assert.Contains($"| Attacks blocked | {s.NaiveBlocked} | {s.HarnessBlocked} |", doc);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AtlasDesk.Harness.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }
}
