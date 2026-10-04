using System.Linq;
using SmartAgent.Core.Pm;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

/// <summary>
/// Capability-driven requirement capture: the app's own controllers, hubs,
/// entities and views must name the epics and stories — requirements follow
/// the application's core functionality, not its folder names.
/// </summary>
public class PmCapabilityTests
{
    private static SourceSnapshot RecitalAppSnapshot()
    {
        var files = new List<SourceFile>
        {
            new() { Path = "README.md", Content = "# Shunya Recital Hub\n\nOverview\n\nA web app for managing sacred text recitals, live events and community membership.\n" },
            new() { Path = "MvcApp/Controllers/AuthController.cs", Content =
                "public class AuthController : Controller {\n    public IActionResult Login() { return View(); }\n    public IActionResult Logout() { return RedirectToAction(\"Index\"); }\n    public IActionResult SwitchUser(int id) { return View(); }\n}\n" },
            new() { Path = "MvcApp/Controllers/LibraryController.cs", Content =
                "public class LibraryController : Controller {\n    public IActionResult Index() { return View(); }\n    [HttpPost] public IActionResult Create(LibraryItem item) { return RedirectToAction(\"Index\"); }\n    [HttpPost] public IActionResult Edit(LibraryItem item) { return RedirectToAction(\"Index\"); }\n    [HttpPost] public IActionResult Delete(int id) { return RedirectToAction(\"Index\"); }\n}\n" },
            new() { Path = "MvcApp/Models/LibraryItem.cs", Content =
                "public class LibraryItem {\n    public int Id { get; set; }\n    public string Title { get; set; }\n    public string LinesJson { get; set; }\n}\n" },
            new() { Path = "MvcApp/Models/UserProfile.cs", Content =
                "public class UserProfile {\n    public int Id { get; set; }\n    public string DisplayName { get; set; }\n}\n" },
            new() { Path = "MvcApp/Views/Library/Index.cshtml", Content = "<h1>Library</h1>\n" },
            new() { Path = "MvcApp/Views/Library/Create.cshtml", Content = "<form></form>\n" },
            // secondary root: a legacy client platform that must NOT be feature-ified
            new() { Path = "legacy-client/components/List.tsx", Content = "export function List() { return null; }\n" },
            new() { Path = "legacy-client/components/Grid.tsx", Content = "export function Grid() { return null; }\n" },
            new() { Path = "legacy-client/api-zod/schema.ts", Content = "export const s = 1;\n" },
        };
        return new SourceSnapshot
        {
            SourceType = SourceType.ZipArchive, SourceName = "test/recital",
            SourceDetail = "10 files analyzed", Files = files
        };
    }

    [Fact]
    public void ProfileApp_resolves_controllers_into_named_capabilities()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var profile = analyzer.ProfileApp(snapshot);

        Assert.NotNull(profile);
        Assert.Equal("Shunya Recital Hub", profile.AppName);
        Assert.Contains("sacred text recitals", profile.Purpose);
        Assert.Equal("MvcApp", profile.Root);
        Assert.Contains(profile.Capabilities, c => c.Feature == "Auth");
        Assert.Contains(profile.Capabilities, c => c.Feature == "Library");
        var library = profile.Capabilities.Single(c => c.Feature == "Library");
        Assert.Equal(new[] { "Index", "Create", "Edit", "Delete" }, library.Methods);
        Assert.Contains("LibraryItem", library.Entities);
        Assert.Contains("Index", library.Views);
        Assert.Contains("legacy-client", profile.OtherRoots.Select(r => r.Root));
    }

    [Fact]
    public void FromCapabilities_names_epics_after_core_functionality()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var profile = analyzer.ProfileApp(snapshot, classes);
        Assert.NotNull(profile);

        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromSource(snapshot, classes, profile);

        Assert.Contains(epics, e => e.Name.Contains("Library", StringComparison.OrdinalIgnoreCase));
        // authentication is shipped in the foundation epic, grounded in the real controller
        Assert.Contains(stories, s => s.Title.Contains("AuthController"));
        // the legacy client root is one consolidation epic, not a feature epic
        Assert.Contains(epics, e => e.Name.Contains("legacy-client", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(epics, e => e.Name.Contains("components core domain"));

        // stories name the real action methods, views and entities
        var text = string.Join("\n", stories.Select(s => s.Title + "\n" + string.Join("\n", s.AcceptanceCriteria)));
        Assert.Contains("Index", text);
        Assert.Contains("Create", text);
        Assert.Contains("LibraryItem", text);
        Assert.Contains("sacred text recitals", string.Join("\n", epics.Select(e => e.Description)));
    }

    [Fact]
    public void FromCapabilities_yields_layered_buildable_stories()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var profile = analyzer.ProfileApp(snapshot, classes);
        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromSource(snapshot, classes, profile);

        // every layer of the taxonomy is represented with grounded stories
        Assert.Contains(stories, s => s.Layer == "Controllers");
        Assert.Contains(stories, s => s.Layer == "Data Adapter");
        Assert.Contains(stories, s => s.Layer == "Frontend");
        Assert.Contains(stories, s => s.Layer == "Framework");
        // phases progress framework → build → enhance → release
        Assert.Contains(stories, s => s.Phase == "framework");
        Assert.Contains(stories, s => s.Phase == "build");
        Assert.Contains(stories, s => s.Phase == "release");
        // all stories reference a real epic
        foreach (var story in stories)
            Assert.Contains(epics, e => e.Key == story.EpicKey);
    }
}
