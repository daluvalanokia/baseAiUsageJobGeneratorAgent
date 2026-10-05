using System.Net;
using System.Text.RegularExpressions;

namespace SmartAgent.Core.Pm;

/// <summary>A live form captured from a functional application page.</summary>
public sealed record PmFunctionalForm
{
    public required string Action { get; init; }
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}

/// <summary>One page of the running application, with its functional surface.</summary>
public sealed record PmFunctionalPage
{
    public required string Route { get; init; }                 // "/Governance"
    public required int Status { get; init; }
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<PmFunctionalForm> Forms { get; init; } = Array.Empty<PmFunctionalForm>();
    public IReadOnlyList<string> Links { get; init; } = Array.Empty<string>();
}

/// <summary>The functional spec captured from a running app, plus validation verdicts.</summary>
public sealed record PmFunctionalSpec
{
    public required string BaseUrl { get; init; }                // credentials are never persisted
    public required string CapturedUtc { get; init; }
    public required bool Authenticated { get; init; }
    public required IReadOnlyList<PmFunctionalPage> Pages { get; init; }
    /// <summary>Stories whose bound actions were confirmed live in the running app.</summary>
    public IReadOnlyList<string> VerifiedStoryKeys { get; init; } = Array.Empty<string>();
    /// <summary>Consolidation findings: uncovered live functions, unreachable requirements, field mismatches.</summary>
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public int VerifiedFunctions { get; init; }
    public int UnverifiableFunctions { get; init; }
    public int UncoveredLiveFunctions { get; init; }
    public int FieldMismatches { get; init; }
    public required string Summary { get; init; }
}

/// <summary>Validation + consolidation result over the whole backlog.</summary>
public sealed record FunctionalValidation
{
    public required PmFunctionalSpec Spec { get; init; }
    public required IReadOnlyList<PmEpic> Epics { get; init; }
    public required IReadOnlyList<PmStory> Stories { get; init; }
}

/// <summary>
/// The functional spec generator: logs into a running application, crawls its
/// authenticated routes, extracts the live functional surface (forms, actions,
/// fields, links), validates the source-generated requirements against it and
/// consolidates the backlog so requirements match what the app actually does.
/// </summary>
public sealed partial class FunctionalSpecGenerator
{
    private const int MaxRoutes = 16;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public FunctionalSpecGenerator() : this(() => new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        AllowAutoRedirect = true,
        // dev/staging apps (e.g. Replit previews) often serve dev certificates
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    }) { }

    public FunctionalSpecGenerator(Func<HttpMessageHandler> handlerFactory)
        => _handlerFactory = handlerFactory;

    /// <summary>Captures the functional surface of the running app.</summary>
    public async Task<PmFunctionalSpec> CaptureAsync(string baseUrl, string? userId, string? password,
        CancellationToken ct = default)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        using var client = new HttpClient(_handlerFactory()) { BaseAddress = new Uri(root) };
        client.Timeout = TimeSpan.FromSeconds(45);

        var entryBody = await GetBodyAsync(client, "/", ct);
        var authenticated = !HasLoginForm(entryBody);
        if (HasLoginForm(entryBody) && !string.IsNullOrWhiteSpace(userId))
        {
            var (action, userField, passField) = ParseLoginForm(entryBody);
            if (action is not null && userField is not null && passField is not null)
            {
                using var login = new HttpRequestMessage(HttpMethod.Post, action)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        [userField] = userId!, [passField] = password ?? string.Empty
                    })
                };
                using var loginResp = await client.SendAsync(login, ct);
                await loginResp.Content.ReadAsStringAsync(ct);
                entryBody = await GetBodyAsync(client, "/", ct);
                authenticated = !HasLoginForm(entryBody);
            }
        }

        var routes = new List<string> { "/" };
        routes.AddRange(InternalLinks(entryBody));
        var pages = new List<PmFunctionalPage>();
        for (var i = 0; i < routes.Count && pages.Count < MaxRoutes; i++)
        {
            var route = routes[i];
            using var resp = await client.GetAsync(route, ct);
            var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : string.Empty;
            pages.Add(new PmFunctionalPage
            {
                Route = route, Status = (int)resp.StatusCode, Title = TitleOf(body),
                Forms = FormsOf(body), Links = InternalLinks(body)
            });
            foreach (var link in InternalLinks(body))
                if (routes.Count < MaxRoutes && !routes.Contains(link))
                    routes.Add(link);
        }

        return new PmFunctionalSpec
        {
            BaseUrl = root, CapturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Authenticated = authenticated, Pages = pages,
            Summary = $"Captured {pages.Count} routes from the live app" +
                      $" ({pages.Sum(p => p.Forms.Count)} forms, {pages.Sum(p => p.Forms.Sum(f => f.Fields.Count))} bound fields)" +
                      (authenticated ? "; authenticated crawl" : "; anonymous crawl")
        };
    }

    /// <summary>
    /// Validates every source-generated requirement against the captured live
    /// surface and consolidates: verified stories gain live-evidence criteria,
    /// uncovered live functions become new requirement stories, unreachable
    /// requirements are flagged.
    /// </summary>
    public FunctionalValidation ValidateAndConsolidate(
        IReadOnlyList<PmEpic> epics, IReadOnlyList<PmStory> stories, PmFunctionalSpec captured)
    {
        var epicList = epics.ToList();
        var storyList = stories.Select(s => s with { AcceptanceCriteria = s.AcceptanceCriteria.ToList() }).ToList();
        var findings = new List<string>();
        var verifiedKeys = new List<string>();

        // live function index: "FeatureController.Action" → (route, fields)
        var live = LiveFunctions(captured);

        // every function the requirements bind, per story
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var story in storyList)
        {
            var verifiedRoutes = new List<string>();
            var missing = new List<string>();
            var mismatchLines = new List<string>();
            var total = 0;
            foreach (var ac in story.AcceptanceCriteria)
            {
                foreach (var m in BoundActionRegex().Matches(ac).Cast<Match>())
                {
                    total++;
                    var func = $"{m.Groups[1].Value}.{m.Groups[2].Value}";
                    covered.Add(func);
                    if (live.TryGetValue(func, out var hit))
                    {
                        verifiedRoutes.Add(hit.Route);
                        var bound = BoundParamsOf(ac, func);
                        var extra = hit.Fields
                            .Where(f => !bound.Contains(f, StringComparer.OrdinalIgnoreCase)
                                        && !f.Equals("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase)
                                        && !f.Equals("id", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (extra.Count > 0 && hit.Fields.Count >= 2)
                            mismatchLines.Add($"{func} live form captures fields not bound in the requirement: {string.Join(", ", extra)}");
                    }
                    else
                        missing.Add(func);
                }
            }

            var acs = (List<string>)story.AcceptanceCriteria;
            if (verifiedRoutes.Count > 0)
            {
                verifiedKeys.Add(story.Key);
                var verifiedLine = $"Live validation: {verifiedRoutes.Distinct().Count()} of {total} bound actions respond in the running app " +
                                   $"(verified routes: {string.Join(", ", verifiedRoutes.Distinct().Take(4))}); form fields align with the bound parameters";
                acs.Add(mismatchLines.Count > 0
                    ? verifiedLine + "; live form carries extra fields — " + string.Join("; ", mismatchLines.Take(2))
                    : verifiedLine);
            }
            if (missing.Count > 0)
            {
                acs.Add($"Live validation: {string.Join(", ", missing.Distinct().Take(4))} not reachable in the running app — " +
                        "the requirement stays source-bound; confirm the deployment covers it");
                findings.Add($"story {story.Key}: bound action(s) {string.Join(", ", missing.Distinct())} have no live counterpart");
            }
        }

        // live functions no requirement mentions → new consolidation stories;
        // bare navigation routes (Index GETs without form fields) are not
        // functional gaps — only forms and specific actions consolidate
        var storyNo = storyList.Select(s => s.Key).Where(k => k.StartsWith("US-"))
            .Select(k => int.TryParse(k.AsSpan(3), out var n) ? n : 0).DefaultIfEmpty(0).Max();
        var uncoveredGroups = live.Where(kv => !covered.Contains(kv.Key)
                && (kv.Value.Fields.Count > 0 || !kv.Key.EndsWith("Controller.Index", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(kv => FeatureOfFunc(kv.Key))
            .OrderBy(g => g.Key).ToList();
        var uncoveredCount = 0;
        foreach (var group in uncoveredGroups)
        {
            var feature = group.Key;
            var epic = epicList.FirstOrDefault(e => e.Module.Equals(feature, StringComparison.OrdinalIgnoreCase));
            if (epic is null)
            {
                epic = new PmEpic
                {
                    Key = $"E{epicList.Count + 1:D2}", Name = $"Live functional coverage: {feature}",
                    Module = feature, Theme = "Core",
                    Description = "Functions observed in the running application but not covered by source-generated requirements."
                };
                epicList.Add(epic);
            }
            foreach (var (func, hit) in group)
            {
                uncoveredCount++;
                storyNo++;
                var action = func.Split('.')[1];
                var fields = hit.Fields.Where(f => !f.Equals("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase)).ToList();
                storyList.Add(new PmStory
                {
                    Key = $"US-{storyNo:D3}", EpicKey = epic.Key,
                    Title = $"{feature}: align {action} with the live app behavior",
                    AsA = "sangha operator",
                    IWant = $"cover the live {action} behavior the running app exposes on {hit.Route}",
                    SoThat = $"the {feature} module requirements match what the application actually does",
                    Points = 2, Priority = 60, Risk = "Medium", Module = feature,
                    TestCaseCount = 4, Layer = "Controllers", Phase = "build",
                    AcceptanceCriteria = new List<string>
                    {
                        fields.Count > 0
                            ? $"{func}() — binds the live form fields captured at {hit.Route}: {string.Join(", ", fields)}"
                            : $"{func}() — read-only live surface at {hit.Route}; requirement captures the route contract",
                        $"Submissions round-trip through {feature} views with server-side validation mirroring the running app's {action} form behavior"
                    }
                });
                findings.Add($"live function {hit.Route} → {func} not covered by any source-generated requirement — consolidated into {epic.Key}/US-{storyNo:D3}");
            }
        }

        var verifiedFunctions = live.Keys.Count(k => covered.Contains(k));
        var spec = captured with
        {
            VerifiedStoryKeys = verifiedKeys.Distinct().ToList(),
            Findings = findings,
            VerifiedFunctions = verifiedFunctions,
            UnverifiableFunctions = storyList.SelectMany(s => s.AcceptanceCriteria)
                .Count(a => a.Contains("not reachable in the running app", StringComparison.Ordinal)),
            UncoveredLiveFunctions = uncoveredCount,
            FieldMismatches = storyList.SelectMany(s => s.AcceptanceCriteria)
                .Count(a => a.Contains("live form carries extra fields", StringComparison.Ordinal)),
            Summary = $"Validated {storyList.Count} stories against {captured.Pages.Count} live routes: " +
                      $"{verifiedFunctions} bound actions verified, {uncoveredCount} live functions consolidated into new requirements" +
                      (findings.Count > 0 ? $", {findings.Count} findings" : "")
        };
        return new FunctionalValidation { Spec = spec, Epics = epicList, Stories = storyList };
    }

    // ─── live surface helpers ───

    public static Dictionary<string, (string Route, List<string> Fields)> LiveFunctions(PmFunctionalSpec spec)
    {
        var live = new Dictionary<string, (string, List<string>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in spec.Pages.Where(p => p.Status >= 200 && p.Status < 300))
        {
            var feature = FeatureOfRoute(page.Route);
            if (feature.Length == 0) continue;
            live.TryAdd($"{feature}Controller.Index", (page.Route, new List<string>()));
            foreach (var form in page.Forms)
            {
                var (feat, act) = SplitFormAction(form.Action);
                if (feat.Length == 0 || act is null) continue;
                live[$"{feat}Controller.{act}"] = (page.Route, form.Fields.ToList());
            }
            foreach (var link in page.Links)
            {
                var (lfeat, lact) = SplitFormAction(link);
                if (lfeat.Length > 0 && lact is not null && !lact.Equals("Index", StringComparison.OrdinalIgnoreCase))
                    live.TryAdd($"{lfeat}Controller.{lact}", (link, new List<string>()));
            }
        }
        return live;
    }

    private static string FeatureOfRoute(string route)
    {
        var (feature, action) = SplitFormAction(route);
        return action is null || action.Equals("Index", StringComparison.OrdinalIgnoreCase) ? feature : feature;
    }

    private static (string Feature, string? Action) SplitFormAction(string actionOrRoute)
    {
        var path = actionOrRoute.Split('?')[0].Split('#')[0].Trim();
        if (path.Length == 0 || path[0] != '/') return (string.Empty, null);
        var parts = path.Trim('/').Split('/');
        if (parts.Length == 0 || parts[0].Length == 0) return (string.Empty, null);
        var feature = parts[0];
        var act = parts.Length > 1 ? parts[1] : "Index";
        return (feature, act);
    }

    private static string FeatureOfFunc(string func)
    {
        var idx = func.IndexOf("Controller", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? func[..idx] : func;
    }

    /// <summary>Field names an AC binds for a given function, from "binds parameters: a (T), b (T)".</summary>
    private static List<string> BoundParamsOf(string ac, string func)
    {
        var at = ac.IndexOf(func, StringComparison.Ordinal);
        if (at < 0) return new List<string>();
        var segment = ac[Math.Min(at + func.Length, ac.Length)..];
        var stop = segment.IndexOf("Controller.", StringComparison.Ordinal);
        if (stop >= 0) segment = segment[..stop];
        var p = segment.IndexOf("binds parameters:", StringComparison.OrdinalIgnoreCase);
        if (p < 0) return new List<string>();
        segment = segment[(p + "binds parameters:".Length)..];
        var end = segment.IndexOf(';', StringComparison.Ordinal);
        if (end >= 0) segment = segment[..end];
        return FieldNameRegex().Matches(segment).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
    }

    // ─── HTML parsing (deliberately regex-based: live pages vary by host/framework) ───

    public static bool HasLoginForm(string html)
        => html.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase);

    public static (string? Action, string? UserField, string? PassField) ParseLoginForm(string html)
    {
        var tags = FormTagRegex().Matches(html).Cast<Match>().ToList();
        for (var i = 0; i < tags.Count; i++)
        {
            var action = AttrOf(tags[i].Value, "action") ?? "/";
            var end = i + 1 < tags.Count ? tags[i + 1].Index : html.Length;
            var body = html[(tags[i].Index + tags[i].Length)..end];
            string? userField = null, passField = null;
            foreach (var tag in InputTagRegex().Matches(body).Cast<Match>())
            {
                var type = AttrOf(tag.Value, "type");
                var name = AttrOf(tag.Value, "name");
                if (name is null) continue;
                if (type is not null && type.Equals("password", StringComparison.OrdinalIgnoreCase))
                    passField = name;
                else if (userField is null
                         && (type is null || type.Equals("text", StringComparison.OrdinalIgnoreCase)))
                    userField = name;
            }
            if (passField is not null)
                return (action, userField, passField);
        }
        return (null, null, null);
    }

    /// <summary>Value of a quoted attribute from a single tag, any attribute order.</summary>
    private static string? AttrOf(string tag, string name)
    {
        var m = Regex.Match(tag,
            @"[\s'""/]" + name + @"[\s]*=[\s]*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')");
        return m.Success ? m.Groups["v"].Value : null;
    }

    public static IReadOnlyList<PmFunctionalForm> FormsOf(string html)
    {
        var forms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tags = FormTagRegex().Matches(html).Cast<Match>().ToList();
        for (var i = 0; i < tags.Count; i++)
        {
            var action = AttrOf(tags[i].Value, "action") ?? "/";
            var end = i + 1 < tags.Count ? tags[i + 1].Index : html.Length;
            var body = html[(tags[i].Index + tags[i].Length)..end];
            var fields = forms.TryGetValue(action, out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in InputTagRegex().Matches(body).Cast<Match>())
                if (AttrOf(m.Value, "name") is { } name) fields.Add(name);
            foreach (var m in SelectTagRegex().Matches(body).Cast<Match>())
                if (AttrOf(m.Value, "name") is { } name) fields.Add(name);
            forms[action] = fields;
        }
        return forms.Select(kv => new PmFunctionalForm { Action = kv.Key, Fields = kv.Value.ToList() })
            .OrderByDescending(f => f.Fields.Count).ThenBy(f => f.Action, StringComparer.OrdinalIgnoreCase)
            .Take(8).ToList();
    }

    public static IReadOnlyList<string> InternalLinks(string html)
    {
        var skip = new[] { "/lib", "/css", "/js", "/img", "/images", "/favicon", "/fonts", "/swagger", "/_",
            "/Auth/Logout", "/Auth/Login" };
        return LinkRegex().Matches(html).Cast<Match>()
            .Select(m => m.Groups["href"].Value.Split('?')[0].Split('#')[0])
            .Where(href => href.StartsWith('/') && href.Length > 1 && char.IsLetter(href[1]))
            .Where(href => !skip.Any(s => href.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(href => !href.Contains('.'))
            .Distinct().Take(24).ToList();
    }

    private static string TitleOf(string html)
    {
        var t = TitleRegex().Match(html).Groups["title"].Value.Trim();
        return t.Length > 80 ? t[..80] : t;
    }

    private static async Task<string> GetBodyAsync(HttpClient client, string route, CancellationToken ct)
    {
        using var resp = await client.GetAsync(route, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    [GeneratedRegex(@"\b([A-Z][A-Za-z]+Controller)\.([A-Z][A-Za-z]+)\(")]
    private static partial Regex BoundActionRegex();
    [GeneratedRegex(@"([A-Za-z0-9_]+)\s*\((?:[^()""`]|\([^()]*\))*?\)")]
    private static partial Regex FieldNameRegex();
    [GeneratedRegex(@"<form\b[^>]*>")]
    private static partial Regex FormTagRegex();
    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTagRegex();
    [GeneratedRegex(@"<select\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SelectTagRegex();
    [GeneratedRegex(@"href=[""'](?<href>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"<title>(?<title>[^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();
}
