using System.Text.Json.Serialization;

namespace SmartAgent.Core.Pm;

/// <summary>RACI responsibility types.</summary>
public enum RaciType
{
    Responsible,
    Accountable,
    Consulted,
    Informed
}

/// <summary>One project resource (team member) in the PM engagement model.</summary>
public sealed record PmResource
{
    public required string Key { get; init; }              // "po", "arch-1", "dev-3"
    public required string Name { get; init; }
    public required string Role { get; init; }             // "Product Owner", "Developer", ...
    public string Squad { get; init; } = string.Empty;     // "A" .. "H"
    public decimal HourlyRate { get; init; }               // blended USD rate
    public decimal HoursPerSprint { get; init; } = 60m;    // productive hours per 2-week sprint
    public int Identifier { get; init; }
}

/// <summary>The standard RACI chart for agile software delivery.</summary>
public static class RaciChart
{
    /// <summary>Agile delivery phases every story/sprint walks through.</summary>
    public static readonly string[] Phases =
    {
        "Requirements", "Architecture & Design", "Implementation",
        "Testing & QA", "Deployment", "Budget Approval",
        "Stakeholder Communication", "Risk Management", "Release Management"
    };

    /// <summary>Standard engagement roles with blended rates (USD/hour).</summary>
    public static readonly (string Role, decimal Rate)[] StandardRoles =
    {
        ("Product Owner", 95m), ("Scrum Master", 85m), ("Solution Architect", 115m),
        ("Dev Lead", 105m), ("Senior Developer", 95m), ("Developer", 75m),
        ("QA Lead", 95m), ("QA Engineer", 70m), ("DevOps Engineer", 95m),
        ("Database Admin", 90m), ("UI/UX Designer", 80m), ("Business Analyst", 80m),
        ("Data Engineer", 90m), ("Technical Writer", 60m)
    };

    /// <summary>
    /// Standard RACI matrix: phase → role → responsibility. Exactly one
    /// Accountable per phase (agile standard).
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, RaciType>> Matrix = new()
    {
        ["Requirements"] = new() { ["Business Analyst"] = RaciType.Responsible, ["Product Owner"] = RaciType.Accountable,
            ["Solution Architect"] = RaciType.Consulted, ["QA Lead"] = RaciType.Consulted, ["Data Engineer"] = RaciType.Informed,
            ["Developer"] = RaciType.Informed },
        ["Architecture & Design"] = new() { ["Solution Architect"] = RaciType.Responsible, ["Dev Lead"] = RaciType.Accountable,
            ["Product Owner"] = RaciType.Consulted, ["UI/UX Designer"] = RaciType.Consulted, ["Data Engineer"] = RaciType.Consulted,
            ["Developer"] = RaciType.Informed },
        ["Implementation"] = new() { ["Developer"] = RaciType.Responsible, ["Dev Lead"] = RaciType.Accountable,
            ["Senior Developer"] = RaciType.Responsible, ["Solution Architect"] = RaciType.Consulted, ["QA Engineer"] = RaciType.Informed },
        ["Testing & QA"] = new() { ["QA Engineer"] = RaciType.Responsible, ["QA Lead"] = RaciType.Accountable,
            ["Developer"] = RaciType.Consulted, ["Product Owner"] = RaciType.Informed },
        ["Deployment"] = new() { ["DevOps Engineer"] = RaciType.Responsible, ["Dev Lead"] = RaciType.Accountable,
            ["Database Admin"] = RaciType.Consulted, ["Developer"] = RaciType.Consulted },
        ["Budget Approval"] = new() { ["Product Owner"] = RaciType.Accountable,
            ["Scrum Master"] = RaciType.Consulted, ["Business Analyst"] = RaciType.Consulted,
            ["Dev Lead"] = RaciType.Informed },
        ["Stakeholder Communication"] = new() { ["Scrum Master"] = RaciType.Responsible, ["Product Owner"] = RaciType.Accountable,
            ["Dev Lead"] = RaciType.Consulted, ["Technical Writer"] = RaciType.Consulted },
        ["Risk Management"] = new() { ["Scrum Master"] = RaciType.Responsible, ["Product Owner"] = RaciType.Accountable,
            ["Solution Architect"] = RaciType.Consulted, ["Dev Lead"] = RaciType.Consulted },
        ["Release Management"] = new() { ["DevOps Engineer"] = RaciType.Responsible, ["Product Owner"] = RaciType.Accountable,
            ["QA Lead"] = RaciType.Consulted, ["Technical Writer"] = RaciType.Consulted, ["Database Admin"] = RaciType.Informed }
    };

    /// <summary>RACI rows for one phase, resolved against concrete resources.</summary>
    public static IReadOnlyList<(string ResourceKey, string Role, RaciType Type)> AssignmentsFor(
        string phase, IReadOnlyList<PmResource> resources) =>
        Matrix.TryGetValue(phase, out var row)
            ? resources.Where(r => row.ContainsKey(r.Role))
                .Select(r => (r.Key, r.Role, row[r.Role])).ToList()
            : Array.Empty<(string, string, RaciType)>();

    /// <summary>Full chart: one row per phase with assignments for the given team.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(string ResourceKey, string Role, RaciType Type)>> FullChart(
        IReadOnlyList<PmResource> resources) =>
        Phases.ToDictionary(p => p, p => (IReadOnlyList<(string, string, RaciType)>)AssignmentsFor(p, resources));

    /// <summary>Engagement weight of a resource across all phases (0..1):
    /// R=1.0, A=0.5, C=0.25, I=0.05 per phase, averaged.</summary>
    public static decimal EngagementFactor(PmResource resource) =>
        Math.Clamp(Phases.Sum(p => Matrix[p].TryGetValue(resource.Role, out var t)
            ? t switch { RaciType.Responsible => 1m, RaciType.Accountable => 0.5m,
                          RaciType.Consulted => 0.25m, _ => 0.05m } : 0m) / Phases.Length, 0m, 1m);
}

/// <summary>An epic theme in the agile backlog.</summary>
public sealed record PmEpic
{
    public required string Key { get; init; }              // "E01"
    public required string Name { get; init; }
    public required string Module { get; init; }
    public required string Theme { get; init; }             // Foundation / Core / UI / Data / QA / Release
    public string Description { get; init; } = string.Empty;
    public string Squad { get; init; } = "A";
}

/// <summary>An agile user story (feasible deliverable unit).</summary>
public sealed record PmStory
{
    public required string Key { get; init; }               // "US-001"
    public required string EpicKey { get; init; }
    public required string Title { get; init; }
    public required string AsA { get; init; }
    public required string IWant { get; init; }
    public required string SoThat { get; init; }
    public int Points { get; set; }                        // Fibonacci (repairable by the quality engine)
    public int Priority { get; init; }                       // higher = earlier
    public string Risk { get; init; } = "Medium";
    public IReadOnlyList<string> AcceptanceCriteria { get; set; } = Array.Empty<string>();
    public string Module { get; init; } = string.Empty;
    public int TestCaseCount { get; init; }
    /// <summary>Architecture layer: Database, Data Adapter, Controllers, Frontend, Server, Framework.</summary>
    public string Layer { get; init; } = string.Empty;
    /// <summary>Delivery phase: framework, build, enhance or release.</summary>
    public string Phase { get; init; } = "build";
}

/// <summary>One 2-week sprint in the multi-year program.</summary>
public sealed record PmSprint
{
    public int Number { get; init; }
    public required string Name { get; init; }
    public required string Goal { get; init; }
    public required string Quarter { get; init; }           // "Q1"
    public int StartWeek { get; init; }
    public int EndWeek { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public IReadOnlyList<string> StoryKeys { get; set; } = Array.Empty<string>();
    public int Points { get; set; }
    public int CapacityPoints { get; init; }
    public decimal BudgetUsd { get; init; }
    public string Release { get; init; } = string.Empty;
    public string Milestone { get; init; } = string.Empty;
    public string Status { get; init; } = "planned";
    /// <summary>Delivery phase of this sprint: framework, build, enhance, release or stabilization.</summary>
    public string Phase { get; init; } = string.Empty;
    public PmSprintDetail? Detail { get; init; }
    public IReadOnlyDictionary<string, decimal> EngagedHours { get; init; } =
        new Dictionary<string, decimal>();
}

/// <summary>One quarter of the program.</summary>
public sealed record PmQuarter
{
    public required string Key { get; init; }               // "Q1".."Q8"
    public required string Name { get; init; }
    public required string Theme { get; init; }
    public int SprintFrom { get; init; }
    public int SprintTo { get; init; }
    public int WeekFrom { get; init; }
    public int WeekTo { get; init; }
    public string Release { get; init; } = string.Empty;
}

/// <summary>A delivery squad owning a set of epics.</summary>
public sealed record PmSquad
{
    public required string Key { get; init; }                // "A".."H"
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public IReadOnlyList<string> ResourceKeys { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> EpicKeys { get; init; } = Array.Empty<string>();
}

/// <summary>Program budget model.</summary>
public sealed record PmBudget
{
    public decimal TotalUsd { get; init; }
    public decimal PerYearUsd { get; init; }                // per year, averaged
    public IReadOnlyList<decimal> QuarterTotals { get; init; } = Array.Empty<decimal>();
    public decimal CommittedHours { get; init; }
    public decimal BudgetCapPerYearUsd { get; init; }       // 0 = unconstrained
    public bool WithinCap { get; init; } = true;
    public IReadOnlyList<RoleCost> CostByRole { get; init; } = Array.Empty<RoleCost>();
}

/// <summary>Program cost rollup per engagement role.</summary>
public sealed record RoleCost(string Role, decimal CostUsd);

/// <summary>
/// A generated multi-year agile delivery program: requirements captured from
/// GitHub source or a prompt, split into epics/stories/sprints/quarters with
/// squads, RACI resource engagement and a full budget model.
/// </summary>
public sealed class ProgramPlan
{
    public required string Name { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string SourceType { get; set; } = "github";
    public string SourceName { get; set; } = string.Empty;
    public string SourceDetail { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public int Years { get; set; }
    public int SprintLengthWeeks { get; set; } = 2;
    public int Sprints { get; set; }
    public int Weeks { get; set; }
    public int TotalStoryPoints { get; set; }
    public int UserStoryCount { get; set; }
    public int TestCaseCount { get; set; }
    public int UnitTestCount { get; set; }
    public int SystemTestCount { get; set; }
    public DateOnly ProgramStart { get; set; }
    public DateOnly ProgramEnd { get; set; }

    public IReadOnlyList<PmResource> Resources { get; set; } = Array.Empty<PmResource>();
    public IReadOnlyList<PmSquad> Squads { get; set; } = Array.Empty<PmSquad>();
    public IReadOnlyList<PmEpic> Epics { get; set; } = Array.Empty<PmEpic>();
    public IReadOnlyList<PmStory> Stories { get; set; } = Array.Empty<PmStory>();
    public IReadOnlyList<PmSprint> SprintPlans { get; set; } = Array.Empty<PmSprint>();
    public IReadOnlyList<PmQuarter> Quarters { get; set; } = Array.Empty<PmQuarter>();
    public PmBudget Budget { get; set; } = new();
    public PmPlatform? Platform { get; set; }
    public IReadOnlyList<PmMilestone> Milestones { get; set; } = Array.Empty<PmMilestone>();
    public IReadOnlyList<PmUnitTestCase> UnitTests { get; set; } = Array.Empty<PmUnitTestCase>();
    public IReadOnlyList<PmSystemTest> SystemTests { get; set; } = Array.Empty<PmSystemTest>();
    public IReadOnlyList<PmIssue> IssueRegister { get; set; } = Array.Empty<PmIssue>();
    public Dictionary<string, string> RoleOutputs { get; set; } = new();  // role → threaded deliverable
    public string GovernorReportFile { get; set; } = string.Empty;
    /// <summary>Quality engine verdict: standard-of-expectation score and repair history.</summary>
    public PmQualityReport? Quality { get; set; }
    /// <summary>Functional spec generator verdict: live app surface + requirement validation.</summary>
    public PmFunctionalSpec? FunctionalSpec { get; set; }
    /// <summary>Free-form user prompt that drove this generation ("create new app" / "enhance app: ...").</summary>
    public string? Prompt { get; set; }
}
