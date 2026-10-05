using Xunit;
using SmartAgent.Core.Pm;

namespace SmartAgent.Tests;

public class PromptStudioTests
{
    // ---------- intent ----------

    [Theory]
    [InlineData("create new app for library management with members, checkouts, fines", PmPromptIntent.CreateApp)]
    [InlineData("please scaffold a new MVC app for a dental clinic", PmPromptIntent.CreateApp)]
    [InlineData("enhance app: add audit logging and CSV exports to the Governance module", PmPromptIntent.EnhanceApp)]
    [InlineData("enhance the app with a reporting module", PmPromptIntent.EnhanceApp)]
    [InlineData("Build a family safety platform. Track live locations. Support chat. Manage devices.", PmPromptIntent.Ingest)]
    [InlineData("The app must print labels and track outages across queues.", PmPromptIntent.Ingest)]
    public void Classifies_prompt_intent(string prompt, PmPromptIntent expected)
        => Assert.Equal(expected, PromptStudio.IntentOf(prompt));

    [Fact]
    public void Extracts_feature_modules_from_a_prompt()
    {
        var modules = PromptStudio.ModulesOf("create new app for library management with members, checkouts, fines and returns");
        Assert.Contains("Member", modules);
        Assert.Contains("Checkout", modules);
        Assert.Contains("Fine", modules);
        Assert.Contains("Return", modules);
        Assert.DoesNotContain(modules, m => m.Equals("the", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- create new app: framework modules ----------

    [Fact]
    public void Create_app_prompt_scaffolds_framework_modules_with_all_layers()
    {
        var (epics, stories) = PromptStudio.FrameworkStories("LibraryHub",
            "create new app for library management with members, checkouts");

        // framework scaffold epic + one epic per requested module
        Assert.Equal("Framework", epics[0].Module);
        Assert.Contains(epics, e => e.Module == "Member");
        Assert.Contains(epics, e => e.Module == "Checkout");

        // scaffold stories grounded in the MVC solution base
        Assert.Contains(stories, s => s.Title.Contains("solution and project scaffold", StringComparison.Ordinal));
        Assert.Contains(stories, s => s.IWant.Contains("Program.cs composition root", StringComparison.Ordinal));
        Assert.Contains(stories, s => s.Title.Contains("Shared layout", StringComparison.Ordinal));
        Assert.Contains(stories, s => s.Title.Contains("Home controller", StringComparison.Ordinal));

        // per module: controller/actions + client-side views, data model, repository, database scripts
        foreach (var module in new[] { "Member", "Checkout" })
        {
            Assert.Contains(stories, s => s.Module == module && s.Title.Contains("server-side controller", StringComparison.Ordinal)
                && s.Title.Contains("client-side views", StringComparison.Ordinal));
            Assert.Contains(stories, s => s.Module == module && s.Layer == "Database" && s.Title.Contains("data model", StringComparison.Ordinal));
            Assert.Contains(stories, s => s.Module == module && s.Layer == "Data Adapter" && s.Title.Contains("repository", StringComparison.Ordinal));
            Assert.Contains(stories, s => s.Module == module && s.Title.Contains("database scripts", StringComparison.Ordinal));
        }

        // evidence-bound criteria pass the quality engine's standard
        Assert.All(stories, s => Assert.InRange(s.AcceptanceCriteria.Count, 2, 6));
        Assert.All(stories, s => Assert.Contains(s.AcceptanceCriteria, a => !AcComposer.IsGeneric(a)));
        Assert.All(stories, s => Assert.Contains(new[] { 1, 2, 3, 5, 8, 13 }, p => p == s.Points));
    }

    [Fact]
    public void Create_app_stories_bind_framework_files_and_actions()
    {
        var (_, stories) = PromptStudio.FrameworkStories("LibraryHub", "create new app with members");
        var ui = stories.Single(s => s.Module == "Member" && s.Layer == "Controllers");
        Assert.Contains(ui.AcceptanceCriteria, a => a.Contains("MemberController serves Index, Details, Create, Edit and Delete", StringComparison.Ordinal));
        Assert.Contains(ui.AcceptanceCriteria, a => a.Contains("Views/Member", StringComparison.Ordinal));
        var db = stories.Single(s => s.Module == "Member" && s.Title.Contains("database scripts", StringComparison.Ordinal));
        Assert.Contains(db.AcceptanceCriteria, a => a.Contains("Scripts/0001_Create_Member.sql", StringComparison.Ordinal));
    }

    // ---------- enhance app: affected modules ----------

    [Fact]
    public void Enhance_prompt_validates_affected_modules_and_updates_artifacts()
    {
        var known = new[] { "Governance", "Admin", "Events" };
        var (epics, stories) = PromptStudio.EnhancementStories(
            "enhance app: add audit logging and CSV exports to the Governance module", known);

        Assert.Single(epics);
        var validate = stories.Single(s => s.Title.Contains("Validate all affected modules", StringComparison.Ordinal));
        Assert.Contains(validate.AcceptanceCriteria, a => a.Contains("Governance", StringComparison.Ordinal)
            && a.Contains("controllers, actions, view models, data models, repositories and database scripts", StringComparison.Ordinal));

        var gov = stories.Single(s => s.Title == "Enhance Governance module");
        Assert.Contains(gov.AcceptanceCriteria, a => a.Contains("GovernanceController actions updated", StringComparison.Ordinal));
        Assert.Contains(gov.AcceptanceCriteria, a => a.Contains("migration script", StringComparison.Ordinal));
        Assert.Contains(gov.AcceptanceCriteria, a => a.Contains("living artifacts (requirements, design document, sequence diagram) are regenerated", StringComparison.Ordinal));
    }

    [Fact]
    public void Enhance_prompt_adds_new_modules_following_the_framework_conventions()
    {
        var known = new[] { "Governance" };
        var (_, stories) = PromptStudio.EnhancementStories(
            "enhance app: add a reporting module with exports", known);

        var reporting = stories.Single(s => s.Title.Contains("Add Reporting module", StringComparison.Ordinal));
        Assert.Contains(reporting.AcceptanceCriteria, a => a.Contains("ReportingController with CRUD actions", StringComparison.Ordinal));
        Assert.Contains(reporting.AcceptanceCriteria, a => a.Contains("Models/Reporting.cs", StringComparison.Ordinal));
        Assert.Contains(reporting.AcceptanceCriteria, a => a.Contains("Scripts/<next>_Create_Reporting.sql", StringComparison.Ordinal));
    }

    [Fact]
    public void Bare_enhance_prompt_affects_every_known_module()
    {
        var known = new[] { "Governance", "Admin", "Events" };
        var (_, stories) = PromptStudio.EnhancementStories("enhance the app", known);

        Assert.Contains(stories, s => s.Title.Contains("Validate all affected modules", StringComparison.Ordinal));
        Assert.Contains(stories, s => s.Title == "Enhance Governance module");
        Assert.Contains(stories, s => s.Title == "Enhance Admin module");
    }

    // ---------- change impact + living artifacts ----------

    [Fact]
    public void Change_impact_lists_every_resource_of_affected_modules()
    {
        var stories = PromptStudio.FrameworkStories("LibraryHub", "create new app with members").Stories;
        var rows = PromptStudio.ChangeImpact(stories, new[] { "Member" });

        Assert.Contains(rows, r => r.Module == "Member" && r.Resource.Contains("MemberController") && r.Kind.Contains("client-side views"));
        Assert.Contains(rows, r => r.Module == "Member" && r.Resource.Contains("Models/Member.cs") && r.Kind.Contains("view models"));
        Assert.Contains(rows, r => r.Module == "Member" && r.Resource.Contains("IMemberRepository"));
        Assert.Contains(rows, r => r.Module == "Member" && r.Resource.Contains("Scripts/*_Member*.sql"));
    }

    private static ProgramPlan MinimalPlan()
    {
        var (epics, stories) = PromptStudio.FrameworkStories("LibraryHub", "create new app with members");
        return new ProgramPlan
        {
            Name = "LibraryHub", Slug = "libraryhub", SourceType = "prompt", SourceName = "prompt",
            Epics = epics, Stories = stories, UserStoryCount = stories.Count,
            TotalStoryPoints = stories.Sum(s => s.Points), Sprints = 4, Years = 1
        };
    }

    [Fact]
    public void Living_artifacts_compose_requirements_design_and_sequence()
    {
        var plan = MinimalPlan();
        var files = LivingArtifactsComposer.Compose(plan, "create new app for library management with members").ToList();

        Assert.Equal(4, files.Count);
        var names = files.Select(f => f.Name).ToList();
        Assert.Contains("requirements.md", names);
        Assert.Contains("design-document.md", names);
        Assert.Contains("sequence-diagram.md", names);
        Assert.Contains("change-impact.md", names);

        var requirements = files.Single(f => f.Name == "requirements.md").Content;
        Assert.Contains("living artifact", requirements);
        Assert.Contains("US-001", requirements);
        Assert.Contains("As a", requirements);

        var design = files.Single(f => f.Name == "design-document.md").Content;
        Assert.Contains("```mermaid", design);
        Assert.Contains("Building Block View", design);

        var sequence = files.Single(f => f.Name == "sequence-diagram.md").Content;
        Assert.Contains("```mermaid", sequence);

        var change = files.Single(f => f.Name == "change-impact.md").Content;
        Assert.Contains("full delivery", change);
    }

    [Fact]
    public void Change_impact_artifact_marks_enhancements_with_affected_resources()
    {
        var plan = MinimalPlan();
        var change = LivingArtifactsComposer.Compose(plan, "enhance app: add audit logging to the Member module")
            .Single(f => f.Name == "change-impact.md").Content;

        Assert.Contains("**enhancement**", change);
        Assert.Contains("MemberController", change);
        Assert.Contains("IMemberRepository", change);
    }

    [Fact]
    public void Living_artifacts_write_to_a_separate_solution_folder()
    {
        var plan = MinimalPlan();
        var root = Path.Combine(Path.GetTempPath(), "pm-artifacts-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var dir = LivingArtifactsComposer.Write(plan, root, "create new app with members");
            Assert.Equal("living-artifacts", Path.GetFileName(dir));
            Assert.True(File.Exists(Path.Combine(dir, "requirements.md")));
            Assert.True(File.Exists(Path.Combine(dir, "design-document.md")));
            Assert.True(File.Exists(Path.Combine(dir, "sequence-diagram.md")));
            Assert.True(File.Exists(Path.Combine(dir, "change-impact.md")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ---------- end to end through the ingestor ----------

    [Fact]
    public void Ingestor_routes_create_and_enhance_prompts_through_the_studio()
    {
        var ingestor = new RequirementIngestor();

        var (cEpics, cStories) = ingestor.FromPrompt("create new app for library management with members, checkouts");
        Assert.Contains(cEpics, e => e.Module == "Framework");
        Assert.Contains(cStories, s => s.Module == "Checkout" && s.Layer == "Database");

        var (eEpics, eStories) = ingestor.FromPrompt("enhance app: add audit logging to the Governance module");
        Assert.Contains(eStories, s => s.Title == "Enhance Governance module");

        // legacy sentence prompts keep the old behavior
        var (epics, stories) = ingestor.FromPrompt(
            "Build a family safety platform. Track live locations. Support chat. Manage devices. Alert on outages.");
        Assert.Single(epics);
        Assert.True(stories.Count >= 4);
    }
}
