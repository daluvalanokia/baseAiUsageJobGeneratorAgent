using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using SmartAgent.Core;
using SmartAgent.Core.Dat;
using SmartAgent.Domain;
using SmartAgent.Web.Models;

namespace SmartAgent.Web.Controllers;

/// <summary>
/// DAT framework surface — the top-level multi-threaded processing layer:
///   • ObjectBank   : comma-separated OO classification catalog (.dat) with
///                    hash-dictionary load and threaded object rebuild
///   • SubjectBank  : per-subject formula '.dat' columns (math, physics,
///                    chemistry, ...) with a built-in expression evaluator
///   • ThreadGovernor: bounded worker pool — create, distribute, redistribute
///                    failed work, release, and consolidate results to '.dat'
/// Same optional X-Api-Key guard as the other mutating endpoints.
/// </summary>
[ApiController]
[Route("api/dat")]
public sealed class DatController(
    ObjectBank objectBank,
    SubjectBank subjectBank,
    ThreadGovernor governor,
    IConfiguration config) : ControllerBase
{
    // ---------- status ----------

    [HttpGet("status")]
    public ActionResult Status()
    {
        return Ok(new
        {
            classes = objectBank.Count,
            subjects = subjectBank.Subjects,
            subjectFormulas = subjectBank.Subjects.ToDictionary(
                s => s, s => subjectBank.List(s).Count),
            governor = new { governor.LatestRunTasks, Threads = governor.LatestRunThreads, governor.LatestRunUtc }
        });
    }

    // ---------- object bank ----------

    [HttpPost("classes")]
    public ActionResult UpsertClass([FromBody] ClassDefinitionRequest body)
    {
        if (!Authorized()) return Unauthorized();
        if (body is null || string.IsNullOrWhiteSpace(body.ClassName))
            return BadRequest(new { error = "className is required" });

        try
        {
            var (def, action) = objectBank.Upsert(body.ToDefinition());
            return Ok(new { action, @class = def });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("classes")]
    public ActionResult ListClasses()
    {
        var all = objectBank.All();
        return Ok(new
        {
            count = all.Count,
            columns = ClassDefinition.Header,
            classes = all.Values.OrderBy(c => c.Key)
        });
    }

    /// <summary>Spawn threads and rebuild every banked class into a fully property-populated object.</summary>
    [HttpPost("classes/rebuild")]
    public ActionResult Rebuild([FromBody] RebuildRequest? body)
    {
        if (!Authorized()) return Unauthorized();
        if (objectBank.Count == 0) return Ok(new { rebuilt = 0, threads = 0, objects = Array.Empty<ResolvedObjectDto>() });

        var threads = body?.MaxThreads is >= 1 and <= 64 ? body.MaxThreads.Value : 4;
        var objects = objectBank.Rebuild(threads);
        return Ok(new
        {
            rebuilt = objects.Count,
            threads,
            objects = objects.Select(o => new ResolvedObjectDto
            {
                ClassName = o.ClassName, Namespace = o.Namespace, Kind = o.Kind,
                Properties = o.Properties.ToDictionary(kv => kv.Key, kv => kv.Value),
                MissingTypes = o.MissingTypes, ThreadId = o.ThreadId, ResolvedUtc = o.ResolvedUtc
            })
        });
    }

    // ---------- subject formula banks ----------

    [HttpPost("subjects")]
    public ActionResult UpsertFormula([FromBody] SubjectFormulaRequest body)
    {
        if (!Authorized()) return Unauthorized();
        if (body is null || string.IsNullOrWhiteSpace(body.Subject) ||
            string.IsNullOrWhiteSpace(body.FormulaName) || string.IsNullOrWhiteSpace(body.Expression))
            return BadRequest(new { error = "subject, formulaName and expression are required" });

        try
        {
            var (formula, action) = subjectBank.Upsert(body.Subject, new SubjectFormula
            {
                FormulaName = body.FormulaName, Expression = body.Expression,
                Variables = body.Variables, Units = body.Units, Category = body.Category
            });
            return Ok(new { action, formula });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("subjects/{subject}")]
    public ActionResult ListFormulas(string subject) => Ok(new
    {
        subject,
        columns = SubjectFormula.Header,
        formulas = subjectBank.List(subject)
    });

    /// <summary>Evaluates a banked formula with the given variable values.</summary>
    [HttpPost("evaluate")]
    public ActionResult Evaluate([FromBody] EvaluateFormulaRequest body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Subject) || string.IsNullOrWhiteSpace(body.FormulaName))
            return BadRequest(new { error = "subject and formulaName are required" });

        try
        {
            var result = subjectBank.Evaluate(body.Subject, body.FormulaName, body.Variables);
            return Ok(new
            {
                subject = body.Subject, formula = result.Formula.FormulaName,
                expression = result.Formula.Expression, value = result.Value, units = result.Units,
                inputs = result.Inputs
            });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    // ---------- thread governor ----------

    /// <summary>
    /// Demonstrates the top-level threading model: distributes TaskCount units
    /// of subject-formula evaluation work across a bounded worker pool,
    /// redistributes failed first attempts, releases threads and consolidates
    /// every result into a '.dat' archive.
    /// </summary>
    [HttpPost("distribute")]
    public async Task<ActionResult> Distribute([FromBody] DistributeRequest? body, CancellationToken ct)
    {
        if (!Authorized()) return Unauthorized();

        var subject = string.IsNullOrWhiteSpace(body?.Subject) ? "consolidation" : body!.Subject.Trim();
        var taskCount = body?.TaskCount is >= 1 and <= 500 ? body!.TaskCount.Value : 12;
        var maxThreads = body?.MaxThreads is >= 1 and <= 64 ? body!.MaxThreads.Value : 4;
        var workMs = body?.WorkMs is >= 0 and <= 5000 ? body!.WorkMs.Value : 20;
        var failFirst = Math.Clamp(body?.FailFirstAttempts ?? 0, 0, taskCount);

        // spread the tasks over every formula in every subject (round-robin)
        var formulas = subjectBank.Subjects
            .SelectMany(s => subjectBank.List(s).Select(f => (Subject: s, Formula: f)))
            .ToList();
        if (formulas.Count == 0)
            return BadRequest(new { error = "no formulas banked to distribute" });

        var failOnce = new ConcurrentDictionary<string, bool>();
        foreach (var i in Enumerable.Range(1, failFirst)) failOnce[$"T{i:D4}"] = true;
        var random = new Random(42);

        var tasks = Enumerable.Range(1, taskCount).Select(i =>
        {
            var (formulaSubject, formula) = formulas[(i - 1) % formulas.Count];
            var inputs = SubjectBank.DeclaredVariables(formula)
                .ToDictionary(v => v, _ => 1 + random.NextDouble() * 9, StringComparer.OrdinalIgnoreCase);
            return new GovernorTask
            {
                Id = $"T{i:D4}",
                Subject = $"{formulaSubject}:{formula.FormulaName}",
                Body = async token =>
                {
                    await Task.Delay(workMs, token);
                    if (failOnce.TryRemove($"T{i:D4}", out _))
                        throw new InvalidOperationException("simulated first-attempt failure (redistribution test)");
                    var result = subjectBank.Evaluate(formulaSubject, formula.FormulaName, inputs);
                    return $"{formula.FormulaName}={result.Value:0.####}{result.Units}";
                }
            };
        }).ToList();

        var path = Path.Combine(subjectBank.RootDirectory, "consolidations", $"{subject.ToLowerInvariant()}.dat");

        try
        {
            var report = await governor.DistributeAsync(tasks, maxThreads, path, ct);
            return Ok(new DistributeResponse
            {
                Tasks = report.Tasks, Threads = report.Threads, Succeeded = report.Succeeded,
                Failed = report.Failed, ElapsedMs = report.ElapsedMs, ConsolidationFile = path,
                Results = report.Results.Select(r => (object)new
                {
                    r.Id, r.Subject, r.Status, r.Attempts, r.ThreadId, r.ElapsedMs,
                    output = r.Output, error = r.Error
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private bool Authorized()
    {
        var key = config["SmartAgent:ApiKey"];
        return string.IsNullOrEmpty(key) ||
               Request.Headers.TryGetValue("X-Api-Key", out var provided) && provided == key;
    }
}
