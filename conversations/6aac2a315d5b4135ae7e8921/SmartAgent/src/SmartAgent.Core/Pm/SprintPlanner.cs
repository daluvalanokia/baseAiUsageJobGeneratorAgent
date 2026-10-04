namespace SmartAgent.Core.Pm;

/// <summary>
/// Builds the multi-year delivery program: roster from the standard RACI
/// chart, squads per domain, sprints (2-week) across N years, quarters with
/// release milestones, and a full budget from RACI-weighted resource
/// engagement.
/// </summary>
public sealed partial class SprintPlanner
{
    private static readonly string[] Year1Themes = { "Foundation & Auth", "Core Domain", "Integration & API", "Hardening & RC" };
    private static readonly string[] YearNThemes = { "Scale & Optimization", "Advanced Features", "Platform Extensions", "Sustain & GA" };
    private static readonly string[] SquadLetters = { "A", "B", "C", "D", "E", "F", "G", "H" };

    /// <summary>Derives the delivery team from the standard RACI roster.</summary>
    public IReadOnlyList<PmResource> Roster(int? teamSize, int moduleCount)
    {
        var target = teamSize is >= 6 and <= 200 ? teamSize.Value : Math.Clamp(10 + moduleCount * 2, 12, 60);
        var roster = new List<PmResource>();
        // critical singleton roles first (one each)
        foreach (var (role, rate) in RaciChart.StandardRoles.Where(r => r.Role is not ("Developer" or "QA Engineer")))
            roster.Add(MakeResource(role, rate, roster.Count + 1));
        // then scale the delivery factory
        var dev = RaciChart.StandardRoles.First(r => r.Role == "Developer");
        var qa = RaciChart.StandardRoles.First(r => r.Role == "QA Engineer");
        while (roster.Count < target)
        {
            var (role, rate) = roster.Count % 3 == 2 ? qa : dev;
            roster.Add(MakeResource(role, rate, roster.Count + 1));
        }
        return roster;

        static PmResource MakeResource(string role, decimal rate, int id) => new()
        {
            Key = $"{role.ToLowerInvariant().Replace(' ', '-').Replace("/", "")}-{id}",
            Name = $"{role} {id:D2}", Role = role, HourlyRate = rate, Identifier = id
        };
    }

    /// <summary>Forms delivery squads: domains from epics, resources spread across squads.</summary>
    public IReadOnlyList<PmSquad> FormSquads(IReadOnlyList<PmResource> resources, IReadOnlyList<PmEpic> epics)
    {
        var squadCount = Math.Clamp((int)Math.Ceiling(resources.Count / 6.0), 1, SquadLetters.Length);
        var squads = new List<PmSquad>();
        for (var i = 0; i < squadCount; i++)
        {
            var domainEpics = epics.Where((_, idx) => idx % squadCount == i).Select(e => e.Key).ToList();
            var domains = string.Join(", ", epics.Where((_, idx) => idx % squadCount == i)
                .Select(e => e.Module).Distinct().Take(3));
            squads.Add(new PmSquad
            {
                Key = SquadLetters[i],
                Name = $"Squad {SquadLetters[i]}",
                Domain = domainEpics.Count > 0 ? domains : "Cross-cutting & Release",
                EpicKeys = domainEpics,
                ResourceKeys = resources.Where((_, idx) => idx % squadCount == i).Select(r => r.Key).ToList()
            });
        }
        // shared roles (PO, SM, Architect) join every squad's engagement view
        foreach (var squad in squads)
            squad.ResourceKeys = squad.ResourceKeys.Concat(resources
                .Where(r => r.Role is "Product Owner" or "Scrum Master" or "Solution Architect")
                .Select(r => r.Key)).Distinct().ToList();
        return squads;
    }

    /// <summary>Plans the full program: sprints, quarters, releases, budget.</summary>
    public ProgramPlan Plan(string name, string sourceType, string sourceName, string sourceDetail,
        IReadOnlyList<PmResource> resources, IReadOnlyList<PmEpic> epics, IReadOnlyList<PmStory> stories,
        IReadOnlyList<PmSquad> squads, PmOptions options)
    {
        var sprintCount = options.Years * 26;
        var quarterCount = options.Years * 4;
        var devCount = resources.Count(r => r.Role is "Developer" or "Senior Developer" or "Dev Lead");
        var velocity = Math.Max(10, devCount * options.VelocityPerDev);
        var start = options.StartDate ?? NextMonday(DateOnly.FromDateTime(DateTime.UtcNow));

        var quarters = BuildQuarters(quarterCount, sprintCount);
        var sprints = BuildSprints(sprintCount, quarters, start, options.SprintLengthWeeks);

        // fill sprints: stories by priority (desc), stabilization sprints at 50%
        var ordered = stories.OrderByDescending(s => s.Priority).ThenBy(s => s.Risk == "High" ? 0 : 1).ThenBy(s => s.Key).ToList();
        var assignments = sprints.Select(_ => new List<PmStory>()).ToList();
        var cursor = 0;
        for (var i = 0; i < sprints.Count && cursor < ordered.Count; i++)
        {
            var capacity = i % 6 == 5 ? velocity / 2 : velocity;    // every 6th sprint = stabilization
            var used = 0;
            while (cursor < ordered.Count && used + ordered[cursor].Points <= capacity)
            {
                assignments[i].Add(ordered[cursor]);
                used += ordered[cursor].Points;
                cursor++;
            }
        }

        // RACI-weighted engagement & budget
        var engagement = resources.ToDictionary(r => r.Key,
            r => Math.Round(r.HoursPerSprint * RaciChart.EngagementFactor(r), 1));
        var sprintBudget = Math.Round(resources.Sum(r => engagement[r.Key] * r.HourlyRate), 0);
        var sprintPlans = new List<PmSprint>();
        for (var i = 0; i < sprints.Count; i++)
        {
            var q = quarters.First(q => sprints[i].Number >= q.SprintFrom && sprints[i].Number <= q.SprintTo);
            var planned = assignments[i];
            var remaining = ordered.Skip(cursor).Where(s => !planned.Contains(s)).ToList();
            sprintPlans.Add(sprints[i] with
            {
                StoryKeys = planned.Select(s => s.Key).ToList(),
                Points = planned.Sum(s => s.Points),
                CapacityPoints = i % 6 == 5 ? velocity / 2 : velocity,
                BudgetUsd = sprintBudget,
                Goal = planned.Count > 0
                    ? $"Deliver {planned.Count} stories ({planned.Sum(s => s.Points)} pts): {planned[0].Module} — {Truncate(planned[0].Title, 60)}"
                    : $"Stabilization, hardening & {q.Theme.ToLowerInvariant()} readiness",
                Release = q.Release
            });
        }

        var totalBudget = sprintPlans.Sum(s => s.BudgetUsd);
        var perYear = Math.Round(totalBudget / Math.Max(1, options.Years), 0);
        var quarterTotals = quarters.Select(q =>
            sprintPlans.Where(s => s.Number >= q.SprintFrom && s.Number <= q.SprintTo).Sum(s => s.BudgetUsd)).ToList();
        var committedHours = engagement.Values.Sum() * sprintCount;
        var byRole = resources.GroupBy(r => r.Role)
            .Select(g => new RoleCost(g.Key, Math.Round(g.Sum(r => engagement[r.Key] * r.HourlyRate) * sprintCount, 0)))
            .OrderByDescending(x => x.CostUsd).ToList();

        return new ProgramPlan
        {
            Name = name, SourceType = sourceType, SourceName = sourceName, SourceDetail = sourceDetail,
            Slug = Slugify(name), Years = options.Years, SprintLengthWeeks = options.SprintLengthWeeks,
            Sprints = sprintCount, Weeks = sprintCount * options.SprintLengthWeeks,
            TotalStoryPoints = stories.Sum(s => s.Points), UserStoryCount = stories.Count,
            TestCaseCount = stories.Sum(s => s.TestCaseCount),
            ProgramStart = start, ProgramEnd = sprints[^1].EndDate,
            Resources = resources, Squads = squads, Epics = epics, Stories = stories,
            SprintPlans = sprintPlans, Quarters = quarters,
            Budget = new PmBudget
            {
                TotalUsd = totalBudget, PerYearUsd = perYear, QuarterTotals = quarterTotals,
                CommittedHours = committedHours, BudgetCapPerYearUsd = options.BudgetCapPerYearUsd,
                WithinCap = options.BudgetCapPerYearUsd <= 0 || perYear <= options.BudgetCapPerYearUsd,
                CostByRole = byRole
            }
        };
    }

    private static IReadOnlyList<PmQuarter> BuildQuarters(int quarterCount, int sprintCount)
    {
        var baseSprints = sprintCount / quarterCount;
        var extra = sprintCount % quarterCount;     // release quarters get the extra sprints
        var quarters = new List<PmQuarter>();
        var sprint = 1;
        for (var i = 1; i <= quarterCount; i++)
        {
            var count = baseSprints + (extra > 0 ? 1 : 0);
            if (extra > 0) extra--;
            var year = (i - 1) / 4 + 1;
            var themes = year == 1 ? Year1Themes : YearNThemes;
            var theme = themes[(i - 1) % 4] + (year > 1 ? $" (Year {year})" : "");
            var release = i % 4 == 0
                ? (year == 1 ? "RC1 / Year 1 Release" : (i == quarterCount ? "GA Release" : $"RC{year} / Year {year} Release"))
                : year == 1 && i == 1 ? "Alpha"
                : year == 1 && i == 2 ? "Beta 1"
                : year == 1 && i == 3 ? "Beta 2"
                : year > 1 && i % 4 == 1 ? $"Y{year} Beta" : string.Empty;
            quarters.Add(new PmQuarter
            {
                Key = $"Q{i}", Name = $"Q{i} — {theme}", Theme = theme,
                SprintFrom = sprint, SprintTo = sprint + count - 1,
                WeekFrom = (sprint - 1) * 2 + 1, WeekTo = (sprint + count - 1) * 2,
                Release = release
            });
            sprint += count;
        }
        return quarters;
    }

    private static IReadOnlyList<PmSprint> BuildSprints(int sprintCount, IReadOnlyList<PmQuarter> quarters,
        DateOnly start, int lengthWeeks)
    {
        var sprints = new List<PmSprint>();
        var cursor = start;
        for (var i = 1; i <= sprintCount; i++)
        {
            var end = cursor.AddDays(lengthWeeks * 7 - 1);
            sprints.Add(new PmSprint
            {
                Number = i, Name = $"Sprint {i}",
                Quarter = quarters.First(q => i >= q.SprintFrom && i <= q.SprintTo).Key,
                StartWeek = (i - 1) * lengthWeeks + 1, EndWeek = i * lengthWeeks,
                StartDate = cursor, EndDate = end, Goal = "TBD"
            });
            cursor = end.AddDays(1);
        }
        return sprints;
    }

    private static DateOnly NextMonday(DateOnly from)
    {
        var offset = from.DayOfWeek == DayOfWeek.Sunday ? 1 : (DayOfWeek.Monday - from.DayOfWeek + 7) % 7;
        return from.AddDays(offset == 0 ? 0 : offset);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Slugify(string name) =>
        string.Concat(name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')).Replace("--", "-").Trim('-');
}
