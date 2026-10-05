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

                // ── phase-banded, squad-aware distribution across the horizon ──
        // The program resolves into explicit delivery phases, so any source
        // becomes a truthful long-horizon plan:
        //   1. framework  — base framework code generation (scaffold, auth, tooling)
        //   2. build      — basic module functionality in vertical slices:
        //                   one module's database + server + controllers + frontend
        //                   changes land in the same sprint wave
        //   3. enhance    — field extensions & history-detected change patterns
        //   4. release    — hardening & GA train on the tail sprints
        // Stabilization sprints (every 6th) stay clear of new work.
        var themeOf = epics.ToDictionary(e => e.Key, e => e.Theme, StringComparer.Ordinal);
        var squadOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var squad in squads)
            foreach (var eKey in squad.EpicKeys)
                if (!squadOf.ContainsKey(eKey)) squadOf[eKey] = squad.Key;
        foreach (var e in epics)
            if (!squadOf.ContainsKey(e.Key)) squadOf[e.Key] = e.Squad;   // fallback for shared/unassigned epics
        var phases = stories.Select(s => s.Phase).DefaultIfEmpty("build").Distinct().ToList();
        var releaseTrain = stories
            .Where(s => themeOf.GetValueOrDefault(s.EpicKey, "Core") == "Release" || s.Phase == "release")
            .ToList();
        // a story is owned by exactly one band: release-train stories never
        // double back into the framework/build/enhance waves (quality standard:
        // every story scheduled exactly once)
        var trainKeys = releaseTrain.Select(s => s.Key).ToHashSet();
        var framework = stories.Where(s => s.Phase == "framework" && !trainKeys.Contains(s.Key))
            .OrderByDescending(s => s.Priority).ToList();
        var build = stories.Where(s => (s.Phase == "build" || s.Phase == string.Empty) && !trainKeys.Contains(s.Key))
            .OrderByDescending(s => s.Priority).ThenBy(s => s.Risk == "High" ? 0 : 1).ThenBy(s => s.Key)
            .ToList();
        var enhance = stories.Where(s => s.Phase == "enhance" && !trainKeys.Contains(s.Key))
            .OrderByDescending(s => s.Priority).ToList();

        var reserveCount = Math.Clamp(sprints.Count / 13, 1, 4);          // tail sprints: hardening/release train
        var deliverable = sprints.Count - reserveCount;
        // adaptive band sizes: the framework band fits its scaffold backlog,
        // the enhance band fits its change-pattern backlog (capped at 40%)
        var frameworkEnd = Math.Clamp(framework.Count > 0 ? framework.Count : 2, 2, Math.Max(2, deliverable / 8));
        var enhanceCount = enhance.Count > 0
            ? Math.Clamp(enhance.Count + 1, 2, Math.Max(2, (int)Math.Round(deliverable * 0.4)))
            : 0;
        var enhanceStart = deliverable - enhanceCount;
        List<int> Band(int from, int toExclusive) =>
            Enumerable.Range(from, Math.Max(0, toExclusive - from)).Where(i => i % 6 != 5).ToList();

        var assignments = sprints.Select(_ => new List<PmStory>()).ToList();
        var phasesOf = sprints.Select(_ => "").ToList();
        foreach (var i in Enumerable.Range(0, sprints.Count))
            phasesOf[i] = i % 6 == 5 ? "stabilization" : "";
        // proportional placement: item j of M lands at position j*N/M of the
        // band's N eligible sprints, so each band is evenly populated
        int PlaceAt(List<int> band, int j, int m) =>
            band.Count == 0 ? -1 : band[Math.Min(band.Count - 1, j * band.Count / Math.Max(1, m))];

        // framework band: scaffold stories first, then move on
        var fwBand = Band(0, frameworkEnd);
        foreach (var i in fwBand) phasesOf[i] = "framework";
        var placedFramework = Math.Min(framework.Count, fwBand.Count);
        for (var j = 0; j < placedFramework; j++)   // only what fits — overflow joins the build band exactly once
        {
            var i = PlaceAt(fwBand, j, placedFramework);
            if (i >= 0) assignments[i].Add(framework[j]);
        }
        if (framework.Count > placedFramework)
            build = framework.Skip(placedFramework).Concat(build).ToList();

        // build band: vertical module slices — up to 3 stories of the same
        // module per sprint, waves spread evenly across the band
        var waves = new List<List<PmStory>>();
        var wave = new List<PmStory>();
        foreach (var st in build)
        {
            if (wave.Count >= 3 || (wave.Count > 0 && wave[0].Module != st.Module))
            {
                waves.Add(wave);
                wave = new List<PmStory>();
            }
            wave.Add(st);
        }
        if (wave.Count > 0) waves.Add(wave);
        var buildBand = Band(frameworkEnd, enhanceStart);
        foreach (var i in buildBand) phasesOf[i] = "build";
        var placedWaves = Math.Min(waves.Count, buildBand.Count);
        for (var j = 0; j < placedWaves; j++)  // only what fits — overflow joins the enhance band exactly once
        {
            var i = PlaceAt(buildBand, j, placedWaves);
            if (i >= 0) assignments[i].AddRange(waves[j]);
        }
        if (waves.Count > placedWaves)
            enhance = waves.Skip(placedWaves).SelectMany(w => w).Concat(enhance).ToList();

        // enhance band: field extensions & change-pattern stories
        var enhanceBand = Band(enhanceStart, deliverable);
        foreach (var i in enhanceBand) phasesOf[i] = "enhance";
        var placedEnhance = Math.Min(enhance.Count, enhanceBand.Count);
        for (var j = 0; j < placedEnhance; j++) // only what fits — overflow joins the release train exactly once
        {
            var i = PlaceAt(enhanceBand, j, placedEnhance);
            if (i >= 0) assignments[i].Add(enhance[j]);
        }
        if (enhance.Count > placedEnhance)
            releaseTrain = enhance.Skip(placedEnhance).Concat(releaseTrain).ToList();

        // release train on the tail sprints
        var reserve = Enumerable.Range(sprints.Count - reserveCount, reserveCount).ToList();
        for (var j = 0; j < releaseTrain.Count && reserve.Count > 0; j++)
        {
            var i = reserve[Math.Min(reserve.Count - 1, j * reserve.Count / Math.Max(1, releaseTrain.Count))];
            assignments[i].Add(releaseTrain[j]);
            phasesOf[i] = "release";
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
            var phaseLabel = planned.Count > 0 ? phasesOf[i] : phasesOf[i] is "" or "stabilization" ? "stabilization" : phasesOf[i];
            var layerSpan = planned.Count > 0
                ? string.Join("/", planned.Select(x => x.Layer).Where(l => l.Length > 0).Distinct().Take(3))
                : "";
            sprintPlans.Add(sprints[i] with
            {
                StoryKeys = planned.Select(s => s.Key).ToList(),
                Points = planned.Sum(s => s.Points),
                CapacityPoints = i % 6 == 5 ? velocity / 2 : velocity,
                BudgetUsd = sprintBudget,
                Phase = phaseLabel,
                Goal = planned.Count > 0
                    ? $"[{phaseLabel}] Squads {string.Join("+", planned.Select(x => squadOf.GetValueOrDefault(x.EpicKey, "A")).Distinct())}: "
                     + $"{planned.Count} stories ({planned.Sum(s => s.Points)} pts, {layerSpan}) — {planned[0].Module}: {Truncate(planned[0].Title, 60)}"
                    : $"[{phaseLabel}] Stabilization, hardening & {q.Theme.ToLowerInvariant()} readiness",
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
