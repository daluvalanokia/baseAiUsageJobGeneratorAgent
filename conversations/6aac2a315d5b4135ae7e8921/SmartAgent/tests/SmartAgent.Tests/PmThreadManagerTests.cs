using System.Diagnostics;
using SmartAgent.Core.Pm;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class PmThreadManagerTests
{
    private static SourceSnapshot MultiFileSnapshot(int controllers, int views)
    {
        var files = new List<SourceFile> { new() { Path = "repo/README.md", Content = "# Demo app" } };
        for (var i = 1; i <= controllers; i++)
            files.Add(new SourceFile
            {
                Path = $"repo/src/App.Web/Controllers/Module{i}Controller.cs",
                Content = $$"""
                    using Microsoft.AspNetCore.Mvc;
                    public sealed class Module{{i}}Controller : Controller
                    {
                        public IActionResult Index() => View();
                        public IActionResult Detail(int id) => View();
                        [HttpPost] public IActionResult Create(string title) => RedirectToAction(nameof(Index));
                    }
                    public sealed class Module{{i}}Entity
                    {
                        public int Id { get; set; }
                        [System.ComponentModel.DataAnnotations.StringLength(120)]
                        public string Title { get; set; } = "";
                    }
                    """
            });
        for (var i = 1; i <= views; i++)
            files.Add(new SourceFile
            {
                Path = $"repo/src/App.Web/Views/Module{i}/Index.cshtml",
                Content = $"<form method=\"post\" action=\"/Module{i}/Create\">" +
                          "<input type=\"text\" name=\"Title\" /><button>save</button></form><table><tr><td>1</td></tr></table>"
            });
        return new SourceSnapshot
        {
            SourceType = SourceType.GitHub, SourceName = "owner/repo", SourceDetail = "branch main",
            Files = files
        };
    }

    // ---------- CPU fan-out ----------

    [Fact]
    public void Fan_out_preserves_input_order()
    {
        var manager = new PmThreadManager(cpuThreads: 4);
        var items = Enumerable.Range(0, 50).ToList();
        // bodies finish out of order (reverse sleep) — output must still match input order
        var results = manager.FanOut("test: order", items,
            i => { Thread.Sleep((50 - i)); return $"item-{i}"; });
        Assert.Equal(50, results.Count);
        for (var i = 0; i < 50; i++) Assert.Equal($"item-{i}", results[i]);
    }

    [Fact]
    public void Fan_out_records_stage_timing()
    {
        var manager = new PmThreadManager(cpuThreads: 4);
        manager.FanOut("test: timing", Enumerable.Range(0, 20).ToList(), i => i * 2);
        var timing = Assert.Single(manager.Timings);
        Assert.Equal("test: timing", timing.Stage);
        Assert.Equal(20, timing.Items);
        Assert.Equal(4, timing.Threads);
        Assert.True(timing.ElapsedMs >= 0);
    }

    [Fact]
    public void Sequential_fallback_matches_parallel_results()
    {
        var snapshot = MultiFileSnapshot(controllers: 6, views: 8);
        var parallel = new SourceAnalyzer(new PmThreadManager(cpuThreads: 4));
        var sequential = new SourceAnalyzer(PmThreadManager.SequentialFallback());

        var classesParallel = parallel.Classes(snapshot);
        var classesSequential = sequential.Classes(snapshot);
        Assert.Equal(classesSequential.Count, classesParallel.Count);
        for (var i = 0; i < classesSequential.Count; i++)
        {
            Assert.Equal(classesSequential[i].Name, classesParallel[i].Name);
            Assert.Equal(classesSequential[i].Kind, classesParallel[i].Kind);
            Assert.Equal(classesSequential[i].Methods, classesParallel[i].Methods);
        }

        var viewsParallel = parallel.ViewDetailsOf(snapshot);
        var viewsSequential = sequential.ViewDetailsOf(snapshot);
        Assert.Equal(viewsSequential.Count, viewsParallel.Count);
        for (var i = 0; i < viewsSequential.Count; i++)
        {
            Assert.Equal(viewsSequential[i].Name, viewsParallel[i].Name);
            Assert.Equal(viewsSequential[i].Fields, viewsParallel[i].Fields);
        }
    }

    // ---------- I/O fan-out ----------

    [Fact]
    public async Task Fan_out_async_bounds_in_flight_requests()
    {
        var manager = new PmThreadManager(ioThreads: 3);
        var inFlight = 0;
        var peak = 0;
        var gate = new ManualResetEventSlim(false);
        var items = Enumerable.Range(0, 12).ToList();

        // first three lanes park at the gate so concurrency is observable
        var started = 0;
        var results = await manager.FanOutAsync("test: io bound", items, async (i, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (gate) if (now > peak) peak = now;
            if (Interlocked.Increment(ref started) <= 3) gate.Wait(500);
            await Task.Delay(10, ct);
            Interlocked.Decrement(ref inFlight);
            return i;
        });
        Assert.Equal(items.Count, results.Count);
        for (var i = 0; i < items.Count; i++) Assert.Equal(i, results[i]);
        Assert.True(peak <= 3, $"peak concurrency {peak} exceeded the 3-lane bound");
        var timing = Assert.Single(manager.Timings);
        Assert.Equal(3, timing.Threads);
    }

    [Fact]
    public async Task Fan_out_async_speeds_up_io_bound_work()
    {
        var manager = new PmThreadManager(ioThreads: 6);
        var sw = Stopwatch.StartNew();
        await manager.FanOutAsync("test: io speed", Enumerable.Range(0, 6).ToList(),
            async (i, ct) => { await Task.Delay(60, ct); return i; });
        sw.Stop();
        // six 60ms fetches on six lanes must finish far below the 360ms serial time
        Assert.True(sw.ElapsedMilliseconds < 300, $"wave crawl took {sw.ElapsedMilliseconds}ms — lanes not running in parallel");
    }

    // ---------- generator integration ----------

    [Fact]
    public async Task Functional_crawl_fans_out_and_still_sees_every_route()
    {
        var handler = new FakeAppHandler();
        for (var i = 1; i <= 6; i++)
            handler.Add($"/Mod{i}", $"<html><head><title>Mod{i}</title></head><body><a href=\"/Mod{i}\">self</a> link</body></html>");
        handler.Add("/Dashboard", "<html><body>" +
            "<a href=\"/Mod1\">1</a> <a href=\"/Mod2\">2</a> <a href=\"/Mod3\">3</a> " +
            "<a href=\"/Mod4\">4</a> <a href=\"/Mod5\">5</a> <a href=\"/Mod6\">6</a></body></html>");
        var gen = new FunctionalSpecGenerator(() => handler, new PmThreadManager(ioThreads: 4));

        var spec = await gen.CaptureAsync("https://app.test:5000/Dashboard", "user", "pass");

        for (var i = 1; i <= 6; i++)
            Assert.Contains(spec.Pages, p => p.Route == $"/Mod{i}");
        Assert.NotEmpty(spec.Summary);
    }

    [Fact]
    public async Task Governor_generation_records_parallel_stage_timings()
    {
        var governor = new PmGovernor(new SmartAgent.Core.Dat.ThreadGovernor(),
            new RequirementIngestor(), new SprintPlanner(), new PmQualityEngine(),
            new FunctionalSpecGenerator(), new PmThreadManager(cpuThreads: 4));
        var root = Path.Combine(Path.GetTempPath(), "smartagent-tt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = await governor.GenerateAsync(MultiFileSnapshot(controllers: 3, views: 3),
                new PmOptions { Name = "ThreadTest" }, root);
            Assert.NotEmpty(plan.Timings);
            Assert.Contains(plan.Timings, t => t.Stage.Contains("class extraction"));
            Assert.Contains(plan.Timings, t => t.Stage.Contains("sprint engineering pack"));
            Assert.NotEmpty(plan.ThreadSummary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
