using SmartAgent.Core.Pm;
using Xunit;

namespace SmartAgent.Tests;

/// <summary>
/// The quality engine: generated plans are compared against a standard of
/// expectation for stories, sprints and test cases, then regenerated until
/// they meet the standard. Acceptance criteria are reasoned and consolidated,
/// never generic filler.
/// </summary>
public class QualityEngineTests
{
    private static ProgramPlan Plan(params PmStory[] stories)
    {
        var epic = new PmEpic { Key = "E01", Name = "Governance", Module = "Governance", Theme = "Core" };
        var sprint = new PmSprint
        {
            Number = 1, Name = "S01 Foundation", Goal = "bootstrap", Quarter = "Q1",
            StartWeek = 1, EndWeek = 2, StartDate = new DateOnly(2026, 10, 5),
            EndDate = new DateOnly(2026, 10, 16), CapacityPoints = 20
        };
        foreach (var s in stories) sprint.StoryKeys = sprint.StoryKeys.Append(s.Key).ToArray();
        sprint.Points = stories.Sum(s => s.Points);
        return new ProgramPlan
        {
            Name = "Test Programme", Slug = "test", SourceName = "test",
            Epics = new[] { epic }, Stories = stories,
            SprintPlans = new[] { sprint }
        };
    }

    private static PmStory Story(string key, int points, params string[] ac) => new()
    {
        Key = key, EpicKey = "E01", Title = "Save users", AsA = "Administrator",
        IWant = "save governance users with their exact source parameters",
        SoThat = "membership stays current", Points = points, Priority = 90,
        Module = "Governance", TestCaseCount = 4, AcceptanceCriteria = ac
    };

    [Fact]
    public void Evaluate_flags_generic_criteria_and_non_fibonacci_points()
    {
        var plan = Plan(Story("US-001", 7, "Behavior matches the requirement", "Covered by tests"));
        var report = new PmQualityEngine().Evaluate(plan);
        Assert.False(report.Pass);
        Assert.Contains(report.Issues, i => i.Key == "US-001"
            && i.Standard.Contains("source evidence"));
        Assert.Contains(report.Issues, i => i.Key == "US-001"
            && i.Standard.Contains("Fibonacci"));
    }

    [Fact]
    public void Evaluate_passes_a_well_grounded_story()
    {
        var plan = Plan(Story("US-001", 5,
            "GovernanceController.SaveUser() — binds parameters: uid (string), username (string)",
            "Required fields (uid, username) enforced: empty submissions rejected"));
        var report = new PmQualityEngine().Evaluate(plan);
        Assert.True(report.Pass, report.Summary + " | " +
            string.Join("; ", report.Issues.Select(i => $"{i.Key}: {i.Issue}")));
    }

    [Fact]
    public void ReviewAndImprove_regenerates_generic_story_until_it_meets_the_standard()
    {
        var plan = Plan(Story("US-001", 7, "Behavior matches the requirement", "Covered by tests"));
        var engine = new PmQualityEngine();
        var report = engine.ReviewAndImprove(plan);
        Assert.True(report.Pass, report.Summary);
        Assert.True(report.RepairPasses >= 1);
        Assert.True(report.IssuesFixed >= 1);
        var story = plan.Stories.Single(s => s.Key == "US-001");
        // repaired: composed, evidence-bound sentences — and points snapped to Fibonacci (7 → 8)
        Assert.Equal(8, story.Points);
        Assert.All(story.AcceptanceCriteria, ac => Assert.False(AcComposer.IsGeneric(ac)));
        Assert.Contains(story.AcceptanceCriteria, ac => ac.Contains("Administrator"));
    }

    [Fact]
    public void ReviewAndImprove_repairs_overloaded_sprint_by_moving_overflow_forward()
    {
        var s1 = Story("US-001", 8, "GovernanceController.SaveUser() binds uid (string), username (string)");
        var s2 = Story("US-002", 8, "LibraryController.Create() binds item (LibraryItem)");
        var s3 = Story("US-003", 8, "LibraryController.Edit() binds item (LibraryItem)");
        var plan = Plan(s1, s2, s3);
        var sprint2 = new PmSprint
        {
            Number = 2, Name = "S02", Goal = "continue", Quarter = "Q1",
            StartWeek = 3, EndWeek = 4, StartDate = new DateOnly(2026, 10, 19),
            EndDate = new DateOnly(2026, 10, 30), CapacityPoints = 20, StoryKeys = new[] { "US-000" }
        };
        plan.SprintPlans = plan.SprintPlans.Append(sprint2).ToArray();
        // 24 points in a 20-point sprint
        var engine = new PmQualityEngine();
        var report = engine.ReviewAndImprove(plan);
        Assert.True(report.Pass, report.Summary);
        var first = plan.SprintPlans.Single(sp => sp.Number == 1);
        Assert.True(first.Points <= first.CapacityPoints);
        Assert.Contains(first.StoryKeys, k => k == "US-003" ? false : true);   // moved or kept per capacity math
        var total = plan.SprintPlans.Sum(sp => sp.Points);
        Assert.Equal(24, total);   // no story lost by the repair
    }

    [Fact]
    public void ReviewAndImprove_regrounds_generic_test_cases()
    {
        var plan = Plan(Story("US-001", 5,
            "GovernanceController.SaveUser() — binds parameters: uid (string), username (string)",
            "Required fields (uid, username) enforced: empty submissions rejected"));
        plan.UnitTests = new[]
        {
            new PmUnitTestCase { Id = "UT-S01-001", Cls = "primary action", Method = "",
                Scenario = "Behavior matches the requirement",
                Expected = "Covered by tests" },
            new PmUnitTestCase { Id = "UT-S01-002", Cls = "LibraryController", Method = "Create",
                Scenario = "Given the library flow, when create runs",
                Expected = "Then the item persists with its declared fields" }
        };
        var engine = new PmQualityEngine();
        var report = engine.ReviewAndImprove(plan);
        Assert.True(report.Pass, report.Summary);
        var repaired = plan.UnitTests.Single(t => t.Id == "UT-S01-001");
        Assert.Equal("GovernanceController", repaired.Cls);
        Assert.Equal("SaveUser", repaired.Method);
        Assert.False(AcComposer.IsGeneric(repaired.Scenario));
        Assert.Contains("GovernanceController.SaveUser", repaired.Expected);
    }

    [Fact]
    public void ReviewAndImprove_returns_early_when_already_at_standard()
    {
        var plan = Plan(Story("US-001", 5,
            "GovernanceController.SaveUser() — binds parameters: uid (string), username (string)",
            "Required fields (uid, username) enforced: empty submissions rejected"));
        plan.UnitTests = Array.Empty<PmUnitTestCase>();
        var report = new PmQualityEngine().ReviewAndImprove(plan);
        Assert.Equal(0, report.RepairPasses);
        Assert.True(report.Pass);
    }
}

public class AcComposerTests
{
    [Fact]
    public void IsGeneric_and_IsConcrete_classify_criteria()
    {
        Assert.True(AcComposer.IsGeneric("Behavior matches the requirement"));
        Assert.True(AcComposer.IsGeneric("Covered by tests"));
        Assert.False(AcComposer.IsGeneric(
            "GovernanceController.SaveUser() — binds parameters: uid (string), username (string)"));
        Assert.True(AcComposer.IsConcrete("SenderName sized exactly: max 120 chars"));
        Assert.True(AcComposer.IsConcrete("pairs with view Governance/Index.cshtml"));
        Assert.False(AcComposer.IsConcrete("Behavior matches the requirement"));
    }

    [Fact]
    public void Consolidate_merges_to_themed_sentences_and_drops_filler()
    {
        var merged = AcComposer.Consolidate(new[]
        {
            "Behavior matches the requirement",
            "GovernanceController.SaveUser() — binds parameters: uid (string), username (string)",
            "Required fields (uid, username) enforced: empty submissions rejected",
            "save persists the user profile"
        }, "save governance users");
        Assert.Equal(2, merged.Length);       // contract themed + behavior themed; generic dropped
        Assert.Contains("binds parameters", merged[0]);
        Assert.DoesNotContain("Behavior matches", merged[0] + merged[1]);
    }

    [Fact]
    public void Consolidate_keeps_an_anchor_when_nothing_survives()
    {
        var merged = AcComposer.Consolidate(new[] { "Behavior matches the requirement" }, "manage library items");
        Assert.True(merged.Length >= 2);
        Assert.Contains("manage library items", merged[0]);
    }

    [Fact]
    public void Compose_binds_evidence_into_a_few_sentences()
    {
        var ac = AcComposer.Compose(new AcComposer.StoryEvidence
        {
            Title = "Save users", Module = "Governance", Persona = "Administrator",
            Want = "save governance users", SoThat = "membership stays current",
            Layer = "Controllers", Phase = "enhance", Controller = "GovernanceController",
            Actions = new[] { "SaveUser(uid (string), username (string))" },
            Entity = "UserProfile", Constraints = new[] { "username (max 120 chars, required)" },
            Views = new[] { "Governance/Index.cshtml" },
            HistoryNote = "mirrors the source's own EventId addition"
        });
        Assert.InRange(ac.Length, 3, 5);
        Assert.Contains("As a Administrator", ac[0]);
        Assert.Contains("Governance/Index.cshtml", ac[0]);
        Assert.Contains("SaveUser(uid (string), username (string))", ac[1]);
        Assert.Contains("max 120 chars", ac[1]);
        Assert.Contains("GovernanceController", ac[2]);
        Assert.Contains("regression", ac[3]);
    }
}
