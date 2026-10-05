using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core.Pm;

/// <summary>A class captured from the source with its public surface.</summary>
public sealed record PmClassInfo
{
    public required string Name { get; init; }
    public required string Module { get; init; }
    public required string Kind { get; init; }          // Controller / Service / Entity / Hub / Component / Other
    public required string File { get; init; }
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
    /// <summary>Method name → its real parameters ("uid (string?)", "userId (string)")
    /// — the ground truth for what a controller action actually binds.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MethodParams { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>A database table extracted from entity models in the source.</summary>
public sealed record PmTableInfo
{
    public required string Name { get; init; }
    public IReadOnlyList<(string Name, string ClrType)> Columns { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>
/// Deep source capture: reads any GitHub/ZIP source snapshot and grounds every
/// PM artifact in the actual code — classes, methods, entity tables, package
/// dependencies, config keys and the detected tech platform. Sprint artifacts
/// (unit/system tests, dual-provider DB scripts with rollbacks, code
/// artifacts, HLD, DDD with sequence steps, implementation checklists) are
/// derived deterministically so the same source always yields the same plan.
/// </summary>
/// <summary>One user-facing capability extracted from the application source
/// (a controller, SignalR hub or domain entity group with its actual methods, views and entities).</summary>
public sealed record PmCapability
{
    public required string Feature { get; init; }                // "Library", "Live sessions"
    public required string Kind { get; init; }                   // Controller | Hub | Entities | Services
    public required string Root { get; init; }                   // application root folder
    public string Class { get; init; } = "";                    // controller/hub class name
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
    /// <summary>Each action method's real parameters, copied from the owning class — the
    /// ground truth for what a controller action actually binds and validates.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MethodParams { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyList<string> Views { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Entities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PmEntityDetail> EntityDetails { get; init; } = Array.Empty<PmEntityDetail>();
    public IReadOnlyList<PmViewDetail> ViewDetails { get; init; } = Array.Empty<PmViewDetail>();
    public int FileCount { get; init; }
}

/// <summary>An entity with its actual fields (property + CLR type) extracted from the source model.</summary>
/// <summary>Where a settings/config field is actually consumed: the setting's
/// field, the module consuming it, and the file that references it.</summary>
public sealed record PmSettingUsage(string Field, string Module, string File);

/// <summary>A field the source repository ADDED to an entity over its own
/// history — extracted from commit diffs, the ground truth for
/// "this module evolves by adding these fields".</summary>
public sealed record PmFieldHistory
{
    public required string Entity { get; init; }
    public required string Field { get; init; }
    public required string Type { get; init; }
    public required string Module { get; init; }
    public required string File { get; init; }
    public int Commits { get; init; }
    public DateTimeOffset First { get; init; }
    public DateTimeOffset Last { get; init; }
}

public sealed record PmEntityDetail
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();    // "Title (string)"
    /// <summary>Field name → size info: explicit "max 100 (StringLength)" when the source
    /// declares it, otherwise an inferred default so DDL requirements always state a size.</summary>
    public IReadOnlyDictionary<string, string> FieldSizes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> RequiredFields { get; init; } = Array.Empty<string>();
}

/// <summary>A view (cshtml/razor) with its actual input fields and wired behaviors.</summary>
public sealed record PmViewDetail
{
    public required string Name { get; init; }             // "Library/Create.cshtml"
    public required string Feature { get; init; }          // "Library"
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();    // form fields
    public IReadOnlyList<string> Behaviors { get; init; } = Array.Empty<string>();  // "POST Create", "SignalR SendChatMessage"
}

/// <summary>The profiled application behind a source snapshot: what the app
/// actually does (readme title/purpose), its platform, the capabilities its
/// controllers/hubs/entities expose, and any secondary source roots.</summary>
public sealed record PmAppProfile
{
    public required string AppName { get; init; }                // readme title
    public required string Purpose { get; init; }                // readme purpose sentence
    public required string Root { get; init; }                   // dominant application folder
    public required IReadOnlyList<PmCapability> Capabilities { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Root, int Files)> OtherRoots { get; init; } = Array.Empty<(string, int)>();
}

public sealed partial class SourceAnalyzer
{
    /// <summary>Captures classes with their public methods from source files.</summary>
    public IReadOnlyList<PmClassInfo> Classes(SourceSnapshot snapshot)
    {
        var result = new List<PmClassInfo>();
        // Roslyn gives exact methods/params/kinds for C# — compiler truth, not heuristics
        foreach (var rc in RoslynReader.ReadClasses(snapshot.Files))
            result.Add(new PmClassInfo
            {
                Name = rc.Name, Module = ModuleOf(rc.File), File = rc.File, Kind = rc.Kind,
                Methods = rc.Methods, MethodParams = rc.MethodParams
            });
        foreach (var file in snapshot.Files)
        {
            if (!IsCode(file.Path) || file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var module = ModuleOf(file.Path);
            foreach (var m in ClassRegex().Matches(file.Content).Cast<Match>())
            {
                var name = m.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var body = ExtractBalanced(m.Index, file.Content);
                var methodMatches = MethodRegex().Matches(body).Cast<Match>()
                    .Where(x => !string.IsNullOrWhiteSpace(x.Groups["method"].Value))
                    .DistinctBy(x => x.Groups["method"].Value).Take(8).ToList();
                var methods = methodMatches.Select(x => x.Groups["method"].Value).ToList();
                var methodParams = new Dictionary<string, IReadOnlyList<string>>();
                foreach (var mm in methodMatches)
                {
                    var paramsText = ExtractParenBalanced(mm.Index + mm.Length - 1, body);
                    methodParams[mm.Groups["method"].Value] = ParseParams(paramsText);
                }
                result.Add(new PmClassInfo
                {
                    Name = name, Module = module, File = file.Path,
                    Kind = KindOf(name, file.Path), Methods = methods, MethodParams = methodParams
                });
            }
        }
        return result;
    }

    /// <summary>Extracts entity tables (name + columns) from model classes.</summary>
    public IReadOnlyList<PmTableInfo> Tables(SourceSnapshot snapshot)
    {
        var tables = new List<PmTableInfo>();
        foreach (var file in snapshot.Files.Where(f => IsCode(f.Path)))
        {
            foreach (var m in ClassRegex().Matches(file.Content).Cast<Match>())
            {
                var body = ExtractBalanced(m.Index, file.Content);
                var cols = ColumnRegex().Matches(body).Cast<Match>()
                    .Select(c => (c.Groups["col"].Value, c.Groups["type"].Value))
                    .Where(c => !string.IsNullOrWhiteSpace(c.Item1))
                    .ToList();
                if (cols.Count == 0) continue;
                var tableAttr = TableAttrRegex().Match(file.Content[..Math.Min(m.Index + m.Length, file.Content.Length)]);
                var name = tableAttr.Success
                    ? tableAttr.Groups["table"].Value
                    : ToSnakeCase(m.Groups["name"].Value);
                if (tables.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                tables.Add(new PmTableInfo { Name = name, Columns = cols.Take(20).ToList() });
            }
        }
        return tables;
    }

    /// <summary>Extracts external package dependencies from project manifests.</summary>
    public IReadOnlyList<string> Dependencies(SourceSnapshot snapshot)
    {
        var deps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            if (file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                foreach (var m in PackageRefRegex().Matches(file.Content).Cast<Match>())
                    deps.Add($"{m.Groups["pkg"].Value} {m.Groups["ver"].Value}".Trim());
            else if (file.Path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
                foreach (Match j in JsonDepRegex().Matches(file.Content))
                    if (!j.Groups["pkg"].Value.Contains(':'))
                        deps.Add($"{j.Groups["pkg"].Value} {j.Groups["ver"].Value}".Trim());
            else if (file.Path.EndsWith("pom.xml", StringComparison.OrdinalIgnoreCase))
                foreach (Match mv in MavenDepRegex().Matches(file.Content))
                    deps.Add(mv.Groups["pkg"].Value);
        }
        return deps.Take(24).ToList();
    }

    /// <summary>Config keys / environment variables referenced by the source.</summary>
    public IReadOnlyList<string> ConfigKeys(SourceSnapshot snapshot)
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files.Where(f => IsCode(f.Path)))
        {
            foreach (var m in ConfigKeyRegex().Matches(file.Content).Cast<Match>())
                keys.Add(m.Groups["key"].Value);
            foreach (var m in EnvKeyRegex().Matches(file.Content).Cast<Match>())
                keys.Add(m.Groups["key"].Value);
        }
        return keys.Take(16).ToList();
    }

    /// <summary>Detects the tech platform profile from the source manifests.</summary>
    public PmPlatform DetectPlatform(SourceSnapshot snapshot)
    {
        var files = snapshot.Files;
        var hasCsproj = files.Any(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var hasMaven = files.Any(f => f.Path.EndsWith("pom.xml", StringComparison.OrdinalIgnoreCase)
                                    || f.Path.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase));
        var hasNode = files.Any(f => f.Path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase));
        var hasPy = files.Any(f => f.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase));

        if (hasCsproj)
        {
            var fx = files.Where(f => f.Path.EndsWith(".csproj"))
                .Select(f => TargetFxRegex().Match(f.Content)).Where(m => m.Success)
                .Select(m => m.Groups["ver"].Value.Replace("net", "").Trim()).FirstOrDefault() ?? "8.0";
            return MsPlatform(snapshot, fx);
        }
        if (hasMaven) return new PmPlatform
        {
            Name = "Java Enterprise Stack", ShortName = "Java Enterprise", Color = "#E76F00",
            Os = "Red Hat Enterprise Linux / Oracle Linux", Backend = "Spring Boot 3 / Java 21", BackendLang = "Java",
            PrimaryDb = "Oracle Database", SecondaryDb = "PostgreSQL 16",
            Infra = new[] { "AWS", "Apache Tomcat", "Kubernetes", "Maven" },
            BestFor = "Massive transactional systems, banking, insurance, telecom",
            Advantages = new[] { "Extremely mature ecosystem", "High scalability & concurrency",
                "Strong enterprise patterns (DDD, CQRS)", "Excellent Spring ecosystem" },
            Arch = new PmPlatformArch
            {
                WebLayer = new[] { "Spring MVC REST Controllers", "Spring Security (JWT)", "CORS Filter", "WebSocket (STOMP)" },
                ServiceLayer = ServiceLayerOf(snapshot),
                DataLayer = new[] { "Spring Data JPA / Hibernate", "Oracle JDBC (prod)", "PostgreSQL (alt)", "HikariCP pool" },
                DomainTables = DomainTablesOf(snapshot)
            }
        };
        if (hasNode) return new PmPlatform
        {
            Name = "Node / TypeScript Stack", ShortName = "Node & TS", Color = "#3178C6",
            Os = "Ubuntu / Alpine (containers)", Backend = "NestJS / Express", BackendLang = "TypeScript",
            PrimaryDb = "PostgreSQL 16", SecondaryDb = "MongoDB",
            Infra = new[] { "Docker", "Nginx", "GitHub Actions CI", "pnpm" },
            BestFor = "Rapid product delivery, realtime apps, API-first services",
            Advantages = new[] { "Fast iteration loop", "One language across the stack", "Huge npm ecosystem", "Realtime-first (Socket.IO)" },
            Arch = new PmPlatformArch
            {
                WebLayer = new[] { "REST controllers / routers", "Passport or JWT auth", "Zod / class-validator DTOs", "Socket.IO gateways" },
                ServiceLayer = ServiceLayerOf(snapshot),
                DataLayer = new[] { "Prisma / TypeORM", "PostgreSQL 16 (prod)", "MongoDB (alt)", "Migration CLI" },
                DomainTables = DomainTablesOf(snapshot)
            }
        };
        if (hasPy) return new PmPlatform
        {
            Name = "Python Enterprise Stack", ShortName = "Python", Color = "#3776AB",
            Os = "Ubuntu (containers)", Backend = "FastAPI / Django", BackendLang = "Python",
            PrimaryDb = "PostgreSQL 16", SecondaryDb = "MySQL 8",
            Infra = new[] { "Docker", "Gunicorn + Nginx", "GitHub Actions CI", "pip / poetry" },
            BestFor = "Data-driven platforms, ML integrations, rapid internal tooling",
            Advantages = new[] { "Fastest prototyping", "Rich data/ML ecosystem", "Readable, low-ceremony code", "Strong community" },
            Arch = new PmPlatformArch
            {
                WebLayer = new[] { "FastAPI / Django routes", "OAuth2 + JWT", "Pydantic schemas", "Celery workers" },
                ServiceLayer = ServiceLayerOf(snapshot),
                DataLayer = new[] { "SQLAlchemy 2 ORM", "PostgreSQL 16 (prod)", "MySQL 8 (alt)", "Alembic migrations" },
                DomainTables = DomainTablesOf(snapshot)
            }
        };
        return MsPlatform(snapshot, "8.0");
    }

    // ─── per-sprint artifact builders (grounded in captured classes/tables/deps) ───

    /// <summary>Unit test catalog for one story: class/method grounded cases.</summary>
    public IReadOnlyList<PmUnitTestCase> UnitTestsFor(PmStory story, IReadOnlyList<PmClassInfo> classes,
        int sprintNo, int startSeq)
    {
        var moduleClasses = classes.Where(c => c.Module.Equals(story.Module, StringComparison.OrdinalIgnoreCase)).ToList();
        if (moduleClasses.Count == 0) moduleClasses = classes.Take(3).ToList();
        var cases = new List<PmUnitTestCase>();
        var seq = startSeq;
        foreach (var cls in moduleClasses.Take(3))
        {
            var methods = cls.Methods.Count > 0 ? cls.Methods : new List<string> { "primary action" };
            foreach (var method in methods.Take(3))
            {
                cases.AddRange(cls.Kind switch
                {
                    "Controller" => new[]
                    {
                        Case(cls.Name, method, "Happy path — valid input, authenticated user", "200 OK with expected payload"),
                        Case(cls.Name, method, "Authorization check — insufficient role", "403 Forbidden"),
                        Case(cls.Name, method, "Invalid input — missing required field", "400 with validation error")
                    },
                    "Service" => new[]
                    {
                        Case(cls.Name, method, "Happy path — valid input", "Expected result returned, state persisted"),
                        Case(cls.Name, method, "Invalid input — missing required field", "ValidationException or 400 response"),
                        Case(cls.Name, method, "Idempotency — repeat call", "No duplicate side effects")
                    },
                    "Entity" => new[]
                    {
                        Case(cls.Name, method, "Model binding — round trip", "Fields persist and reload identically"),
                        Case(cls.Name, method, "Constraint — max length exceeded", "Validation error surfaced")
                    },
                    "Hub" => new[]
                    {
                        Case(cls.Name, method, "Client connect + receive", "Event delivered to subscribed clients"),
                        Case(cls.Name, method, "Reconnect after drop", "State resynced without duplicates")
                    },
                    _ => new[]
                    {
                        Case(cls.Name, method, "Happy path — valid input", "Expected output"),
                        Case(cls.Name, method, "Edge case — boundary input", "Handled without exception")
                    }
                });
            }
        }
        if (story.Title.Contains("Database", StringComparison.OrdinalIgnoreCase)
            || story.Module.Contains("Data", StringComparison.OrdinalIgnoreCase))
            cases.Add(Case("DbContext / Repository", "Write then read", "Provider parity — primary DB", "Record persisted and retrievable"));
        if (cases.Count == 0)
        {
            cases.Add(Case($"{story.Module}Controller", "Primary action", "Happy path — valid input, authenticated user", "200 OK with expected payload"));
            cases.Add(Case($"{story.Module}Service", "Handle", "Invalid input — missing required field", "ValidationException or 400 response"));
            cases.Add(Case($"{story.Module}Repository", "Write then read", "Provider parity — primary DB", "Record persisted and retrievable"));
        }

        return cases.Take(Math.Max(4, Math.Min(story.TestCaseCount, 12))).ToList();

        PmUnitTestCase Case(string cls, string method, string scenario, string expected) => new()
        {
            Id = $"UT-S{sprintNo:D2}-{seq++:D3}", Cls = cls, Method = method,
            Scenario = scenario, Expected = expected
        };
    }

    /// <summary>System test for a story: feature-level acceptance walk.</summary>
    public PmSystemTest SystemTestFor(PmStory story, int sprintNo, int seq) => new()
    {
        Id = $"ST-S{sprintNo:D2}-{seq:D3}",
        Feature = story.Title,
        Precondition = $"Authenticated user with access to {story.Module}; staging deployed on both database providers",
        Steps = $"Navigate to {story.Module} screens → exercise \"{story.Title}\" → verify acceptance criteria",
        Expected = story.AcceptanceCriteria.Count > 0 ? story.AcceptanceCriteria[0] : "Story acceptance criteria met",
        Env = "Staging — primary + secondary DB"
    };

    /// <summary>Dual-provider migration script for the tables a sprint touches.</summary>
    public PmDbScript DbScriptFor(int sprintNo, string theme, IReadOnlyList<PmTableInfo> tables,
        IReadOnlyList<PmTableInfo> alreadyCreated)
    {
        var fresh = tables.Where(t => !alreadyCreated.Any(a =>
            a.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var forward = new List<string>();
        var rollback = new List<string>();
        var seed = new List<string>();

        foreach (var t in fresh.Take(4))
        {
            forward.Add(SqlServerDdl(t));
            rollback.Add($"DROP TABLE IF EXISTS [{t.Name}];");
            seed.Add(SqlServerSeed(t));
        }
        if (fresh.Count == 0)
        {
            var tune = tables.FirstOrDefault();
            forward.Add(tune is not null
                ? $"-- Sprint {sprintNo} ({theme}): performance & index hardening on [{tune.Name}]\nCREATE NONCLUSTERED INDEX [IX_{tune.Name}_id] ON [{tune.Name}] ([id]);"
                : $"-- Sprint {sprintNo} ({theme}): schema hardening — no new tables this sprint");
            rollback.Add("-- Rollback: DROP INDEX per index created");
            seed.Add("-- Seed: no-op (idempotent re-runs)");
        }
        var migrationClass = $"{sprintNo:D3}_{Snake(theme)}";
        return new PmDbScript
        {
            MigrationClass = migrationClass,
            PrimaryLabel = "SQL Server (Primary)",
            PrimaryDdl = string.Join("\nGO\n", forward),
            SecondaryLabel = "Oracle (Alternate)",
            SecondaryDdl = string.Join("\n", fresh.Take(4).Select(OracleDdl)
                .DefaultIfEmpty($"-- {migrationClass}: hardening only (no new tables)")),
            RollbackPrimary = string.Join("\n", rollback),
            RollbackSecondary = string.Join("\n", fresh.Take(4).Select(t => $"DROP TABLE {t.Name};")
                .DefaultIfEmpty("-- Rollback: no-op")),
            SeedDmlPrimary = string.Join("\n", seed),
            SeedDmlSecondary = "-- Equivalent Oracle seed DML (SYSTIMESTAMP defaults)"
        };
    }

    /// <summary>High-level design for the module(s) a sprint delivers.</summary>
    public PmHld HldFor(string module, IReadOnlyList<PmStory> sprintStories,
        IReadOnlyList<PmClassInfo> classes, IReadOnlyList<string> dependencies)
    {
        var moduleClasses = classes.Where(c => c.Module.Equals(module, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        return new PmHld
        {
            ModuleName = $"{module}Module",
            Purpose = $"Deliver {module} capability as an incremental, regression-free addition: " +
                      string.Join("; ", sprintStories.Take(3).Select(s => s.Title)),
            Components = moduleClasses.Count > 0
                ? moduleClasses.Select(c => $"{c.Name} ({c.Kind})").ToList()
                : new List<string> { $"{module} domain model", $"{module} service layer", $"{module} persistence" },
            ExternalDeps = dependencies.Take(4).ToList(),
            Nfrs = new[]
            {
                "< 500ms API response time (p95)",
                "All endpoints work identically on primary and secondary DB providers",
                "No regression in prior sprint features (CI gate)"
            }
        };
    }

    /// <summary>Detailed design: pseudo-code, sequence, data flow, error handling.</summary>
    public PmDdd DddFor(string module, IReadOnlyList<PmStory> sprintStories,
        IReadOnlyList<PmClassInfo> classes, IReadOnlyList<string> configKeys, PmPlatform platform)
    {
        var cls = classes.FirstOrDefault(c => c.Module.Equals(module, StringComparison.OrdinalIgnoreCase))
                  ?? classes.FirstOrDefault();
        var service = classes.FirstOrDefault(c => c.Kind == "Service")?.Name ?? $"{module}Service";
        var entry = cls?.Name ?? $"{module}Controller";
        var method = cls?.Methods.FirstOrDefault() ?? "Handle";
        var pseudo = cls is null
            ? $"// {module} — grounded in captured requirements\npublic async Task<IActionResult> {method}(Request req)\n{{\n    validate(req);                          // guard clauses\n    var result = await _{service.ToCamel()}.HandleAsync(req);\n    return Ok(Map(result));\n}}"
            : $"// grounded in {cls.File}\npublic async Task<IActionResult> {method}(...)\n{{\n    validate(input);                        // guard clauses\n    var entity = await _{service.ToCamel()}.LoadAsync(id);\n    if (entity is null) return NotFound();\n    await _{service.ToCamel()}.ApplyAsync(entity, input);\n    await _repository.SaveChangesAsync();\n    return Ok(entity);\n}}";
        return new PmDdd
        {
            PseudoCode = pseudo,
            Sequence = new[]
            {
                new PmSequenceStep { From = "Client", To = entry, Label = "HTTP request" },
                new PmSequenceStep { From = entry, To = service, Label = "validated command" },
                new PmSequenceStep { From = service, To = "Repository / DbContext", Label = "domain operation" },
                new PmSequenceStep { From = "Repository / DbContext", To = platform.PrimaryDb, Label = "SQL" },
                new PmSequenceStep { From = platform.PrimaryDb, To = "Client", Label = "result", IsReturn = true }
            },
            DataFlow = $"{entry} → {service} → Repository → {platform.PrimaryDb} (mirrored on {platform.SecondaryDb})",
            ErrorHandling = "Guard-clause validation returns 400; unauthenticated requests return 401/403; " +
                            "persistence failures roll back the unit of work and surface a typed error; " +
                            "all exceptions logged with a correlation id",
            ConfigDeps = configKeys.Count > 0 ? configKeys.Take(6).ToList() : new List<string> { "ConnectionStrings:DefaultConnection" }
        };
    }

    /// <summary>Implementation checklist for a sprint (release-aware).</summary>
    public IReadOnlyList<string> ImplChecklist(string release, IReadOnlyList<PmStory> stories) =>
        !string.IsNullOrEmpty(release)
            ? new[]
            {
                $"All {release} stories accepted by Product Owner",
                "Migration scripts applied to staging on BOTH DB providers",
                "Rollback scripts tested (tables drop cleanly)",
                "Full regression suite passed",
                "Performance benchmark meets SLO",
                "Security checklist cleared",
                "Release notes drafted · PM + QA sign-off"
            }
            : new[]
            {
                "Migration applied to staging DB (primary + secondary)",
                "Rollback scripts tested",
                stories.Count > 0 ? $"Unit tests green in CI for {stories.Count} stories" : "Unit tests green in CI",
                "Code review completed by Dev Lead",
                "Smoke test passed on staging",
                "Sprint demo to Product Owner"
            };

    /// <summary>Code artifact ledger for a sprint: new vs modified vs cumulative.</summary>
    public PmCodeArtifact CodeArtifactsFor(IReadOnlyList<SourceFile> files, string module,
        HashSet<string> cumulative, bool stabilization)
    {
        var newFiles = stabilization
            ? new List<string>()
            : files.Where(f => ModuleOf(f.Path).Equals(module, StringComparison.OrdinalIgnoreCase)
                               && !cumulative.Contains(f.Path))
                .Take(12).Select(f => f.Path).ToList();
        foreach (var f in newFiles) cumulative.Add(f);
        var modified = stabilization
            ? files.Where(f => f.Path.EndsWith(".md") || f.Path.EndsWith(".yml") || f.Path.EndsWith(".yaml")
                               || f.Path.Contains("test", StringComparison.OrdinalIgnoreCase)
                               || f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Take(6).Select(f => f.Path).ToList()
            : files.Where(f => ModuleOf(f.Path).Equals(module, StringComparison.OrdinalIgnoreCase)
                               && cumulative.Contains(f.Path)
                               && !newFiles.Contains(f.Path)).Take(3).Select(f => f.Path).ToList();
        return new PmCodeArtifact
        {
            NewFiles = newFiles, ModifiedFiles = modified, CumulativeCount = cumulative.Count
        };
    }

    // ─── platform helpers ───────────────────────────────────────────────


    // ─────────────────────────────────────────────────────────────
    //  Application capability profiling: resolve what the source
    //  app actually DOES (controllers, hubs, entities, views) into
    //  named user-facing capabilities, so requirements follow the
    //  product's core functionality instead of its folder names.
    // ─────────────────────────────────────────────────────────────

    private static readonly string[] AppMarkers =
        { "Controllers", "Hubs", "Models", "Areas", "Pages", "controllers", "hubs", "models", "pages" };

    /// <summary>Application root of a source path: the folder that owns the app's
    /// Controllers/Models/Hubs content; falls back to the top folder.</summary>
    private static string AppRootOf(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
            if (Array.Exists(AppMarkers, m => parts[i].Equals(m, StringComparison.OrdinalIgnoreCase)))
                return parts[i - 1];
        return parts.Length > 0 ? parts[0] : "app";
    }

    /// <summary>Profiles the dominant application: title + purpose from the
    /// readme, one capability per controller/hub with its real methods,
    /// views and entities, and the platform footprint (SignalR, EF Core...).</summary>
    public PmAppProfile? ProfileApp(SourceSnapshot snapshot, IReadOnlyList<PmClassInfo>? classes = null)
    {
        classes ??= Classes(snapshot);
        var contentBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapshot.Files) contentBy.TryAdd(f.Path, f.Content);

        var rootGroups = classes
            .Where(c => c.Kind is "Controller" or "Hub" or "Entity" or "Service")
            .GroupBy(c => AppRootOf(c.File))
            .OrderByDescending(g => g.Count(c => c.Kind is "Controller" or "Hub"))
            .ThenByDescending(g => g.Count())
            .ToList();
        if (rootGroups.Count == 0) return null;
        var root = rootGroups[0].Key;
        var rootClasses = rootGroups[0].ToList();

        var fileCountByRoot = snapshot.Files
            .Where(f => !Array.Exists(VendorPrefixes, v => f.Path.Split('/')[0].StartsWith(v, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(f => AppRootOf(f.Path))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // controllers → capabilities with their real action methods
        var capabilities = new List<PmCapability>();
        var entityDetails = EntityDetailsOf(snapshot, classes);
        var viewDetails = ViewDetailsOf(snapshot);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in rootClasses.Where(c => c.Kind is "Controller" or "Hub")
                     .OrderByDescending(c => c.Methods.Count))
        {
            var feature = c.Name.Replace("Controller", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Hub", "", StringComparison.OrdinalIgnoreCase);
            var views = snapshot.Files
                .Where(f => f.Path.Contains($"/Views/{feature}/", StringComparison.OrdinalIgnoreCase)
                            && f.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f.Path)).Distinct().ToList();
            var content = contentBy.GetValueOrDefault(c.File) ?? "";
            var entities = rootClasses.Where(e => e.Kind == "Entity"
                    && (e.Name.StartsWith(feature, StringComparison.OrdinalIgnoreCase)
                        || content.Contains(e.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Name).ToList();
            foreach (var e in entities) claimed.Add(e);
            capabilities.Add(new PmCapability
            {
                Feature = feature, Kind = c.Kind, Root = root, Class = c.Name,
                Methods = c.Methods, MethodParams = c.MethodParams, Views = views, Entities = entities,
                EntityDetails = entityDetails.Where(d => entities.Contains(d.Name)).ToList(),
                ViewDetails = viewDetails.Where(v => v.Feature.Equals(feature, StringComparison.OrdinalIgnoreCase)).ToList(),
                FileCount = Math.Max(1, fileCountByRoot.GetValueOrDefault(root, 1) / 4 + views.Count + entities.Count)
            });
        }
        // unclaimed domain entities → one data-model capability
        var freeEntities = rootClasses.Where(c => c.Kind == "Entity" && !claimed.Contains(c.Name))
            .Select(c => c.Name).ToList();
        if (freeEntities.Count > 0)
            capabilities.Add(new PmCapability
            {
                Feature = "Domain data model", Kind = "Entities", Root = root,
                Entities = freeEntities,
                EntityDetails = entityDetails.Where(d => freeEntities.Contains(d.Name)).ToList(),
                FileCount = freeEntities.Count
            });
        // services → one capability
        var services = rootClasses.Where(c => c.Kind == "Service").Select(c => c.Name).ToList();
        if (services.Count > 0)
            capabilities.Add(new PmCapability
            {
                Feature = "Application services", Kind = "Services", Root = root,
                Entities = services, FileCount = services.Count
            });

        // readme title + purpose
        var readme = snapshot.Files
            .Where(f => Regex.IsMatch(Path.GetFileName(f.Path), @"^(readme|replit|about|overview)\.md$", RegexOptions.IgnoreCase)
                        && f.Path.Split('/').Length <= 3)
            .OrderBy(f => f.Path.Split('/').Length).FirstOrDefault();
        string appName = "", purpose = "";
        if (readme != null)
        {
            var lines = readme.Content.Split('\n');
            var title = lines.FirstOrDefault(l => l.StartsWith("#") && l.Trim('#').Trim().Length > 3)
                        ?? lines.FirstOrDefault(l => l.Trim().Length is > 3 and < 90);
            if (title != null) appName = title.Trim().TrimStart('#').Trim();
            // first substantial paragraph (>= 40 chars) is the app's purpose statement
            var paragraphs = new List<string>();
            var current = new List<string>();
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0)
                {
                    if (current.Count > 0) { paragraphs.Add(string.Join(" ", current)); current.Clear(); }
                }
                else if (!t.StartsWith("#")) current.Add(t);
            }
            if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
            purpose = paragraphs.FirstOrDefault(p => p.Length >= 40)
                      ?? paragraphs.OrderByDescending(p => p.Length).FirstOrDefault() ?? "";
            if (purpose.Length > 240) purpose = purpose[..237] + "...";
        }
        if (appName.Length == 0) appName = root;

        // platform footprint
        var sample = string.Join("\n", snapshot.Files.Take(120).Select(f => f.Content));
        var platforms = new List<string>();
        if (Regex.IsMatch(sample, @"Microsoft\.AspNetCore\.SignalR|Hubs/", RegexOptions.IgnoreCase)) platforms.Add("SignalR real-time");
        if (Regex.IsMatch(sample, @"EntityFrameworkCore|DbContext", RegexOptions.IgnoreCase)) platforms.Add("EF Core");
        if (Regex.IsMatch(sample, "SQLite", RegexOptions.IgnoreCase)) platforms.Add("SQLite");
        if (Regex.IsMatch(sample, "MongoDB|IMongoClient", RegexOptions.IgnoreCase)) platforms.Add("MongoDB");
        if (Regex.IsMatch(sample, "Redis|IDatabase", RegexOptions.IgnoreCase)) platforms.Add("Redis");
        if (Regex.IsMatch(sample, "Identity|ApplicationUser", RegexOptions.IgnoreCase)) platforms.Add("Identity");

        var otherRoots = fileCountByRoot
            .Where(kv => !kv.Key.Equals(root, StringComparison.OrdinalIgnoreCase)
                         && !Array.Exists(SupportFolders, sf => kv.Key.Equals(sf, StringComparison.OrdinalIgnoreCase)))
            .Where(kv => kv.Value >= 3)
            .OrderByDescending(kv => kv.Value).Take(2)
            .Select(kv => (kv.Key, kv.Value)).ToList();

        if (capabilities.Count < 2) return null;    // not enough signal → legacy module flow
        return new PmAppProfile
        {
            AppName = appName, Purpose = purpose, Root = root,
            Capabilities = capabilities, Platforms = platforms, OtherRoots = otherRoots
        };
    }


    // ─────────────────────────────────────────────────────────────
    //  Field-level detail extraction: entities with their actual
    //  properties, views with their actual form fields and wired
    //  behaviors — the raw material for detailed, specific
    //  requirements connecting views, fields and behavior.
    // ─────────────────────────────────────────────────────────────

    /// <summary>Entity models with their real fields: for each entity class,
    /// every public property with its CLR type ("Title (string)").</summary>
    public IReadOnlyList<PmEntityDetail> EntityDetailsOf(SourceSnapshot snapshot, IReadOnlyList<PmClassInfo>? classes = null)
    {
        classes ??= Classes(snapshot);
        // Roslyn path: exact properties with exact [StringLength]/[MaxLength]/[Required]/[Key] attributes
        var roslynEntities = RoslynReader.ReadEntities(snapshot.Files)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var details = new List<PmEntityDetail>();
        foreach (var c in classes.Where(x => x.Kind == "Entity"))
        {
            if (roslynEntities.TryGetValue(c.Name, out var re))
            {
                var fields = re.Properties.Take(12)
                    .Select(p => $"{p.Name} ({p.Type})").ToList();
                if (fields.Count == 0) continue;
                var sizes = new Dictionary<string, string>();
                var required = new List<string>();
                foreach (var prop in re.Properties.Take(12))
                {
                    var isString = prop.Type.Contains("string", StringComparison.OrdinalIgnoreCase);
                    var isNullable = prop.Type.Contains('?');
                    if (prop.DeclaredSize is int n)
                        sizes[prop.Name] = $"max {n} chars (declared via StringLength/MaxLength)";
                    else if (isString)
                        sizes[prop.Name] = isNullable
                            ? "max 255 chars (inferred default — source does not declare an explicit size)"
                            : "max 255 chars (inferred default — source does not declare an explicit size; required)";
                    if (prop.KeyAttr || prop.RequiredAttr || (isString && !isNullable))
                        required.Add(prop.Name);
                }
                if (!details.Any(d => d.Name == c.Name))
                    details.Add(new PmEntityDetail
                    {
                        Name = c.Name, Fields = fields, FieldSizes = sizes, RequiredFields = required
                    });
                continue;
            }
            var contentBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in snapshot.Files) contentBy.TryAdd(f.Path, f.Content);
            if (!contentBy.TryGetValue(c.File, out var content)) continue;
            var classMatch = ClassRegex().Matches(content).Cast<Match>()
                .FirstOrDefault(m => m.Groups["name"].Value == c.Name);
            if (classMatch == null) continue;
            var body = ExtractBalanced(classMatch.Index, content);
            var colMatches = ColumnRegex().Matches(body).Cast<Match>()
                .DistinctBy(x => x.Groups["col"].Value).Take(12).ToList();
            var legacyFields = colMatches
                .Select(x => $"{x.Groups["col"].Value} ({x.Groups["type"].Value.Trim()})").ToList();
            var legacySizes = new Dictionary<string, string>();
            var legacyRequired = new List<string>();
            var prevEnd = 0;
            foreach (var cm in colMatches)
            {
                var window = body[prevEnd..cm.Index];      // attributes sit just before the property
                prevEnd = cm.Index + cm.Length;
                var col = cm.Groups["col"].Value;
                var type = cm.Groups["type"].Value.Trim();
                var sl = Regex.Match(window, @"(?:StringLength|MaxLength)\((?<n>\d+)");
                var isKey = Regex.IsMatch(window, @"\[Key\]");
                var isRequiredAttr = Regex.IsMatch(window, @"\[Required\]");
                if (sl.Success)
                    legacySizes[col] = $"max {sl.Groups["n"].Value} chars (declared via StringLength/MaxLength)";
                else if (type.Contains("string", StringComparison.OrdinalIgnoreCase))
                    legacySizes[col] = type.Contains('?')
                        ? "max 255 chars (inferred default — source does not declare an explicit size)"
                        : "max 255 chars (inferred default — source does not declare an explicit size; required)";
                if (isKey || isRequiredAttr
                    || (type.Contains("string", StringComparison.OrdinalIgnoreCase) && !type.Contains('?')))
                    legacyRequired.Add(col);
            }
            if (legacyFields.Count > 0 && !details.Any(d => d.Name == c.Name))
                details.Add(new PmEntityDetail
                {
                    Name = c.Name, Fields = legacyFields, FieldSizes = legacySizes, RequiredFields = legacyRequired
                });
        }
        return details;
    }

    /// <summary>Views with their real form fields (asp-for/name inputs) and
    /// behaviors (form posts, fetches, SignalR invokes, in-feature links).</summary>
    /// <summary>
    /// For a settings/config entity, finds every file (outside the model itself)
    /// that references one of its fields — the grounded answer to "this setting
    /// is used by this module and drives this functionality".
    /// </summary>
    public IReadOnlyList<PmSettingUsage> SettingUsageOf(SourceSnapshot snapshot, PmEntityDetail config)
    {
        var result = new List<PmSettingUsage>();
        var cols = config.Fields
            .Select(f => f[..f.LastIndexOf(" (", StringComparison.Ordinal)]).ToList();
        foreach (var f in snapshot.Files.Where(f =>
                     (IsCode(f.Path) || f.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                     && !f.Path.EndsWith($"{config.Name}.cs", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var col in cols)
            {
                if (f.Content.Contains($".{col}"))
                    result.Add(new PmSettingUsage(col, ModuleOf(f.Path), f.Path));
            }
        }
        return result.GroupBy(u => (u.Field, u.File)).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Fields the repository itself added over its commit history (properties
    /// appearing in '+' diff lines of entity/model files). Each distinct
    /// (entity, field) with its module and commit evidence.
    /// </summary>
    public IReadOnlyList<PmFieldHistory> FieldHistoryPatterns(SourceSnapshot snapshot)
    {
        var result = new List<PmFieldHistory>();
        foreach (var commit in snapshot.History)
        foreach (var patch in commit.Patches.Where(p => p.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var entity = Path.GetFileNameWithoutExtension(patch.Path);
            foreach (var m in AddedPropertyRegex().Matches(patch.Patch).Cast<Match>())
            {
                var type = m.Groups["type"].Value.Trim();
                if (type is "new" or "override" or "static" or "return" or "await") continue;
                result.Add(new PmFieldHistory
                {
                    Entity = entity, Field = m.Groups["col"].Value, Type = type,
                    Module = ModuleOf(patch.Path), File = patch.Path,
                    Commits = 1, First = commit.Date, Last = commit.Date
                });
            }
        }
        return result
            .GroupBy(r => (r.Entity, r.Field))
            .Select(g => new PmFieldHistory
            {
                Entity = g.Key.Entity, Field = g.Key.Field,
                Type = g.OrderByDescending(r => r.Last).First().Type,
                Module = g.First().Module, File = g.First().File,
                Commits = g.Count(), First = g.Min(r => r.First), Last = g.Max(r => r.Last)
            })
            .OrderByDescending(r => r.Commits).ThenBy(r => r.Last).ToList();
    }

    public IReadOnlyList<PmViewDetail> ViewDetailsOf(SourceSnapshot snapshot)
    {
        var result = new List<PmViewDetail>();
        foreach (var f in snapshot.Files.Where(x =>
                     x.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                     || x.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string feature = parts.Length > 1 ? parts[^2] : "";
            var viewName = Path.GetFileName(f.Path);
            if (feature.Length == 0 || viewName.StartsWith("_", StringComparison.Ordinal)) continue;

            var fields = new List<string>();
            foreach (Match m in InputFieldRegex().Matches(f.Content))
                fields.Add(m.Groups["f"].Success ? m.Groups["f"].Value : m.Groups["f2"].Value);
            fields = fields.Select(v => v.Split('.')[0].TrimEnd(']').Replace("[", ""))
                .Where(v => v.Length > 1 && !v.StartsWith("__", StringComparison.Ordinal))
                .Distinct().Take(10).ToList();

            var behaviors = new List<string>();
            foreach (Match m in FormActionRegex().Matches(f.Content)) behaviors.Add($"POST {m.Groups["a"].Value}");
            foreach (Match m in LinkRegex().Matches(f.Content))
                if (m.Groups["c"].Value.Equals(feature, StringComparison.OrdinalIgnoreCase))
                    behaviors.Add($"link {feature}/{m.Groups["a"].Value}");
            foreach (Match m in FetchRegex().Matches(f.Content)) behaviors.Add($"fetch {m.Groups["u"].Value}");
            foreach (Match m in HubInvokeRegex().Matches(f.Content)) behaviors.Add($"SignalR {m.Groups["m"].Value}");
            behaviors = behaviors.Distinct().Take(8).ToList();

            if (fields.Count > 0 || behaviors.Count > 0)
                result.Add(new PmViewDetail { Name = viewName, Feature = feature, Fields = fields, Behaviors = behaviors });
        }
        return result;
    }

    private PmPlatform MsPlatform(SourceSnapshot snapshot, string fx)
    {
        var controllers = snapshot.Files.Count(f => f.Path.Contains("Controller", StringComparison.OrdinalIgnoreCase));
        var hasSignalR = snapshot.Files.Any(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                                 && f.Content.Contains("Hub", StringComparison.Ordinal));
        return new PmPlatform
        {
            Name = "Microsoft Enterprise Stack", ShortName = "MS Enterprise", Color = "#0078D4",
            Os = "Windows Server / Ubuntu (containers)", Backend = $"ASP.NET Core {fx} MVC", BackendLang = "C#",
            PrimaryDb = "SQL Server 2019", SecondaryDb = "Oracle 19c",
            Infra = new[] { "Microsoft Azure", "IIS", "Docker", "Kubernetes" },
            BestFor = "Enterprises on the Microsoft ecosystem, ERP/CRM/internal systems, AD integration",
            Advantages = new[] { "Excellent enterprise support & SLA", "Strong security tooling",
                "Mature Visual Studio ecosystem", "Long-term vendor stability" },
            Arch = new PmPlatformArch
            {
                WebLayer = new[] { $"ASP.NET Core {fx} MVC", "Razor Views (.cshtml)",
                    "Cookie/JWT Authentication", hasSignalR ? "SignalR Hubs" : "REST controllers" },
                ServiceLayer = ServiceLayerOf(snapshot),
                DataLayer = new[] { "EF Core DbContext", "SQL Server 2019 (prod)", "Oracle 19c (alt)",
                    "DB_PROVIDER env switch", controllers > 0 ? $"{controllers} MVC controllers" : "REST API surface" },
                DomainTables = DomainTablesOf(snapshot)
            }
        };
    }

    private static string[] ServiceLayerOf(SourceSnapshot snapshot)
    {
        var services = snapshot.Files.Where(f => f.Path.Contains("Service", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        return services.Count > 0
            ? services.ToArray()
            : new[] { "Domain services per module", "Validation pipeline", "Notification service", "Mapping profiles" };
    }

    private string[] DomainTablesOf(SourceSnapshot snapshot)
    {
        var tables = Tables(snapshot);
        return tables.Count > 0
            ? tables.Take(4).Select(t => $"{t.Name} ({t.Columns.Count} columns)").ToArray()
            : new[] { "Domain models in Models/", "Repository abstractions", "DTOs / view models" };
    }

    // ─── DDL generation (dual provider) ─────────────────────────────────

    private static string SqlServerDdl(PmTableInfo t)
    {
        var cols = t.Columns.Where(c => !Col(c.Name).Equals("id", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"    [{Col(c.Name)}] {SqlType(c.ClrType)}").ToList();
        return $"CREATE TABLE [{t.Name}] (\n    [id] INT IDENTITY(1,1) PRIMARY KEY,\n{string.Join(",\n", cols)},\n    [created_at] DATETIME2 DEFAULT GETUTCDATE()\n);";
    }

    private static string OracleDdl(PmTableInfo t)
    {
        var cols = t.Columns.Where(c => !Col(c.Name).Equals("id", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"    {Col(c.Name)} {OracleType(c.ClrType)}").ToList();
        return $"CREATE TABLE {t.Name} (\n    id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,\n{string.Join(",\n", cols)},\n    created_at TIMESTAMP DEFAULT SYSTIMESTAMP\n);";
    }

    private static string SqlServerSeed(PmTableInfo t)
    {
        var first = t.Columns.FirstOrDefault().Name;
        return string.IsNullOrEmpty(first)
            ? $"-- Seed {t.Name}: reference rows"
            : $"INSERT INTO [{t.Name}] ([{Col(first)}]) SELECT 'seed-{Col(first)}' WHERE NOT EXISTS (SELECT 1 FROM [{t.Name}]);";
    }

    private static string SqlType(string clr) => clr.Trim() switch
    {
        "int" or "Int32" => "INT", "long" or "Int64" => "BIGINT",
        "decimal" => "DECIMAL(18,4)", "double" => "FLOAT", "bool" or "Boolean" => "BIT",
        "DateTime" => "DATETIME2", "string" or "String" => "NVARCHAR(100)", _ => "NVARCHAR(200)"
    };

    private static string OracleType(string clr) => clr.Trim() switch
    {
        "int" or "Int32" => "NUMBER(10)", "long" or "Int64" => "NUMBER(19)",
        "decimal" => "NUMBER(18,4)", "double" => "BINARY_DOUBLE", "bool" or "Boolean" => "NUMBER(1)",
        "DateTime" => "TIMESTAMP", "string" or "String" => "VARCHAR2(100)", _ => "VARCHAR2(200)"
    };

    // ─── parsing helpers ─────────────────────────────────────────────────


    // ─── layer taxonomy & history analysis ─────────────────────────────────

    /// <summary>A change/enhancement pattern detected in repository history.</summary>
    public sealed record PmChangePattern
    {
        public required string Module { get; init; }
        public required string Layer { get; init; }
        public required string Headline { get; init; }
        public required IReadOnlyList<string> Examples { get; init; }
        public int Commits { get; init; }
        public DateTimeOffset Last { get; init; }
    }

    /// <summary>
    /// Classifies a source path into an architecture layer:
    /// Database, Data Adapter, Controllers, Frontend or Server.
    /// </summary>
    public static string LayerOf(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (p.EndsWith(".sql") || p.Contains("migration") || p.EndsWith(".db")
            || p.EndsWith(".db-shm") || p.EndsWith(".db-wal") || p.EndsWith(".sqlite")
            || p.Contains("/dbscripts") || p.Contains("/schema")) return "Database";
        if (p.Contains("controller") || p.Contains("/routes") || p.Contains("/api/")
            && !p.Contains("client")) return "Controllers";
        if (p.Contains("dbcontext") || p.Contains("repositor")
            || p.Contains("api-client") || p.Contains("api-zod") || p.Contains("api-spec")
            || p.Contains("/data/") || p.Contains("dao") || p.Contains("persistence")) return "Data Adapter";
        if (p.EndsWith(".cshtml") || p.EndsWith(".tsx") || p.EndsWith(".css")
            || p.EndsWith(".jsx") || p.EndsWith(".vue") || p.EndsWith(".html")
            || p.Contains("/views/") || p.Contains("/components/") || p.Contains("/pages/")) return "Frontend";
        return "Server";
    }

    /// <summary>
    /// Detects real change/enhancement patterns from the captured commit
    /// history: groups commits by (module, layer) of their touched paths and
    /// surfaces the recurring waves — the honest signal of where the
    /// repository's maintenance effort actually goes.
    /// </summary>
    public IReadOnlyList<PmChangePattern> ChangePatterns(SourceSnapshot snapshot)
    {
        var patterns = new List<PmChangePattern>();
        foreach (var commit in snapshot.History)
        {
            var code = commit.TouchedPaths.Where(t => !t.EndsWith(".db") && !t.EndsWith(".db-shm")
                && !t.EndsWith(".db-wal") && !t.Contains("attached_assets")).ToList();
            if (code.Count == 0) continue;
            foreach (var group in code.GroupBy(LayerOf))
            {
                var module = group.Select(FirstProductFolder).GroupBy(m => m)
                    .OrderByDescending(g => g.Count()).First().Key;
                patterns.Add(new PmChangePattern
                {
                    Module = module,
                    Layer = group.Key,
                    Headline = FirstLine(commit.Message),
                    Examples = group.OrderBy(p => p.Length).Take(3).ToList(),
                    Commits = 1,
                    Last = commit.Date
                });
            }
        }
        return patterns
            .GroupBy(p => (p.Module, p.Layer))
            .Select(g => new PmChangePattern
            {
                Module = g.Key.Module, Layer = g.Key.Layer,
                Headline = g.OrderByDescending(p => p.Last).First().Headline,
                Examples = g.SelectMany(p => p.Examples).Distinct().Take(3).ToList(),
                Commits = g.Count(), Last = g.Max(p => p.Last)
            })
            .OrderByDescending(p => p.Commits).ThenBy(p => p.Layer).ToList();
    }

    private static string FirstLine(string message) =>
        message.Split('\n', 2)[0].Trim();

    private static string FirstProductFolder(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "core" : segments[0];
    }

    /// <summary>Kind heuristic shared with the Roslyn reader (path/name based).</summary>
    internal static string KindOfPublic(string className, string path) => KindOf(className, path);

    private static string KindOf(string className, string path) =>
        className.Contains("Controller", StringComparison.OrdinalIgnoreCase) ? "Controller"
        : path.Contains("Hub", StringComparison.OrdinalIgnoreCase) && className.EndsWith("Hub") ? "Hub"
        : className.Contains("Service", StringComparison.OrdinalIgnoreCase) ? "Service"
        : path.Contains("Models", StringComparison.OrdinalIgnoreCase)
          || path.Contains("entities", StringComparison.OrdinalIgnoreCase) ? "Entity"
        : path.Contains("components", StringComparison.OrdinalIgnoreCase) ? "Component"
        : "Other";

    /// <summary>Module of a source path, mirroring RequirementIngestor's logic.</summary>
    /// <summary>Client-side vendor bundles excluded from module detection.</summary>
    private static readonly string[] VendorPrefixes =
        { "jquery", "bootstrap", "popper", "fontawesome", "font-awesome", "modernizr",
          "moment", "lodash", "chart", "sweetalert", "select2", "datatables" };

    /// <summary>Support folders that are not product modules.</summary>
    private static readonly string[] SupportFolders =
        { "artifacts", "attached_assets", "assets", "docs", "documentation", "scripts",
          "tools", "test", "tests", "e2e", "coverage", "dist", "build" };

    /// <summary>Module of a source path: product folder only (vendors/support folders excluded).</summary>
    internal static string ModuleOf(string path)
    {
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

    internal static string ToSnakeCase(string s) =>
        Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    private static string Snake(string s)
    {
        var filtered = new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ').ToArray()).Replace(' ', '_');
        return filtered.ToLowerInvariant();
    }

    private static string Col(string raw) =>
        ToSnakeCase(raw.Replace("[", "").Replace("]", "").Split(':')[0].Trim());

    /// <summary>Extracts a balanced {…} body starting at a class declaration index.</summary>
    /// <summary>Extracts a method's real parameter list ("uid (string?)", "userId (string)")
    /// — the ground truth for what a controller action actually binds, used instead of
    /// guessing from entity/view field names.</summary>
    private static string ExtractParenBalanced(int openParenIndex, string content)
    {
        if (openParenIndex < 0 || openParenIndex >= content.Length || content[openParenIndex] != '(')
            return string.Empty;
        var depth = 0;
        for (var i = openParenIndex; i < content.Length && i < openParenIndex + 2000; i++)
        {
            if (content[i] == '(') depth++;
            else if (content[i] == ')' && --depth == 0) return content[(openParenIndex + 1)..i];
        }
        return string.Empty;
    }

    private static List<string> ParseParams(string paramsText)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(paramsText)) return result;
        var parts = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < paramsText.Length; i++)
        {
            var c = paramsText[i];
            if (c is '<' or '[' or '(') depth++;
            else if (c is '>' or ']' or ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(paramsText[start..i]); start = i + 1; }
        }
        parts.Add(paramsText[start..]);

        foreach (var raw in parts)
        {
            var p = Regex.Replace(raw, @"\[[^\]]*\]", "").Trim();      // strip [FromBody] etc.
            if (p.Length == 0) continue;
            p = Regex.Replace(p, @"\s*=\s*.+$", "").Trim();            // strip default value
            var m = Regex.Match(p, @"^(?:this\s+)?(?<type>[A-Za-z_][A-Za-z0-9_<>\[\]\?,\s]*?)\s+(?<name>[A-Za-z_]\w*)$");
            result.Add(m.Success ? $"{m.Groups["name"].Value} ({m.Groups["type"].Value.Trim()})" : p);
        }
        return result;
    }

    private static string ExtractBalanced(int classIndex, string content)
    {
        var start = content.IndexOf('{', classIndex);
        if (start < 0) return string.Empty;
        var depth = 0;
        for (var i = start; i < content.Length && i < start + 8000; i++)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}' && --depth == 0) return content[start..i];
        }
        return content[start..Math.Min(content.Length, start + 4000)];
    }

    private static bool IsCode(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?:class|record|interface)\s+(?:sealed\s+|abstract\s+|partial\s+)?(?<name>[A-Z][A-Za-z0-9_]*)")]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"\bpublic\s+(?:async\s+Task<[^>]*>|async\s+Task|[A-Za-z0-9_<>\[\]?,\s]+?)\s+(?<method>[A-Z][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"\bpublic\s+(?:required\s+|virtual\s+|readonly\s+)*(?:\[[^\]]*\]\s+)*(?<type>[A-Za-z_][A-Za-z0-9_<>\[\]\?,\s]*?)\s+(?<col>[A-Z][A-Za-z0-9_]*)\s*(?:\{[^}]*\}|;)")]
    private static partial Regex ColumnRegex();

    [GeneratedRegex(@"^\+\s*(?:public|private)\s+(?:virtual\s+|required\s+|readonly\s+)*(?<type>[A-Za-z_][\w<>\[\],\s\?]*)\s+(?<col>[A-Z]\w*)\s*\{\s*get;", RegexOptions.Multiline)]
    private static partial Regex AddedPropertyRegex();

    [GeneratedRegex(@"\[Table\(""(?<table>[A-Za-z0-9_]+)""")]
    private static partial Regex TableAttrRegex();

    [GeneratedRegex(@"<PackageReference\s+Include=""(?<pkg>[^""]+)""\s+(?:Version=""(?<ver>[^""]*)"")?")]
    private static partial Regex PackageRefRegex();

    [GeneratedRegex(@"""(?<pkg>[^""]+)"":\s*""(?<ver>[^""]*)""")]
    private static partial Regex JsonDepRegex();

    [GeneratedRegex(@"<artifactId>(?<pkg>[^<]+)</artifactId>")]
    private static partial Regex MavenDepRegex();

    [GeneratedRegex(@"<TargetFramework>(?<ver>[^<]+)</TargetFramework>")]
    private static partial Regex TargetFxRegex();

    [GeneratedRegex(@"(?:config\[|Configuration\[|GetValue<[^>]+>\(|GetSection\()""(?<key>[A-Za-z0-9:_\-]+)""")]
    private static partial Regex ConfigKeyRegex();

    [GeneratedRegex(@"(?:Environment\.GetEnvironmentVariable\(|process\.env\.)[""']?(?<key>[A-Za-z0-9_]+)")]
    private static partial Regex EnvKeyRegex();

    [GeneratedRegex(@"asp-for=""(?<f>[A-Za-z0-9_.\[\]]+)""|name=""(?<f2>[A-Za-z][A-Za-z0-9_]*)""")]
    private static partial Regex InputFieldRegex();

    [GeneratedRegex(@"(?:asp-action|asp-page|action)=""(?<a>[A-Za-z0-9_]+)""")]
    private static partial Regex FormActionRegex();

    [GeneratedRegex(@"href=""/?(?<c>[A-Za-z0-9_]+)/(?<a>[A-Za-z0-9_]+)""")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?:fetch|\$\.(?:post|get|ajax))\([""'](?<u>[^""')]+)")]
    private static partial Regex FetchRegex();

    [GeneratedRegex(@"connection\.(?:invoke|send)\([""'](?<m>[^""']+)""")]
    private static partial Regex HubInvokeRegex();
}

internal static class StringPmExtensions
{
    public static string ToCamel(this string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
