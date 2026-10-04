using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SmartAgent.Core.Dat;

/// <summary>A single unit of work handed to the governor for threaded execution.</summary>
public sealed record GovernorTask
{
    public required string Id { get; init; }
    public string Subject { get; init; } = "general";
    /// <summary>Priority processing: higher runs first (default 0, FIFO within a level).</summary>
    public int Priority { get; init; }
    public required Func<CancellationToken, Task<string>> Body { get; init; }
}

/// <summary>Per-task outcome recorded by the governor.</summary>
public sealed record GovernorTaskResult
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }        // succeeded | failed
    public int Attempts { get; init; }
    public int ThreadId { get; init; }
    /// <summary>Governor lane that executed the task (stable across thread-pool hops).</summary>
    public int Lane { get; init; }
    public int Priority { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>Bytes allocated on the executing lane while running this task.</summary>
    public long AllocatedBytes { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
}

/// <summary>
/// Top-level multi-threading framework with memory-managed, priority-aware lanes:
///   • priority processing  — higher-priority tasks are taken from the queue first
///   • memory accounting    — per-task and per-lane allocation is measured with
///                            GC.GetAllocatedBytesForCurrentThread()
///   • high-memory lanes    — a lane crossing LaneMemoryBudgetBytes is trimmed
///                            (GC.Collect) and recycled; the supervisor spawns a
///                            fresh replacement lane while work remains
///   • total budget         — lane spawning stops once TotalMemoryBudgetBytes
///                            is allocated, keeping the run inside its footprint
///   • adaptive spawning    — the supervisor grows lanes toward the backlog
///                            (backlog / TasksPerLane) up to maxThreads, so extra
///                            lanes are spawned only when they speed processing up
///   • redistribution       — a failed task is re-queued once (bounded retries)
///   • consolidation        — every result is archived to a '.dat' CSV report
/// </summary>
public sealed class ThreadGovernor
{
    /// <summary>Bounded retries per task (redistribution attempts).</summary>
    public int MaxRetriesPerTask { get; init; } = 1;
    /// <summary>Lanes created before adaptive spawning kicks in (0 = auto: min(4, maxThreads)).</summary>
    public int InitialLanes { get; init; }
    /// <summary>Backlog per lane that justifies spawning another lane.</summary>
    public int TasksPerLane { get; init; } = 8;
    /// <summary>Per-lane allocation budget; crossing it recycles (trims) the lane.</summary>
    public long LaneMemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>Run-wide allocation budget; no new lanes spawn beyond it.</summary>
    public long TotalMemoryBudgetBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long LatestRunTasks { get; private set; }
    public int LatestRunThreads { get; private set; }
    public DateTime? LatestRunUtc { get; private set; }
    public int LatestRunLanesSpawned { get; private set; }
    public int LatestRunLanesRecycled { get; private set; }
    public long LatestRunAllocatedBytes { get; private set; }

    private sealed record PendingEntry(GovernorTask Task, int Attempt, long Seq);

    /// <summary>Shared mutable state for one governed run.</summary>
    private sealed class RunState
    {
        public required object Sync { get; init; }
        public required List<PendingEntry> Pending { get; init; }
        public required ConcurrentDictionary<string, GovernorTaskResult> Results { get; init; }
        public required CancellationToken Ct { get; init; }
        public required List<Task> Workers { get; init; }
        public int LaneSeq;
        public int Remaining;          // tasks not yet terminally resolved
        public long TotalAllocated;    // run-wide allocation, Interlocked
        public int ActiveLanes;        // lanes currently draining
        public int PeakLanes;
        public int LanesSpawned;
        public int LanesRecycled;
        public int SpawnCeiling;       // hard cap on lane spawns (bounded recycling)
        public long Seq;
        public bool Trimmed;          // one GC trim pass after the total budget trip

        public void Enqueue(GovernorTask task, int attempt)
        {
            lock (Sync) Pending.Add(new PendingEntry(task, attempt, Interlocked.Increment(ref Seq)));
        }

        /// <summary>Priority processing: highest priority first, FIFO within a level.</summary>
        public bool TryTake(out PendingEntry entry)
        {
            lock (Sync)
            {
                if (Pending.Count == 0) { entry = null!; return false; }
                var best = 0;
                for (var i = 1; i < Pending.Count; i++)
                {
                    var p = Pending[i];
                    var b = Pending[best];
                    if (p.Task.Priority > b.Task.Priority ||
                        (p.Task.Priority == b.Task.Priority && p.Seq < b.Seq))
                        best = i;
                }
                entry = Pending[best];
                Pending.RemoveAt(best);
                return true;
            }
        }
    }

    public async Task<ConsolidationReport> DistributeAsync(
        IReadOnlyList<GovernorTask> tasks,
        int maxThreads,
        string consolidationPath,
        CancellationToken ct = default,
        int? initialLanes = null,
        int? tasksPerLane = null,
        long? laneMemoryBudgetBytes = null,
        long? totalMemoryBudgetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0) throw new ArgumentException("At least one task is required");
        var maxLanes = Math.Clamp(maxThreads, 1, 64);
        var perLane = Math.Max(1, tasksPerLane ?? TasksPerLane);
        var laneBudget = laneMemoryBudgetBytes ?? LaneMemoryBudgetBytes;
        var totalBudget = totalMemoryBudgetBytes ?? TotalMemoryBudgetBytes;
        var initial = Math.Clamp(initialLanes ?? InitialLanes, 0, maxLanes);
        if (initial == 0) initial = Math.Min(4, Math.Min(maxLanes, tasks.Count));

        var state = new RunState
        {
            Sync = new object(),
            Pending = new List<PendingEntry>(tasks.Count),
            Results = new ConcurrentDictionary<string, GovernorTaskResult>(StringComparer.OrdinalIgnoreCase),
            Ct = ct,
            Workers = new List<Task>(),
            Remaining = tasks.Count,
            SpawnCeiling = maxLanes + tasks.Count   // every task may, worst case, recycle a lane
        };
        foreach (var task in tasks) state.Enqueue(task, 1);

        var sw = Stopwatch.StartNew();

        void SpawnLane()
        {
            var laneId = Interlocked.Increment(ref state.LaneSeq);
            // claim the lane synchronously so the spawn cap is honored even
            // before the async lane body starts running
            Interlocked.Increment(ref state.ActiveLanes);
            var worker = Task.Run(() => RunLaneAsync(state, laneId, laneBudget), ct);
            lock (state.Sync) state.Workers.Add(worker);
            Interlocked.Increment(ref state.LanesSpawned);
        }

        // seed the initial lanes
        for (var i = 0; i < initial; i++) SpawnLane();
        TrackPeak(state);

        // supervisor: adaptive spawning + memory-resolved lane replacement
        while (Volatile.Read(ref state.Remaining) > 0 && !ct.IsCancellationRequested)
        {
            await Task.Delay(5, ct).ConfigureAwait(false);
            if (Volatile.Read(ref state.Remaining) == 0) break;

            // resolve memory usage: once the run-wide budget trips, trim once and
            // stop adding parallel lanes (existing lanes keep draining)
            var allocated = Volatile.Read(ref state.TotalAllocated);
            if (allocated >= totalBudget)
            {
                if (!state.Trimmed)
                {
                    state.Trimmed = true;
                    GC.Collect();
                }
            }

            int backlog;
            lock (state.Sync) backlog = state.Pending.Count;

            // target lanes from the backlog, grown toward maxLanes; memory cap applies
            var target = Math.Min(maxLanes, Math.Max(initial, (backlog + perLane - 1) / perLane));
            if (allocated < totalBudget)
            {
                while (Volatile.Read(ref state.Remaining) > 0 &&
                       Volatile.Read(ref state.LanesSpawned) < state.SpawnCeiling &&
                       Volatile.Read(ref state.ActiveLanes) < target)
                {
                    SpawnLane();
                    TrackPeak(state);
                }
            }
        }

        Task[] workersSnapshot;
        lock (state.Sync) workersSnapshot = state.Workers.ToArray();
        await Task.WhenAll(workersSnapshot).ConfigureAwait(false);

        sw.Stop();
        LatestRunTasks = tasks.Count;
        LatestRunThreads = state.PeakLanes;          // peak lanes created and released by this run
        LatestRunLanesSpawned = state.LanesSpawned;
        LatestRunLanesRecycled = state.LanesRecycled;
        LatestRunAllocatedBytes = state.TotalAllocated;
        LatestRunUtc = DateTime.UtcNow;

        var ordered = state.Results.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var report = new ConsolidationReport(
            tasks.Count, state.PeakLanes,
            ordered.Count(r => r.Status == "succeeded"), ordered.Count(r => r.Status == "failed"),
            sw.ElapsedMilliseconds, consolidationPath, ordered,
            state.LanesSpawned, state.LanesRecycled, state.TotalAllocated);

        Consolidate(consolidationPath, report);
        return report;
    }

    private static void TrackPeak(RunState s)
    {
        int observed, candidate;
        do
        {
            observed = Volatile.Read(ref s.PeakLanes);
            candidate = Math.Max(observed, Volatile.Read(ref s.ActiveLanes));
        }
        while (Interlocked.CompareExchange(ref s.PeakLanes, candidate, observed) != observed);
    }

    private async Task RunLaneAsync(RunState state, int laneId, long laneBudget)
    {
        try
        {
            var laneAllocated = 0L;
            while (!state.Ct.IsCancellationRequested)
            {
                if (!state.TryTake(out var entry))
                {
                    // queue empty → release the lane once no work remains anywhere
                    if (Volatile.Read(ref state.Remaining) == 0) return;
                    await Task.Delay(1).ConfigureAwait(false);   // poll until work or done
                    continue;
                }

                var (task, attempt) = (entry.Task, entry.Attempt);
                var sw = Stopwatch.StartNew();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(state.Ct);
                    var output = await task.Body(timeout.Token).ConfigureAwait(false);
                    var delta = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    Interlocked.Add(ref state.TotalAllocated, delta);
                    laneAllocated += delta;
                    state.Results[task.Id] = new GovernorTaskResult
                    {
                        Id = task.Id, Subject = task.Subject, Status = "succeeded", Attempts = attempt,
                        ThreadId = Environment.CurrentManagedThreadId, Lane = laneId, Priority = task.Priority,
                        ElapsedMs = sw.ElapsedMilliseconds, AllocatedBytes = delta, Output = output
                    };
                    if (Interlocked.Decrement(ref state.Remaining) == 0) return;
                }
                catch (Exception ex)
                {
                    var delta = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    Interlocked.Add(ref state.TotalAllocated, delta);
                    laneAllocated += delta;
                    if (attempt <= MaxRetriesPerTask && !state.Ct.IsCancellationRequested)
                    {
                        state.Enqueue(task, attempt + 1);   // redistribute; any lane may pick it up
                        continue;
                    }
                    state.Results[task.Id] = new GovernorTaskResult
                    {
                        Id = task.Id, Subject = task.Subject, Status = "failed", Attempts = attempt,
                        ThreadId = Environment.CurrentManagedThreadId, Lane = laneId, Priority = task.Priority,
                        ElapsedMs = sw.ElapsedMilliseconds, AllocatedBytes = delta, Error = ex.Message
                    };
                    if (Interlocked.Decrement(ref state.Remaining) == 0) return;
                }

                // high-memory lane focus: a lane crossing its budget is trimmed and
                // recycled; the supervisor spawns a fresh replacement while work remains
                if (laneAllocated >= laneBudget)
                {
                    Interlocked.Increment(ref state.LanesRecycled);
                    GC.Collect();          // resolve memory usage before replacing the lane
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref state.ActiveLanes);
        }
    }

    private static void Consolidate(string path, ConsolidationReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path, append: false);
        w.WriteLine("# ConsolidationReport");
        w.WriteLine($"# GeneratedUtc={DateTime.UtcNow:O},Tasks={report.Tasks},Threads={report.Threads},Succeeded={report.Succeeded},Failed={report.Failed},ElapsedMs={report.ElapsedMs}");
        w.WriteLine($"# LanesSpawned={report.LanesSpawned},LanesRecycled={report.LanesRecycled},AllocatedBytes={report.AllocatedBytes}");
        w.WriteLine("TaskId,Subject,Priority,Status,Attempts,ThreadId,Lane,ElapsedMs,AllocatedBytes,Output");
        foreach (var r in report.Results)
        {
            var payload = r.Status == "failed" && !string.IsNullOrEmpty(r.Error) ? $"error: {r.Error}" : r.Output;
            w.WriteLine($"{r.Id},{r.Subject},{r.Priority},{r.Status},{r.Attempts},{r.ThreadId},{r.Lane},{r.ElapsedMs},{r.AllocatedBytes},{Csv(payload)}");
        }
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\""
            : value;
}

public sealed record ConsolidationReport(
    int Tasks, int Threads, int Succeeded, int Failed, long ElapsedMs,
    string ConsolidationFile, IReadOnlyList<GovernorTaskResult> Results,
    int LanesSpawned = 0, int LanesRecycled = 0, long AllocatedBytes = 0);
