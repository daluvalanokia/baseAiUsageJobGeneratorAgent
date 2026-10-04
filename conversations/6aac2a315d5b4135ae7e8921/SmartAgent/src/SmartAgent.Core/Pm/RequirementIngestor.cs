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

    /// <summary>Max class-grounded feature-slice stories per module.</summary>
    private const int MaxSliceStoriesPerModule = 4;
    /// <summary>Max file-cluster stories per module (asset-heavy modules without classes).</summary>
    private const int MaxFileStoriesPerModule = 3;
    /// <summary>Cap on the generated backlog (keeps the plan reference-grade but bounded).</summary>
    private const int MaxStories = 150;

    /// <summary>
    /// Captures the agile backlog: epics + stories from a source snapshot.
    /// When class analysis is supplied, every module is reviewed in depth:
    /// controllers, services and domain types become feature-slice stories
    /// with acceptance criteria naming their actual methods, and asset-heavy
    /// modules (views, function components, schemas) become file-grounded
    /// stories — so requirements resolve the real source, not a summary.
    /// </summary>
    public (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromSource(
        SourceSnapshot snapshot, IReadOnlyList<PmClassInfo>? classes = null)
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
            int points, int priority, string risk, string[] ac,
            string layer = "", string phase = "build")
        {
            storyNo++;
            stories.Add(new PmStory { Key = $"US-{storyNo:D3}", EpicKey = epic.Key, Title = title,
                AsA = asA, IWant = want, SoThat = soThat, Points = points, Priority = priority,
                Risk = risk, Module = epic.Module, TestCaseCount = Math.Max(2, points * 2),
                AcceptanceCriteria = ac, Layer = layer, Phase = phase });
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
            new[] { "Solution builds on .NET 8", "CI pipeline green on main", "README documents build" },
            "Framework", "framework");
        AddStory(foundation, "Authentication & user management",
            "product owner", "authentication with role-based access for all user personas",
            "access is governed end to end", 13, 95, "High",
            new[] { "Login/logout works for all roles", "Roles enforced server-side", "Password reset flow" },
            "Framework", "framework");
        AddStory(foundation, "Base framework code generation & tooling",
            "developer", "scaffolding, shared libraries and code-generation templates",
            "every module is generated from the same base", 8, 92, "Medium",
            new[] { "Module templates generate a working vertical slice",
                    "Shared validation, logging and error handling are wired in",
                    "Generated code passes lint and build gates" },
            "Framework", "framework");

        // one epic per detected module: domain + in-depth source review
        var priority = 90;
        foreach (var module in modules)
        {
            if (stories.Count >= MaxStories) break;
            var moduleFiles = snapshot.Files
                .Where(f => ModuleOf(f.Path).Equals(module, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var fileCount = moduleFiles.Count;
            var isUi = moduleFiles.Any(f =>
                f.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                || f.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                || f.Path.Contains("Controller", StringComparison.OrdinalIgnoreCase)
                || f.Path.Contains("Views/", StringComparison.OrdinalIgnoreCase));
            var theme = isUi ? "UI" : "Core";
            var moduleClasses = (classes ?? Array.Empty<PmClassInfo>())
                .Where(c => c.Module.Equals(module, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Methods.Count).ThenBy(c => c.Name).ToList();
            var controllers = moduleClasses.Where(c => c.Kind is "Controller" or "Hub").ToList();
            var entities = moduleClasses.Where(c => c.Kind == "Entity").ToList();
            var services = moduleClasses.Where(c => c.Kind is "Service" or "Other" && c.Methods.Count > 0).ToList();
            var profile = moduleClasses.Count > 0
                ? $"{fileCount} source files, {moduleClasses.Count} analyzed types "
                  + $"({controllers.Count} controllers, {entities.Count} domain types, {services.Count} service types)"
                : $"{fileCount} source files";
            var layers = moduleFiles.Select(f => SourceAnalyzer.LayerOf(f.Path)).Distinct().ToList();
            AddEpic($"{module} core domain", module, theme,
                $"Implement the {module} domain model, services and contracts ({profile} in scope; "
                + $"layers: {string.Join(", ", layers)})");
            var points = ClampFibonacci((int)Math.Ceiling(fileCount / 2.0));
            var isMvc = moduleFiles.Any(f => f.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));
            // server side layer
            AddStory(epics[^1], $"{module} server-side domain & services",
                "user", $"working {module} domain logic with persistence",
                $"{module} functionality is delivered incrementally", points, priority, "Medium",
                new[] { $"Domain model for {module} implemented", $"Unit tests cover core rules", "API/UI consumes the domain" },
                "Server", "build");
            // data adapter layer
            if (layers.Contains("Data Adapter") || entities.Count > 0)
                AddStory(epics[^1], $"{module} data adapter & persistence layer",
                    "developer", $"repository/DbContext and data-access contracts for {module}",
                    "server code is decoupled from storage", ClampFibonacci(3 + entities.Count), priority - 3, "Medium",
                    new[] { $"{module} entities mapped through the data adapter",
                            "Query contracts return deterministic results",
                            "Connection and transaction handling covered by tests" },
                    "Data Adapter", "build");
            // controllers / actions layer
            if (controllers.Count > 0 || isMvc)
                AddStory(epics[^1], $"{module} controllers & actions",
                    "user", $"request handling for {module} with validation and auth guards",
                    $"{module} is reachable and safe end to end", ClampFibonacci(2 + controllers.Count), priority - 4, "High",
                    new[] { $"All {module} actions validate input and enforce auth",
                            "Action results follow the platform conventions",
                            "Error paths return correct status codes" },
                    "Controllers", "build");
            // frontend layer
            if (isUi)
                AddStory(epics[^1],
                    isMvc ? $"{module} MVC views & forms" : $"{module} UI screens & interaction",
                    "user", $"{(isMvc ? "MVC screens" : "UI screens")} for {module} with validation",
                    $"{module} is usable end to end", ClampFibonacci(points / 2), priority - 5, "Medium",
                    new[] { $"Views render with model validation", "Auth guards all actions", "Navigation and error states handled" },
                    "Frontend", "build");
            // database scripts layer
            if (layers.Contains("Database"))
                AddStory(epics[^1], $"{module} database scripts & schema",
                    "database admin", $"versioned DDL and seed scripts for {module}",
                    $"{module} environments rebuild deterministically", 5, priority - 6, "Medium",
                    new[] { $"Tables and constraints scripted for {module}",
                            "Seed data idempotent", "Rollback documented" },
                    "Database", "build");

            // ── in-depth review: class-grounded feature slices ──
            var p = priority - 2;
            foreach (var cls in controllers.Take(3).Concat(entities).Concat(services).Take(MaxSliceStoriesPerModule))
            {
                if (stories.Count >= MaxStories) break;
                var methods = cls.Methods.Count > 0 ? cls.Methods : new List<string> { "primary action" };
                var points2 = ClampFibonacci(1 + methods.Count);
                string title; string want; string[] ac;
                if (cls.Kind is "Controller" or "Hub")
                {
                    title = $"{cls.Name} endpoints & behavior";
                    want = $"{cls.Name} fully working ({string.Join(", ", methods.Take(3))})";
                    ac = new[]
                    {
                        $"{methods[0]} returns the expected result for valid input",
                        $"{methods[Math.Min(1, methods.Count - 1)]} enforces authorization and validation",
                        "Invalid input maps to 400/403 with clear error bodies"
                    };
                }
                else if (cls.Kind == "Entity")
                {
                    title = $"{cls.Name} persistence & validation";
                    want = $"{cls.Name} persisted with enforced invariants";
                    ac = new[]
                    {
                        $"{cls.Name} round-trips through persistence identically",
                        "Constraint violations surface as validation errors",
                        $"Consume sites validated against {cls.Name} contract"
                    };
                }
                else
                {
                    title = $"{cls.Name} service logic";
                    want = $"{cls.Name} rules implemented ({string.Join(", ", methods.Take(3))})";
                    ac = new[]
                    {
                        $"{methods[0]} returns the expected result for valid input",
                        "Invalid input is rejected without side effects",
                        "Repeat calls stay idempotent"
                    };
                }
                var sliceLayer = cls.Kind is "Controller" or "Hub" ? "Controllers"
                    : cls.Kind == "Entity" ? "Data Adapter" : "Server";
                AddStory(epics[^1], title, "user", want, $"{cls.Name} is production-grade",
                    points2, Math.Max(15, p), cls.Kind == "Controller" ? "High" : "Medium", ac, sliceLayer);
                p -= 2;
            }

            // ── asset-heavy modules: file-grounded stories (function components, schemas, views) ──
            if (moduleClasses.Count < 4)
                foreach (var f in moduleFiles
                             .Where(f => IsCodePath(f.Path) && !IsTestPath(f.Path))
                             .OrderByDescending(f => f.Content.Length)
                             .Take(MaxFileStoriesPerModule))
                {
                    if (stories.Count >= MaxStories) break;
                    var topic = FeatureName(f.Path);
                    AddStory(epics[^1], $"{topic} ({module}) implementation", "user",
                        $"{topic} implemented and integrated into {module}",
                        $"{module} delivers {topic.ToLowerInvariant()} end to end",
                        ClampFibonacci(3 + f.Content.Length / 4000), Math.Max(15, p), "Medium",
                        new[] { $"{topic} behavior implemented per the source contract",
                                "Edge cases and error states handled",
                                "Covered by unit + integration tests" },
                        SourceAnalyzer.LayerOf(f.Path) is "Frontend" or "Controllers" or "Database" or "Data Adapter"
                            ? SourceAnalyzer.LayerOf(f.Path) : "Server");
                    p -= 2;
                }

            // ── integration & contract story per module ──
            AddStory(epics[^1], $"{module} integration & contract tests",
                "qa lead", $"integration suite proving {module} contracts end to end",
                $"{module} regressions surface before release", 5, Math.Max(15, priority - 8), "Medium",
                new[] { $"{module} contracts asserted against consumers",
                        "Critical paths integration-tested in CI",
                        "Contract drift breaks the build" }, "Server");

            priority = Math.Max(20, priority - 10);
        }

        // ── enhancement wave: real change patterns from repository history ──
        // Commits reveal where the source actually evolves; each recurring
        // (module, layer) pattern becomes a Year-2 enhancement story so the
        // plan stretches across the horizon with grounded change work.
        var patterns = new SourceAnalyzer().ChangePatterns(snapshot)
            .Where(p => modules.Contains(p.Module, StringComparer.OrdinalIgnoreCase))
            .Take(10).ToList();
        var enhanceEpic = new PmEpic { Key = $"E{epicNo + 1:D2}", Name = "Field & Enhancement Waves",
            Module = modules[0], Theme = "Core",
            Description = "Year-2 enhancement waves derived from the repository's own commit "
                + $"history ({snapshot.History.Count} commits reviewed): field extensions, "
                + "feature polish and change patterns per module" };
        epics.Add(enhanceEpic);
        epicNo++;
        var enhancePriority = 52;
        foreach (var pattern in patterns.Where(p => p.Commits >= 1))
        {
            if (stories.Count >= MaxStories) break;
            var headline = pattern.Headline.Length > 70 ? pattern.Headline[..67] + "..." : pattern.Headline;
            AddStory(enhanceEpic, $"Enhance {pattern.Module} ({pattern.Layer.ToLowerInvariant()}): {headline}",
                "user", $"the '{headline}' change pattern carried into the rebuild",
                $"{pattern.Module} keeps evolving the way the source did", ClampFibonacci(3 + pattern.Commits),
                Math.Max(30, enhancePriority), "Medium",
                new[] { $"Change wave mirrors the source pattern '{headline}' ({pattern.Commits} commits touched {pattern.Layer.ToLowerInvariant()} code)",
                        $"Touched paths covered: {string.Join(", ", pattern.Examples.Take(2))}",
                        $"Regression tests prove the enhanced {pattern.Layer.ToLowerInvariant()} behavior",
                        "Frontend, server and database components updated together" },
                pattern.Layer, "enhance");
            enhancePriority -= 2;
        }
        // field extensions: extend each data-bearing module with new fields end to end
        var fieldModules = new[] { modules[0], modules[^1] }.Concat(modules.Where(m =>
            snapshot.Files.Any(f => ModuleOf(f.Path).Equals(m, StringComparison.OrdinalIgnoreCase)
                && SourceAnalyzer.LayerOf(f.Path) == "Data Adapter"))).Distinct().Take(4);
        foreach (var module in fieldModules)
        {
            if (stories.Count >= MaxStories) break;
            AddStory(enhanceEpic, $"Extend {module} data model: new fields end to end",
                "product owner", $"additional fields on {module} records across db, server and UI",
                $"{module} grows without schema drift", 8, Math.Max(30, enhancePriority), "Medium",
                new[] { $"New {module} columns added via versioned migration",
                        "Server validation and data adapter mapping updated for the new fields",
                        "Frontend forms display and edit the new fields",
                        "Existing {module} data migrates losslessly" },
                "Database", "enhance");
            enhancePriority -= 2;
        }

        // data & persistence epic
        AddEpic("Data & persistence", modules[0], "Data",
            "Schema management, migrations, seed data and database scripts across the platform");
        AddStory(epics[^1], "Database schema, migrations & seed data",
            "database admin", "versioned migrations with seeded reference data",
            "environments can be rebuilt deterministically", 8, 60, "Medium",
            new[] { "Migrations run on SQL Server", "Seed data idempotent", "Rollback documented" },
            "Database", "build");

        // QA epic
        AddEpic("Testing & QA", modules[0], "QA",
            "Test strategy: unit, integration and acceptance coverage per squad domain");
        AddStory(epics[^1], "Automated test suites per module",
            "qa lead", "unit + integration suites wired into CI",
            "regressions surface before release", 8, 55, "Medium",
            new[] { "Unit tests per module", "Integration tests for critical paths", "Coverage reported in CI" },
            "Server", "build");

        // release epic — always last
        AddEpic("Hardening & release", modules[^1], "Release",
            "Performance hardening, security review, documentation and the GA release train");
        AddStory(epics[^1], "Security review & performance hardening",
            "operations", "security pass and load-tested release candidates",
            "the platform is production-ready", 13, 40, "High",
            new[] { "Security checklist cleared", "Load test meets SLO", "Runbook published" },
            "Framework", "release");
        AddStory(epics[^1], "GA release & operations handover",
            "operations", "GA release with monitoring and handover documentation",
            "operations can run the platform", 5, 35, "Medium",
            new[] { "GA build signed off", "Monitoring dashboards live", "Handover doc accepted" },
            "Framework", "release");

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


    private static bool IsCodePath(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestPath(string path) =>
        path.Contains("test", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase));

    /// <summary>A human feature name from a file path ("ui/alert-dialog.tsx" → "Alert dialog UI component").</summary>
    private static string FeatureName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var words = Regex.Replace(Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1"), "[-_.]+", " ").Trim();
        if (words.Length == 0) words = "feature";
        words = char.ToUpperInvariant(words[0]) + words[1..];
        var kind = path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "UI component"
            : path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? "view"
            : path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "handler"
            : "module";
        return $"{words} {kind}";
    }

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
