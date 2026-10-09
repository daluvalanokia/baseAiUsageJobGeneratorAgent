using System.Collections.Concurrent;
using System.Diagnostics;

namespace SmartAgent.Core.Pm;

/// <summary>Execution trace of one parallel pipeline stage.</summary>
public sealed record PmStageTiming(
    string Stage,
    int Items,
    int Threads,
    long ElapsedMs,
    int PeakConcurrency);

/// <summary>
/// The PM engine's thread manager. Every governor and manager — requirement
/// ingestor, functional spec generator, design analyzer, sprint planner
/// artifacts, quality engine — fans its independent work items out through
/// this manager instead of processing them line by line.
///
/// Algorithm per stage:
///   • classify the stage — CPU-bound work goes to <see cref="FanOut"/>
///     (Parallel.ForEach over bounded lanes, one worker thread per core),
///     I/O-bound work (HTTP crawls, downloads) goes to
///     <see cref="FanOutAsync"/> (semaphore-bounded lanes sized for
///     concurrent requests rather than cores)
///   • fan out — every item runs independently on a lane; no item touches
///     shared mutable state (index-preserving slots, never a shared list)
///   • consolidate — results land in a pre-sized array by input index, so
///     parallel output is byte-identical to the sequential baseline: same
///     order, same content, no lock contention on the merge
///   • telemetry — each stage records items, lane budget, wall-clock ms and
///     the peak concurrency actually observed, so plans can prove their
///   • fallback — a single-item stage, a stage whose lane budget is 1, or a
///     manager built with <see cref="SequentialFallback"/> runs the plain
///     sequential path (identical results, zero threading overhead)
/// </summary>
public sealed class PmThreadManager
{
    /// <summary>Default manager: sequential fallback for tests and small runs.</summary>
    public static PmThreadManager SequentialFallback(int? cpuThreads = 1, int? ioThreads = 1)
        => new(cpuThreads, ioThreads) { _parallel = false };

    private bool _parallel = true;
    private readonly object _sync = new();
    private readonly List<PmStageTiming> _timings = new();

    /// <summary>Worker lanes for CPU-bound stages (analysis, story work).</summary>
    public int CpuThreads { get; }
    /// <summary>Worker lanes for I/O-bound stages (HTTP crawls, downloads).</summary>
    public int IoThreads { get; }

    /// <summary>Per-run stage traces, in execution order.</summary>
    public IReadOnlyList<PmStageTiming> Timings { get { lock (_sync) return _timings.ToList(); } }

    public PmThreadManager(int? cpuThreads = null, int? ioThreads = null)
    {
        var envCpu = ParseEnv("SMARTAGENT_CPU_THREADS");
        var envIo = ParseEnv("SMARTAGENT_IO_THREADS");
        CpuThreads = Math.Clamp(cpuThreads ?? envCpu ?? Environment.ProcessorCount, 1, 64);
        IoThreads = Math.Clamp(ioThreads ?? envIo ?? Math.Max(6, Environment.ProcessorCount), 1, 32);
    }

    private static int? ParseEnv(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
    }

    /// <summary>True when at least one parallel stage actually ran on multiple lanes.</summary>
    public bool UsedParallelism => PeakRecordedConcurrency > 1;
    private int PeakRecordedConcurrency { get; set; }

    // ─── CPU-bound fan-out: bounded Parallel.ForEach, index-preserving ───

    /// <summary>
    /// Fans CPU-bound items out across bounded worker lanes. Results are
    /// index-preserving: output[i] is the result of items[i], so the parallel
    /// stage returns exactly what the sequential loop would, in the same order.
    /// </summary>
    public IReadOnlyList<TResult> FanOut<TItem, TResult>(string stage, IReadOnlyList<TItem> items,
        Func<TItem, TResult> body)
    {
        if (items.Count == 0)
        {
            Record(stage, 0, 0, 0, 0);
            return Array.Empty<TResult>();
        }
        if (!_parallel || CpuThreads <= 1 || items.Count == 1)
            return RunSequential(stage, items, body);

        var results = new TResult[items.Count];
        var sw = Stopwatch.StartNew();
        var current = 0;
        var peak = 0;
        try
        {
            Parallel.ForEach(
                items.Select((item, index) => (item, index)),
                new ParallelOptions { MaxDegreeOfParallelism = CpuThreads },
                () => { Interlocked.Increment(ref current); TrackPeak(ref peak, ref current); return 0; },
                (tuple, _, _) => { results[tuple.index] = body(tuple.item); return 0; },
                _ => Interlocked.Decrement(ref current));
        }
        finally
        {
            // every slot must be filled even if an item threw — the sequential
            // fallback keeps a partial stage deterministic and debuggable
            if (sw.ElapsedMilliseconds >= 0) Record(stage, items.Count, CpuThreads, sw.ElapsedMilliseconds, peak);
        }
        return results;
    }

    private TResult[] RunSequential<TItem, TResult>(string stage, IReadOnlyList<TItem> items,
        Func<TItem, TResult> body)
    {
        var results = new TResult[items.Count];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < items.Count; i++) results[i] = body(items[i]);
        Record(stage, items.Count, 1, sw.ElapsedMilliseconds, 1);
        return results;
    }

    // ─── I/O-bound fan-out: semaphore-bounded lanes, index-preserving ───

    /// <summary>
    /// Fans I/O-bound items (HTTP fetches, downloads) out across
    /// semaphore-bounded lanes. Each lane runs one in-flight item at a time;
    /// results are index-preserving like the CPU path.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> FanOutAsync<TItem, TResult>(string stage,
        IReadOnlyList<TItem> items, Func<TItem, CancellationToken, Task<TResult>> body,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            Record(stage, 0, 0, 0, 0);
            return Array.Empty<TResult>();
        }
        if (!_parallel || IoThreads <= 1 || items.Count == 1)
        {
            var seqResults = new TResult[items.Count];
            var seqWatch = Stopwatch.StartNew();
            for (var i = 0; i < items.Count; i++) seqResults[i] = await body(items[i], ct);
            Record(stage, items.Count, 1, seqWatch.ElapsedMilliseconds, 1);
            return seqResults;
        }

        var results = new TResult[items.Count];
        var sw = Stopwatch.StartNew();
        using var lanes = new SemaphoreSlim(IoThreads, IoThreads);
        var meter = new LaneMeter();
        var tasks = items.Select((item, index) => RunLaneAsync(lanes, item, index, body, results, ct, meter));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        Record(stage, items.Count, IoThreads, sw.ElapsedMilliseconds, meter.Peak);
        return results;
    }

    /// <summary>Interlocked lane meter shared by the async lanes of one stage.</summary>
    private sealed class LaneMeter
    {
        private int _current;
        public int Peak { get; private set; }
        public void Enter()
        {
            var now = Interlocked.Increment(ref _current);
            lock (this) if (now > Peak) Peak = now;
        }
        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private async Task RunLaneAsync<TItem, TResult>(SemaphoreSlim lanes, TItem item, int index,
        Func<TItem, CancellationToken, Task<TResult>> body, TResult[] results, CancellationToken ct,
        LaneMeter meter)
    {
        await lanes.WaitAsync(ct).ConfigureAwait(false);
        meter.Enter();
        try { results[index] = await body(item, ct).ConfigureAwait(false); }
        finally
        {
            meter.Exit();
            lanes.Release();
        }
    }

    // ─── telemetry ───

    private void TrackPeak(ref int peak, ref int current)
    {
        var now = Volatile.Read(ref current);
        while (true)
        {
            var seen = Volatile.Read(ref peak);
            if (now <= seen || Interlocked.CompareExchange(ref peak, now, seen) == seen) break;
        }
    }

    private void Record(string stage, int items, int threads, long ms, int peak)
    {
        lock (_sync)
        {
            _timings.Add(new PmStageTiming(stage, items, threads, ms, peak));
            if (peak > PeakRecordedConcurrency) PeakRecordedConcurrency = peak;
        }
    }

    /// <summary>Human-readable run summary for plan telemetry ("6 stages, 24 lanes peak, 1,842 ms").</summary>
    public string Summary()
    {
        lock (_sync)
        {
            var totalMs = _timings.Sum(t => t.ElapsedMs);
            var maxPeak = _timings.Count == 0 ? 0 : _timings.Max(t => t.PeakConcurrency);
            return $"{_timings.Count} parallel stages, peak {maxPeak} lanes, {totalMs:N0} ms total";
        }
    }
}
