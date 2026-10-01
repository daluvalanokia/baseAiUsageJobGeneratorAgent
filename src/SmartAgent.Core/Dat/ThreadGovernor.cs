using System.Collections.Concurrent;
using System.Diagnostics;

namespace SmartAgent.Core.Dat;

/// <summary>A single unit of work handed to the governor for threaded execution.</summary>
public sealed record GovernorTask
{
    public required string Id { get; init; }
    public string Subject { get; init; } = "general";
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
    public long ElapsedMs { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
}

/// <summary>
/// Top-level multi-threading framework: creates a bounded pool of worker
/// threads, distributes tasks from a shared queue (fast threads naturally
/// take more work), redistributes a failed task by re-queuing it (bounded
/// retries), releases threads as the queue drains, and consolidates every
/// result into a comma-separated '.dat' archive.
/// </summary>
public sealed class ThreadGovernor
{
    public int MaxRetriesPerTask { get; init; } = 1;
    public long LatestRunTasks { get; private set; }
    public int LatestRunThreads { get; private set; }
    public DateTime? LatestRunUtc { get; private set; }

    public async Task<ConsolidationReport> DistributeAsync(
        IReadOnlyList<GovernorTask> tasks,
        int maxThreads,
        string consolidationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0) throw new ArgumentException("At least one task is required");
        maxThreads = Math.Clamp(maxThreads, 1, 64);

        var queue = new ConcurrentQueue<(GovernorTask Task, int Attempt)>();
        foreach (var task in tasks) queue.Enqueue((task, 1));
        var results = new ConcurrentDictionary<string, GovernorTaskResult>(StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();

        var workerCount = Math.Min(maxThreads, tasks.Count);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => RunWorkerAsync(queue, results, ct), ct));
        await Task.WhenAll(workers);

        LatestRunTasks = tasks.Count;
        // worker lanes created and released by this run
        LatestRunThreads = workerCount;
        LatestRunUtc = DateTime.UtcNow;

        var ordered = results.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var report = new ConsolidationReport(
            tasks.Count, workerCount,
            ordered.Count(r => r.Status == "succeeded"), ordered.Count(r => r.Status == "failed"),
            sw.ElapsedMilliseconds, consolidationPath, ordered);

        Consolidate(consolidationPath, report);
        return report;
    }

    private async Task RunWorkerAsync(ConcurrentQueue<(GovernorTask Task, int Attempt)> queue,
        ConcurrentDictionary<string, GovernorTaskResult> results,
        CancellationToken ct)
    {
        // drain the shared queue; when a task fails with retries left it is
        // re-queued (redistribution) and any worker — this one or another —
        // may pick it up
        while (queue.TryDequeue(out var entry))
        {
            var (task, attempt) = entry;
            var sw = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var output = await task.Body(timeout.Token);
                results[task.Id] = new GovernorTaskResult
                {
                    Id = task.Id, Subject = task.Subject, Status = "succeeded", Attempts = attempt,
                    ThreadId = Environment.CurrentManagedThreadId, ElapsedMs = sw.ElapsedMilliseconds, Output = output
                };
            }
            catch (Exception ex)
            {
                if (attempt <= MaxRetriesPerTask && !ct.IsCancellationRequested)
                {
                    queue.Enqueue((task, attempt + 1));   // redistribute
                    continue;
                }
                results[task.Id] = new GovernorTaskResult
                {
                    Id = task.Id, Subject = task.Subject, Status = "failed", Attempts = attempt,
                    ThreadId = Environment.CurrentManagedThreadId, ElapsedMs = sw.ElapsedMilliseconds,
                    Error = ex.Message
                };
            }
        }
        // queue empty → thread is released (worker exits)
    }

    private static void Consolidate(string path, ConsolidationReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path, append: false);
        w.WriteLine("# ConsolidationReport");
        w.WriteLine($"# GeneratedUtc={DateTime.UtcNow:O},Tasks={report.Tasks},Threads={report.Threads},Succeeded={report.Succeeded},Failed={report.Failed},ElapsedMs={report.ElapsedMs}");
        w.WriteLine("TaskId,Subject,Status,Attempts,ThreadId,ElapsedMs,Output");
        foreach (var r in report.Results)
        {
            var payload = r.Status == "failed" && !string.IsNullOrEmpty(r.Error) ? $"error: {r.Error}" : r.Output;
            w.WriteLine($"{r.Id},{r.Subject},{r.Status},{r.Attempts},{r.ThreadId},{r.ElapsedMs},{Csv(payload)}");
        }
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\""
            : value;
}

public sealed record ConsolidationReport(
    int Tasks, int Threads, int Succeeded, int Failed, long ElapsedMs,
    string ConsolidationFile, IReadOnlyList<GovernorTaskResult> Results);
