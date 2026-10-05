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
            new() { Path = "MvcApp/Views/Library/Index.cshtml", Content =
                "<h1>Library</h1>\n<a href=\"/Library/Create\">New</a> <a href=\"/Library/Edit\">Edit</a>\n" },
            new() { Path = "MvcApp/Views/Library/Create.cshtml", Content =
                "<form asp-action=\"Create\" method=\"post\">\n<input asp-for=\"Title\" />\n<input asp-for=\"LinesJson\" />\n<input name=\"__RequestVerificationToken\" />\n</form>\n" },
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
    public void Details_extraction_captures_entity_fields_and_view_fields_behaviors()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();

        var entities = analyzer.EntityDetailsOf(snapshot);
        var libraryItem = entities.Single(e => e.Name == "LibraryItem");
        Assert.Contains("Title (string)", libraryItem.Fields);
        Assert.Contains("LinesJson (string)", libraryItem.Fields);

        var views = analyzer.ViewDetailsOf(snapshot);
        var create = views.Single(v => v.Name == "Create.cshtml");
        Assert.Equal("Library", create.Feature);
        Assert.Contains("Title", create.Fields);
        Assert.Contains("LinesJson", create.Fields);
        Assert.DoesNotContain(create.Fields, f => f.StartsWith("__"));
        Assert.Contains("POST Create", create.Behaviors);
        var index = views.Single(v => v.Name == "Index.cshtml");
        Assert.Contains("link Library/Create", index.Behaviors);
        Assert.Contains("link Library/Edit", index.Behaviors);
    }

    [Fact]
    public void FromCapabilities_writes_specific_requirements_connecting_views_fields_behavior()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var profile = analyzer.ProfileApp(snapshot, classes);
        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromSource(snapshot, classes, profile);

        // controller story: per-action requirements wired to views & fields
        var controllerStory = stories.Single(s => s.Layer == "Controllers" && s.Title.StartsWith("Library:"));
        Assert.Contains(controllerStory.AcceptanceCriteria, a => a.Contains("LibraryController.Create()")
            && a.Contains("Library/Create.cshtml") && a.Contains("Title"));
        Assert.Contains(controllerStory.AcceptanceCriteria, a => a.Contains("LibraryController.Index()"));

        // data adapter story: real entity fields named
        var adapterStory = stories.Single(s => s.Layer == "Data Adapter" && s.Title.StartsWith("LibraryItem"));
        Assert.Contains(adapterStory.AcceptanceCriteria, a => a.Contains("Id") && a.Contains("Title") && a.Contains("LinesJson"));

        // frontend story: view-level fields and behaviors
        var frontendStory = stories.Single(s => s.Layer == "Frontend" && s.Title.StartsWith("Library screens"));
        Assert.Contains(frontendStory.AcceptanceCriteria, a => a.Contains("Create.cshtml") && a.Contains("Title") && a.Contains("POST Create"));
        Assert.Contains(frontendStory.AcceptanceCriteria, a => a.Contains("Index.cshtml") && a.Contains("link Library/Create"));

        // database story: column-level DDL requirements
        var dbStory = stories.Single(s => s.Layer == "Database" && s.Title.StartsWith("Library schema"));
        Assert.Contains(dbStory.AcceptanceCriteria, a => a.Contains("LibraryItem") && a.Contains("Title (string)"));
    }

    [Fact]
    public void Enhancement_waves_split_field_extensions_into_layer_components()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var profile = analyzer.ProfileApp(snapshot, classes);
        var ingestor = new RequirementIngestor();
        var (epics, stories) = ingestor.FromSource(snapshot, classes, profile);

        var extend = stories.Where(s => s.Phase == "enhance" && s.Title.Contains("add fields")).ToList();
        Assert.Contains(extend, s => s.Layer == "Database" && s.Title.Contains("database migration"));
        Assert.Contains(extend, s => s.Layer == "Data Adapter" && s.Title.Contains("server & data adapter"));
        Assert.Contains(extend, s => s.Layer == "Frontend" && s.Title.Contains("frontend forms & views"));
        // the concrete fields come from what the source model lacks (LibraryItem has no audit fields)
        Assert.Contains(extend, s => s.Title.Contains("CreatedAt"));
        // database component runs before server, server before frontend (descending priority)
        var db = extend.Single(s => s.Layer == "Database" && s.Module == "Library");
        var sv = extend.Single(s => s.Layer == "Data Adapter" && s.Module == "Library");
        var fe = extend.Single(s => s.Layer == "Frontend" && s.Module == "Library");
        Assert.True(db.Priority >= sv.Priority && sv.Priority >= fe.Priority);
    }

    [Fact]
    public void Classes_captures_real_method_parameters()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var ctrl = classes.First(c => c.Name == "LibraryController");
        Assert.True(ctrl.MethodParams.TryGetValue("Create", out var createParams));
        Assert.Contains("item (LibraryItem)", createParams!);
        Assert.True(ctrl.MethodParams.TryGetValue("Delete", out var deleteParams));
        Assert.Contains("id (int)", deleteParams!);
    }

    [Fact]
    public void EntityDetailsOf_captures_field_sizes_explicit_and_inferred()
    {
        var snapshot = RecitalAppSnapshot();
        var analyzer = new SourceAnalyzer();
        var entities = analyzer.EntityDetailsOf(snapshot);
        var libraryItem = entities.Single(e => e.Name == "LibraryItem");
        Assert.True(libraryItem.FieldSizes.ContainsKey("Title"));
        Assert.Contains("inferred default", libraryItem.FieldSizes["Title"]);
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

// ─────────────────── Roslyn reader (compiler-grade extraction) ───────────────────

public class RoslynReaderTests
{
    private static readonly string GovernanceControllerCs = """
        using Microsoft.AspNetCore.Mvc;
        public class GovernanceController : Controller
        {
            public IActionResult Index(string? search) { return View(); }
            [HttpPost]
            public IActionResult SaveUser(string uid, string? userId,
                string username, string email, string name, string usertype, string userlevel,
                string? phone = null, string? address = null)
                => RedirectToAction("Index");
            [HttpPost, ValidateAntiForgeryToken]
            public IActionResult DeleteUser(string uid) => RedirectToAction("Index");
            private void Helper() { }
            public int NotAMethodProperty { get; set; }
        }
        """;

    private static readonly string EventHubCs = """
        using Microsoft.AspNetCore.SignalR;
        public class EventHub : Hub
        {
            public async Task SendMessage(string eventId, string text) { await Clients.All.SendAsync("msg", text); }
        }
        """;

    private static readonly string EntityCs = """
        using System.ComponentModel.DataAnnotations;
        public class ChatMessage
        {
            [Key] public int Id { get; set; }
            [Required, StringLength(120)] public string SenderName { get; set; } = "";
            public string? Text { get; set; }
            [MaxLength(500)] public string Body { get; set; } = "";
        }
        """;

    private static SourceSnapshot Snapshot(params (string path, string content)[] files)
    {
        var list = files.Select(f => new SourceFile { Path = f.path, Content = f.content }).ToList();
        return new SourceSnapshot
        {
            SourceType = SourceType.ZipArchive, SourceName = "test/roslyn",
            SourceDetail = $"{list.Count} files", Files = list
        };
    }

    [Fact]
    public void RoslynReader_reads_exact_method_parameters()
    {
        var classes = RoslynReader.ReadClasses(new List<SourceFile>
        {
            new() { Path = "MvcApp/Controllers/GovernanceController.cs", Content = GovernanceControllerCs }
        });
        var controller = classes.Single(c => c.Name == "GovernanceController");
        Assert.Equal("Controller", controller.Kind);
        Assert.Contains("SaveUser", controller.Methods);
        Assert.DoesNotContain("Helper", controller.Methods);          // private methods excluded
        Assert.Equal("uid (string)", controller.MethodParams["DeleteUser"].Single());
        Assert.Equal(
            "uid (string), userId (string?), username (string), email (string), name (string), "
            + "usertype (string), userlevel (string), phone (string?), address (string?)",
            string.Join(", ", controller.MethodParams["SaveUser"]));
        Assert.Equal("search (string?)", controller.MethodParams["Index"].Single());
    }

    [Fact]
    public void RoslynReader_kinds_hubs_from_base_list()
    {
        var classes = RoslynReader.ReadClasses(new List<SourceFile>
        {
            new() { Path = "MvcApp/Hubs/EventHub.cs", Content = EventHubCs }
        });
        var hub = classes.Single(c => c.Name == "EventHub");
        Assert.Equal("Hub", hub.Kind);
        Assert.Equal("eventId (string), text (string)", string.Join(", ", hub.MethodParams["SendMessage"]));
    }

    [Fact]
    public void RoslynReader_reads_entity_attributes_exactly()
    {
        var entities = RoslynReader.ReadEntities(new List<SourceFile>
        {
            new() { Path = "MvcApp/Models/ChatMessage.cs", Content = EntityCs }
        });
        var chat = entities.Single(e => e.Name == "ChatMessage");
        var sender = chat.Properties.Single(p => p.Name == "SenderName");
        Assert.Equal(120, sender.DeclaredSize);
        Assert.True(sender.RequiredAttr);
        var body = chat.Properties.Single(p => p.Name == "Body");
        Assert.Equal(500, body.DeclaredSize);
        var text = chat.Properties.Single(p => p.Name == "Text");
        Assert.Null(text.DeclaredSize);
        Assert.Equal("string?", text.Type);
        var id = chat.Properties.Single(p => p.Name == "Id");
        Assert.True(id.KeyAttr);
    }

    [Fact]
    public void EntityDetailsOf_uses_declared_sizes_from_roslyn()
    {
        var snapshot = Snapshot(
            ("MvcApp/Models/ChatMessage.cs", EntityCs));
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        Assert.Contains(classes, c => c.Name == "ChatMessage" && c.Kind == "Entity");
        var details = analyzer.EntityDetailsOf(snapshot, classes);
        var chat = details.Single(d => d.Name == "ChatMessage");
        Assert.Equal("max 120 chars (declared via StringLength/MaxLength)", chat.FieldSizes["SenderName"]);
        Assert.Equal("max 500 chars (declared via StringLength/MaxLength)", chat.FieldSizes["Body"]);
        Assert.Contains("SenderName", chat.RequiredFields);
        Assert.Contains("Body", chat.RequiredFields);
        Assert.DoesNotContain("Text", chat.RequiredFields);   // string? is optional
        Assert.Contains("Id (int)", chat.Fields);
    }

    [Fact]
    public void Classes_survives_roslyn_swap_for_capability_profile()
    {
        // the end-to-end contract: SourceAnalyzer.Classes still feeds ProfileApp
        var snapshot = Snapshot(
            ("README.md", "# Shunya Recital Hub\n\nA web app for managing recitals.\n"),
            ("MvcApp/Controllers/LibraryController.cs", GovernanceControllerCs.Replace("Governance", "Library")),
            ("MvcApp/Models/LibraryItem.cs", EntityCs.Replace("ChatMessage", "LibraryItem")));
        var analyzer = new SourceAnalyzer();
        var classes = analyzer.Classes(snapshot);
        var controller = classes.Single(c => c.Name == "LibraryController");
        Assert.Equal("Controller", controller.Kind);
        Assert.Equal("uid (string)", controller.MethodParams["DeleteUser"].Single());
        Assert.Equal("MvcApp", controller.Module);
    }
}

// ─────────────────── Architecture composer (arc42 + C4) ───────────────────

public class ArchitectureComposerTests
{
    private static ProgramPlan Plan()
    {
        var epic = new PmEpic { Key = "E01", Name = "Governance", Module = "Governance", Theme = "Core" };
        var story = new PmStory
        {
            Key = "US-001", EpicKey = "E01", Title = "Save users", AsA = "Administrator",
            IWant = "save users", SoThat = "membership stays current", Points = 5, Priority = 1,
            Module = "Governance", Phase = "build",
            AcceptanceCriteria = new[] { "Controller: GovernanceController.SaveUser() binds real parameters" }
        };
        return new ProgramPlan
        {
            Name = "MvcReshunyasanga Programme", Slug = "test", SourceName = "github",
            Years = 2, Sprints = 52, Weeks = 104, UserStoryCount = 1, TotalStoryPoints = 5,
            Epics = new[] { epic }, Stories = new[] { story },
            Platform = new PmPlatform
            {
                Name = "Microsoft Enterprise Stack", ShortName = ".NET", Color = "#512BD4",
                Os = "Windows Server", Backend = "ASP.NET Core 8 MVC", BackendLang = "C#",
                PrimaryDb = "SQL Server 2022", SecondaryDb = "Oracle 19c",
                Infra = new[] { "IIS", "Kestrel" },
                BestFor = "Enterprise line-of-business apps",
                Advantages = new[] { "Mature patterns" },
                Arch = new PmPlatformArch
                {
                    WebLayer = new[] { "MVC Controllers" },
                    ServiceLayer = new[] { "Scoped services" },
                    DataLayer = new[] { "EF Core 8", "SQL Server (prod)" },
                    DomainTables = Array.Empty<string>()
                }
            }
        };
    }

    [Fact]
    public void Compose_builds_c4_diagrams_and_arc42_sections()
    {
        var arch = ArchitectureComposer.Compose(Plan());
        Assert.Contains("flowchart LR", arch.ContextDiagram);
        Assert.Contains("db[(\"", arch.ContextDiagram);              // cylinder datastore node
        Assert.Contains("ASP.NET Core 8 MVC", arch.ContainerDiagram);
        Assert.Contains("SQL Server 2022", arch.ContextDiagram);
        Assert.Contains("EF Core 8", arch.ContainerDiagram);
        // runtime view mines the real controller action from story acceptance criteria
        Assert.Single(arch.RuntimeViews);
        Assert.Contains("sequenceDiagram", arch.RuntimeViews[0]);
        Assert.Contains("GovernanceController.SaveUser()", arch.RuntimeViews[0]);
        // building blocks reflect the plan's module
        Assert.Contains(("Governance", "Core", 1, 5), arch.BuildingBlocks);
        // deployment + ADRs grounded in the platform
        Assert.Contains(arch.DeploymentNodes, d => d.Contains("SQL Server 2022"));
        Assert.Contains(arch.Decisions, d => d.Id == "ADR-002" && d.Title.Contains("SQL Server 2022"));
        Assert.Contains(arch.Decisions, d => d.Id == "ADR-004" && d.Decision.Contains("commit"));
    }

    [Fact]
    public void Compose_survives_missing_platform()
    {
        var plan = Plan(); plan.Platform = null;
        var arch = ArchitectureComposer.Compose(plan);
        Assert.Contains("flowchart LR", arch.ContextDiagram);
        Assert.Contains("relational database", arch.ContextDiagram);
        Assert.Contains("ADR-004", arch.Decisions.Select(d => d.Id));
    }
}
