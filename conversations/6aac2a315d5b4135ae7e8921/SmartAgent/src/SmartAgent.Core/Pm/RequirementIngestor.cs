using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core.Pm;

/// <summary>Options driving program generation.</summary>
public sealed record PmOptions
{
    public string Name { get; init; } = "Agile Delivery Programme";
    public int Years { get; init; } = 2;                  // multi-year horizon
    public int? TeamSize { get; init; }                    // default: derived from modules
    public decimal BudgetCapPerYearUsd { get; init; }      // 0 = unconstrained
    public int VelocityPerDev { get; init; } = 10;         // story points per sprint per developer
    public int SprintLengthWeeks { get; init; } = 2;
    public DateOnly? StartDate { get; init; }
}

/// <summary>
/// Agile requirement capture: turns a GitHub source snapshot (or a prompt)
/// into epics and user stories — feasible deliverable models with acceptance
/// criteria, story points, priority and risk. Deterministic heuristics keep
/// the split reproducible; every story is grounded in actual source files.
/// </summary>
public sealed partial class RequirementIngestor
{
    /// <summary>Modules detected in the source (project → file count).</summary>
    public IReadOnlyList<string> DetectModules(SourceSnapshot snapshot)
    {
        var modules = snapshot.Files
            .Select(f => ModuleOf(f.Path))
            .Where(m => !string.IsNullOrEmpty(m))
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(8)
            .ToList();
        return modules.Count > 0 ? modules : new List<string> { "Core Platform" };
    }

    /// <summary>Max class-grounded feature-slice stories per module.</summary>
    private const int MaxSliceStoriesPerModule = 4;
    /// <summary>Max file-cluster stories per module (asset-heavy modules without classes).</summary>
    private const int MaxFileStoriesPerModule = 3;
    /// <summary>Cap on the generated backlog (keeps the plan reference-grade but bounded).</summary>
    private const int MaxStories = 150;

    /// <summary>
    /// Captures the agile backlog: epics + stories from a source snapshot.
    /// When class analysis is supplied, every module is reviewed in depth:
    /// controllers, services and domain types become feature-slice stories
    /// with acceptance criteria naming their actual methods, and asset-heavy
    /// modules (views, function components, schemas) become file-grounded
    /// stories — so requirements resolve the real source, not a summary.
    /// </summary>
    public (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromSource(
        SourceSnapshot snapshot, IReadOnlyList<PmClassInfo>? classes = null, PmAppProfile? profile = null)
    {
        // capability-driven capture: the app's own controllers/hubs/entities name
        // the epics, so requirements follow core functionality, not folder names
        if (profile is { Capabilities.Count: >= 2 })
            return FromCapabilities(snapshot, profile);
        return FromModules(snapshot, classes);
    }

    /// <summary>Splits a PascalCase name into words ("LiveSession" → "Live session").</summary>
    private static string WordsOf(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1").Trim().ToLowerInvariant();

    /// <summary>The user persona a capability serves, by keyword.</summary>
    private static string PersonaOf(string feature)
    {
        var f = feature.ToLowerInvariant();
        if (f.Contains("auth") || f.Contains("login") || f.Contains("account")) return "user";
        if (f.Contains("admin") || f.Contains("govern") || f.Contains("member")
            || f.Contains("config") || f.Contains("setting") || f.Contains("role")) return "administrator";
        if (f.Contains("librar") || f.Contains("catalog") || f.Contains("collection")
            || f.Contains("scripture") || f.Contains("text")) return "curator";
        if (f.Contains("event") || f.Contains("session") || f.Contains("live")
            || f.Contains("stage") || f.Contains("chat")) return "participant";
        if (f.Contains("record") || f.Contains("archive") || f.Contains("history")) return "archivist";
        if (f.Contains("dashboard") || f.Contains("report") || f.Contains("analytic")) return "member";
        return "user";
    }

    /// <summary>Action verbs implied by a capability's real methods.</summary>
    private static string VerbsOf(IReadOnlyList<string> methods)
    {
        var verbs = new List<string>();
        foreach (var m in methods)
        {
            var verb = m.ToLowerInvariant() switch
            {
                var x when x.Contains("index") || x.Contains("list") || x.Contains("all") => "browse",
                var x when x.Contains("create") || x.Contains("add") || x.Contains("new") || x.Contains("upload") => "create",
                var x when x.Contains("edit") || x.Contains("update") => "edit",
                var x when x.Contains("delete") || x.Contains("remove") => "delete",
                var x when x.Contains("login") || x.Contains("signin") => "log in",
                var x when x.Contains("logout") || x.Contains("signout") => "log out",
                var x when x.Contains("switch") => "switch users",
                var x when x.Contains("search") || x.Contains("find") || x.Contains("filter") => "search",
                var x when x.Contains("view") || x.Contains("detail") || x.Contains("show") || x.Contains("get") => "view",
                var x when x.Contains("join") || x.Contains("connect") => "join",
                var x when x.Contains("send") || x.Contains("post") || x.Contains("broadcast") => "send",
                var x when x.Contains("plan") || x.Contains("schedule") => "schedule",
                var x when x.Contains("import") => "import",
                var x when x.Contains("export") => "export",
                _ => null
            };
            if (verb != null && !verbs.Contains(verb)) verbs.Add(verb);
            if (verbs.Count >= 5) break;
        }
        if (verbs.Count == 0) return "use";
        return verbs.Count == 1 ? verbs[0]
            : string.Join(", ", verbs[..^1]) + " and " + verbs[^1];
    }

    /// <summary>The domain noun a capability works on: its entities, else its feature name.</summary>
    private static string NounOf(PmCapability cap) =>
        cap.Entities.Count > 0
            ? string.Join(", ", cap.Entities.Take(2).Select(e => WordsOf(e) + "s"))
            : WordsOf(cap.Feature) + "s";

    /// <summary>Capability-driven backlog: every epic is one core capability of
    /// the app (its controller/hub/entities), every story names the real
    /// action methods, views and domain entities from the source.</summary>
    private (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromCapabilities(
        SourceSnapshot snapshot, PmAppProfile profile)
    {
        var epics = new List<PmEpic>();
        var stories = new List<PmStory>();
        var storyNo = 0;
        void AddEpic(PmEpic e) { epics.Add(e); }
        void AddStory(PmEpic epic, string title, string asA, string want, string soThat,
            int points, int priority, string risk, string[] ac,
            string layer = "", string phase = "build", string module = "")
        {
            storyNo++;
            stories.Add(new PmStory { Key = $"US-{storyNo:D3}", EpicKey = epic.Key, Title = title,
                AsA = asA, IWant = want, SoThat = soThat, Points = points, Priority = priority,
                Risk = risk, Module = module.Length > 0 ? module : epic.Module,
                TestCaseCount = Math.Max(2, points * 2),
                AcceptanceCriteria = ac, Layer = layer, Phase = phase });
        }
        var appWords = WordsOf(profile.AppName);
        var platforms = string.Join(", ", profile.Platforms);
        var persistedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── E01 foundation, grounded in the app's own identity ──
        var foundation = new PmEpic { Key = "E01", Name = "Foundation & platform",
            Module = profile.Root, Theme = "Foundation",
            Description = $"Bootstrap {profile.AppName}: solution skeleton, authentication and tooling"
                + (profile.Purpose.Length > 0 ? $" — {profile.Purpose}" : "")
                + (platforms.Length > 0 ? $" Platform: {platforms}." : "") };
        AddEpic(foundation);
        AddStory(foundation, $"{profile.AppName} solution bootstrap & repository structure",
            "developer", "a versioned, buildable solution skeleton with CI",
            "every squad starts from green", 8, 100, "Medium",
            new[] { "Solution builds cleanly on the target framework", "CI pipeline green on main",
                    "README documents the build and run steps" },
            "Framework", "framework");

        var authCap = profile.Capabilities.FirstOrDefault(c =>
            c.Feature.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || c.Feature.Contains("login", StringComparison.OrdinalIgnoreCase)
            || c.Feature.Contains("account", StringComparison.OrdinalIgnoreCase));
        if (authCap != null)
            AddStory(foundation, $"Authentication & access control ({authCap.Class})",
                "user", $"working sign-in flows: {VerbsOf(authCap.Methods)}",
                "access to the platform is governed end to end", 13, 95, "High",
                new[] { string.Join(", ", authCap.Methods.Take(4)) + " all behave per the source contract",
                        "Sessions/roles enforced server-side on every guarded action",
                        "Invalid credentials and expired sessions redirect safely" },
                "Framework", "framework");
        else
            AddStory(foundation, "Authentication & user management",
                "product owner", "authentication with role-based access for all user personas",
                "access is governed end to end", 13, 95, "High",
                new[] { "Login/logout works for all roles", "Roles enforced server-side", "Password reset flow" },
                "Framework", "framework");
        AddStory(foundation, "Base framework code generation & tooling",
            "developer", "scaffolding, shared libraries and code-generation templates",
            "every module is generated from the same base", 8, 92, "Medium",
            new[] { "Module templates generate a working vertical slice",
                    "Shared validation, logging and error handling are wired in",
                    "Generated code passes lint and build gates" },
            "Framework", "framework");

        // ── one epic per core capability, stories grounded in its real source ──
        var priority = 90;
        foreach (var cap in profile.Capabilities)
        {
            if (cap == authCap) continue;    // shipped in the foundation epic
            if (stories.Count >= MaxStories) break;
            var feature = cap.Feature;
            var featureWords = WordsOf(feature);
            var noun = NounOf(cap);
            var persona = PersonaOf(feature);
            var verbs = cap.Methods.Count > 0 ? VerbsOf(cap.Methods) : "manage";
            var evidence = (cap.Class.Length > 0 ? $"{cap.Class} (" + string.Join(", ", cap.Methods.Take(4)) + ")" : "")
                + (cap.Views.Count > 0 ? $"; views: {string.Join(", ", cap.Views)}" : "")
                + (cap.Entities.Count > 0 ? $"; entities: {string.Join(", ", cap.Entities)}" : "");
            var epic = new PmEpic { Key = $"E{epics.Count + 1:D2}",
                Name = char.ToUpperInvariant(featureWords[0]) + featureWords[1..],
                Module = feature, Theme = cap.Views.Count > 0 ? "UI" : "Core",
                Description = $"{char.ToUpperInvariant(featureWords[0]) + featureWords[1..]} capability of {profile.AppName}"
                    + (evidence.Length > 0 ? $" — resolved from source: {evidence}" : "") };
            AddEpic(epic);

            if (cap.Kind == "Hub")
            {
                AddStory(epic, $"{feature} real-time sync",
                    persona, $"live {featureWords} state broadcast to every connected client",
                    $"all participants see the same {featureWords} state at once",
                    ClampFibonacci(5 + cap.Methods.Count), priority, "High",
                    new[] { $"{cap.Class} broadcasts state changes to all connected clients",
                            "Late joiners and reconnects receive the current state",
                            "Concurrent updates stay consistent across clients" },
                    "Server");
            }
            else if (cap.Kind is "Entities" or "Services")
            {
                foreach (var entity in cap.Entities.Take(3))
                {
                    if (!persistedEntities.Add(entity)) continue;
                    var detail = cap.EntityDetails.FirstOrDefault(d => d.Name == entity);
                    var fields = detail != null ? string.Join(", ", detail.Fields) : "";
                    AddStory(epic, $"{entity} persistence & invariants",
                        persona, $"{WordsOf(entity)}s stored with enforced validation",
                        $"{appWords} data stays consistent", ClampFibonacci(3), Math.Max(20, priority - 5), "Medium",
                        new[] { fields.Length > 0
                                ? $"{entity} fields ({fields}) round-trip through the data adapter identically"
                                : $"{entity} round-trips through persistence identically",
                                "Constraint violations surface as validation errors",
                                $"Consumers of {entity} validated against its contract" },
                        cap.Kind == "Entities" ? "Data Adapter" : "Server");
                }
            }
            else
            {
                // end-to-end flows through the controller's real actions,
                // one specific requirement per action, wired to its view & fields
                var acs = new List<string>();
                var primary = cap.EntityDetails.FirstOrDefault();
                var fieldList = primary != null ? string.Join(", ", primary.Fields.Take(6)) : "";
                foreach (var m in cap.Methods.Take(6))
                {
                    var view = cap.ViewDetails.FirstOrDefault(v =>
                        Path.GetFileNameWithoutExtension(v.Name).Equals(m, StringComparison.OrdinalIgnoreCase));
                    var req = $"{cap.Class}.{m}()";
                    if (cap.MethodParams.TryGetValue(m, out var realParams) && realParams.Count > 0)
                        req += $" — binds parameters: {string.Join(", ", realParams.Take(8))}";
                    if (view != null)
                        req += $"; pairs with view {feature}/{view.Name}"
                            + (view.Fields.Count > 0 ? $" (fields: {string.Join(", ", view.Fields.Take(5))})" : "");
                    else if (cap.ViewDetails.Count > 0)
                        req += $"; paired with the {feature} views per the source layout";
                    if (fieldList.Length > 0 && (m.StartsWith("Create", StringComparison.OrdinalIgnoreCase)
                        || m.StartsWith("Edit", StringComparison.OrdinalIgnoreCase)
                        || m.StartsWith("Add", StringComparison.OrdinalIgnoreCase)
                        || m.StartsWith("Save", StringComparison.OrdinalIgnoreCase)))
                        req += $"; persists entity fields: {fieldList}";
                    acs.Add(req);
                }
                acs.Add("Authorization and model validation enforced on every action; error paths return correct status codes");
                AddStory(epic, $"{feature}: {verbs} {noun} end to end",
                    persona, $"the {featureWords} workflows ({verbs}) working per the source behavior",
                    $"{appWords} delivers the {featureWords} workflow end to end",
                    ClampFibonacci(2 + cap.Methods.Count), priority,
                    cap.Methods.Count > 4 ? "High" : "Medium",
                    acs.ToArray(), "Controllers");

                foreach (var entity in cap.Entities.Take(2))
                {
                    if (!persistedEntities.Add(entity)) continue;
                    var detail = cap.EntityDetails.FirstOrDefault(d => d.Name == entity);
                    var fields = detail != null ? string.Join(", ", detail.Fields) : "";
                    var required = detail != null && detail.RequiredFields.Count > 0
                        ? string.Join(", ", detail.RequiredFields.Take(5))
                        : "";
                    var sizeAcs = detail != null && detail.FieldSizes.Count > 0
                        ? detail.FieldSizes.Take(6)
                            .Select(kv => $"{entity}.{kv.Key} sized exactly: {kv.Value}").ToList()
                        : new List<string>();
                    var acList = new List<string>();
                    if (fields.Length > 0)
                        acList.Add($"{entity} fields ({fields}) round-trip through the data adapter identically");
                    else
                        acList.Add($"{entity} round-trips through persistence identically");
                    if (required.Length > 0)
                        acList.Add($"Required fields ({required}) enforced: null/empty submissions rejected with validation errors");
                    else
                        acList.Add("Constraint violations surface as validation errors");
                    acList.AddRange(sizeAcs);
                    acList.Add("Seed data covers representative records");
                    AddStory(epic, $"{entity} persistence & invariants",
                        persona, $"{WordsOf(entity)}s stored with enforced validation, field sizes and constraints",
                        $"{appWords} data stays consistent", ClampFibonacci(3), Math.Max(20, priority - 3), "Medium",
                        acList.ToArray(), "Data Adapter");
                }

                // settings & configuration: config-type entities drive other modules'
                // behavior — state per setting where it is edited and which module consumes it
                var settingEntity = cap.Entities.FirstOrDefault(e =>
                    Regex.IsMatch(e, "config|setting|preference|option", RegexOptions.IgnoreCase));
                if (settingEntity != null)
                {
                    var sDetail = cap.EntityDetails.FirstOrDefault(d => d.Name == settingEntity);
                    if (sDetail != null)
                    {
                        var usage = new SourceAnalyzer().SettingUsageOf(snapshot, sDetail);
                        var editorMethod = cap.Methods.FirstOrDefault(m =>
                            Regex.IsMatch(m, "UpdateConfig|SaveConfig|Setting|Config", RegexOptions.IgnoreCase))
                            ?? cap.Methods.FirstOrDefault();
                        var settingAcs = new List<string>();
                        var usageAcs = usage.GroupBy(u => u.Field).Take(8).Select(g =>
                            $"{settingEntity}.{g.Key} setting — edited in the {feature} screens, persisted via {cap.Class}.{editorMethod}(), "
                            + $"consumed by {string.Join(", ", g.Select(x => x.Module).Distinct())} "
                            + $"(e.g. {Path.GetFileName(g.First().File)}) to drive that module's runtime behavior").ToList();
                        settingAcs.AddRange(usageAcs);
                        var unused = sDetail.Fields.Where(f =>
                            !usage.Any(u => f.StartsWith(u.Field + " ", StringComparison.Ordinal))).Take(6);
                        settingAcs.AddRange(unused.Select(f =>
                        {
                            var col = f[..f.LastIndexOf(" (", StringComparison.Ordinal)];
                            return $"{settingEntity}.{col} persisted and exposed for runtime consumption (no consumer found in the source scan)";
                        }));
                        settingAcs.Add("Setting changes take effect without redeploying; invalid values rejected at the {feature} boundary");
                        AddStory(epic, $"{feature} settings & configuration ({settingEntity})",
                            "admin", $"every {settingEntity} setting wired to the module behavior it drives",
                            $"{appWords} behavior stays configurable without code changes",
                            ClampFibonacci(3 + usage.Count / 3), Math.Max(20, priority - 2), "Medium",
                            settingAcs.ToArray(), "Controllers");
                    }
                }

                if (cap.Views.Count > 0)
                {
                    // one specific requirement per view: its real form fields and wired behaviors
                    var viewAcs = new List<string>();
                    foreach (var v in cap.ViewDetails.Take(4))
                    {
                        var req = $"{feature}/{v.Name}";
                        if (v.Fields.Count > 0) req += $" — fields: {string.Join(", ", v.Fields)}";
                        if (v.Behaviors.Count > 0) req += $"; behaviors: {string.Join(", ", v.Behaviors)}";
                        viewAcs.Add(req);
                    }
                    var missing = cap.Views.Where(vn => !cap.ViewDetails.Any(vd => Path.GetFileNameWithoutExtension(vd.Name) == vn)).ToList();
                    if (missing.Count > 0)
                        viewAcs.Add($"{string.Join(", ", missing)} render with model validation per the source markup");
                    viewAcs.Add("Auth guards every screen and action; navigation and error states handled");
                    AddStory(epic, $"{feature} screens ({string.Join(", ", cap.Views.Take(4))})",
                        persona, $"the {featureWords} screens rendered per the source views",
                        $"{persona}s can complete every {featureWords} task from the UI",
                        ClampFibonacci(2 + cap.Views.Count), Math.Max(20, priority - 4), "Medium",
                        viewAcs.ToArray(), "Frontend");
                }

                if (cap.Entities.Count > 0)
                {
                    var ddlAcs = new List<string>();
                    foreach (var d in cap.EntityDetails.Take(3))
                    {
                        var cols = d.Fields.Select(f =>
                        {
                            var col = f[..f.LastIndexOf(" (", StringComparison.Ordinal)];
                            return d.FieldSizes.TryGetValue(col, out var size)
                                ? $"{f} [{size}]"
                                : f;
                        }).ToList();
                        ddlAcs.Add($"{d.Name} → table with columns {string.Join(", ", cols)} "
                            + "(dual-provider DDL: SQL Server + Oracle, per the source schema)");
                    }
                    ddlAcs.Add($"Seed data idempotent for {string.Join(", ", cap.Entities)}; rollback scripts documented");
                    AddStory(epic, $"{feature} schema, migrations & seeds",
                        "database admin", $"versioned DDL and seeds for the {featureWords} tables",
                        $"{featureWords} environments rebuild deterministically", 5, Math.Max(20, priority - 6), "Medium",
                        ddlAcs.ToArray(), "Database");
                }

                AddStory(epic, $"{feature} integration & contract tests",
                    "qa lead", $"an integration suite proving the {featureWords} contracts end to end",
                    $"{featureWords} regressions surface before release", 5, Math.Max(15, priority - 8), "Medium",
                    new[] { $"{cap.Class} contracts asserted against consumers",
                            "Critical paths integration-tested in CI",
                            "Contract drift breaks the build" }, "Server");
            }
            priority = Math.Max(20, priority - 8);
        }

        // ── secondary source roots: consolidate, don't feature-ify ──
        foreach (var (root, files) in profile.OtherRoots)
        {
            if (stories.Count >= MaxStories) break;
            var epic = new PmEpic { Key = $"E{epics.Count + 1:D2}", Name = $"{root} platform consolidation",
                Module = root, Theme = "Core",
                Description = $"Consolidate the {root} source root ({files} files) into the primary "
                    + $"{profile.AppName} platform — replace what is legacy, keep what is load-bearing" };
            AddEpic(epic);
            AddStory(epic, $"{root} audit: what is live, legacy or duplicated",
                "solution architect", $"an inventory of every {root} capability still in use",
                "the consolidation is evidence-based, not guesswork", 5, 45, "Medium",
                new[] { $"All {root} entry points and consumers inventoried",
                        "Live vs replaceable parts classified with evidence",
                        "Findings reviewed with the product owner" }, "Server");
            AddStory(epic, $"Migrate live {root} capabilities into {profile.AppName}",
                "developer", $"in-use {root} functionality delivered by the primary platform",
                "one platform serves every user", 13, 42, "High",
                new[] { $"Live {root} features reachable in the primary app",
                        "Data migrated losslessly where applicable",
                        "Cutover verified with parity tests" }, "Server");
            AddStory(epic, $"Decommission retired {root} surface",
                "operations", $"dead {root} code paths removed with regression cover",
                "maintenance cost drops", 8, 38, "Medium",
                new[] { $"Retired {root} paths deleted", "No consumer references remain",
                        "Regression suite green after removal" }, "Server");
        }

        // ── enhancement wave: the repository's own commit patterns, mapped to capabilities ──
        var patterns = new SourceAnalyzer().ChangePatterns(snapshot).Take(10).ToList();
        var enhanceEpic = new PmEpic { Key = $"E{epics.Count + 1:D2}", Name = "Field & enhancement waves",
            Module = profile.Root, Theme = "Core",
            Description = $"Year-2 enhancement waves derived from the repository's own commit history "
                + $"({snapshot.History.Count} commits reviewed), each mapped to the capability it evolves" };
        AddEpic(enhanceEpic);
        var enhancePriority = 52;
        var seenHeadlines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns.Where(p => p.Commits >= 1
                     && !p.Headline.Trim().Equals("Initial commit", StringComparison.OrdinalIgnoreCase)
                     && !p.Headline.Trim().Equals("initial commit", StringComparison.OrdinalIgnoreCase)))
        {
            if (stories.Count >= MaxStories) break;
            var headline0 = pattern.Headline.Length > 70 ? pattern.Headline[..67] + "..." : pattern.Headline;
            if (!seenHeadlines.Add(headline0)) continue;
            // map the pattern's touched paths to the capability it evolves
            var capOfPattern = profile.Capabilities.FirstOrDefault(c =>
                pattern.Examples.Any(x => x.Contains($"/{c.Feature}", StringComparison.OrdinalIgnoreCase)
                    || x.Contains(c.Feature, StringComparison.OrdinalIgnoreCase)))
                ?? profile.Capabilities.FirstOrDefault(c => c.Root.Equals(pattern.Module, StringComparison.OrdinalIgnoreCase));
            var target = capOfPattern != null ? WordsOf(capOfPattern.Feature) : pattern.Module.ToLowerInvariant();
            var headline = pattern.Headline.Length > 70 ? pattern.Headline[..67] + "..." : pattern.Headline;
            AddStory(enhanceEpic, $"Enhance {target}: {headline}",
                "user", $"the '{headline}' change pattern carried into the rebuild",
                $"{target} keeps evolving the way the source did", ClampFibonacci(3 + pattern.Commits),
                Math.Max(30, enhancePriority), "Medium",
                new[] { $"Change wave mirrors the source pattern '{headline}' ({pattern.Commits} commits touched {pattern.Layer.ToLowerInvariant()} code)",
                        $"Touched paths covered: {string.Join(", ", pattern.Examples.Take(2))}",
                        LayerComponentsOf(pattern.Examples),
                        $"Regression tests prove the enhanced {pattern.Layer.ToLowerInvariant()} behavior" },
                pattern.Layer, "enhance", capOfPattern?.Feature ?? pattern.Module);
            enhancePriority -= 2;
        }
        // history-grounded field additions: fields the source repo itself added over
        // its commit history — each becomes one requirement spanning data model,
        // controller actions, database and views, exactly as the source evolved
        var profileEntities = profile.Capabilities.SelectMany(c => c.Entities)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fieldHistory = new SourceAnalyzer().FieldHistoryPatterns(snapshot)
            .Where(f => !f.Field.StartsWith("Dispose") && f.Field != "Id") // lifecycle noise / key columns
            .Where(f => !Regex.IsMatch(f.Entity,
                @"ViewModel$|Dto$|Options$|Result$|Error|Program$|Startup$|Model$"))
            .GroupBy(f => (f.Entity, f.Field)).Select(g => g.First()) // distinct entity.field
            .OrderByDescending(f => profileEntities.Contains(f.Entity)) // real entities first
            .ThenByDescending(f => f.Commits)
            .Take(6).ToList();
        foreach (var fh in fieldHistory)
        {
            if (stories.Count >= MaxStories) break;
            var capOfField = profile.Capabilities.FirstOrDefault(c =>
                    c.Entities.Contains(fh.Entity, StringComparer.OrdinalIgnoreCase))
                ?? profile.Capabilities.FirstOrDefault(c =>
                    c.Feature.Equals(fh.Module, StringComparison.OrdinalIgnoreCase));
            var module = capOfField?.Feature ?? fh.Module;
            var actionList = capOfField != null
                ? capOfField.Methods.Where(m =>
                      Regex.IsMatch(m, "Create|Edit|Add|Save|Update", RegexOptions.IgnoreCase)).Take(3).ToList()
                : new List<string>();
            var bindingActions = actionList.Count > 0
                ? string.Join(", ", actionList.Select(m => $"{capOfField!.Class}.{m}()"))
                : $"the {module} create/edit/save action methods";
            var sizeTxt = fh.Type.Contains("string", StringComparison.OrdinalIgnoreCase)
                ? $"{fh.Field} ({fh.Type}, max 255 chars unless declared otherwise)" : $"{fh.Field} ({fh.Type})";
            AddStory(enhanceEpic, $"Add field {fh.Field} to {fh.Entity} in {module}",
                "user", $"the {fh.Field} addition the source itself made, carried through every layer",
                $"{module} evolves exactly the way the source history shows",
                5, Math.Max(30, enhancePriority), "Medium",
                new[] { $"Data model: extend {fh.Entity} with {sizeTxt} (mirrors the source's own '{fh.Field}' addition in {Path.GetFileName(fh.File)})",
                        $"Controller: modify {module} action methods ({bindingActions}) to bind and validate {fh.Field} per the source binding pattern",
                        $"Database: ALTER TABLE {fh.Entity} ADD {fh.Field} column via versioned, dual-provider migration (SQL Server + Oracle)",
                        $"Views: the {module} forms render and submit {fh.Field} with client validation mirroring server rules",
                        $"Grounding: the source repository added this field in its own history ({fh.Commits} commit(s), last seen {fh.Last:yyyy-MM-dd})" },
                "Data Adapter", "enhance", module);
            enhancePriority -= 2;
        }
        foreach (var cap in profile.Capabilities.Where(c => c.EntityDetails.Count > 0).Take(3))
        {
            if (stories.Count >= MaxStories) break;
            // concrete fields the source model lacks, derived from the entity's own columns
            var detail = cap.EntityDetails[0];
            var existing = detail.Fields.Select(f => f.Split(' ')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = new[] { "CreatedAt (DateTime)", "UpdatedAt (DateTime)", "Status (string)", "DisplayOrder (int)", "Tags (string)" };
            var newFields = candidates.Where(c => !existing.Contains(c.Split(' ')[0])).Take(2).ToList();
            if (newFields.Count == 0) continue;
            var fieldsTxt = string.Join(", ", newFields.Select(f => f.Split(' ')[0]));
            var viewsTxt = cap.ViewDetails.Count > 0
                ? string.Join(", ", cap.ViewDetails.Select(v => $"{cap.Feature}/{v.Name}").Take(3))
                : $"the {WordsOf(cap.Feature)} screens";
            var baseP = Math.Max(30, enhancePriority);
            // enhancement wave split into per-layer components so sprints carry
            // explicit frontend/server/database work: database first, then server, then frontend
            AddStory(enhanceEpic, $"{WordsOf(cap.Feature)}: add fields {fieldsTxt} — database migration",
                "database admin", $"ALTER TABLE {detail.Name} ADD {fieldsTxt} via versioned migration",
                $"the {WordsOf(cap.Feature)} data model grows without schema drift", 3, baseP, "Medium",
                new[] { $"Dual-provider DDL (SQL Server + Oracle): ALTER TABLE {detail.Name} ADD {string.Join(", ", newFields)}",
                        $"Columns nullable-safe for existing {detail.Name} rows; migration idempotent and reversible",
                        $"Rollback script verified: dropping {fieldsTxt} restores the source schema" },
                "Database", "enhance", cap.Feature);
            AddStory(enhanceEpic, $"{WordsOf(cap.Feature)}: add fields {fieldsTxt} — server & data adapter",
                "developer", $"data adapter mapping, validation and contracts updated for {fieldsTxt} on {detail.Name}",
                $"server-side {WordsOf(cap.Feature)} code serves the new fields end to end", 3, Math.Max(25, baseP - 1), "Medium",
                new[] { $"Data adapter maps {string.Join(", ", newFields)} onto {detail.Name} (persistence round-trip verified)",
                        $"Server validation for {fieldsTxt}: values conform to their CLR types before persistence",
                        $"Service/API contract for {WordsOf(cap.Feature)} exposes {fieldsTxt}; contract tests updated" },
                "Data Adapter", "enhance", cap.Feature);
            AddStory(enhanceEpic, $"{WordsOf(cap.Feature)}: add fields {fieldsTxt} — frontend forms & views",
                "user", $"{viewsTxt} display and edit {fieldsTxt}",
                $"{WordsOf(cap.Feature)} users see the enriched data in the UI", 5,
                Math.Max(22, baseP - 2), "Medium",
                new[] { $"{viewsTxt} render and bind {fieldsTxt} per the source markup patterns",
                        $"Client-side validation of {fieldsTxt} mirrors the server rules",
                        $"Existing {WordsOf(cap.Feature)} views degrade gracefully for rows without {fieldsTxt}" },
                "Frontend", "enhance", cap.Feature);
            enhancePriority -= 6;
        }

        // ── data & persistence epic ──
        var dataEpic = new PmEpic { Key = $"E{epics.Count + 1:D2}", Name = "Data & persistence",
            Module = profile.Root, Theme = "Data",
            Description = "Schema management, migrations, seed data and database scripts across the platform"
                + (platforms.Length > 0 ? $" (platform: {platforms})" : "") };
        AddEpic(dataEpic);
        AddStory(dataEpic, "Database schema, migrations & seed data",
            "database admin", "versioned migrations with seeded reference data",
            "environments can be rebuilt deterministically", 8, 60, "Medium",
            new[] { "Migrations run on the platform database", "Seed data idempotent", "Rollback documented" },
            "Database", "build");

        // ── QA epic ──
        var qaEpic = new PmEpic { Key = $"E{epics.Count + 1:D2}", Name = "Testing & QA",
            Module = profile.Root, Theme = "QA",
            Description = $"Test strategy: unit, integration and acceptance coverage per capability of {profile.AppName}" };
        AddEpic(qaEpic);
        AddStory(qaEpic, "Automated test suites per capability",
            "qa lead", "unit + integration suites wired into CI",
            "regressions surface before release", 8, 55, "Medium",
            new[] { "Unit tests per capability", "Integration tests for critical paths", "Coverage reported in CI" },
            "Server", "build");

        // ── release epic ──
        var relEpic = new PmEpic { Key = $"E{epics.Count + 1:D2}", Name = "Hardening & release",
            Module = profile.Root, Theme = "Release",
            Description = $"Performance hardening, security review, documentation and the GA release train for {profile.AppName}" };
        AddEpic(relEpic);
        AddStory(relEpic, "Security review & performance hardening",
            "operations", "security pass and load-tested release candidates",
            "the platform is production-ready", 13, 40, "High",
            new[] { "Security checklist cleared", "Load test meets SLO", "Runbook published" },
            "Framework", "release");
        AddStory(relEpic, "GA release & operations handover",
            "operations", "GA release with monitoring and handover documentation",
            "operations can run the platform", 5, 35, "Medium",
            new[] { "GA build signed off", "Monitoring dashboards live", "Handover doc accepted" },
            "Framework", "release");

        return (epics, stories);
    }

    /// <summary>Splits a change pattern's touched paths into explicit
    /// frontend / server / database component requirements for the sprint.</summary>
    private static string LayerComponentsOf(IReadOnlyList<string> examples)
    {
        var fe = new List<string>(); var srv = new List<string>(); var db = new List<string>();
        foreach (var ex in examples.Take(6))
        {
            if (ex.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                fe.Add(Path.GetFileName(ex));
            else if (ex.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".java", StringComparison.OrdinalIgnoreCase)
                || ex.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                srv.Add(Path.GetFileName(ex));
            else if (ex.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                || ex.Contains("migration", StringComparison.OrdinalIgnoreCase))
                db.Add(Path.GetFileName(ex));
        }
        var parts = new List<string>();
        if (srv.Count > 0) parts.Add($"Server: {string.Join(", ", srv.Take(3))}");
        if (fe.Count > 0) parts.Add($"frontend: {string.Join(", ", fe.Take(3))}");
        if (db.Count > 0) parts.Add($"database: {string.Join(", ", db.Take(3))}");
        if (db.Count == 0) parts.Add("database: schema unchanged or migration not required");
        return $"Sprint components — {string.Join("; ", parts)}";
    }

    /// <summary>Legacy folder-module flow (kept for sources without detectable capabilities).</summary>
    private (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromModules(
        SourceSnapshot snapshot, IReadOnlyList<PmClassInfo>? classes = null)
    {
        var modules = DetectModules(snapshot);
        var epics = new List<PmEpic>();
        var stories = new List<PmStory>();
        var epicNo = 0;
        var storyNo = 0;

        void AddEpic(string name, string module, string theme, string description)
        {
            epicNo++;
            epics.Add(new PmEpic { Key = $"E{epicNo:D2}", Name = name, Module = module,
                Theme = theme, Description = description });
        }

        void AddStory(PmEpic epic, string title, string asA, string want, string soThat,
            int points, int priority, string risk, string[] ac,
            string layer = "", string phase = "build", string module = "")
        {
            storyNo++;
            stories.Add(new PmStory { Key = $"US-{storyNo:D3}", EpicKey = epic.Key, Title = title,
                AsA = asA, IWant = want, SoThat = soThat, Points = points, Priority = priority,
                Risk = risk, Module = module.Length > 0 ? module : epic.Module,
                TestCaseCount = Math.Max(2, points * 2),
                AcceptanceCriteria = ac, Layer = layer, Phase = phase });
        }

        // E01: foundation & auth — always first
        var foundation = new PmEpic { Key = "E01", Name = "Foundation & Auth",
            Module = modules[0], Theme = "Foundation",
            Description = "Solution bootstrap, CI/CD, environments, identity and access management" };
        epicNo = 1;
        epics.Add(foundation);
        AddStory(foundation, "Solution bootstrap & repository structure",
            "developer", "a versioned solution skeleton with buildable projects and CI",
            "every squad can start from green", 8, 100, "Medium",
            new[] { "Solution builds on .NET 8", "CI pipeline green on main", "README documents build" },
            "Framework", "framework");
        AddStory(foundation, "Authentication & user management",
            "product owner", "authentication with role-based access for all user personas",
            "access is governed end to end", 13, 95, "High",
            new[] { "Login/logout works for all roles", "Roles enforced server-side", "Password reset flow" },
            "Framework", "framework");
        AddStory(foundation, "Base framework code generation & tooling",
            "developer", "scaffolding, shared libraries and code-generation templates",
            "every module is generated from the same base", 8, 92, "Medium",
            new[] { "Module templates generate a working vertical slice",
                    "Shared validation, logging and error handling are wired in",
                    "Generated code passes lint and build gates" },
            "Framework", "framework");

        // one epic per detected module: domain + in-depth source review
        var priority = 90;
        foreach (var module in modules)
        {
            if (stories.Count >= MaxStories) break;
            var moduleFiles = snapshot.Files
                .Where(f => ModuleOf(f.Path).Equals(module, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var fileCount = moduleFiles.Count;
            var isUi = moduleFiles.Any(f =>
                f.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                || f.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                || f.Path.Contains("Controller", StringComparison.OrdinalIgnoreCase)
                || f.Path.Contains("Views/", StringComparison.OrdinalIgnoreCase));
            var theme = isUi ? "UI" : "Core";
            var moduleClasses = (classes ?? Array.Empty<PmClassInfo>())
                .Where(c => c.Module.Equals(module, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Methods.Count).ThenBy(c => c.Name).ToList();
            var controllers = moduleClasses.Where(c => c.Kind is "Controller" or "Hub").ToList();
            var entities = moduleClasses.Where(c => c.Kind == "Entity").ToList();
            var services = moduleClasses.Where(c => c.Kind is "Service" or "Other" && c.Methods.Count > 0).ToList();
            var profile = moduleClasses.Count > 0
                ? $"{fileCount} source files, {moduleClasses.Count} analyzed types "
                  + $"({controllers.Count} controllers, {entities.Count} domain types, {services.Count} service types)"
                : $"{fileCount} source files";
            var layers = moduleFiles.Select(f => SourceAnalyzer.LayerOf(f.Path)).Distinct().ToList();
            AddEpic($"{module} core domain", module, theme,
                $"Implement the {module} domain model, services and contracts ({profile} in scope; "
                + $"layers: {string.Join(", ", layers)})");
            var points = ClampFibonacci((int)Math.Ceiling(fileCount / 2.0));
            var isMvc = moduleFiles.Any(f => f.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));
            // server side layer
            AddStory(epics[^1], $"{module} server-side domain & services",
                "user", $"working {module} domain logic with persistence",
                $"{module} functionality is delivered incrementally", points, priority, "Medium",
                new[] { $"Domain model for {module} implemented", $"Unit tests cover core rules", "API/UI consumes the domain" },
                "Server", "build");
            // data adapter layer
            if (layers.Contains("Data Adapter") || entities.Count > 0)
                AddStory(epics[^1], $"{module} data adapter & persistence layer",
                    "developer", $"repository/DbContext and data-access contracts for {module}",
                    "server code is decoupled from storage", ClampFibonacci(3 + entities.Count), priority - 3, "Medium",
                    new[] { $"{module} entities mapped through the data adapter",
                            "Query contracts return deterministic results",
                            "Connection and transaction handling covered by tests" },
                    "Data Adapter", "build");
            // controllers / actions layer
            if (controllers.Count > 0 || isMvc)
                AddStory(epics[^1], $"{module} controllers & actions",
                    "user", $"request handling for {module} with validation and auth guards",
                    $"{module} is reachable and safe end to end", ClampFibonacci(2 + controllers.Count), priority - 4, "High",
                    new[] { $"All {module} actions validate input and enforce auth",
                            "Action results follow the platform conventions",
                            "Error paths return correct status codes" },
                    "Controllers", "build");
            // frontend layer
            if (isUi)
                AddStory(epics[^1],
                    isMvc ? $"{module} MVC views & forms" : $"{module} UI screens & interaction",
                    "user", $"{(isMvc ? "MVC screens" : "UI screens")} for {module} with validation",
                    $"{module} is usable end to end", ClampFibonacci(points / 2), priority - 5, "Medium",
                    new[] { $"Views render with model validation", "Auth guards all actions", "Navigation and error states handled" },
                    "Frontend", "build");
            // database scripts layer
            if (layers.Contains("Database"))
                AddStory(epics[^1], $"{module} database scripts & schema",
                    "database admin", $"versioned DDL and seed scripts for {module}",
                    $"{module} environments rebuild deterministically", 5, priority - 6, "Medium",
                    new[] { $"Tables and constraints scripted for {module}",
                            "Seed data idempotent", "Rollback documented" },
                    "Database", "build");

            // ── in-depth review: class-grounded feature slices ──
            var p = priority - 2;
            foreach (var cls in controllers.Take(3).Concat(entities).Concat(services).Take(MaxSliceStoriesPerModule))
            {
                if (stories.Count >= MaxStories) break;
                var methods = cls.Methods.Count > 0 ? cls.Methods : new List<string> { "primary action" };
                var points2 = ClampFibonacci(1 + methods.Count);
                string title; string want; string[] ac;
                if (cls.Kind is "Controller" or "Hub")
                {
                    title = $"{cls.Name} endpoints & behavior";
                    want = $"{cls.Name} fully working ({string.Join(", ", methods.Take(3))})";
                    ac = new[]
                    {
                        $"{methods[0]} returns the expected result for valid input",
                        $"{methods[Math.Min(1, methods.Count - 1)]} enforces authorization and validation",
                        "Invalid input maps to 400/403 with clear error bodies"
                    };
                }
                else if (cls.Kind == "Entity")
                {
                    title = $"{cls.Name} persistence & validation";
                    want = $"{cls.Name} persisted with enforced invariants";
                    ac = new[]
                    {
                        $"{cls.Name} round-trips through persistence identically",
                        "Constraint violations surface as validation errors",
                        $"Consume sites validated against {cls.Name} contract"
                    };
                }
                else
                {
                    title = $"{cls.Name} service logic";
                    want = $"{cls.Name} rules implemented ({string.Join(", ", methods.Take(3))})";
                    ac = new[]
                    {
                        $"{methods[0]} returns the expected result for valid input",
                        "Invalid input is rejected without side effects",
                        "Repeat calls stay idempotent"
                    };
                }
                var sliceLayer = cls.Kind is "Controller" or "Hub" ? "Controllers"
                    : cls.Kind == "Entity" ? "Data Adapter" : "Server";
                AddStory(epics[^1], title, "user", want, $"{cls.Name} is production-grade",
                    points2, Math.Max(15, p), cls.Kind == "Controller" ? "High" : "Medium", ac, sliceLayer);
                p -= 2;
            }

            // ── asset-heavy modules: file-grounded stories (function components, schemas, views) ──
            if (moduleClasses.Count < 4)
                foreach (var f in moduleFiles
                             .Where(f => IsCodePath(f.Path) && !IsTestPath(f.Path))
                             .OrderByDescending(f => f.Content.Length)
                             .Take(MaxFileStoriesPerModule))
                {
                    if (stories.Count >= MaxStories) break;
                    var topic = FeatureName(f.Path);
                    AddStory(epics[^1], $"{topic} ({module}) implementation", "user",
                        $"{topic} implemented and integrated into {module}",
                        $"{module} delivers {topic.ToLowerInvariant()} end to end",
                        ClampFibonacci(3 + f.Content.Length / 4000), Math.Max(15, p), "Medium",
                        new[] { $"{topic} behavior implemented per the source contract",
                                "Edge cases and error states handled",
                                "Covered by unit + integration tests" },
                        SourceAnalyzer.LayerOf(f.Path) is "Frontend" or "Controllers" or "Database" or "Data Adapter"
                            ? SourceAnalyzer.LayerOf(f.Path) : "Server");
                    p -= 2;
                }

            // ── integration & contract story per module ──
            AddStory(epics[^1], $"{module} integration & contract tests",
                "qa lead", $"integration suite proving {module} contracts end to end",
                $"{module} regressions surface before release", 5, Math.Max(15, priority - 8), "Medium",
                new[] { $"{module} contracts asserted against consumers",
                        "Critical paths integration-tested in CI",
                        "Contract drift breaks the build" }, "Server");

            priority = Math.Max(20, priority - 10);
        }

        // ── enhancement wave: real change patterns from repository history ──
        // Commits reveal where the source actually evolves; each recurring
        // (module, layer) pattern becomes a Year-2 enhancement story so the
        // plan stretches across the horizon with grounded change work.
        var patterns = new SourceAnalyzer().ChangePatterns(snapshot)
            .Where(p => modules.Contains(p.Module, StringComparer.OrdinalIgnoreCase))
            .Take(10).ToList();
        var enhanceEpic = new PmEpic { Key = $"E{epicNo + 1:D2}", Name = "Field & Enhancement Waves",
            Module = modules[0], Theme = "Core",
            Description = "Year-2 enhancement waves derived from the repository's own commit "
                + $"history ({snapshot.History.Count} commits reviewed): field extensions, "
                + "feature polish and change patterns per module" };
        epics.Add(enhanceEpic);
        epicNo++;
        var enhancePriority = 52;
        foreach (var pattern in patterns.Where(p => p.Commits >= 1))
        {
            if (stories.Count >= MaxStories) break;
            var headline = pattern.Headline.Length > 70 ? pattern.Headline[..67] + "..." : pattern.Headline;
            AddStory(enhanceEpic, $"Enhance {pattern.Module} ({pattern.Layer.ToLowerInvariant()}): {headline}",
                "user", $"the '{headline}' change pattern carried into the rebuild",
                $"{pattern.Module} keeps evolving the way the source did", ClampFibonacci(3 + pattern.Commits),
                Math.Max(30, enhancePriority), "Medium",
                new[] { $"Change wave mirrors the source pattern '{headline}' ({pattern.Commits} commits touched {pattern.Layer.ToLowerInvariant()} code)",
                        $"Touched paths covered: {string.Join(", ", pattern.Examples.Take(2))}",
                        $"Regression tests prove the enhanced {pattern.Layer.ToLowerInvariant()} behavior",
                        "Frontend, server and database components updated together" },
                pattern.Layer, "enhance", pattern.Module);
            enhancePriority -= 2;
        }
        // field extensions: extend each data-bearing module with new fields end to end
        var fieldModules = modules.Where(m =>
            snapshot.Files.Any(f => ModuleOf(f.Path).Equals(m, StringComparison.OrdinalIgnoreCase)
                && SourceAnalyzer.LayerOf(f.Path) is "Data Adapter" or "Database")).Take(4);
        foreach (var module in fieldModules)
        {
            if (stories.Count >= MaxStories) break;
            AddStory(enhanceEpic, $"Extend {module} data model: new fields end to end",
                "product owner", $"additional fields on {module} records across db, server and UI",
                $"{module} grows without schema drift", 8, Math.Max(30, enhancePriority), "Medium",
                new[] { $"New {module} columns added via versioned migration",
                        "Server validation and data adapter mapping updated for the new fields",
                        "Frontend forms display and edit the new fields",
                        "Existing data migrates losslessly" },
                "Database", "enhance", module);
            enhancePriority -= 2;
        }

        // data & persistence epic
        AddEpic("Data & persistence", modules[0], "Data",
            "Schema management, migrations, seed data and database scripts across the platform");
        AddStory(epics[^1], "Database schema, migrations & seed data",
            "database admin", "versioned migrations with seeded reference data",
            "environments can be rebuilt deterministically", 8, 60, "Medium",
            new[] { "Migrations run on SQL Server", "Seed data idempotent", "Rollback documented" },
            "Database", "build");

        // QA epic
        AddEpic("Testing & QA", modules[0], "QA",
            "Test strategy: unit, integration and acceptance coverage per squad domain");
        AddStory(epics[^1], "Automated test suites per module",
            "qa lead", "unit + integration suites wired into CI",
            "regressions surface before release", 8, 55, "Medium",
            new[] { "Unit tests per module", "Integration tests for critical paths", "Coverage reported in CI" },
            "Server", "build");

        // release epic — always last
        AddEpic("Hardening & release", modules[^1], "Release",
            "Performance hardening, security review, documentation and the GA release train");
        AddStory(epics[^1], "Security review & performance hardening",
            "operations", "security pass and load-tested release candidates",
            "the platform is production-ready", 13, 40, "High",
            new[] { "Security checklist cleared", "Load test meets SLO", "Runbook published" },
            "Framework", "release");
        AddStory(epics[^1], "GA release & operations handover",
            "operations", "GA release with monitoring and handover documentation",
            "operations can run the platform", 5, 35, "Medium",
            new[] { "GA build signed off", "Monitoring dashboards live", "Handover doc accepted" },
            "Framework", "release");

        return (epics, stories);
    }

    /// <summary>Prompt-driven capture: requirement sentences become stories.</summary>
    public (IReadOnlyList<PmEpic> Epics, IReadOnlyList<PmStory> Stories) FromPrompt(string prompt)
    {
        var sentences = SentenceRegex().Split(prompt)
            .Select(s => s.Trim())
            .Where(s => s.Length > 12)
            .Take(40).ToList();
        if (sentences.Count == 0)
            return FromSource(new SourceSnapshot
            {
                SourceType = SourceType.ZipArchive, SourceName = "prompt",
                SourceDetail = "empty prompt", Files = new List<SourceFile>()
            });     // fallback: foundation-only backlog

        var epics = new List<PmEpic> { new() { Key = "E01", Name = "Prompt Requirements",
            Module = "Core Platform", Theme = "Core", Description = "Captured directly from the prompt" } };
        var stories = new List<PmStory>();
        for (var i = 0; i < sentences.Count; i++)
        {
            stories.Add(new PmStory { Key = $"US-{i + 1:D3}", EpicKey = "E01",
                Title = Truncate(sentences[i], 80), AsA = "user",
                IWant = sentences[i].ToLowerInvariant(), SoThat = "the requirement is delivered",
                Points = ClampFibonacci(2 + i % 4), Priority = 100 - i, Risk = i < 5 ? "High" : "Medium",
                Module = "Core Platform", TestCaseCount = 4,
                AcceptanceCriteria = new[] { "Behavior matches the requirement", "Covered by tests" } });
        }
        return (epics, stories);
    }

    /// <summary>Client-side vendor bundles excluded from module detection.</summary>
    private static readonly string[] VendorPrefixes =
        { "jquery", "bootstrap", "popper", "fontawesome", "font-awesome", "modernizr",
          "moment", "lodash", "chart", "sweetalert", "select2", "datatables" };

    /// <summary>Support folders that are not product modules.</summary>
    private static readonly string[] SupportFolders =
        { "artifacts", "attached_assets", "assets", "docs", "documentation", "scripts",
          "tools", "test", "tests", "e2e", "coverage", "dist", "build" };


    private static bool IsCodePath(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestPath(string path) =>
        path.Contains("test", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase));

    /// <summary>A human feature name from a file path ("ui/alert-dialog.tsx" → "Alert dialog UI component").</summary>
    private static string FeatureName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var words = Regex.Replace(Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1"), "[-_.]+", " ").Trim();
        if (words.Length == 0) words = "feature";
        words = char.ToUpperInvariant(words[0]) + words[1..];
        var kind = path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "UI component"
            : path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? "view"
            : path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "handler"
            : "module";
        return $"{words} {kind}";
    }

    /// <summary>Module of a source path: product folder only (vendors/support folders excluded).</summary>
    private static string ModuleOf(string path)
    {
        // src/<Project>/... → project; fall back to top folder, then file name topic
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // client-side vendor bundles are never product modules
        foreach (var part in parts)
            if (Array.Exists(VendorPrefixes, v => part.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                return string.Empty;
        // src/<module>/... or lib/<module>/... → the module is the folder under the marker
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] is "src" or "lib" && i + 1 < parts.Length - 1)
                return parts[i + 1].Replace("SmartAgent.", "").Replace("SmartAgent", "Core");
        // else the top folder is the module — unless it is a root config file or support folder
        return parts.Length > 1 && !Array.Exists(SupportFolders, s => parts[0].Equals(s, StringComparison.OrdinalIgnoreCase))
            ? parts[0] : string.Empty;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int ClampFibonacci(int v) => v switch
    {
        <= 1 => 1, <= 2 => 2, <= 3 => 3, <= 5 => 5, <= 8 => 8, _ => 13
    };

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();
}
