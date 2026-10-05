namespace SmartAgent.Core.Pm;

/// <summary>arc42-style architecture views composed from a generated program plan.</summary>
public sealed record PmArchitecture
{
    /// <summary>System scope sentence (arc42 §2).</summary>
    public required string Scope { get; init; }
    /// <summary>Quality goals / drivers sentence (arc42 §1).</summary>
    public required string QualityGoals { get; init; }
    /// <summary>C4 level 1 — system context diagram (Mermaid flowchart).</summary>
    public required string ContextDiagram { get; init; }
    /// <summary>C4 level 2 — container diagram (Mermaid flowchart).</summary>
    public required string ContainerDiagram { get; init; }
    /// <summary>C4 level 3-flavored runtime views (Mermaid sequence diagrams) per top capability.</summary>
    public IReadOnlyList<string> RuntimeViews { get; init; } = Array.Empty<string>();
    /// <summary>Building block view (arc42 §5): module, theme, stories, points.</summary>
    public IReadOnlyList<(string Module, string Theme, int Stories, int Points)> BuildingBlocks { get; init; }
        = Array.Empty<(string, string, int, int)>();
    /// <summary>Deployment view (arc42 §7): infrastructure nodes from the platform profile.</summary>
    public IReadOnlyList<string> DeploymentNodes { get; init; } = Array.Empty<string>();
    /// <summary>Architecture decision records (arc42 §9), grounded in the platform profile.</summary>
    public IReadOnlyList<(string Id, string Title, string Status, string Context, string Decision)> Decisions { get; init; }
        = Array.Empty<(string, string, string, string, string)>();
}

/// <summary>
/// Composes arc42 documentation views + C4 (context/container/runtime) diagrams
/// from the generated plan. Grounded: every element is derived from the program's
/// own epics, stories (including captured action names) and platform profile.
/// </summary>
public static class ArchitectureComposer
{
    public static PmArchitecture Compose(ProgramPlan plan)
    {
        var platform = plan.Platform;
        var appName = plan.Name;

        var actors = plan.Stories.Select(s => s.AsA).Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (actors.Count == 0) actors = new List<string> { "Member", "Administrator" };

        var arch = platform?.Arch;
        var webLayer = arch?.WebLayer ?? Array.Empty<string>();
        var dataLayer = arch?.DataLayer ?? Array.Empty<string>();
        var serviceLayer = arch?.ServiceLayer ?? Array.Empty<string>();
        var backend = platform?.Backend ?? "Web application";
        var primaryDb = platform?.PrimaryDb ?? "relational database";

        // ── C4 level 1: system context ──
        var context = $"""
            flowchart LR
                subgraph actors["Actors"]
                    {Node("a1", actors.FirstOrDefault() ?? "Member")}
                    {Node("a2", actors.Count > 1 ? actors[1] : "Administrator")}
                end
                {Node("sys", appName, "system")}
                {Node("db", primaryDb, "external")}
                a1 -->|"uses"| sys
                a2 -->|"administers"| sys
                sys -->|"reads / writes"| db
            """;

        // ── C4 level 2: containers (layers from the platform profile) ──
        var containers = new List<string>
        {
            "flowchart LR",
            Node("user", actors.FirstOrDefault() ?? "Member", "person"),
            Node("web", $"{appName} [web — {backend}]", "container"),
            Node("db", primaryDb, "database"),
            "user -->|\"https / browser\"| web",
            "web -->|\"entity framework / data access\"| db"
        };
        var idx = 0;
        foreach (var layer in webLayer.Take(3)) containers.Add(Node($"wl{idx++}", layer, "component"));
        foreach (var svc in serviceLayer.Take(2)) containers.Add(Node($"sv{idx++}", svc, "component"));
        foreach (var dat in dataLayer.Take(3)) containers.Add(Node($"dl{idx++}", dat, "component"));
        var containerDiagram = string.Join("\n", containers);

        // ── runtime views: sequence diagrams per top module, with its real actions ──
        var runtime = new List<string>();
        foreach (var module in plan.Stories
                     .GroupBy(s => s.Module, StringComparer.OrdinalIgnoreCase)
                     .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                     .OrderByDescending(g => g.Count()).Take(2))
        {
            var actions = module.SelectMany(s => s.AcceptanceCriteria)
                .Select(a => System.Text.RegularExpressions.Regex.Match(a,
                    @"\b([A-Z]\w*Controller)\.([A-Z]\w*)\(\)"))
                .Where(m => m.Success)
                .Select(m => (Controller: m.Groups[1].Value, Action: m.Groups[2].Value))
                .Distinct().Take(4).ToList();
            var seq = new List<string> { "sequenceDiagram",
                $"actor User as {actors.FirstOrDefault() ?? "Member"}",
                "participant Web as Web App",
                "participant Db as Database" };
            if (actions.Count == 0)
            {
                seq.Add("User->>Web: browse " + module.Key.ToLowerInvariant() + " screens");
                seq.Add("Web->>Db: load " + module.Key.ToLowerInvariant() + " data");
                seq.Add("Db-->>Web: records");
                seq.Add("Web-->>User: rendered view");
            }
            else
            {
                foreach (var (controller, action) in actions)
                {
                    seq.Add($"User->>Web: {controller}.{action}()");
                    seq.Add("Web->>Db: query / persist");
                    seq.Add("Db-->>Web: result");
                    seq.Add($"Web-->>User: {action.ToLowerInvariant()} response");
                }
            }
            runtime.Add(string.Join("\n", seq));
        }

        // ── building blocks (arc42 §5) ──
        var blocks = plan.Stories.GroupBy(s => s.Module, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g => (g.Key,
                plan.Epics.FirstOrDefault(e => e.Module.Equals(g.Key, StringComparison.OrdinalIgnoreCase))?.Theme ?? "Core",
                g.Count(), g.Sum(s => s.Points)))
            .OrderByDescending(b => b.Item4).ToList();

        // ── deployment view (arc42 §7) ──
        var deploy = new List<string>();
        if (platform is not null)
        {
            deploy.Add($"Operating system: {platform.Os}");
            deploy.Add($"Runtime: {platform.Backend} ({platform.BackendLang})");
            foreach (var i in platform.Infra) deploy.Add($"Infrastructure: {i}");
        }
        deploy.Add($"Primary datastore: {primaryDb}");
        if (platform is not null && !string.IsNullOrWhiteSpace(platform.SecondaryDb))
            deploy.Add($"Secondary datastore: {platform.SecondaryDb}");

        // ── decisions (arc42 §9) — ADRs grounded in the detected platform ──
        var adrs = new List<(string, string, string, string, string)>();
        if (platform is not null)
        {
            adrs.Add(("ADR-001", $"Run {appName} as a {platform.ShortName} web application", "Accepted",
                $"{platform.Name} detected as the platform of the existing source.",
                $"Continue with {platform.Backend}; best for {platform.BestFor.ToLowerInvariant()}."));
            adrs.Add(("ADR-002", $"{primaryDb} as the primary persistence store", "Accepted",
                "The source already binds this database technology.",
                $"Keep {primaryDb} primary" +
                (string.IsNullOrWhiteSpace(platform.SecondaryDb) ? "." : $"; {platform.SecondaryDb} as secondary/alternate.")));
            var advantage = platform.Advantages.FirstOrDefault() ?? "";
            if (!string.IsNullOrWhiteSpace(advantage))
                adrs.Add(("ADR-003", "Preserve the source's architectural style", "Accepted",
                    $"The existing codebase demonstrates: {advantage.ToLowerInvariant()}.",
                    "Requirements and enhancements mirror the source's own patterns rather than introducing a competing style."));
        }
        adrs.Add(("ADR-004", "Mirror source history for evolution", "Accepted",
            "Field-level enhancements are derived from the repository's own commit history.",
            "Every 'add field' requirement is grounded in a real source commit, keeping the roadmap faithful to how the codebase actually evolves."));

        return new PmArchitecture
        {
            Scope = $"{appName} — the existing {platform?.ShortName ?? "web"} application "
                + $"ingested from {plan.SourceName}, rebuilt and extended across {plan.Sprints} sprints.",
            QualityGoals = $"{plan.UserStoryCount} requirements keep the app's current behaviors working "
                + $"while {plan.Stories.Count(s => s.Phase == "enhance")} enhancement stories extend it along its own history.",
            ContextDiagram = context,
            ContainerDiagram = containerDiagram,
            RuntimeViews = runtime,
            BuildingBlocks = blocks,
            DeploymentNodes = deploy,
            Decisions = adrs
        };
    }

    /// <summary>Mermaid node with a quoted label (parentheses are legal inside quotes).</summary>
    private static string Node(string id, string label, string shape = "plain") => shape switch
    {
        "person" => $"    {id}[\"🧍 {label}\"]",
        "system" => $"    {id}[\"{label}\"]",
        "container" => $"    {id}[\"{label}\"]",
        "database" => $"    {id}[(\"{label}\")]",
        "external" => $"    {id}[(\"{label}\")]",
        "component" => $"    {id}[\"{label}\"]",
        _ => $"    {id}[\"{label}\"]"
    };
}
