using System.Text;
using System.Text.RegularExpressions;

namespace SmartAgent.Core.Pm;

/// <summary>What a free-form user prompt is asking the PM engine to do.</summary>
public enum PmPromptIntent
{
    /// <summary>Scaffold a brand-new app from the framework module design (create new app).</summary>
    CreateApp,
    /// <summary>Enhance an existing app: validate all affected modules and update living artifacts.</summary>
    EnhanceApp,
    /// <summary>Plain requirement prompt — sentences become stories (legacy behavior).</summary>
    Ingest
}

/// <summary>
/// Turns a free-form user prompt into a plan: classifies the intent
/// ("create new app" vs "enhance app"), extracts the feature modules,
/// and generates framework-module stories grounded in the standard MVC
/// solution base (client-side and server-side UI, controller/actions,
/// view models, data models, repositories and database scripts).
/// </summary>
public static partial class PromptStudio
{
    // ─── intent ───

    public static PmPromptIntent IntentOf(string prompt)
    {
        var low = prompt.ToLowerInvariant();
        if (CreateAppRegex().IsMatch(low)) return PmPromptIntent.CreateApp;
        if (EnhanceAppRegex().IsMatch(low)) return PmPromptIntent.EnhanceApp;
        return PmPromptIntent.Ingest;
    }

    [GeneratedRegex(@"\b(create|build|scaffold|spin up|start)\b[^.?!]{0,40}\b(new\s+app|new\s+(?:\.net\s+)?mvc\s+app|app\b)")]
    private static partial Regex CreateAppRegex();
    [GeneratedRegex(@"\b(enhance|enhancement|extend|improve)\b[^.?!]{0,40}\b(app|application|solution|module)s?\b|enhance (the )?app")]
    private static partial Regex EnhanceAppRegex();

    // ─── module extraction ───

    private static readonly string[] StopWords =
    {
        "the", "and", "with", "for", "app", "application", "solution", "module", "modules",
        "create", "build", "new", "manage", "support", "track", "system", "platform",
        "using", "including", "also", "should", "must", "page", "pages", "screen", "screens",
        "build", "add", "alert", "live", "list", "exports", "audit", "report", "reporting",
        "logging", "management", "when", "then", "every", "each"
    };

    /// <summary>Feature modules named by the prompt, PascalCased and de-duplicated.</summary>
    public static IReadOnlyList<string> ModulesOf(string prompt)
    {
        var modules = new List<string>();
        // explicit list first: "with members, checkouts and fines"
        var withList = WithListRegex().Match(prompt);
        if (withList.Success)
            foreach (var raw in withList.Groups["items"].Value.Split(new[] { ",", " and ", " & " },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddModule(modules, raw);
        // "<verb> a reporting module" — any case, explicit module naming
        foreach (var m in ModulePhraseRegex().Matches(prompt).Cast<Match>())
            AddModule(modules, m.Groups["name"].Value, explicitModule: true);
        // then capitalized feature nouns
        foreach (var m in CapitalizedRegex().Matches(prompt).Cast<Match>())
            AddModule(modules, m.Value);
        return modules.Take(8).ToList();
    }

    private static void AddModule(List<string> modules, string raw, bool explicitModule = false)
    {
        var name = Regex.Replace(raw.Trim(), @"[^\w\s-]", "").Trim();
        if (name.Length < 3 || name.Contains(' ')) return;
        if (!explicitModule && StopWords.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        if (name.EndsWith('s')) name = name[..^1];           // "checkouts" → "Checkout"
        var pascal = char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        if (!modules.Contains(pascal, StringComparer.Ordinal) && !char.IsDigit(pascal[0]))
            modules.Add(pascal);
    }

    /// <summary>"reporting" → "Reporting" (module casing for phrase matches).</summary>
    private static string Pascalize(string raw)
    {
        var name = Regex.Replace(raw.Trim(), @"[^\w\s-]", "").Trim();
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
    }

    [GeneratedRegex(@"\bwith\s+(?<items>[^.!?\n]{3,160})")]
    private static partial Regex WithListRegex();
    [GeneratedRegex(@"\b(?:add|new|create|build)\s+(?:an?\s+)?(?<name>[a-z][a-z-]{2,})\s+module\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModulePhraseRegex();
    [GeneratedRegex(@"\b[A-Z][a-z]{2,}\b")]
    private static partial Regex CapitalizedRegex();

    // ─── create new app: framework module stories from the MVC solution base ───

    /// <summary>
    /// Stories for a brand-new app, grounded in the standard MVC solution base
    /// (solution + Program + shared layout + Home controller + site assets),
    /// then one framework module per requested feature: server-side controller
    /// and actions, client-side views, view models, data models, repositories
    /// and database scripts.
    /// </summary>
    public static (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FrameworkStories(
        string appName, string prompt)
    {
        var modules = ModulesOf(prompt);
        if (modules.Count == 0) modules = new List<string> { "App" };

        var epics = new List<PmEpic>();
        var stories = new List<PmStory>();
        var no = 0;

        epics.Add(new PmEpic
        {
            Key = "E01", Name = "Framework scaffold", Module = "Framework", Theme = "Core",
            Description = "Standard MVC solution base: solution and project files, composition root, " +
                         "shared layout, Home controller and views, site assets and configuration."
        });
        foreach (var (title, want, acs, points) in ScaffoldStories(appName))
        {
            no++;
            stories.Add(new PmStory
            {
                Key = $"US-{no:D3}", EpicKey = "E01", Title = title, AsA = "developer",
                IWant = want, SoThat = "the app runs on the standard framework base from the first commit",
                Points = points, Priority = 100 - no, Risk = "Medium", Module = "Framework",
                TestCaseCount = 3, Layer = "Framework", Phase = "build",
                AcceptanceCriteria = acs.ToList()
            });
        }

        foreach (var module in modules)
        {
            var epic = new PmEpic
            {
                Key = $"E{epics.Count + 1:D2}", Name = $"{module} module", Module = module, Theme = "Core",
                Description = $"Framework module for {module}: server-side controller and actions, " +
                              "client-side views, view models, data model, repository and database scripts."
            };
            epics.Add(epic);
            foreach (var (title, want, acs, points, layer) in ModuleStories(module))
            {
                no++;
                stories.Add(new PmStory
                {
                    Key = $"US-{no:D3}", EpicKey = epic.Key, Title = title, AsA = "sangha operator",
                    IWant = want, SoThat = $"the {module} module works end to end (controller, views, model, repository, database)",
                    Points = points, Priority = 90 - no, Risk = "Low", Module = module,
                    TestCaseCount = 4, Layer = layer, Phase = "build",
                    AcceptanceCriteria = acs.ToList()
                });
            }
        }
        return (epics, stories);
    }

    private static IEnumerable<(string Title, string Want, string[] Acs, int Points)> ScaffoldStories(string appName)
    {
        yield return ($"{appName}: solution and project scaffold",
            "a .NET 8 MVC solution with the app project, Program.cs composition root, appsettings and launch settings",
            new[]
            {
                "Solution file <Name>.sln references the single web project targeting net8.0 with the MVC SDK",
                "Program.cs composes Kestrel, controller-with-views routing and the appsettings configuration chain (dev override included)",
                "dotnet build and dotnet run serve a working index route on the configured launch URL"
            }, 3);
        yield return ("Shared layout and error views",
            "the shared UI chrome: _Layout.cshtml, Error.cshtml and the validation scripts partial",
            new[]
            {
                "Views/Shared/_Layout.cshtml renders nav, main section and footer partials and is inherited by every page via _ViewStart.cshtml",
                "Views/Shared/Error.cshtml shows the safe error surface with a request trace id; status codes map to the right view",
                "Views/Shared/_ValidationScriptsPartial.cshtml loads jquery-validation-unobtrusive on every form-bearing page"
            }, 3);
        yield return ("Home controller and landing pages",
            "the HomeController with Index and Privacy actions and their client-side views",
            new[]
            {
                "HomeController serves Index and Privacy with dedicated views under Views/Home and a ViewModel for the landing data",
                "ErrorViewModel carries ActivityId / ShowRequestId so the error view only exposes safe data",
                "Routes resolve for / and /Home/Privacy with 200 status and the shared layout applied"
            }, 2);
        yield return ("Site assets and static file pipeline",
            "wwwroot served: site.css, site.js, favicon and the static files middleware wired in Program.cs",
            new[]
            {
                "wwwroot/css/site.css and wwwroot/js/site.js are referenced by the layout and served with cache headers",
                "Static files middleware is enabled in Program.cs and unreachable paths 404 instead of leaking the file tree"
            }, 1);
    }

    private static IEnumerable<(string Title, string Want, string[] Acs, int Points, string Layer)> ModuleStories(string module)
    {
        yield return ($"{module}: server-side controller and client-side views",
            $"the {module}Controller with full CRUD actions and its client-side views",
            new[]
            {
                $"{module}Controller serves Index, Details, Create, Edit and Delete actions, each returning a view under Views/{module}",
                $"Views/{module} render list, detail and form surfaces; forms post with anti-forgery tokens and client-side validation",
                $"{module}ViewModel binds the form fields; model validation errors re-render the form with messages"
            }, 5, "Controllers");
        yield return ($"{module}: data model",
            $"the {module} entity with validated fields",
            new[]
            {
                $"Models/{module}.cs defines the entity with a key Id and validated fields ([Required], [StringLength] with max sizes)",
                "Invalid states are impossible to persist: validation runs before any repository call",
                "The model is registered for Entity Framework mapping with the configured provider"
            }, 2, "Database");
        yield return ($"{module}: repository",
            $"an async repository over the {module} entity",
            new[]
            {
                $"I{module}Repository declares List, Get, Add, Update and Remove as async operations",
                $"{module}Repository implements them against the data layer without leaking queries into the controllers",
                "Controllers depend only on the repository interface, resolved from the DI container"
            }, 3, "Data Adapter");
        yield return ($"{module}: database scripts",
            $"versioned SQL scripts for the {module} table",
            new[]
            {
                $"Scripts/0001_Create_{module}.sql creates the table with primary key, required columns and sensible indexes",
                "A seed script inserts representative rows so pages render with real data on first run",
                "Scripts are idempotent (IF NOT EXISTS guards) and run in order in fresh environments"
            }, 2, "Database");
    }

    // ─── enhance app: validate affected modules, update living artifacts ───

    /// <summary>
    /// Enhancement stories for an existing app: every module the prompt
    /// names (or that the source already contains) gets a story binding the
    /// affected resources — controller/actions, view models, data models,
    /// repositories and database scripts — with migration scripts.
    /// </summary>
    public static (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) EnhancementStories(
        string prompt, IReadOnlyCollection<string> knownModules)
    {
        var asked = ModulesOf(prompt);
        var phraseAdded = ModulePhraseRegex().Matches(prompt).Cast<Match>()
            .Select(m => m.Groups["name"].Value)
            .Select(Pascalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // an explicitly asked-for new module is "added"; the other named
        // modules (and everything the source already contains) is "affected"
        var affected = asked.Where(a => knownModules.Contains(a, StringComparer.OrdinalIgnoreCase)
            || (knownModules.Count == 0 && !phraseAdded.Contains(a))).ToList();
        var added = asked.Where(a => !affected.Contains(a)).ToList();
        // bare "enhance app" with no named modules: every known module is affected
        if (affected.Count == 0 && added.Count == 0)
            affected = knownModules.Take(6).ToList();

        var epicKey = affected.Count > 0 ? affected[0] : "Enhancement";
        var epics = new List<PmEpic>
        {
            new()
            {
                Key = "EE01", Name = "Enhancement: live validation", Module = epicKey, Theme = "Core",
                Description = "Enhancement request: all affected modules are validated against the source " +
                              "and the living artifacts (requirements, design document, sequence diagram) are regenerated."
            }
        };
        var stories = new List<PmStory>();
        var no = 0;

        void Add(string title, string want, string[] acs, int points, string module)
        {
            no++;
            stories.Add(new PmStory
            {
                Key = $"US-E{no:D3}", EpicKey = "EE01", Title = title, AsA = "sangha operator",
                IWant = want, SoThat = "the enhancement lands without regressing the affected modules",
                Points = points, Priority = 95 - no, Risk = "Medium", Module = module,
                TestCaseCount = 4, Layer = "Controllers", Phase = "build",
                AcceptanceCriteria = acs.ToList()
            });
        }

        if (affected.Count > 0)
        {
            Add("Validate all affected modules for the enhancement",
                $"every module touched by the enhancement checked against source and the running app: {string.Join(", ", affected)}",
                new[]
                {
                    $"Affected modules validated: {string.Join(", ", affected)} — controllers, actions, view models, data models, repositories and database scripts enumerated per module",
                    "Each affected controller action is exercised with valid and invalid input; regression tests cover the module's existing behavior",
                    "Field-level changes (sizes, requiredness) reflect in both the view models and the database scripts"
                }, 5, epicKey);
            foreach (var module in affected.Take(4))
                Add($"Enhance {module} module",
                    $"the {module} module updated to serve the enhancement",
                    new[]
                    {
                        $"{module}Controller actions updated where the enhancement changes inputs or outputs; new actions follow the module's controller/view conventions",
                        $"{module} views updated with the enhanced client-side fields; validation messages match the view model constraints",
                        $"A migration script Scripts/<next>_{module}_enhance.sql alters the table when the enhancement adds or widens columns",
                        "The living artifacts (requirements, design document, sequence diagram) are regenerated to include this enhancement"
                    }, 5, module);
        }
        foreach (var module in added.Take(4))
            Add($"Add {module} module for the enhancement",
                $"a new {module} module (controller/actions, views, view model, data model, repository, database script)",
                new[]
                {
                    $"{module}Controller with CRUD actions and Views/{module} client-side surfaces, following the framework module conventions",
                    $"Models/{module}.cs and I{module}Repository / {module}Repository complete the server-side stack",
                    $"Scripts/<next>_Create_{module}.sql creates the module table with indexes and seed data"
                }, 8, module);
        return (epics, stories);
    }

    /// <summary>
    /// Change-impact rows for the living artifacts: module → affected
    /// server-side and client-side resources.
    /// </summary>
    public static IReadOnlyList<(string Module, string Resource, string Kind)> ChangeImpact(
        IReadOnlyList<PmStory> stories, IReadOnlyCollection<string> promptModules)
    {
        var rows = new List<(string, string, string)>();
        var modules = promptModules.Count > 0
            ? promptModules
            : stories.Select(s => s.Module).Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        foreach (var module in modules)
        {
            var m = module;
            rows.Add((m, $"{m}Controller actions + Views/{m}", "controllers / client-side views"));
            rows.Add((m, $"Models/{m}.cs, {m}ViewModel", "data models / view models"));
            rows.Add((m, $"I{m}Repository / {m}Repository", "repositories"));
            rows.Add((m, $"Scripts/*_{m}*.sql", "database scripts"));
        }
        return rows;
    }
}

/// <summary>
/// Writes the living artifacts folder for a solution: requirements,
/// design document and sequence diagram, regenerated from the plan on
/// every generation so they always match the current backlog.
/// </summary>
public static class LivingArtifactsComposer
{
    public const string FolderName = "living-artifacts";

    /// <summary>Writes the artifacts into <paramref name="root"/>/living-artifacts.</summary>
    public static string Write(ProgramPlan plan, string root, string? prompt = null)
    {
        var dir = Path.Combine(root, FolderName);
        Directory.CreateDirectory(dir);
        var files = Compose(plan, prompt);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);
        return dir;
    }

    /// <summary>The living artifacts for a plan, as (filename, markdown).</summary>
    public static IReadOnlyList<(string Name, string Content)> Compose(ProgramPlan plan, string? prompt = null)
    {
        var arch = ArchitectureComposer.Compose(plan);
        var sb = new StringBuilder();

        // ── requirements.md ──
        sb.AppendLine($"# {plan.Name} — Requirements (living artifact)");
        sb.AppendLine();
        sb.AppendLine($"_Regenerated {plan.GeneratedUtc:yyyy-MM-dd HH:mm} UTC from the program plan. " +
                      "This file is living: it is rewritten on every plan generation._");
        sb.AppendLine();
        sb.AppendLine($"- Source: {plan.SourceType} — {plan.SourceName}");
        sb.AppendLine($"- Stories: {plan.UserStoryCount} ({plan.TotalStoryPoints} points) across {plan.Epics.Count} epics");
        sb.AppendLine($"- Quality: {(plan.Quality is { } q ? $"{q.Score}/100 — {q.Summary}" : "not scored")}");
        if (!string.IsNullOrWhiteSpace(prompt))
            sb.AppendLine($"- Prompt: {prompt}");
        sb.AppendLine();
        foreach (var epic in plan.Epics)
        {
            sb.AppendLine($"## {epic.Key} — {epic.Name}");
            sb.AppendLine(epic.Description);
            sb.AppendLine();
            foreach (var story in plan.Stories.Where(s => s.EpicKey == epic.Key))
            {
                sb.AppendLine($"### {story.Key} — {story.Title} ({story.Points} pts, {story.Priority})");
                sb.AppendLine($"**As a** {story.AsA} **I want** {story.IWant} **so that** {story.SoThat}");
                foreach (var ac in story.AcceptanceCriteria)
                    sb.AppendLine($"- {ac}");
                sb.AppendLine();
            }
        }

        var requirements = sb.ToString();

        // ── design-document.md ──
        sb = new StringBuilder();
        sb.AppendLine($"# {plan.Name} — Design document (living artifact)");
        sb.AppendLine();
        sb.AppendLine("## 1. Introduction and Goals");
        sb.AppendLine(arch.Scope);
        sb.AppendLine();
        sb.AppendLine(arch.QualityGoals);
        sb.AppendLine();
        sb.AppendLine("## 2. Building Block View");
        sb.AppendLine();
        sb.AppendLine("| Module | Theme | Stories | Points |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var b in arch.BuildingBlocks)
            sb.AppendLine($"| {b.Module} | {b.Theme} | {b.Stories} | {b.Points} |");
        sb.AppendLine();
        sb.AppendLine("## 3. System Context (C4 level 1)");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine(arch.ContextDiagram);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 4. Containers (C4 level 2)");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine(arch.ContainerDiagram);
        sb.AppendLine("```");
        sb.AppendLine();
        if (arch.DeploymentNodes.Count > 0)
        {
            sb.AppendLine("## 5. Deployment View");
            foreach (var node in arch.DeploymentNodes)
                sb.AppendLine($"- {node}");
            sb.AppendLine();
        }
        if (arch.Decisions.Count > 0)
        {
            sb.AppendLine("## 6. Architecture Decisions");
            foreach (var d in arch.Decisions)
            {
                sb.AppendLine($"### {d.Id} — {d.Title} ({d.Status})");
                sb.AppendLine($"**Context:** {d.Context}");
                sb.AppendLine($"**Decision:** {d.Decision}");
                sb.AppendLine();
            }
        }
        var design = sb.ToString();

        // ── sequence-diagram.md ──
        sb = new StringBuilder();
        sb.AppendLine($"# {plan.Name} — Sequence diagrams (living artifact)");
        sb.AppendLine();
        if (arch.RuntimeViews.Count == 0)
            sb.AppendLine("_No runtime views captured for this plan._");
        foreach (var view in arch.RuntimeViews)
        {
            sb.AppendLine("```mermaid");
            sb.AppendLine(view);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        var sequence = sb.ToString();

        // ── change-impact.md (enhancement prompts) ──
        sb = new StringBuilder();
        sb.AppendLine($"# {plan.Name} — Change impact (living artifact)");
        sb.AppendLine();
        var intent = string.IsNullOrWhiteSpace(prompt) ? PmPromptIntent.Ingest : PromptStudio.IntentOf(prompt!);
        var promptModules = string.IsNullOrWhiteSpace(prompt)
            ? new List<string>()
            : PromptStudio.ModulesOf(prompt!).ToList();
        if (intent == PmPromptIntent.EnhanceApp)
        {
            sb.AppendLine("This generation is an **enhancement**: all affected modules are validated against the source " +
                          "and the running app; this file lists every touched resource.");
            sb.AppendLine();
            sb.AppendLine("| Module | Affected resource | Kind |");
            sb.AppendLine("|---|---|---|");
            foreach (var (module, resource, kind) in PromptStudio.ChangeImpact(plan.Stories, promptModules))
                sb.AppendLine($"| {module} | {resource} | {kind} |");
        }
        else
        {
            sb.AppendLine("This generation is a full delivery (no enhancement prompt): every module's " +
                          "controllers/actions, view models, data models, repositories and database scripts " +
                          "are covered by the requirements and design artifacts in this folder.");
            if (promptModules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Modules named by the prompt and the resources each one touches:");
                sb.AppendLine();
                sb.AppendLine("| Module | Affected resource | Kind |");
                sb.AppendLine("|---|---|---|");
                foreach (var (module, resource, kind) in PromptStudio.ChangeImpact(plan.Stories, promptModules))
                    sb.AppendLine($"| {module} | {resource} | {kind} |");
            }
        }
        sb.AppendLine();
        var change = sb.ToString();

        return new[]
        {
            ("requirements.md", requirements),
            ("design-document.md", design),
            ("sequence-diagram.md", sequence),
            ("change-impact.md", change)
        };
    }
}
