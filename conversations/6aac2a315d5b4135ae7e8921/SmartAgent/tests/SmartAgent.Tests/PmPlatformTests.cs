using System.Linq;
using SmartAgent.Core.Dat;
using SmartAgent.Core.Pm;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

/// <summary>
/// Deep-capture artifact engine: any GitHub source must yield the full
/// reference-grade project development platform — platform profile,
/// per-sprint tests, dual-provider DB scripts with rollbacks, code
/// artifacts, HLD/DDD, issue register and GO/NO-GO milestones.
/// </summary>
public class PmPlatformTests : IDisposable
{
    private readonly string _root;

    public PmPlatformTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "smartagent-pmplat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static SourceSnapshot RichSnapshot() => new()
    {
        SourceType = SourceType.GitHub, SourceName = "owner/claft",
        SourceDetail = "branch main, captured for platform generation",
        Files = new List<SourceFile>
        {
            new()
            {
                Path = "src/Claft.Mvc/Claft.Mvc.csproj",
                Content = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>" +
                          "<ItemGroup><PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"8.0.0\"/>" +
                          "<PackageReference Include=\"BCrypt.Net-Next\" Version=\"4.0.0\"/></ItemGroup></Project>"
            },
            new()
            {
                Path = "src/Claft.Mvc/Controllers/AuthController.cs",
                Content = "public class AuthController : Controller\n{\n    public async Task<IActionResult> Login(LoginViewModel model)\n    {\n        return View(model);\n    }\n    public async Task<IActionResult> Logout()\n    {\n        return RedirectToAction(\"Index\");\n    }\n}"
            },
            new()
            {
                Path = "src/Claft.Mvc/Services/AuthService.cs",
                Content = "public class AuthService\n{\n    public string HashPassword(string plain)\n    {\n        return \"hash\";\n    }\n    public bool ValidatePassword(string plain, string stored)\n    {\n        return true;\n    }\n}"
            },
            new()
            {
                Path = "src/Claft.Mvc/Models/User.cs",
                Content = "[Table(\"claft_users\")]\npublic class User\n{\n    [Column(\"username\")][MaxLength(100)] public string Username { get; set; }\n    [Column(\"password_hash\")][MaxLength(100)] public string PasswordHash { get; set; }\n    [Column(\"role\")][MaxLength(20)] public string Role { get; set; }\n    [Column(\"is_active\")] public bool IsActive { get; set; }\n}"
            },
            new()
            {
                Path = "src/Claft.Mvc/Models/LocationHistory.cs",
                Content = "[Table(\"claft_location_history\")]\npublic class LocationHistory\n{\n    [Column(\"lat\")] public decimal Lat { get; set; }\n    [Column(\"lng\")] public decimal Lng { get; set; }\n    [Column(\"location_name\")] public string LocationName { get; set; }\n    [Column(\"recorded_at\")] public DateTime RecordedAt { get; set; }\n}"
            },
            new()
            {
                Path = "src/Claft.Mvc/Services/GeofenceService.cs",
                Content = "public class GeofenceService\n{\n    public double HaversineDistanceMiles(double lat1, double lng1, double lat2, double lng2)\n    {\n        return 0.0;\n    }\n    public bool IsInsideGeofence(double miles, double radius)\n    {\n        return miles <= radius;\n    }\n}"
            },
            new()
            {
                Path = "src/Claft.Mvc/Program.cs",
                Content = "var builder = WebApplication.CreateBuilder(args);\nvar db = builder.Configuration[\"ConnectionStrings:DefaultConnection\"];\nvar provider = Environment.GetEnvironmentVariable(\"DB_PROVIDER\");\n"
            },
            new() { Path = "src/Claft.Mvc/README.md", Content = "# Claft" }
        }
    };

    private static async Task<ProgramPlan> GeneratePlanAsync(string root)
    {
        var ingestor = new RequirementIngestor();
        var planner = new SprintPlanner();
        var governor = new PmGovernor(new ThreadGovernor(), ingestor, planner);
        var options = new PmOptions { Name = "Claft Platform Programme", Years = 2, TeamSize = 18 };
        return await governor.GenerateAsync(RichSnapshot(), options, Path.Combine(root, "programs"));
    }

    // ---------- deep source capture ----------

    [Fact]
    public void Analyzer_captures_classes_methods_tables_and_dependencies()
    {
        var analyzer = new SourceAnalyzer();
        var snapshot = RichSnapshot();

        var classes = analyzer.Classes(snapshot);
        Assert.Contains(classes, c => c.Name == "AuthController" && c.Kind == "Controller"
                                      && c.Methods.Contains("Login"));
        Assert.Contains(classes, c => c.Name == "AuthService" && c.Kind == "Service"
                                      && c.Methods.Contains("HashPassword"));

        var tables = analyzer.Tables(snapshot);
        Assert.Contains(tables, t => t.Name == "claft_users");
        var users = tables.First(t => t.Name == "claft_users");
        Assert.Contains(users.Columns, c => c.Name == "Username" || c.Name == "user_name");

        var deps = analyzer.Dependencies(snapshot);
        Assert.Contains(deps, d => d.StartsWith("Microsoft.EntityFrameworkCore.SqlServer"));

        var config = analyzer.ConfigKeys(snapshot);
        Assert.Contains(config, k => k.Contains("DB_PROVIDER") || k.Contains("DefaultConnection"));
    }

    [Fact]
    public void Platform_profile_is_detected_from_the_source()
    {
        var platform = new SourceAnalyzer().DetectPlatform(RichSnapshot());
        Assert.Equal("Microsoft Enterprise Stack", platform.Name);
        Assert.Equal("SQL Server 2019", platform.PrimaryDb);
        Assert.Equal("Oracle 19c", platform.SecondaryDb);
        Assert.Contains(platform.Arch.DataLayer, l => l.Contains("EF Core"));
        Assert.Contains(platform.Arch.DomainTables, t => t.Contains("claft_users"));
    }

    // ---------- program-level artifacts ----------

    [Fact]
    public async Task Program_carries_full_reference_grade_artifacts()
    {
        var plan = await GeneratePlanAsync(_root);

        Assert.NotNull(plan.Platform);
        Assert.NotEmpty(plan.Milestones);
        Assert.NotEmpty(plan.UnitTests);
        Assert.NotEmpty(plan.SystemTests);
        Assert.NotEmpty(plan.IssueRegister);
        Assert.True(plan.UnitTestCount > 0 && plan.SystemTestCount > 0);

        // every milestone has GO/NO-GO criteria and lands on a release quarter
        foreach (var m in plan.Milestones)
        {
            Assert.True(m.GoNoGo.Count >= 3, $"milestone {m.Name} lacks GO/NO-GO criteria");
            Assert.Contains(plan.Quarters, q => q.Release == m.Name);
        }

        // issue register: one seeded defect per sprint, KPI-compatible statuses
        Assert.Equal(plan.Sprints, plan.IssueRegister.Count);
        Assert.Contains(plan.IssueRegister, i => i.Status == "Resolved");
        Assert.Contains(plan.IssueRegister, i => i.Status == "Open");
        Assert.All(plan.IssueRegister, i => Assert.Matches(@"^BUG-S\d{2}-001$", i.Id));

        // every sprint carries its engineering detail pack
        foreach (var sprint in plan.SprintPlans)
        {
            Assert.NotNull(sprint.Detail);
            Assert.NotNull(sprint.Detail!.DbScript);
            Assert.NotEmpty(sprint.Detail.ImplChecklist);
            Assert.NotEmpty(sprint.Detail.Hld.Components);
            Assert.NotEmpty(sprint.Detail.Ddd.Sequence);
            if (sprint.StoryKeys.Count > 0)
            {
                Assert.NotEmpty(sprint.Detail.UnitTests);
                Assert.NotEmpty(sprint.Detail.SystemTests);
            }
        }
    }

    [Fact]
    public async Task Db_scripts_are_dual_provider_with_rollback_and_seed()
    {
        var plan = await GeneratePlanAsync(_root);

        // sprint 1 introduces tables: forward DDL on both providers + rollback
        var s1 = plan.SprintPlans[0];
        var db = s1.Detail!.DbScript!;
        Assert.StartsWith("001_", db.MigrationClass);
        Assert.Contains("CREATE TABLE", db.PrimaryDdl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE", db.SecondaryDdl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IDENTITY", db.PrimaryDdl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SYSTIMESTAMP", db.SecondaryDdl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP TABLE", db.RollbackPrimary, StringComparison.OrdinalIgnoreCase);

        // tables are introduced across sprints exactly once (cumulative migrations)
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sprint in plan.SprintPlans)
        {
            var ddl = sprint.Detail!.DbScript!.PrimaryDdl;
            foreach (var table in plan.Platform!.Arch.DomainTables)
            {
                var name = table.Split(' ')[0];
                if (ddl.Contains($"CREATE TABLE [{name}]", StringComparison.OrdinalIgnoreCase))
                    Assert.True(created.Add(name), $"table {name} created twice");
            }
        }
    }

    [Fact]
    public async Task Unit_tests_reference_captured_source_and_follow_id_scheme()
    {
        var plan = await GeneratePlanAsync(_root);

        // captured classes ground the scenarios
        var allScenarios = string.Join("\n", plan.UnitTests.Select(t => $"{t.Cls}.{t.Method}"));
        Assert.Contains("AuthController", allScenarios);
        Assert.Contains("Login", allScenarios);

        Assert.All(plan.UnitTests, t => Assert.Matches(@"^UT-S\d{2}-\d{3}$", t.Id));
        Assert.All(plan.SystemTests, t => Assert.Matches(@"^ST-S\d{2}-\d{3}$", t.Id));

        // cumulative code artifact ledger never decreases
        var previous = -1;
        foreach (var sprint in plan.SprintPlans)
        {
            var cumulative = sprint.Detail!.CodeArtifacts.CumulativeCount;
            Assert.True(cumulative >= previous, $"cumulative ledger decreased at sprint {sprint.Number}");
            previous = cumulative;
        }
    }

    [Fact]
    public async Task Artifact_generation_is_deterministic_for_the_same_source()
    {
        var a = await GeneratePlanAsync(Path.Combine(_root, "a"));
        var b = await GeneratePlanAsync(Path.Combine(_root, "b"));

        Assert.Equal(a.UnitTests.Count, b.UnitTests.Count);
        Assert.Equal(a.SprintPlans[0].Detail!.DbScript!.PrimaryDdl,
            b.SprintPlans[0].Detail!.DbScript!.PrimaryDdl);
        Assert.Equal(a.IssueRegister.Count, b.IssueRegister.Count);
        Assert.Equal(a.Milestones.Select(m => m.Name), b.Milestones.Select(m => m.Name));
        Assert.Equal(a.Platform!.Arch.DomainTables, b.Platform!.Arch.DomainTables);
    }
}
