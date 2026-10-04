using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core.Pm;

/// <summary>Options driving program generation.</summary>
public sealed record PmOptions
{
    public string Name { get; init; } = "Agile Delivery Programme";
    public int Years { get; init; } = 2;                  // multi-year horizon
    public int? TeamSize { get; init; }                    // default: derived from modules
    public decimal BudgetCapPerYearUsd { get; init; }      // 0 = unconstrained
    public int VelocityPerDev { get; init; } = 10;         // story points per sprint per developer
    public int SprintLengthWeeks { get; init; } = 2;
    public DateOnly? StartDate { get; init; }
}

/// <summary>
/// Agile requirement capture: turns a GitHub source snapshot (or a prompt)
/// into epics and user stories — feasible deliverable models with acceptance
/// criteria, story points, priority and risk. Deterministic heuristics keep
/// the split reproducible; every story is grounded in actual source files.
/// </summary>
public sealed partial class RequirementIngestor
{
    /// <summary>Modules detected in the source (project → file count).</summary>
    public IReadOnlyList<string> DetectModules(SourceSnapshot snapshot)
    {
        var modules = snapshot.Files
            .Select(f => ModuleOf(f.Path))
            .Where(m => !string.IsNullOrEmpty(m))
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(8)
            .ToList();
        return modules.Count > 0 ? modules : new List<string> { "Core Platform" };
    }

    /// <summary>Captures the agile backlog: epics + stories from a source snapshot.</summary>
    public (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromSource(
        SourceSnapshot snapshot)
    {
        var modules = DetectModules(snapshot);
        var epics = new List<PmEpic>();
        var stories = new List<PmStory>();
        var epicNo = 0;
        var storyNo = 0;

        void AddEpic(string name, string module, string theme, string description)
        {
            epicNo++;
            epics.Add(new PmEpic { Key = $"E{epicNo:D2}", Name = name, Module = module,
                Theme = theme, Description = description });
        }

        void AddStory(PmEpic epic, string title, string asA, string want, string soThat,
            int points, int priority, string risk, string[] ac)
        {
            storyNo++;
            stories.Add(new PmStory { Key = $"US-{storyNo:D3}", EpicKey = epic.Key, Title = title,
                AsA = asA, IWant = want, SoThat = soThat, Points = points, Priority = priority,
                Risk = risk, Module = epic.Module, TestCaseCount = Math.Max(2, points * 2),
                AcceptanceCriteria = ac });
        }

        // E01: foundation & auth — always first
        var foundation = new PmEpic { Key = "E01", Name = "Foundation & Auth",
            Module = modules[0], Theme = "Foundation",
            Description = "Solution bootstrap, CI/CD, environments, identity and access management" };
        epicNo = 1;
        epics.Add(foundation);
        AddStory(foundation, "Solution bootstrap & repository structure",
            "developer", "a versioned solution skeleton with buildable projects and CI",
            "every squad can start from green", 8, 100, "Medium",
            new[] { "Solution builds on .NET 8", "CI pipeline green on main", "README documents build" });
        AddStory(foundation, "Authentication & user management",
            "product owner", "authentication with role-based access for all user personas",
            "access is governed end to end", 13, 95, "High",
            new[] { "Login/logout works for all roles", "Roles enforced server-side", "Password reset flow" });

        // one epic per detected module: core domain then integration
        var priority = 90;
        foreach (var module in modules)
        {
            var fileCount = snapshot.Files.Count(f =>
                ModuleOf(f.Path).Equals(module, StringComparison.OrdinalIgnoreCase));
            var isUi = snapshot.Files.Any(f => f.Path.Contains(module, StringComparison.OrdinalIgnoreCase)
                && (f.Path.EndsWith(".cshtml") || f.Path.Contains("Controller", StringComparison.OrdinalIgnoreCase)
                    || f.Path.Contains("Views/", StringComparison.OrdinalIgnoreCase)));
            var theme = isUi ? "UI" : "Core";
            AddEpic($"{module} core domain", module, theme,
                $"Implement the {module} domain model, services and contracts ({fileCount} source files in scope)");
            var points = ClampFibonacci((int)Math.Ceiling(fileCount / 2.0));
            AddStory(epics[^1], $"{module} domain model & services",
                "user", $"working {module} domain logic with persistence",
                $"{module} functionality is delivered incrementally", points, priority, "Medium",
                new[] { $"Domain model for {module} implemented", $"Unit tests cover core rules", "API/UI consumes the domain" });
            if (isUi)
                AddStory(epics[^1], $"{module} MVC views & controllers",
                    "user", $"MVC screens for {module} with validation",
                    $"{module} is usable end to end", ClampFibonacci(points / 2), priority - 5, "Medium",
                    new[] { $"Views render with model validation", "Auth guards all actions", "Navigation and error states handled" });
            priority = Math.Max(20, priority - 10);
        }

        // data & persistence epic
        AddEpic("Data & persistence", modules[0], "Data",
            "Schema management, migrations, seed data and database scripts across the platform");
        AddStory(epics[^1], "Database schema, migrations & seed data",
            "database admin", "versioned migrations with seeded reference data",
            "environments can be rebuilt deterministically", 8, 60, "Medium",
            new[] { "Migrations run on SQL Server", "Seed data idempotent", "Rollback documented" });

        // QA epic
        AddEpic("Testing & QA", modules[0], "QA",
            "Test strategy: unit, integration and acceptance coverage per squad domain");
        AddStory(epics[^1], "Automated test suites per module",
            "qa lead", "unit + integration suites wired into CI",
            "regressions surface before release", 8, 55, "Medium",
            new[] { "Unit tests per module", "Integration tests for critical paths", "Coverage reported in CI" });

        // release epic — always last
        AddEpic("Hardening & release", modules[^1], "Release",
            "Performance hardening, security review, documentation and the GA release train");
        AddStory(epics[^1], "Security review & performance hardening",
            "operations", "security pass and load-tested release candidates",
            "the platform is production-ready", 13, 40, "High",
            new[] { "Security checklist cleared", "Load test meets SLO", "Runbook published" });
        AddStory(epics[^1], "GA release & operations handover",
            "operations", "GA release with monitoring and handover documentation",
            "operations can run the platform", 5, 35, "Medium",
            new[] { "GA build signed off", "Monitoring dashboards live", "Handover doc accepted" });

        return (epics, stories);
    }

    /// <summary>Prompt-driven capture: requirement sentences become stories.</summary>
    public (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromPrompt(string prompt)
    {
        var sentences = SentenceRegex().Split(prompt)
            .Select(s => s.Trim())
            .Where(s => s.Length > 12)
            .Take(40).ToList();
        if (sentences.Count == 0)
            return FromSource(new SourceSnapshot
            {
                SourceType = SourceType.ZipArchive, SourceName = "prompt",
                SourceDetail = "empty prompt", Files = new List<SourceFile>()
            });     // fallback: foundation-only backlog

        var epics = new List<PmEpic> { new() { Key = "E01", Name = "Prompt Requirements",
            Module = "Core Platform", Theme = "Core", Description = "Captured directly from the prompt" } };
        var stories = new List<PmStory>();
        for (var i = 0; i < sentences.Count; i++)
        {
            stories.Add(new PmStory { Key = $"US-{i + 1:D3}", EpicKey = "E01",
                Title = Truncate(sentences[i], 80), AsA = "user",
                IWant = sentences[i].ToLowerInvariant(), SoThat = "the requirement is delivered",
                Points = ClampFibonacci(2 + i % 4), Priority = 100 - i, Risk = i < 5 ? "High" : "Medium",
                Module = "Core Platform", TestCaseCount = 4,
                AcceptanceCriteria = new[] { "Behavior matches the requirement", "Covered by tests" } });
        }
        return (epics, stories);
    }

    /// <summary>Client-side vendor bundles excluded from module detection.</summary>
    private static readonly string[] VendorPrefixes =
        { "jquery", "bootstrap", "popper", "fontawesome", "font-awesome", "modernizr",
          "moment", "lodash", "chart", "sweetalert", "select2", "datatables" };

    /// <summary>Support folders that are not product modules.</summary>
    private static readonly string[] SupportFolders =
        { "artifacts", "attached_assets", "assets", "docs", "documentation", "scripts",
          "tools", "test", "tests", "e2e", "coverage", "dist", "build" };

    /// <summary>Module of a source path: product folder only (vendors/support folders excluded).</summary>
    private static string ModuleOf(string path)
    {
        // src/<Project>/... → project; fall back to top folder, then file name topic
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // client-side vendor bundles are never product modules
        foreach (var part in parts)
            if (Array.Exists(VendorPrefixes, v => part.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                return string.Empty;
        // src/<module>/... or lib/<module>/... → the module is the folder under the marker
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] is "src" or "lib" && i + 1 < parts.Length - 1)
                return parts[i + 1].Replace("SmartAgent.", "").Replace("SmartAgent", "Core");
        // else the top folder is the module — unless it is a root config file or support folder
        return parts.Length > 1 && !Array.Exists(SupportFolders, s => parts[0].Equals(s, StringComparison.OrdinalIgnoreCase))
            ? parts[0] : string.Empty;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int ClampFibonacci(int v) => v switch
    {
        <= 1 => 1, <= 2 => 2, <= 3 => 3, <= 5 => 5, <= 8 => 8, _ => 13
    };

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();
}
