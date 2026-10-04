using SmartAgent.Domain;

namespace SmartAgent.Core.Pm;

/// <summary>
/// Artifact attachment: grounds a planned program in deep source capture.
/// Every sprint receives a full engineering detail pack — unit/system tests,
/// a dual-provider DB migration with rollbacks, the code artifact ledger,
/// HLD, DDD (pseudo-code + sequence), and an implementation checklist —
/// plus program-level milestones with GO/NO-GO criteria, a seeded issue
/// register and the detected tech platform profile.
/// </summary>
public sealed partial class SprintPlanner
{
    private static readonly string[] QaReporters = { "QA1", "QA2", "QA3", "QA4" };

    /// <summary>
    /// Attaches all reference-grade artifacts to the plan from the captured
    /// source snapshot. Call once after Plan(); mutates and returns the plan.
    /// </summary>
    public ProgramPlan AttachArtifacts(ProgramPlan plan, SourceSnapshot snapshot)
    {
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var tables = analyzer.Tables(snapshot);
        var deps = analyzer.Dependencies(snapshot);
        var configKeys = analyzer.ConfigKeys(snapshot);
        var platform = analyzer.DetectPlatform(snapshot);
        plan.Platform = platform;

        var cumulativeFiles = new HashSet<string>();
        var createdTables = new List<PmTableInfo>();
        var allUnit = new List<PmUnitTestCase>();
        var allSystem = new List<PmSystemTest>();
        var allIssues = new List<PmIssue>();
        var sprints = plan.SprintPlans.ToList();
        var quarterOf = plan.Quarters.ToDictionary(q => q.Key, q => q);

        for (var index = 0; index < sprints.Count; index++)
        {
            var sprint = sprints[index];
            var stories = sprint.StoryKeys
                .Select(k => plan.Stories.FirstOrDefault(s => s.Key == k))
                .Where(s => s != null).Cast<PmStory>().ToList();
            var stabilization = sprint.Number % 6 == 0;   // every 6th sprint (1-based)
            var quarter = quarterOf[sprint.Quarter];
            var module = stories.Count > 0 ? stories[0].Module
                : plan.Epics.Select(e => e.Module).FirstOrDefault() ?? "Core Platform";

            // tests: grounded in captured classes
            var unitTests = new List<PmUnitTestCase>();
            var seq = 1;
            foreach (var story in stories)
                unitTests.AddRange(analyzer.UnitTestsFor(story, classes, sprint.Number, seq));
            seq = 1;
            var systemTests = stories.Select(s => analyzer.SystemTestFor(s, sprint.Number, seq++)).ToList();

            // db migration: new tables this sprint introduces
            var script = analyzer.DbScriptFor(sprint.Number,
                stabilization ? $"{quarter.Theme} hardening" : module, tables, createdTables);
            foreach (var t in tables.Where(t => !createdTables.Any(c =>
                             c.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
                         && script.PrimaryDdl.Contains($"[{t.Name}]", StringComparison.OrdinalIgnoreCase)).ToList())
                createdTables.Add(t);

            // issues: one per sprint seeded from the risk of its lead story
            var lead = stories.FirstOrDefault();
            var severity = lead?.Risk switch
            {
                "High" => "Major", "Medium" => "Minor", "Low" => "Trivial", _ => "Minor"
            };
            var issue = new PmIssue
            {
                Id = $"BUG-S{sprint.Number:D2}-001",
                SprintNumber = sprint.Number,
                Severity = severity,
                Component = module,
                Description = lead is not null
                    ? $"Edge case in {module} identified during Sprint {sprint.Number} testing (source: {lead.Title})"
                    : $"Minor edge case identified during Sprint {sprint.Number} {quarter.Theme.ToLowerInvariant()} testing",
                Reporter = QaReporters[sprint.Number % QaReporters.Length],
                Assignee = SquadForModule(plan, module),
                Status = sprint.Number == 1 ? "Resolved"
                    : sprint.Number == 2 ? "In Progress"
                    : stabilization ? "In Progress" : "Open"
            };
            var issues = new List<PmIssue> { issue };

            // code artifacts ledger
            var artifacts = analyzer.CodeArtifactsFor(
                snapshot.Files, module, cumulativeFiles, stabilization || stories.Count == 0);

            // HLD + DDD grounded in captured classes
            var hld = analyzer.HldFor(module, stories, classes, deps);
            var ddd = analyzer.DddFor(module, stories, classes, configKeys, platform);

            var milestone = quarter.Release;

            sprints[index] = sprint with
            {
                Milestone = milestone,
                Detail = new PmSprintDetail
                {
                    Hld = hld, Ddd = ddd,
                    UnitTests = unitTests, SystemTests = systemTests, Issues = issues,
                    DbScript = script, CodeArtifacts = artifacts,
                    ImplChecklist = analyzer.ImplChecklist(milestone, stories)
                }
            };
            allUnit.AddRange(unitTests);
            allSystem.AddRange(systemTests);
            allIssues.AddRange(issues);
        }

        plan.SprintPlans = sprints;
        plan.Milestones = BuildMilestones(plan);
        plan.UnitTests = allUnit;
        plan.SystemTests = allSystem;
        plan.IssueRegister = allIssues;
        plan.UnitTestCount = allUnit.Count;
        plan.SystemTestCount = allSystem.Count;
        plan.TestCaseCount = Math.Max(plan.TestCaseCount, allUnit.Count + allSystem.Count);
        return plan;
    }

    /// <summary>Release milestones with GO/NO-GO criteria, from the quarter schedule.</summary>
    private static IReadOnlyList<PmMilestone> BuildMilestones(ProgramPlan plan) =>
        plan.Quarters.Where(q => !string.IsNullOrEmpty(q.Release))
            .Select(q => new PmMilestone
            {
                Name = q.Release, Sprint = q.SprintTo, Week = q.WeekTo, Scope = q.Theme,
                GoNoGo = GoNoGoFor(q.Release)
            })
            .ToList();

    private static string[] GoNoGoFor(string release)
    {
        if (release.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase))
            return new[]
            {
                "All quarter stories accepted",
                "Health/response check < 200ms",
                "Migration scripts run clean on both DB providers",
                "Container image built and deployed",
                "80% unit test coverage on foundation modules"
            };
        if (release.StartsWith("Beta", StringComparison.OrdinalIgnoreCase))
            return new[]
            {
                "Quarter features tested on both DB providers",
                "Regression suite passed",
                "Performance benchmark meets SLO",
                "Rollback scripts tested",
                "No open Critical bugs"
            };
        if (release.StartsWith("RC", StringComparison.OrdinalIgnoreCase))
            return new[]
            {
                "All year stories accepted",
                "Full regression + load test passed",
                "Security review completed",
                "Release notes and runbook drafted",
                "Operations sign-off"
            };
        return new[]   // GA
        {
            "All program stories accepted",
            "24h soak test green",
            "Monitoring and alerting live",
            "Support handbook published",
            "Executive GO sign-off"
        };
    }

    private static string SquadForModule(ProgramPlan plan, string module)
    {
        var squad = plan.Squads.FirstOrDefault(sq => sq.EpicKeys.Any(k =>
            plan.Epics.FirstOrDefault(e => e.Key == k)?.Module.Equals(module, StringComparison.OrdinalIgnoreCase) == true));
        return squad is not null ? $"{squad.Name}" : "Squad A";
    }
}
