using SmartAgent.Core.Dat;
using SmartAgent.Domain;

namespace SmartAgent.Core.Pm;

/// <summary>
/// The PM capability governor. For every planning request it spawns one
/// processing thread per project role (from the standard RACI chart) on the
/// ThreadGovernor's lanes — the Business Analyst refines requirements, the
/// Architect decomposes modules, the QA Lead sizes the test strategy, the
/// DevOps Engineer plans pipelines, the DBA plans schema work, the Scrum
/// Master sets cadence, the Product Owner owns the roadmap, and every
/// delivery role contributes its implementation notes — then consolidates
/// the role outputs into the generated multi-year program.
/// </summary>
public sealed class PmGovernor(ThreadGovernor governor, RequirementIngestor ingestor, SprintPlanner planner,
    PmQualityEngine quality, FunctionalSpecGenerator functionalSpec)
{
    /// <summary>Generates a full program plan from a source snapshot.</summary>
    public async Task<ProgramPlan> GenerateAsync(SourceSnapshot snapshot, PmOptions options,
        string consolidationRoot, CancellationToken ct = default)
    {
        var modules = ingestor.DetectModules(snapshot);
        var resources = planner.Roster(options.TeamSize, modules.Count);
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);   // deep source review input
        var profile = analyzer.ProfileApp(snapshot, classes);   // what the app actually does
        var (epics, stories) = snapshot.SourceType == SourceType.GitHub || snapshot.Files.Count > 0
            ? ingestor.FromSource(snapshot, classes, profile)
            : ingestor.FromPrompt(snapshot.SourceDetail);
        // functional spec generator: crawl the live application, validate the
        // source-generated requirements against its real surface, consolidate
        // uncovered live functions into new requirements
        PmFunctionalSpec? functional = null;
        if (!string.IsNullOrWhiteSpace(options.FunctionalUrl))
        {
            var captured = await functionalSpec.CaptureAsync(
                options.FunctionalUrl!, options.FunctionalUser, options.FunctionalPassword, ct);
            var validation = functionalSpec.ValidateAndConsolidate(epics, stories, captured);
            epics = validation.Epics;
            stories = validation.Stories;
            functional = validation.Spec;
        }

        var squads = planner.FormSquads(resources, epics);

        // one threaded task per RACI role — parallel lanes per role in the project
        var roleTasks = BuildRoleTasks(resources, epics, stories, squads, options).ToList();
        Directory.CreateDirectory(consolidationRoot);
        var slug = Slugify(options.Name);
        var reportPath = Path.Combine(consolidationRoot, $"{slug}.dat");

        var report = await governor.DistributeAsync(roleTasks, maxThreads: 4, consolidationPath: reportPath, ct: ct);
        var roleOutputs = report.Results
            .Where(r => r.Status == "succeeded")
            .ToDictionary(r => r.Subject, r => r.Output);

        var plan = planner.Plan(options.Name,
            snapshot.SourceType.ToString(), snapshot.SourceName, snapshot.SourceDetail,
            resources, epics, stories, squads, options);
        plan = planner.AttachArtifacts(plan, snapshot);   // deep source capture: tests, DB scripts, HLD/DDD, issues, milestones

        // quality engine: compare against the standard of expectation, regenerate
        // failing stories/sprints/test cases until the plan meets the standard
        plan.Quality = quality.ReviewAndImprove(plan);
        plan.FunctionalSpec = functional;

        plan.RoleOutputs = roleOutputs;
        plan.GovernorReportFile = reportPath;
        return plan;
    }

    /// <summary>Priority of each role's planning thread (higher = first).</summary>
    private static int RolePriority(string role) => role switch
    {
        "Business Analyst" => 100, "Solution Architect" => 95, "Product Owner" => 90,
        "Dev Lead" => 85, "QA Lead" => 80, "DevOps Engineer" => 75, "Database Admin" => 70,
        "Scrum Master" => 65, "Senior Developer" => 60, "Developer" => 55,
        "QA Engineer" => 50, "UI/UX Designer" => 45, "Data Engineer" => 40,
        "Technical Writer" => 35, _ => 30
    };

    private static IEnumerable<GovernorTask> BuildRoleTasks(
        IReadOnlyList<PmResource> resources, IReadOnlyList<PmEpic> epics,
        IReadOnlyList<PmStory> stories, IReadOnlyList<PmSquad> squads, PmOptions options)
    {
        var points = stories.Sum(s => s.Points);
        var modules = epics.Select(e => e.Module).Distinct().OrderBy(m => m).ToList();

        GovernorTask MakeTask(string role, Func<string> deliverable) => new()
        {
            Id = $"PM-{role.ToLowerInvariant().Replace(' ', '-').Replace("/", "")}",
            Subject = role, Priority = RolePriority(role),
            Body = _ => Task.FromResult(deliverable())
        };

        yield return MakeTask("Business Analyst", () =>
            $"Captured {stories.Count} user stories across {epics.Count} epics ({points} story points). " +
            $"Top backlog areas: {string.Join("; ", epics.Take(4).Select(e => e.Name))}. " +
            "All stories carry acceptance criteria; high-risk items are flagged for early sprints.");

        yield return MakeTask("Solution Architect", () =>
            $"Module decomposition: {string.Join(", ", modules)}. Layering: domain → services → MVC/API. " +
            "Every epic maps to one squad domain; cross-cutting concerns (auth, data, CI) tracked as foundation epics.");

        yield return MakeTask("Product Owner", () =>
            $"Roadmap: {options.Years}-year program, 26 sprints/year. Releases: Alpha (Q1), Beta 1 (Q2), " +
            "Beta 2 (Q3), RC1/Year 1 (Q4), then scale, advanced features and GA at program end. " +
            "Backlog ordered by priority, then risk.");

        yield return MakeTask("Dev Lead", () =>
            $"Implementation plan: {stories.Count(s => s.Points >= 8)} large stories split across squads; " +
            $"velocity target {Math.Max(10, resources.Count(r => r.Role is "Developer" or "Senior Developer" or "Dev Lead") * options.VelocityPerDev)} pts/sprint. " +
            "Definition of done: code review + tests + docs per story.");

        yield return MakeTask("QA Lead", () =>
            $"Test strategy: {stories.Sum(s => s.TestCaseCount)} test cases estimated " +
            "(unit + integration per module, acceptance per story). Every 6th sprint is a stabilization sprint at 50% capacity.");

        yield return MakeTask("DevOps Engineer", () =>
            "Pipelines: build → test → package → deploy per sprint; environments dev/stage/prod; " +
            "release trains at each quarter milestone with rollback runbooks.");

        yield return MakeTask("Database Admin", () =>
            "Database plan: versioned migrations per module, seed data per environment, " +
            "backup/restore drills each release quarter; schema reviews in Architecture & Design phase (RACI: consulted).");

        yield return MakeTask("Scrum Master", () =>
            $"Cadence: 2-week sprints, {options.Years * 26} total. Ceremonies: planning, daily standup, review, retro. " +
            $"Unplanned stories spill to the next sprint; risk log reviewed weekly with the PO (Accountable).");

        yield return MakeTask("UI/UX Designer", () =>
            $"Design docs: MVC view inventory for {modules.Count(m => m.Length > 0)} modules; " +
            "wireframes per epic theme; design system tokens shared across squads.");

        yield return MakeTask("Data Engineer", () =>
            "Data workstreams: ingestion contracts per module, reporting marts after core domain quarters; " +
            "no production data in dev/stage environments.");

        yield return MakeTask("Technical Writer", () =>
            "Deliverables: README per module, API reference each release, runbooks with GA handover; " +
            "release notes at every quarter milestone (Informed on release management).");

        yield return MakeTask("QA Engineer", () =>
            $"Execution: automated suites wired into CI from sprint 1; {stories.Count(s => s.Risk == "High")} " +
            "high-risk stories get exploratory test passes before merge.");

        yield return MakeTask("Senior Developer", () =>
            "Standards: .NET 8, MVC, async-first, DI everywhere; code review gate per pull request; " +
            "spike budget inside foundation epics for unknowns.");

        yield return MakeTask("Developer", () =>
            $"Squad assignments: {squads.Count} squads, each owning its module epics end to end " +
            "(RACI: Responsible in Implementation, Consulted in Testing & QA).");

    }

    private static string Slugify(string name) =>
        string.Concat(name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            .Replace("--", "-").Trim('-');
}
