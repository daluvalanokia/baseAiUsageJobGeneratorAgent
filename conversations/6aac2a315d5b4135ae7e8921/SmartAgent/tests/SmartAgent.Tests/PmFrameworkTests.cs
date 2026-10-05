using System.Linq;
using SmartAgent.Core.Dat;
using SmartAgent.Core.Pm;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class PmFrameworkTests : IDisposable
{
    private readonly string _root;

    public PmFrameworkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "smartagent-pm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---------- RACI chart ----------

    [Fact]
    public void Raci_chart_has_exactly_one_accountable_per_phase()
    {
        foreach (var phase in RaciChart.Phases)
        {
            var accountables = RaciChart.Matrix[phase].Count(kv => kv.Value == RaciType.Accountable);
            Assert.True(accountables == 1, $"phase '{phase}' has {accountables} Accountable, expected exactly 1");
        }
    }

    [Fact]
    public void Raci_assignments_resolve_against_a_concrete_roster()
    {
        var roster = new SprintPlanner().Roster(teamSize: 18, moduleCount: 4);
        var chart = RaciChart.FullChart(roster);
        Assert.Equal(RaciChart.Phases.Length, chart.Count);
        foreach (var phase in RaciChart.Phases)
        {
            var rows = chart[phase];
            Assert.Contains(rows, r => r.Type == RaciType.Accountable);
            foreach (var (key, _, type) in rows)
                Assert.Contains(roster, r => r.Key == key);
        }
        // engagement factors are RACI-weighted and within (0,1]
        foreach (var resource in roster)
        {
            var f = RaciChart.EngagementFactor(resource);
            Assert.InRange(f, 0.01m, 1m);
        }
    }


    // ---------- phase & layer model (iteration B) ----------

    [Fact]
    public async Task Plan_has_phase_bands_and_layer_tagged_stories()
    {
        var snapshot = new SourceSnapshot
        {
            SourceType = SourceType.GitHub, SourceName = "owner/repo", SourceDetail = "branch main, 8 files, 4 commits reviewed",
            Files = new List<SourceFile>
            {
                new() { Path = "repo/src/App.Web/Controllers/HomeController.cs",
                    Content = "public class HomeController { public IActionResult Index() => View(); }" },
                new() { Path = "repo/src/App.Web/Views/Home/Index.cshtml", Content = "<h1>hi</h1>" },
                new() { Path = "repo/src/App.Core/Services/CatalogService.cs", Content = "public class CatalogService {}" },
                new() { Path = "repo/src/App.Core/Models/Product.cs",
                    Content = "public class Product { public int Id { get; set; } public string Name { get; set; } }" },
                new() { Path = "repo/db/schema.sql", Content = "CREATE TABLE products;" },
                new() { Path = "repo/README.md", Content = "# readme" }
            },
            History = new List<SourceCommit>
            {
                new() { Sha = "a1", Message = "Add catalog search filters", Date = DateTimeOffset.UtcNow,
                    TouchedPaths = new List<string> { "repo/src/App.Web/Controllers/HomeController.cs" } },
                new() { Sha = "b2", Message = "Improve catalog search filters", Date = DateTimeOffset.UtcNow,
                    TouchedPaths = new List<string> { "repo/src/App.Web/Controllers/HomeController.cs" } },
                new() { Sha = "c3", Message = "Update product schema", Date = DateTimeOffset.UtcNow,
                    TouchedPaths = new List<string> { "repo/db/schema.sql" } }
            }
        };

        var ingestor = new RequirementIngestor();
        var planner = new SprintPlanner();
        var governor = new PmGovernor(new ThreadGovernor(), ingestor, planner, new PmQualityEngine());
        var options = new PmOptions { Name = "Phase Programme", Years = 2, TeamSize = 18 };

        var plan = await governor.GenerateAsync(snapshot, options, Path.Combine(_root, "programs"));

        // stories carry layer + phase
        Assert.Contains(plan.Stories, s => s.Layer == "Controllers");
        Assert.Contains(plan.Stories, s => s.Layer == "Frontend");
        Assert.Contains(plan.Stories, s => s.Layer == "Database");
        Assert.Contains(plan.Stories, s => s.Phase == "framework");
        Assert.Contains(plan.Stories, s => s.Phase == "enhance");

        // commit history becomes enhancement stories grounded in the pattern
        var enhance = plan.Stories.Where(s => s.Phase == "enhance").ToList();
        Assert.Contains(enhance, s => s.Title.Contains("catalog search filters"));
        Assert.Contains(enhance, s => s.Layer == "Controllers" && s.Title.Contains("HomeController") == false);

        // sprints are phase-banded: framework before build before enhance
        var numbered = plan.SprintPlans.OrderBy(s => s.Number).ToList();
        var phases = numbered.Select(s => s.Phase).ToList();
        var lastFramework = phases.FindLastIndex(p => p == "framework");
        var firstEnhance = phases.FindIndex(p => p == "enhance");
        var firstBuild = phases.FindIndex(p => p == "build");
        Assert.True(firstBuild < 0 || lastFramework < firstBuild, "framework sprints must precede build sprints");
        Assert.True(firstEnhance < 0 || firstBuild < firstEnhance, "build sprints must precede enhance sprints");

        // build waves are vertical slices: stories in one sprint share a module
        var waveSprint = plan.SprintPlans.FirstOrDefault(s => s.StoryKeys.Count >= 2
            && s.Phase == "build"
            && s.StoryKeys.Select(k => plan.Stories.First(x => x.Key == k)).Select(x => x.Module).Distinct().Count() == 1);
        Assert.NotNull(waveSprint);
        var waveLayers = waveSprint.StoryKeys.Select(k => plan.Stories.First(x => x.Key == k))
            .Select(x => x.Layer).Distinct().ToList();
        Assert.True(waveLayers.Count >= 2, $"vertical slice expected multiple layers, got {string.Join(",", waveLayers)}");

        // enhancement stories keep the target module, not the epic's default
        Assert.Contains(enhance, s => s.Module != plan.Epics[0].Module || s.Layer == "Database");
    }

    [Fact]
    public void Layer_taxonomy_classifies_paths()
    {
        Assert.Equal("Database", SourceAnalyzer.LayerOf("repo/db/schema.sql"));
        Assert.Equal("Controllers", SourceAnalyzer.LayerOf("repo/src/App.Web/Controllers/HomeController.cs"));
        Assert.Equal("Frontend", SourceAnalyzer.LayerOf("repo/src/App.Web/Views/Home/Index.cshtml"));
        Assert.Equal("Frontend", SourceAnalyzer.LayerOf("client/src/components/ui/dialog.tsx"));
        Assert.Equal("Data Adapter", SourceAnalyzer.LayerOf("client/src/api-client/api-client.ts"));
        Assert.Equal("Server", SourceAnalyzer.LayerOf("repo/src/App.Core/Services/CatalogService.cs"));
    }

    // ---------- requirement ingestor ----------

    private static SourceSnapshot Snapshot() => new()
    {
        SourceType = SourceType.GitHub, SourceName = "owner/repo", SourceDetail = "branch main, 12 files analyzed",
        Files = new List<SourceFile>
        {
            new() { Path = "repo/src/SmartAgent.Core/Dat/ThreadGovernor.cs", Content = "// lanes" },
            new() { Path = "repo/src/SmartAgent.Core/Dat/ObjectBank.cs", Content = "// bank" },
            new() { Path = "repo/src/SmartAgent.Web/Controllers/HomeController.cs", Content = "// mvc" },
            new() { Path = "repo/src/SmartAgent.Web/Views/Home/Index.cshtml", Content = "<h1/>" },
            new() { Path = "repo/tests/SmartAgent.Tests/DatFrameworkTests.cs", Content = "// xunit" },
            new() { Path = "repo/README.md", Content = "# readme" }
        }
    };

    [Fact]
    public void Requirements_are_captured_from_source_files()
    {
        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromSource(Snapshot());

        Assert.True(epics.Count >= 5, $"expected ≥5 epics, got {epics.Count}");
        Assert.True(stories.Count >= 8, $"expected ≥8 stories, got {stories.Count}");
        Assert.Contains(epics, e => e.Name == "Foundation & Auth");
        Assert.Contains(epics, e => e.Theme == "Release");

        // modules detected from src/<Project>
        var modules = ingestor.DetectModules(Snapshot());
        Assert.Contains(modules, m => m.StartsWith("Core"));
        Assert.Contains(modules, m => m.StartsWith("Web"));

        foreach (var s in stories)
        {
            Assert.InRange(s.Points, 1, 13);
            Assert.NotEmpty(s.AcceptanceCriteria);
            Assert.NotEmpty(s.AsA);
            Assert.Contains(epics, e => e.Key == s.EpicKey);
        }
        // foundation stories are highest priority
        var foundation = stories.Where(s => s.EpicKey == "E01").ToList();
        Assert.Contains(foundation, s => s.Priority >= 95);
    }

    [Fact]
    public void Requirements_from_prompt_become_stories()
    {
        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromPrompt(
            "Build a family safety platform. Track live locations. Support chat. Manage devices. Alert on outages.");

        Assert.Single(epics);
        Assert.True(stories.Count >= 4);
        Assert.All(stories, s => Assert.NotEmpty(s.AcceptanceCriteria));
    }

    // ---------- sprint planner ----------

    [Fact]
    public async Task Program_plan_spans_multi_year_sprints_with_budget()
    {
        var ingestor = new RequirementIngestor();
        var planner = new SprintPlanner();
        var governor = new PmGovernor(new ThreadGovernor(), ingestor, planner, new PmQualityEngine());
        var options = new PmOptions { Name = "Test Programme", Years = 2, TeamSize = 18, BudgetCapPerYearUsd = 2_000_000m };

        var plan = await governor.GenerateAsync(Snapshot(), options, Path.Combine(_root, "programs"));

        // multi-year program shape
        Assert.Equal(2, plan.Years);
        Assert.Equal(52, plan.Sprints);
        Assert.Equal(104, plan.Weeks);
        Assert.Equal(18, plan.Resources.Count);
        Assert.Equal(8, plan.Quarters.Count);
        Assert.True(plan.ProgramEnd > plan.ProgramStart.AddYears(1));

        // every sprint belongs to exactly one quarter; quarters tile the program
        foreach (var sprint in plan.SprintPlans)
            Assert.Equal(1, plan.Quarters.Count(q => sprint.Number >= q.SprintFrom && sprint.Number <= q.SprintTo));
        Assert.Equal(1, plan.Quarters[0].SprintFrom);
        Assert.Equal(plan.Sprints, plan.Quarters[^1].SprintTo);

        // stories are each assigned to at most one sprint
        var assigned = plan.SprintPlans.SelectMany(s => s.StoryKeys).ToList();
        Assert.Equal(assigned.Count, assigned.Distinct().Count());

        // budget = RACI-weighted engagement across all sprints
        var engagement = plan.Resources.Sum(r => Math.Round(r.HoursPerSprint * RaciChart.EngagementFactor(r), 1) * r.HourlyRate);
        Assert.Equal(Math.Round(engagement * plan.Sprints, 0), plan.Budget.TotalUsd);
        Assert.Equal(plan.SprintPlans.Where(s => s.BudgetUsd > 0).Count(), plan.Sprints);
        Assert.True(plan.Budget.TotalUsd > 0);
        Assert.True(plan.Budget.CommittedHours > 0);

        // releases: Alpha at Q1, GA at the final quarter
        Assert.Equal("Alpha", plan.Quarters[0].Release);
        Assert.Equal("GA Release", plan.Quarters[^1].Release);

        // role-threaded deliverables from parallel lanes
        Assert.True(plan.RoleOutputs.Count >= 10, $"expected ≥10 role outputs, got {plan.RoleOutputs.Count}");
        Assert.Contains(plan.RoleOutputs, kv => kv.Key == "Solution Architect");
        Assert.True(plan.RoleOutputs.All(kv => kv.Value.Length > 20));

        // thread consolidation report written, with lane bookkeeping
        Assert.True(File.Exists(plan.GovernorReportFile), "governor consolidation .dat expected");
        var reportLines = await File.ReadAllLinesAsync(plan.GovernorReportFile);
        Assert.Contains(reportLines, l => l.StartsWith("# LanesSpawned=") && l.Contains("LanesRecycled="));
    }


    [Fact]
    public void Source_review_produces_class_grounded_feature_slices()
    {
        var analyzer = new SourceAnalyzer();
        var snapshot = new SourceSnapshot
        {
            SourceType = SourceType.GitHub, SourceName = "owner/repo", SourceDetail = "branch main, 3 files analyzed",
            Files = new List<SourceFile>
            {
                new() { Path = "r/src/App/Controllers/LibraryController.cs", Content =
                    "public class LibraryController { public IActionResult Index() => View(); public IActionResult Create() => View(); }" },
                new() { Path = "r/src/App/Models/Book.cs", Content =
                    "public class Book { public string Title { get; set; } public int Pages { get; set; } }" },
                new() { Path = "r/src/App/Services/LoanService.cs", Content =
                    "public class LoanService { public bool Renew(int id) => true; }" }
            }
        };
        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromSource(snapshot, analyzer.Classes(snapshot));

        // controller class becomes a feature-slice story with method-named acceptance criteria
        Assert.Contains(stories, s => s.Title.Contains("LibraryController"));
        Assert.Contains(stories, s => s.AcceptanceCriteria.Any(ac => ac.Contains("Index")));
        // entity and service types are reviewed too
        Assert.Contains(stories, s => s.Title.Contains("Book"));
        Assert.Contains(stories, s => s.Title.Contains("LoanService"));
        // integration contract story per module
        Assert.Contains(stories, s => s.Title.Contains("integration & contract tests"));
    }

    [Fact]
    public async Task Program_resolves_backlog_into_populated_sprints_across_horizon()
    {
        var ingestor = new RequirementIngestor();
        var planner = new SprintPlanner();
        var governor = new PmGovernor(new ThreadGovernor(), ingestor, planner, new PmQualityEngine());
        var options = new PmOptions { Name = "Spread Programme", Years = 2, TeamSize = 18 };

        var plan = await governor.GenerateAsync(Snapshot(), options, Path.Combine(_root, "spread"));

        // stories are not dumped into the first sprint: they spread across the horizon
        var populated = plan.SprintPlans.Where(s => s.StoryKeys.Count > 0).ToList();
        Assert.True(populated.Count >= 5, $"expected backlog spread across sprints, got {populated.Count} populated sprints");
        Assert.True(plan.SprintPlans[0].StoryKeys.Count <= 3, "sprint 1 should not hold the whole backlog");
        Assert.All(plan.SprintPlans, s => Assert.True(s.StoryKeys.Count <= 5, $"sprint {s.Number} overloaded"));

        // release-theme stories ride the tail sprints, after the delivery train
        var lastPopulated = plan.SprintPlans.Last(s => s.StoryKeys.Count > 0).Number;
        var releaseStories = plan.Stories.Where(s => plan.Epics.First(e => e.Key == s.EpicKey).Theme == "Release").Select(s => s.Key).ToList();
        Assert.All(releaseStories, key =>
            Assert.Contains(plan.SprintPlans.Skip(plan.SprintPlans.Count - 4), sp => sp.StoryKeys.Contains(key)));

        // every sprint keeps a goal (delivery or stabilization) and budget
        Assert.All(plan.SprintPlans, s => Assert.False(string.IsNullOrWhiteSpace(s.Goal)));
        Assert.All(plan.SprintPlans, s => Assert.True(s.BudgetUsd > 0));
    }

    [Fact]
    public void Budget_cap_flags_overruns()
    {
        var ingestor = new RequirementIngestor();
        var planner = new SprintPlanner();
        var (epics, stories) = ingestor.FromSource(Snapshot());
        var resources = planner.Roster(18, 4);
        var squads = planner.FormSquads(resources, epics);

        var unconstrained = planner.Plan("No Cap", "GitHub", "owner/repo", "x", resources, epics, stories, squads,
            new PmOptions { Years = 2, BudgetCapPerYearUsd = 0 });
        Assert.True(unconstrained.Budget.WithinCap);

        var tiny = planner.Plan("Tiny Cap", "GitHub", "owner/repo", "x", resources, epics, stories, squads,
            new PmOptions { Years = 2, BudgetCapPerYearUsd = 1_000m });
        Assert.False(tiny.Budget.WithinCap);
    }
}
