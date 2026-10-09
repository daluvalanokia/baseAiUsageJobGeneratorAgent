using SmartAgent.Core.Pm;
using Xunit;

namespace SmartAgent.Tests;

public class PmProjectPageTests
{
    // ---------- weeks → years ----------

    [Theory]
    [InlineData(2, 1)]       // a two-week spike still gets one engine year
    [InlineData(26, 1)]      // half year
    [InlineData(52, 1)]      // exactly one year
    [InlineData(53, 2)]      // one week over → two years
    [InlineData(104, 2)]     // two years
    [InlineData(260, 5)]     // max allowed weeks
    public void Weeks_fold_into_whole_engine_years(int weeks, int expectedYears)
    {
        var input = new PmProjectInput { GitHubUrl = "https://github.com/o/r", Weeks = weeks };
        Assert.Equal(expectedYears, input.YearsFor());
    }

    // ---------- total budget → per-year cap ----------

    [Fact]
    public void Total_budget_splits_evenly_across_years()
    {
        var input = new PmProjectInput
        {
            GitHubUrl = "https://github.com/o/r", Weeks = 104, TotalBudgetUsd = 500_000m
        };
        Assert.Equal(250_000m, input.PerYearBudgetFor());
    }

    [Fact]
    public void One_year_budget_stays_whole_and_rounds_up_on_split()
    {
        var oneYear = new PmProjectInput
        {
            GitHubUrl = "https://github.com/o/r", Weeks = 40, TotalBudgetUsd = 120_000m
        };
        Assert.Equal(120_000m, oneYear.PerYearBudgetFor());

        var threeYears = new PmProjectInput
        {
            GitHubUrl = "https://github.com/o/r", Weeks = 130, TotalBudgetUsd = 100_000m
        };
        Assert.Equal(33_333m, threeYears.PerYearBudgetFor());
    }

    [Fact]
    public void Zero_budget_is_unconstrained()
    {
        var input = new PmProjectInput { GitHubUrl = "https://github.com/o/r", Weeks = 52, TotalBudgetUsd = 0m };
        Assert.Equal(0m, input.PerYearBudgetFor());
    }

    // ---------- composed prompt ----------

    [Fact]
    public void Prompt_carries_repo_duration_budget_and_team()
    {
        var input = new PmProjectInput
        {
            Name = "Mvc Reshunyasanga", GitHubUrl = "https://github.com/daluvalanokia/mvcreshunyasanga ",
            Weeks = 104, TotalBudgetUsd = 500_000m, TeamSize = 24
        };
        var prompt = input.ComposePrompt();
        Assert.StartsWith("PM hub analysis of https://github.com/daluvalanokia/mvcreshunyasanga", prompt);
        Assert.Contains("104 weeks", prompt);
        Assert.Contains("budget $500,000 USD", prompt);
        Assert.Contains("team of 24", prompt);
    }

    [Fact]
    public void Prompt_includes_app_url_when_given_and_omits_team_when_blank()
    {
        var input = new PmProjectInput
        {
            GitHubUrl = "https://github.com/o/r", Weeks = 52,
            FunctionalUrl = "https://app.example.com/Dashboard", TotalBudgetUsd = 0m
        };
        var prompt = input.ComposePrompt();
        Assert.Contains("with live app at https://app.example.com/Dashboard", prompt);
        Assert.Contains("budget unconstrained", prompt);
        Assert.DoesNotContain("team of", prompt);
    }

    [Fact]
    public void Composed_prompt_classifies_as_ingest_so_generation_flow_is_unchanged()
    {
        var input = new PmProjectInput
        {
            GitHubUrl = "https://github.com/o/r", Weeks = 104,
            TotalBudgetUsd = 500_000m, TeamSize = 24,
            FunctionalUrl = "https://app.example.com"
        };
        // Ingest → full program generation from the GitHub source (not a
        // create-app scaffold and not an enhance-app change wave)
        Assert.Equal(PmPromptIntent.Ingest, PromptStudio.IntentOf(input.ComposePrompt()));
    }

    // ---------- end-to-end through the governor with the page's inputs ----------

    [Fact]
    public async Task Project_page_inputs_generate_a_full_program()
    {
        var input = new PmProjectInput
        {
            Name = "ThreadPageRun", GitHubUrl = "https://github.com/o/r",
            Weeks = 104, TotalBudgetUsd = 500_000m, TeamSize = 12
        };
        var governor = new PmGovernor(new SmartAgent.Core.Dat.ThreadGovernor(),
            new RequirementIngestor(), new SprintPlanner(), new PmQualityEngine(),
            new FunctionalSpecGenerator(), new PmThreadManager(cpuThreads: 4));
        var root = Path.Combine(Path.GetTempPath(), "smartagent-pp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new SmartAgent.Domain.SourceSnapshot
            {
                SourceType = SmartAgent.Domain.SourceType.GitHub,
                SourceName = "o/r", SourceDetail = "branch main",
                Files = new List<SmartAgent.Domain.SourceFile>
                {
                    new() { Path = "repo/README.md", Content = "# demo" },
                    new()
                    {
                        Path = "repo/src/App.Web/Controllers/BooksController.cs",
                        Content = "public sealed class BooksController : Controller\n" +
                                  "{\n    public IActionResult Index() => View();\n" +
                                  "    public IActionResult Detail(int id) => View();\n}"
                    },
                    new()
                    {
                        Path = "repo/src/App.Web/Views/Books/Index.cshtml",
                        Content = "<form method=\"post\" action=\"/Books/Create\"><input type=\"text\" name=\"Title\" /><button>save</button></form>"
                    }
                }
            };
            var plan = await governor.GenerateAsync(snapshot, new PmOptions
            {
                Name = input.Name, Years = input.YearsFor(), TeamSize = input.TeamSize,
                BudgetCapPerYearUsd = input.PerYearBudgetFor(), Prompt = input.ComposePrompt()
            }, root);

            Assert.Equal("ThreadPageRun", plan.Name);
            Assert.Equal(2, plan.Years);
            Assert.True(plan.Sprints > 0);
            Assert.True(plan.Stories.Count > 0);
            Assert.True(plan.Budget.TotalUsd > 0);
            Assert.NotEmpty(plan.Timings);          // parallel stage telemetry recorded
            Assert.NotEmpty(plan.ThreadSummary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
