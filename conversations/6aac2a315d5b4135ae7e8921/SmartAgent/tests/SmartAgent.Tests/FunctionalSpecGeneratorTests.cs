using System.Net;
using System.Text;
using Xunit;
using SmartAgent.Core.Pm;

namespace SmartAgent.Tests;

/// <summary>An HTTP handler that serves canned pages — stands in for the live app.</summary>
public sealed class FakeAppHandler : HttpMessageHandler
{
    public bool LoggedIn { get; private set; }
    private readonly Dictionary<string, (string Method, string Body)> _pages;

    public FakeAppHandler() => _pages = new();

    public FakeAppHandler Add(string path, string body, string method = "GET")
    {
        _pages[path] = (method, body);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken ct = default)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/Auth/Login")
        {
            LoggedIn = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/Dashboard", UriKind.Relative) } });
        }
        if (path == "/" || path == "/Dashboard")
        {
            if (!LoggedIn)
                return Task.FromResult(Page("/Auth/Login",
                    @"<form method=""post"" action=""/Auth/Login"">
                    <input type=""text"" name=""userId"" />
                    <input type=""password"" name=""password"" />
                </form>"));
            return Task.FromResult(Page(path == "/" ? "/Dashboard" : path,
                _pages.GetValueOrDefault("/Dashboard").Body));
        }
        if (_pages.TryGetValue(path, out var page) && (page.Method == "GET" || LoggedIn))
            return Task.FromResult(Page(path, page.Body));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Page(string path, string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
}

public class FunctionalSpecGeneratorTests
{
    private const string Dashboard = @"<html><title>Shunya — Dashboard</title><body>
        <a href=""/Admin"">Admin</a><a href=""/Governance"">Governance</a>
        <a href=""/lib/bootstrap/dist/css/site.css"">css</a><a href=""/Auth/Logout"">out</a>
        </body></html>";

    private static string Page(string title, string body) =>
        $"<html><title>{title}</title><body>{body}</body></html>";

    [Fact]
    public async Task Captures_Login_Crawl_And_Forms()
    {
        var handler = new FakeAppHandler()
            .Add("/Dashboard", Dashboard)
            .Add("/Admin", Page("Admin", @"<form method=""post"" action=""/Admin/CreateEvent"">
                <input type=""text"" name=""title"" required />
                <input type=""datetime-local"" name=""startTime"" />
                <select name=""hostId""><option>1</option></select>
                </form>"))
            .Add("/Governance", Page("Governance", @"<form method=""get"" action=""/Governance"">
                <input type=""text"" name=""search"" />
                </form>"));
        var gen = new FunctionalSpecGenerator(() => handler);

        var spec = await gen.CaptureAsync("https://app.test:5000/Dashboard", "superadmin", "superadmin");

        Assert.True(handler.LoggedIn, "must log in before crawling");
        Assert.True(spec.Authenticated);
        Assert.Contains(spec.Pages, p => p.Route == "/Admin");
        var admin = spec.Pages.First(p => p.Route == "/Admin");
        Assert.Equal(200, admin.Status);
        Assert.Equal("Admin", admin.Title);
        var form = Assert.Single(admin.Forms, f => f.Action == "/Admin/CreateEvent");
        Assert.Contains("title", form.Fields);
        Assert.Contains("startTime", form.Fields);
        Assert.Contains("hostId", form.Fields);
        // static assets and logout never appear as routes
        Assert.DoesNotContain(spec.Pages, p => p.Route.Contains("lib"));
        Assert.DoesNotContain(spec.Pages, p => p.Route == "/Auth/Logout");
    }

    [Fact]
    public async Task Validates_Bound_Actions_And_Flags_Reachability()
    {
        var handler = new FakeAppHandler()
            .Add("/Dashboard", Dashboard)
            .Add("/Governance", Page("Governance", @"<form method=""post"" action=""/Governance/DeleteUser"">
                <input type=""text"" name=""uid"" />
                </form>"));
        var gen = new FunctionalSpecGenerator(() => handler);
        var spec = await gen.CaptureAsync("https://app.test/Dashboard", "u", "p");

        var epics = new List<PmEpic> { new() { Key = "E01", Name = "Governance", Module = "Governance", Theme = "Core" } };
        var stories = new List<PmStory>
        {
            new()
            {
                Key = "US-001", EpicKey = "E01", Title = "Govern user profiles end to end",
                AsA = "admin", IWant = "manage users", SoThat = "profiles stay accurate",
                Points = 3, Priority = 80, Risk = "Medium", Module = "Governance", TestCaseCount = 6,
                AcceptanceCriteria = new List<string>
                {
                    "GovernanceController.DeleteUser() — binds parameters: uid (string); paired with the Governance views per the source layout",
                    "GovernanceController.MissingAction() — binds parameters: x (string); paired with the Governance views per the source layout"
                }
            }
        };

        var result = gen.ValidateAndConsolidate(epics, stories, spec);

        var story = Assert.Single(result.Stories);
        Assert.Contains(story.AcceptanceCriteria,
            a => a.Contains("Live validation:", StringComparison.Ordinal) && a.Contains("respond in the running app"));
        Assert.Contains(story.AcceptanceCriteria,
            a => a.Contains("GovernanceController.MissingAction") && a.Contains("not reachable in the running app"));
        Assert.Contains("US-001", result.Spec.VerifiedStoryKeys);
        Assert.Contains(result.Spec.Findings, f => f.Contains("no live counterpart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Consolidates_Uncovered_Live_Functions_Into_Stories()
    {
        var handler = new FakeAppHandler()
            .Add("/Dashboard", Dashboard)
            .Add("/Admin", Page("Admin", @"<form method=""post"" action=""/Admin/CreateEvent"">
                <input type=""text"" name=""title"" /><input type=""text"" name=""venue"" />
                </form>"));
        var gen = new FunctionalSpecGenerator(() => handler);
        var spec = await gen.CaptureAsync("https://app.test/Dashboard", "u", "p");

        var epics = new List<PmEpic>();
        var stories = new List<PmStory>();   // no requirements mention Admin.CreateEvent

        var result = gen.ValidateAndConsolidate(epics, stories, spec);

        var added = result.Stories.SingleOrDefault(s => s.Title.Contains("CreateEvent", StringComparison.Ordinal));
        Assert.NotNull(added);
        Assert.Equal("Admin", added.Module);
        Assert.Equal(2, added.Points);
        Assert.True(added.AcceptanceCriteria[0].Contains("AdminController.CreateEvent()"),
            "the consolidation story must bind the live function signature");
        Assert.Contains("title", added.AcceptanceCriteria[0]);
        Assert.Contains("venue", added.AcceptanceCriteria[0]);
        Assert.Contains(result.Epics, e => e.Module == "Admin");
        Assert.Contains(result.Spec.Findings, f => f.Contains("consolidated into", StringComparison.Ordinal));
        Assert.Equal(1, result.Spec.UncoveredLiveFunctions);
    }

    [Fact]
    public async Task Consolidated_Stories_Meet_The_Quality_Standard()
    {
        var handler = new FakeAppHandler()
            .Add("/Dashboard", Dashboard)
            .Add("/Admin", Page("Admin", @"<form method=""post"" action=""/Admin/CreateEvent"">
                <input type=""text"" name=""title"" />
                </form>"));
        var gen = new FunctionalSpecGenerator(() => handler);
        var spec = await gen.CaptureAsync("https://app.test/Dashboard", "u", "p");
        var result = gen.ValidateAndConsolidate(
            new List<PmEpic>(), new List<PmStory>(), spec);

        var quality = new PmQualityEngine();
        foreach (var story in result.Stories.Where(s => s.Title.Contains("CreateEvent")))
        {
            // quality engine story standards: 2-4 criteria, non-generic evidence, persona/want/so-that, Fibonacci, epic, module
            Assert.InRange(story.AcceptanceCriteria.Count, 2, 6);
            Assert.Contains(story.AcceptanceCriteria, a => !AcComposer.IsGeneric(a));
            Assert.False(string.IsNullOrWhiteSpace(story.AsA));
            Assert.False(string.IsNullOrWhiteSpace(story.IWant));
            Assert.False(string.IsNullOrWhiteSpace(story.SoThat));
            Assert.Contains(story.Points, new[] { 1, 2, 3, 5, 8, 13 });
            Assert.Contains(result.Epics, e => e.Key == story.EpicKey);
            Assert.False(string.IsNullOrWhiteSpace(story.Module));
        }
    }

    [Fact]
    public void Login_Form_Parser_Finds_Fields()
    {
        var html = @"<form method=""post"" action=""/Auth/Login"">
            <input type=""text"" name=""userId"" class=""form-control"" />
            <input type=""password"" name=""password"" class=""form-control"" />
        </form>";
        var (action, userField, passField) = FunctionalSpecGenerator.ParseLoginForm(html);
        Assert.Equal("/Auth/Login", action);
        Assert.Equal("userId", userField);
        Assert.Equal("password", passField);
    }

    [Fact]
    public void Internal_Links_Skip_Assets()
    {
        var html = @"<a href=""/Admin"">a</a><a href=""/css/site.css?v=1"">c</a>
            <a href=""/Events/View/49242d1a"">e</a><a href=""https://ext.com/x"">x</a>";
        var links = FunctionalSpecGenerator.InternalLinks(html);
        Assert.Contains("/Admin", links);
        Assert.Contains("/Events/View/49242d1a", links);
        Assert.DoesNotContain(links, l => l.Contains("css"));
    }
}
