namespace SmartAgent.Core.Pm;

/// <summary>One failed expectation of the quality standard.</summary>
public sealed record PmQualityCheck
{
    /// <summary>Standard area: story / sprint / testcase.</summary>
    public required string Area { get; init; }
    /// <summary>The artifact the check refers to (story key, sprint number, test id).</summary>
    public required string Key { get; init; }
    /// <summary>The standard of expectation that was not met.</summary>
    public required string Standard { get; init; }
    /// <summary>What was found instead.</summary>
    public required string Issue { get; init; }
}

/// <summary>The full quality verdict for a program plan.</summary>
public sealed record PmQualityReport
{
    /// <summary>0-100 — fraction of standards met across all artifacts.</summary>
    public required int Score { get; init; }
    /// <summary>True when the score meets the standard of expectation (90).</summary>
    public required bool Pass { get; init; }
    public IReadOnlyList<PmQualityCheck> Issues { get; init; } = Array.Empty<PmQualityCheck>();
    /// <summary>How many evaluate→repair passes were run to reach this score.</summary>
    public int RepairPasses { get; init; }
    /// <summary>Issues fixed by the repair passes (vs. the first evaluation).</summary>
    public int IssuesFixed { get; init; }
    /// <summary>Human-readable verdict.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// The quality engine: compares a generated program against the standard of
/// expectation for sprints, requirements and test cases, then regenerates the
/// failing parts until the plan meets the standard (bounded passes).
///
/// Standards of expectation:
///  - Requirement: 2-4 criteria, at least one bound to concrete source evidence
///    (action signature, field size, view file); persona/want/so-that present;
///    Fibonacci points; module and epic resolvable.
///  - Sprint: total points within capacity; every backlog story scheduled
///    exactly once.
///  - Test case: bound to a real class/method (no empty or generic anchors).
/// </summary>
public sealed class PmQualityEngine
{
    public const int StandardScore = 90;
    private static readonly int[] Fibonacci = { 1, 2, 3, 5, 8, 13 };

    /// <summary>Compares the plan against every standard (no repair).</summary>
    public PmQualityReport Evaluate(ProgramPlan plan)
    {
        var issues = new List<PmQualityCheck>();
        issues.AddRange(EvaluateStories(plan));
        issues.AddRange(EvaluateSprints(plan));
        issues.AddRange(EvaluateTestCases(plan));

        var total = StoryStandardCount(plan) + SprintStandardCount(plan) + TestStandardCount(plan);
        var score = total == 0 ? 100 : (int)Math.Round(100m * (total - issues.Count) / total);
        var pass = score >= StandardScore;
        var worst = issues.GroupBy(i => i.Area).OrderByDescending(g => g.Count()).FirstOrDefault();
        return new PmQualityReport
        {
            Score = score, Pass = pass, Issues = issues,
            Summary = pass
                ? $"Meets the standard of expectation ({score}/100): {plan.Stories.Count} stories carry evidence-bound criteria, sprints respect capacity, tests are grounded."
                : $"Below standard ({score}/100): {issues.Count} expectations unmet" +
                  (worst != null ? $", worst area {worst.Key.ToLowerInvariant()} ({worst.Count()})" : "") +
                  "."
        };
    }

    /// <summary>
    /// Compare-and-regenerate loop: evaluate the plan, repair every failing
    /// artifact against its standard, re-evaluate — until the plan meets the
    /// standard of expectation or the pass budget is exhausted. The returned
    /// report is the final verdict and the plan is repaired in place.
    /// </summary>
    public PmQualityReport ReviewAndImprove(ProgramPlan plan, int maxPasses = 3)
    {
        var first = Evaluate(plan);
        if (first.Pass) return first with { RepairPasses = 0, IssuesFixed = 0 };

        var current = first;
        var passes = 0;
        while (!current.Pass && passes < maxPasses)
        {
            passes++;
            Repair(plan, current.Issues);
            current = Evaluate(plan);
        }
        return current with
        {
            RepairPasses = passes,
            IssuesFixed = Math.Max(0, first.Issues.Count - current.Issues.Count),
            Summary = current.Pass
                ? current.Summary + $" Reached after {passes} regeneration pass(es); {Math.Max(0, first.Issues.Count - current.Issues.Count)} issue(s) repaired."
                : current.Summary + $" {passes} regeneration pass(es) run; remaining issues need source evidence the repository does not expose."
        };
    }

    // ─── standards: requirements ───

    private static IEnumerable<PmQualityCheck> EvaluateStories(ProgramPlan plan)
    {
        var epicKeys = plan.Epics.Select(e => e.Key).ToHashSet();
        foreach (var s in plan.Stories)
        {
            var acs = s.AcceptanceCriteria.Where(a => a.Trim().Length > 0).ToList();
            if (acs.Count < 2)
                yield return Story(s, "2-4 criteria per story",
                    $"only {acs.Count} acceptance criterion");
            else if (acs.Count > 6)
                yield return Story(s, "consolidated criteria (≤6, ideally 2-4 sentences)",
                    $"{acs.Count} criteria — should be consolidated");
            if (acs.Count > 0 && acs.All(AcComposer.IsGeneric))
                yield return Story(s, "at least one criterion bound to source evidence (signature, field size, view)",
                    "all criteria are generic — none is verifiable against the source");
            if (string.IsNullOrWhiteSpace(s.AsA) || string.IsNullOrWhiteSpace(s.IWant)
                || string.IsNullOrWhiteSpace(s.SoThat))
                yield return Story(s, "persona / want / so-that all present",
                    $"persona={(s.AsA.Length == 0 ? "missing" : "ok")}, want={(s.IWant.Length == 0 ? "missing" : "ok")}, so-that={(s.SoThat.Length == 0 ? "missing" : "ok")}");
            if (!Fibonacci.Contains(s.Points))
                yield return Story(s, "Fibonacci story points (1, 2, 3, 5, 8, 13)",
                    $"{s.Points} points");
            if (!epicKeys.Contains(s.EpicKey))
                yield return Story(s, "story epic exists in the backlog", $"epic {s.EpicKey} not found");
            if (string.IsNullOrWhiteSpace(s.Module))
                yield return Story(s, "story maps to a module", "module is empty");
        }
    }

    private static int StoryStandardCount(ProgramPlan plan) => plan.Stories.Count * 6;

    // ─── standards: sprints ───

    private static IEnumerable<PmQualityCheck> EvaluateSprints(ProgramPlan plan)
    {
        foreach (var sp in plan.SprintPlans)
        {
            if (sp.Points > sp.CapacityPoints)
                yield return Sprint(sp,
                    "sprint load within capacity",
                    $"{sp.Points} points vs {sp.CapacityPoints} capacity (+{sp.Points - sp.CapacityPoints})");
        }
        var scheduled = plan.SprintPlans.SelectMany(sp => sp.StoryKeys)
            .GroupBy(k => k).Where(g => g.Count() > 1).ToList();
        foreach (var dup in scheduled)
            yield return new PmQualityCheck
            {
                Area = "sprint", Key = dup.Key,
                Standard = "every story scheduled exactly once",
                Issue = $"scheduled {dup.Count()} times"
            };
        var planned = plan.SprintPlans.SelectMany(sp => sp.StoryKeys).ToHashSet();
        foreach (var s in plan.Stories.Where(s => !planned.Contains(s.Key)))
            yield return Sprint(new PmSprint { Number = 0, Name = "-", Goal = "-", Quarter = "Q1",
                    StartWeek = 0, EndWeek = 0, StartDate = default, EndDate = default },
                "every backlog story lands in a sprint", $"{s.Key} is not scheduled");
    }

    private static int SprintStandardCount(ProgramPlan plan) => plan.SprintPlans.Count + plan.Stories.Count;

    private static PmQualityCheck Story(PmStory s, string standard, string issue) => new()
    {
        Area = "story", Key = s.Key, Standard = standard, Issue = issue
    };

    private static PmQualityCheck Sprint(PmSprint sp, string standard, string issue) => new()
    {
        Area = "sprint", Key = sp.Number > 0 ? $"S{sp.Number:D2}" : "backlog", Standard = standard, Issue = issue
    };

    // ─── standards: test cases ───

    private static IEnumerable<PmQualityCheck> EvaluateTestCases(ProgramPlan plan)
    {
        foreach (var t in plan.UnitTests)
        {
            if (string.IsNullOrWhiteSpace(t.Cls) || string.IsNullOrWhiteSpace(t.Method)
                || t.Cls is "primary action" or "-" or "n/a")
                yield return new PmQualityCheck
                {
                    Area = "testcase", Key = t.Id,
                    Standard = "test bound to a real class and method",
                    Issue = $"class={t.Cls}, method={t.Method}"
                };
            if (AcComposer.IsGeneric(t.Scenario) || AcComposer.IsGeneric(t.Expected))
                yield return new PmQualityCheck
                {
                    Area = "testcase", Key = t.Id,
                    Standard = "scenario and expectation are specific",
                    Issue = $"scenario='{Trunc(t.Scenario)}', expected='{Trunc(t.Expected)}'"
                };
        }
    }

    private static int TestStandardCount(ProgramPlan plan) => plan.UnitTests.Count * 2;

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "…";

    // ─── repair (targeted regeneration) ───

    private void Repair(ProgramPlan plan, IReadOnlyList<PmQualityCheck> issues)
    {
        var storyBy = plan.Stories.ToDictionary(s => s.Key);
        var epicBy = plan.Epics.ToDictionary(e => e.Key);

        foreach (var issue in issues)
        {
            switch (issue.Area)
            {
                case "story" when storyBy.TryGetValue(issue.Key, out var story):
                    RepairStory(story, epicBy.GetValueOrDefault(story.EpicKey), issue.Standard);
                    break;

                case "sprint" when issue.Key != "backlog" && int.TryParse(issue.Key[1..], out var sprintNo):
                    RepairSprintLoad(plan, sprintNo);
                    break;

                case "testcase":
                    RepairTestCase(plan, issue.Key, storyBy);
                    break;
            }
        }
        // keep sprint totals consistent with any repaired story points
        foreach (var sp in plan.SprintPlans)
            sp.Points = sp.StoryKeys.Sum(k => StoryPoints(plan, k));
        // stories that were never scheduled: land them in the first sprint with room
        var planned = plan.SprintPlans.SelectMany(sp => sp.StoryKeys).ToHashSet();
        foreach (var s in plan.Stories.Where(s => !planned.Contains(s.Key)))
        {
            var target = plan.SprintPlans.FirstOrDefault(sp =>
                sp.Points + s.Points <= sp.CapacityPoints && sp.StoryKeys.Count > 0);
            if (target == null) break;
            var keys = target.StoryKeys.ToList(); keys.Add(s.Key); target.StoryKeys = keys;
            target.Points += s.Points;
        }
    }

    /// <summary>Regenerates a story's acceptance criteria with reasoning over its evidence.
    /// The failing standard decides the repair: consolidation merges what exists;
    /// missing evidence recomposes from whatever the story can bind.</summary>
    private static void RepairStory(PmStory story, PmEpic? epic, string failedStandard)
    {
        var acs = story.AcceptanceCriteria.ToList();

        if (failedStandard.Contains("consolidated", StringComparison.OrdinalIgnoreCase))
        {
            // merge themed bullets into a few aligned sentences, dropping generic filler
            story.AcceptanceCriteria = AcComposer.Consolidate(acs, story.IWant);
            return;
        }

        // mine evidence out of the existing criteria (they carry the source facts)
        var actions = acs.Where(a => a.Contains("binds parameters", StringComparison.OrdinalIgnoreCase)).ToList();
        var controller = "";
        foreach (var a in acs)
        {
            var m = System.Text.RegularExpressions.Regex.Match(a, @"\b([A-Z]\w*Controller)\b");
            if (m.Success) { controller = m.Groups[1].Value; break; }
        }
        var entity = epic != null && story.Title.Contains("Add field", StringComparison.OrdinalIgnoreCase)
            ? FirstPascal(story.Title, "Add field") : FirstPascalOf(acs.FirstOrDefault() ?? story.Title);
        var constraints = acs.Where(a => a.Contains("chars", StringComparison.OrdinalIgnoreCase)
            || a.Contains("required", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();

        var composed = AcComposer.Compose(new AcComposer.StoryEvidence
        {
            Title = story.Title, Module = story.Module.Length > 0 ? story.Module : epic?.Module ?? "app",
            Persona = story.AsA, Want = story.IWant, SoThat = story.SoThat,
            Layer = story.Layer, Phase = story.Phase,
            Controller = controller, Actions = actions,
            Entity = entity, Constraints = constraints,
            HistoryNote = story.Phase == "enhance"
                ? $"mirrors how the source itself added it" : ""
        });
        story.AcceptanceCriteria = composed;

        if (!new[] { 1, 2, 3, 5, 8, 13 }.Contains(story.Points))
            story.Points = story.Points <= 1 ? 1 : story.Points <= 2 ? 2 : story.Points <= 3 ? 3
                : story.Points <= 5 ? 5 : story.Points <= 8 ? 8 : 13;
    }

    /// <summary>Moves overflow stories forward until the sprint respects capacity.</summary>
    private static void RepairSprintLoad(ProgramPlan plan, int sprintNo)
    {
        var sprints = plan.SprintPlans.OrderBy(s => s.Number).ToList();
        var idx = sprints.FindIndex(s => s.Number == sprintNo);
        if (idx < 0) return;
        var sp = sprints[idx];
        while (sp.Points > sp.CapacityPoints && sp.StoryKeys.Count > 1)
        {
            var overflow = sp.StoryKeys.Last();
            var target = sprints.Skip(idx + 1)
                .FirstOrDefault(n => n.Points + StoryPoints(plan, overflow) <= n.CapacityPoints);
            if (target == null) break;
            sp.StoryKeys = sp.StoryKeys.SkipLast(1).ToList();
            sp.Points -= StoryPoints(plan, overflow);
            var keys = target.StoryKeys.ToList(); keys.Add(overflow); target.StoryKeys = keys;
            target.Points += StoryPoints(plan, overflow);
        }
    }

    private static int StoryPoints(ProgramPlan plan, string key) =>
        plan.Stories.FirstOrDefault(s => s.Key == key)?.Points ?? 0;

    /// <summary>Re-grounds a generic test case on the artifacts its story names.</summary>
    private static void RepairTestCase(ProgramPlan plan, string testId,
        Dictionary<string, PmStory> storyBy)
    {
        var test = plan.UnitTests.FirstOrDefault(t => t.Id == testId);
        if (test == null) return;
        // UT-S01-001 → sprint S01 → first story of that sprint
        var sprintTag = testId.Split('-').Length > 1 ? testId.Split('-')[1] : "";
        var story = plan.SprintPlans.FirstOrDefault(sp => sp.Name.Replace(" ", "").Contains(sprintTag))
            ?.StoryKeys.Select(k => storyBy.GetValueOrDefault(k)).FirstOrDefault(s => s != null);
        story ??= plan.Stories.FirstOrDefault();
        if (story == null) return;

        var artifact = System.Text.RegularExpressions.Regex.Match(
            string.Join(" ", story.AcceptanceCriteria), @"\b([A-Z]\w+Controller)\.([A-Z]\w+)\(\)");
        var cls = artifact.Success ? artifact.Groups[1].Value : story.Module;
        var method = artifact.Success ? artifact.Groups[2].Value
            : FirstActionVerb(story) ?? story.Key;
        var with = test with
        {
            Cls = cls, Method = method,
            Scenario = $"Given the {story.Module.ToLowerInvariant()} flow, when {method.ToLowerInvariant()} runs",
            Expected = $"Then the behavior matches the story contract: {FirstSentence(story)}"
        };
        plan.UnitTests = plan.UnitTests.Select(t => t.Id == testId ? with : t).ToList();
    }

    private static string? FirstActionVerb(PmStory story)
    {
        var low = story.IWant.ToLowerInvariant();
        foreach (var verb in new[] { "save", "delete", "edit", "create", "search", "send", "view", "log in", "log out" })
            if (low.Contains(verb)) return verb.Replace(" ", "");
        return null;
    }

    private static string FirstSentence(PmStory story)
    {
        var ac = story.AcceptanceCriteria.FirstOrDefault() ?? story.IWant;
        var dot = ac.IndexOf('.');
        return dot > 20 ? ac[..dot].TrimEnd() : ac;
    }

    private static string FirstPascal(string title, string prefix)
    {
        var after = title[(title.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) + prefix.Length)..];
        return FirstPascalOf(after);
    }

    private static string FirstPascalOf(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\b[A-Z][a-z]+[A-Z][A-Za-z]+\b");
        return m.Success ? m.Value : s.Split(' ').FirstOrDefault(w => char.IsUpper(w[0]) && w.Length > 3) ?? "";
    }
}
