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
public sealed partial class SourceAnalyzer
{
    /// <summary>Captures classes with their public methods from source files.</summary>
    public IReadOnlyList<PmClassInfo> Classes(SourceSnapshot snapshot)
    {
        var result = new List<PmClassInfo>();
        foreach (var file in snapshot.Files)
        {
            if (!IsCode(file.Path)) continue;
            var module = ModuleOf(file.Path);
            foreach (var m in ClassRegex().Matches(file.Content).Cast<Match>())
            {
                var name = m.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var body = ExtractBalanced(m.Index, file.Content);
                var methods = MethodRegex().Matches(body).Cast<Match>()
                    .Select(x => x.Groups["method"].Value)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct().Take(8).ToList();
                result.Add(new PmClassInfo
                {
                    Name = name, Module = module, File = file.Path,
                    Kind = KindOf(name, file.Path), Methods = methods
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
        if (moduleClasses.Count == 0) moduleClasses = classes.Take(2).ToList();
        var cases = new List<PmUnitTestCase>();
        var seq = startSeq;
        foreach (var cls in moduleClasses.Take(2))
        {
            var methods = cls.Methods.Count > 0 ? cls.Methods : new List<string> { "primary action" };
            foreach (var method in methods.Take(2))
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

    private static string KindOf(string className, string path) =>
        className.Contains("Controller", StringComparison.OrdinalIgnoreCase) ? "Controller"
        : path.Contains("Hub", StringComparison.OrdinalIgnoreCase) && className.EndsWith("Hub") ? "Hub"
        : className.Contains("Service", StringComparison.OrdinalIgnoreCase) ? "Service"
        : path.Contains("Models", StringComparison.OrdinalIgnoreCase)
          || path.Contains("entities", StringComparison.OrdinalIgnoreCase) ? "Entity"
        : path.Contains("components", StringComparison.OrdinalIgnoreCase) ? "Component"
        : "Other";

    /// <summary>Module of a source path, mirroring RequirementIngestor's logic.</summary>
    internal static string ModuleOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] is "src" or "lib" && i + 1 < parts.Length - 1)
                return parts[i + 1].Replace("SmartAgent.", "").Replace("SmartAgent", "Core");
        return parts.Length > 1 ? parts[0] : Path.GetFileNameWithoutExtension(path);
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
}

internal static class StringPmExtensions
{
    public static string ToCamel(this string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
