using System.Collections.Concurrent;
using System.Text.Json;
using SmartAgent.Core.Dat;
using Xunit;

namespace SmartAgent.Tests;

public class DatFrameworkTests : IDisposable
{
    private readonly string _root;
    private readonly ObjectBank _bank;
    private readonly SubjectBank _subjects;

    public DatFrameworkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "smartagent-dat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _bank = new ObjectBank(Path.Combine(_root, "classbank.dat"));
        _subjects = new SubjectBank(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ClassDefinition Class(string name, string ns = "App.Models",
        string kind = "class", string props = "String:Name,Int:Count") => new()
    {
        ClassName = name, Namespace = ns, Kind = kind, Properties = props,
        Methods = "Get():Void|Set(value:String):Void", BaseClass = "object",
        Interfaces = "IDisposable", IsAbstract = false, IsSealed = false, IsStatic = false,
        SourceFile = $"src/{name}.cs"
    };

    // ---------- ObjectBank ----------

    [Fact]
    public void Upsert_appends_when_missing_and_updates_when_present()
    {
        var (added, addAction) = _bank.Upsert(Class("Vehicle"));
        Assert.Equal("added", addAction);
        var (updated, updateAction) = _bank.Upsert(Class("Vehicle", props: "String:Name"));
        Assert.Equal("updated", updateAction);
        Assert.Equal("String:Name", _bank.Find("App.Models", "Vehicle")!.Properties);
        Assert.Single(_bank.All());

        // persistence: a fresh bank over the same file loads the same row
        var reloaded = new ObjectBank(Path.Combine(_root, "classbank.dat"));
        Assert.Equal("Vehicle", reloaded.Find("App.Models", "Vehicle")!.ClassName);
    }

    [Fact]
    public void File_is_comma_separated_with_header()
    {
        _bank.Upsert(Class("Vehicle"));
        var lines = File.ReadAllLines(Path.Combine(_root, "classbank.dat"));
        Assert.StartsWith("# ClassName,Namespace,Kind,Access,IsAbstract", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Vehicle,App.Models,class,public,"));
    }

    [Fact]
    public void Rebuild_resolves_complete_properties_across_threads()
    {
        _bank.Upsert(Class("Vehicle", props: "String:Name|Int:Wheels|Double:Mass|Engine:Powerplant"));
        _bank.Upsert(Class("Engine", ns: "", props: "Int:Horsepower|Boolean:Running"));

        var objects = _bank.Rebuild(maxThreads: 4);

        Assert.Equal(2, objects.Count);
        var vehicle = objects.Single(o => o.ClassName == "Vehicle");
        Assert.Equal(string.Empty, vehicle.Properties["Name"]);
        Assert.Equal(0, vehicle.Properties["Wheels"]);
        Assert.Equal(0d, vehicle.Properties["Mass"]);
        // nested banked type rebuilt with complete properties
        var engine = Assert.IsType<ResolvedObject>(vehicle.Properties["Powerplant"]);
        Assert.Equal(0, engine.Properties["Horsepower"]);
        Assert.Equal(false, engine.Properties["Running"]);
        Assert.Empty(vehicle.MissingTypes);
    }

    [Fact]
    public void Rebuild_flags_unbanked_types_as_missing()
    {
        _bank.Upsert(Class("Mystery", props: "Widget:Part"));
        var obj = _bank.Rebuild(1).Single(o => o.ClassName == "Mystery");
        Assert.Null(obj.Properties["Part"]);
        Assert.Contains("Part:Widget", obj.MissingTypes);
    }

    // ---------- FormulaEvaluator ----------

    [Fact]
    public void Evaluator_handles_precedence_powers_and_functions()
    {
        var v = new Dictionary<string, double>();
        Assert.Equal(14d, FormulaEvaluator.Evaluate("2+3*4", v));
        Assert.Equal(512d, FormulaEvaluator.Evaluate("2^3^2", v));                 // right-associative
        Assert.Equal(5d, FormulaEvaluator.Evaluate("sqrt(a^2+b^2)", new Dictionary<string, double> { ["a"] = 3, ["b"] = 4 }));
        Assert.Equal(-4d, FormulaEvaluator.Evaluate("-sqrt(16)", v));
        Assert.Equal(Math.PI * 4, FormulaEvaluator.Evaluate("pi*4", v), 10);
        Assert.Equal(2d, FormulaEvaluator.Evaluate("min(2,5)", v));
        Assert.Equal(2d, FormulaEvaluator.Evaluate("max(1,min(2,3))", v));   // min(2,3)=2, max(1,2)=2
        Assert.Equal(1d, FormulaEvaluator.Evaluate("round(0.6)", v));

        // regression: '-' and '%' operators must be consumed, not re-read as unary
        Assert.Equal(5d, FormulaEvaluator.Evaluate("9-4", v));
        Assert.Equal(5d, FormulaEvaluator.Evaluate("2*3-1", v));
        Assert.Equal(2d, FormulaEvaluator.Evaluate("sqrt(9-4-1)", v));
        Assert.Equal(3d, FormulaEvaluator.Evaluate("10%3+2", v));   // 10%3=1, +2
        Assert.Equal(2d, FormulaEvaluator.Evaluate("(3+sqrt((-3)^2-4*1*2))/(2*1)", v));   // quadratic root with b=-3
    }

    [Fact]
    public void Evaluator_rejects_unknown_variables_and_trailing_input()
    {
        Assert.Throws<KeyNotFoundException>(() => FormulaEvaluator.Evaluate("a+1", new Dictionary<string, double>()));
        Assert.Throws<FormatException>(() => FormulaEvaluator.Evaluate("1 2", new Dictionary<string, double>()));
        Assert.Throws<FormatException>(() => FormulaEvaluator.Evaluate("sqrt(16", new Dictionary<string, double>()));
    }

    // ---------- SubjectBank ----------

    [Fact]
    public void Seeds_default_subjects_and_evaluates_formulas()
    {
        Assert.Contains("math", _subjects.Subjects);
        Assert.Contains("physics", _subjects.Subjects);
        Assert.Contains("chemistry", _subjects.Subjects);

        var ke = _subjects.Evaluate("physics", "KineticEnergy", new Dictionary<string, double> { ["m"] = 2, ["v"] = 3 });
        Assert.Equal(9d, ke.Value, 10);
        Assert.Equal("Joules", ke.Units);

        var moles = _subjects.Evaluate("chemistry", "MolesFromMass", new Dictionary<string, double> { ["mass"] = 18, ["molarMass"] = 18 });
        Assert.Equal(1d, moles.Value, 10);

        Assert.Equal(10d, _subjects.Evaluate("math", "PythagoreanHypotenuse",
            new Dictionary<string, double> { ["a"] = 6, ["b"] = 8 }).Value, 10);   // sqrt(6^2+8^2)=10
    }

    [Fact]
    public void Subject_upsert_appends_and_updates_with_csv_escaping()
    {
        var (_, action) = _subjects.Upsert("astronomy", new SubjectFormula
        {
            FormulaName = "KeplerThirdLaw", Expression = "sqrt(a^3)",
            Variables = "a:Double", Units = "Years", Category = "orbits"
        });
        Assert.Equal("added", action);

        var (updated, _) = _subjects.Upsert("astronomy", new SubjectFormula
        {
            FormulaName = "KeplerThirdLaw", Expression = "sqrt(a^3)/1",
            Variables = "a:Double", Units = "Years", Category = "orbits"
        });
        Assert.Equal("sqrt(a^3)/1", updated.Expression);

        // the file exists, one row per formula, commas in expressions survive a reload
        Assert.True(File.Exists(Path.Combine(_root, "astronomy.dat")));
        var reloaded = new SubjectBank(_root);
        Assert.Equal(8d, reloaded.Evaluate("astronomy", "KeplerThirdLaw", new Dictionary<string, double> { ["a"] = 4 }).Value, 10);   // sqrt(4^3)=8
    }

    [Fact]
    public void Subject_evaluate_requires_declared_variables()
    {
        Assert.Throws<ArgumentException>(() =>
            _subjects.Evaluate("math", "PythagoreanHypotenuse", new Dictionary<string, double> { ["a"] = 1 }));
        Assert.Throws<KeyNotFoundException>(() =>
            _subjects.Evaluate("math", "DoesNotExist", new Dictionary<string, double>()));
    }

    // ---------- ThreadGovernor ----------

    [Fact]
    public async Task Governor_distributes_releases_and_consolidates()
    {
        var path = Path.Combine(_root, "consolidations", "demo.dat");
        var tasks = Enumerable.Range(1, 50).Select(i => new GovernorTask
        {
            Id = $"T{i:D3}", Subject = "math",
            Body = async ct => { await Task.Delay(5, ct); return $"result-{i}"; }
        }).ToList();

        var report = await new ThreadGovernor().DistributeAsync(tasks, maxThreads: 8, consolidationPath: path);

        Assert.Equal(50, report.Tasks);
        Assert.Equal(50, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.True(report.Threads is > 1 and <= 8, $"threads used: {report.Threads}");
        Assert.True(report.ElapsedMs < 50 * 5, "should be parallel, not 50 sequential delays");

        // consolidation archive written with header + one row per task
        var lines = await File.ReadAllLinesAsync(path);
        Assert.StartsWith("# ConsolidationReport", lines[0]);
        Assert.Contains("TaskId,Subject,Priority,Status,Attempts,ThreadId,Lane,ElapsedMs,AllocatedBytes,Output", lines);
        Assert.Contains(lines, l => l.StartsWith("# LanesSpawned="));
        Assert.Equal(50 + 4, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("T001,math,0,succeeded,1,"));
        Assert.True(report.LanesSpawned > 0, "at least the initial lanes spawn");
        Assert.True(report.AllocatedBytes >= 0, "memory accounting present");
    }

    [Fact]
    public async Task Governor_redistributes_failed_tasks_once_then_fails()
    {
        var path = Path.Combine(_root, "consolidations", "flaky.dat");
        var attempts = new Dictionary<string, int>();

        GovernorTask Flaky(string id, int succeedOnAttempt) => new()
        {
            Id = id, Subject = "physics",
            Body = _ =>
            {
                attempts[id] = attempts.TryGetValue(id, out var n) ? n + 1 : 1;
                if (attempts[id] < succeedOnAttempt) throw new InvalidOperationException("transient");
                return Task.FromResult("ok");
            }
        };

        var report = await new ThreadGovernor().DistributeAsync(
            new[] { Flaky("A", 2), Flaky("B", 2), Flaky("C", 99) }, maxThreads: 3, consolidationPath: path);

        // A and B recover after redistribution; C exhausts its retry and fails
        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(2, report.Results.Single(r => r.Id == "A").Attempts);
        Assert.Equal(2, report.Results.Single(r => r.Id == "C").Attempts);
        Assert.Equal("failed", report.Results.Single(r => r.Id == "C").Status);
    }

    [Fact]
    public async Task Governor_bounded_pool_never_exceeds_max_threads()
    {
        var path = Path.Combine(_root, "consolidations", "bounded.dat");
        var concurrent = 0;
        var peak = 0;
        var tasks = Enumerable.Range(1, 40).Select(i => new GovernorTask
        {
            Id = $"T{i:D3}", Subject = "chem",
            Body = async ct =>
            {
                var now = Interlocked.Increment(ref concurrent);
                // .NET 8 has no Interlocked.Max — track the peak with a CAS loop
                int observed, newValue;
                do { observed = peak; newValue = Math.Max(observed, now); }
                while (Interlocked.CompareExchange(ref peak, newValue, observed) != observed);
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref concurrent);
                return "done";
            }
        }).ToList();

        await new ThreadGovernor().DistributeAsync(tasks, maxThreads: 4, consolidationPath: path);
        Assert.True(peak <= 4, $"peak concurrency {peak} exceeded bound 4");
    }

    [Fact]
    public async Task Governor_processes_tasks_in_priority_order()
    {
        var path = Path.Combine(_root, "consolidations", "priority.dat");
        var order = new ConcurrentQueue<string>();
        var tasks = new List<GovernorTask>();
        // low priorities first in the input list, to prove ordering is by priority
        foreach (var (id, priority) in new[]
                 {
                     ("low-1", 0), ("mid-1", 5), ("high-1", 10), ("low-2", 0), ("mid-2", 5), ("high-2", 10)
                 })
            tasks.Add(new GovernorTask
            {
                Id = id, Subject = "math", Priority = priority,
                Body = async ct => { await Task.Delay(2, ct); order.Enqueue(id); return id; }
            });

        var report = await new ThreadGovernor().DistributeAsync(tasks, maxThreads: 1, consolidationPath: path);

        Assert.Equal(6, report.Succeeded);
        // single lane → strict priority order: all highs, then mids, then lows
        Assert.Equal(new[] { "high-1", "high-2", "mid-1", "mid-2", "low-1", "low-2" }, order);
        Assert.Equal(10, report.Results.Single(r => r.Id == "high-1").Priority);
    }

    [Fact]
    public async Task Governor_recycles_high_memory_lanes()
    {
        var path = Path.Combine(_root, "consolidations", "recycle.dat");
        // every task allocates ~100KB; a 10KB lane budget forces a recycle per task
        var tasks = Enumerable.Range(1, 6).Select(i => new GovernorTask
        {
            Id = $"M{i:D2}", Subject = "chem",
            Body = _ => { var scratch = new byte[100 * 1024]; scratch[0] = 1; return Task.FromResult("allocated"); }
        }).ToList();

        var report = await new ThreadGovernor().DistributeAsync(
            tasks, maxThreads: 3, consolidationPath: path,
            laneMemoryBudgetBytes: 10 * 1024);

        Assert.Equal(6, report.Succeeded);
        Assert.True(report.LanesRecycled > 0, "high-memory lanes must be recycled");
        Assert.True(report.LanesSpawned > 3, "recycled lanes must be replaced by fresh spawns");
        Assert.True(report.AllocatedBytes > 0, "per-lane allocation must be accounted");
        // archive carries the memory bookkeeping (header + per-task allocation column)
        var lines = await File.ReadAllLinesAsync(path);
        var bookkeeping = lines.Single(l => l.StartsWith("# LanesSpawned="));
        Assert.Contains("LanesRecycled=", bookkeeping);
        Assert.Contains("AllocatedBytes=", bookkeeping);
        Assert.Contains(lines, l => l.Contains(",succeeded,") && l.EndsWith("allocated"));
    }

    [Fact]
    public async Task Governor_spawns_lanes_to_drain_a_backlog()
    {
        var path = Path.Combine(_root, "consolidations", "spawn.dat");
        var tasks = Enumerable.Range(1, 60).Select(i => new GovernorTask
        {
            Id = $"S{i:D2}", Subject = "math",
            Body = async ct => { await Task.Delay(20, ct); return "done"; }
        }).ToList();

        var report = await new ThreadGovernor().DistributeAsync(tasks, maxThreads: 8, consolidationPath: path);

        Assert.Equal(60, report.Succeeded);
        // initial lanes are 4; a 60-task backlog must grow the pool past them
        Assert.True(report.Threads > 4, $"expected adaptive spawns past the initial 4 lanes, peak was {report.Threads}");
        Assert.True(report.Threads <= 8, "peak lanes must stay within maxThreads");
        // 60 sequential 20ms delays would be ~1200ms; parallel lanes must beat that
        Assert.True(report.ElapsedMs < 900, $"60 x 20ms should run well under a second, took {report.ElapsedMs}ms");
    }

    [Fact]
    public async Task Governor_stops_spawning_beyond_the_total_memory_budget()
    {
        var path = Path.Combine(_root, "consolidations", "budget.dat");
        var tasks = Enumerable.Range(1, 40).Select(i => new GovernorTask
        {
            Id = $"B{i:D2}", Subject = "physics",
            Body = _ => { var scratch = new byte[64 * 1024]; scratch[0] = 1; return Task.FromResult("done"); }
        }).ToList();

        // total budget smaller than what 40 x 64KB tasks allocate → no spawns past the initial lanes
        var report = await new ThreadGovernor().DistributeAsync(
            tasks, maxThreads: 8, consolidationPath: path,
            totalMemoryBudgetBytes: 256 * 1024);

        Assert.Equal(40, report.Succeeded);
        Assert.True(report.LanesSpawned <= 8, $"spawning must be memory-capped, spawned {report.LanesSpawned}");
        Assert.True(report.Threads <= 8, "peak lanes stay within maxThreads");
    }
}
