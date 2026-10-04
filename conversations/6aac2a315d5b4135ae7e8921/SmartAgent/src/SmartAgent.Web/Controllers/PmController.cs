using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartAgent.Core.Pm;
using SmartAgent.Domain;
using SmartAgent.SourceProviders;
using SmartAgent.Web.Models;
using SmartAgent.Web.Services;

namespace SmartAgent.Web.Controllers;

/// <summary>
/// Project-management direction layer: user interaction drives program
/// generation (budget, team, years), the MVC PM Hub renders task management
/// (sprints/stories), resource management (teams + RACI chart) and the
/// budget model; /api/pm exposes the same capability as JSON.
/// </summary>
public sealed class PmController(
    PmGovernor pmGovernor, GitHubSourceProvider gitHub, ProgramStore programStore,
    IWebHostEnvironment env, IConfiguration config, ILogger<PmController> logger) : Controller
{
    // ---------- MVC (user interaction) ----------

    [HttpGet]
    public IActionResult Index() => View("Plan", new PmPlanInput { Name = "Agile Delivery Programme" });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Plan(PmPlanInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View("Plan", input);

        try
        {
            var plan = await GenerateAsync(input, ct);
            programStore.Save(plan);
            return RedirectToAction("Hub", new { slug = plan.Slug });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Program generation failed: {ex.Message}");
            logger.LogError(ex, "PM program generation failed for {Name}", input.Name);
            return View("Plan", input);
        }
    }

    [HttpGet]
    public IActionResult Hub(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View("Hub", plan);
    }

    [HttpGet]
    public IActionResult Gantt(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult Sprints(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult Sprint(string slug, int number)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        var sprint = plan.SprintPlans.FirstOrDefault(s => s.Number == number) ?? plan.SprintPlans.FirstOrDefault();
        if (sprint is null) return NotFound($"No sprint {number} in '{slug}'.");
        return View(new PmSprintPage(plan, sprint));
    }

    [HttpGet]
    public IActionResult Teams(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult Requirements(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult DesignDocs(string slug, int number)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(new PmSprintPickerPage(plan, number < 1 ? 1 : number));
    }

    [HttpGet]
    public IActionResult DbScripts(string slug, int number)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(new PmSprintPickerPage(plan, number < 1 ? 1 : number));
    }

    [HttpGet]
    public IActionResult TestCases(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult Issues(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult Releases(string slug)
    {
        var plan = programStore.Load(slug);
        if (plan is null) return NotFound($"No program '{slug}'. Generate one from /Pm.");
        return View(plan);
    }

    [HttpGet]
    public IActionResult Programs()
    {
        ViewBag.Programs = programStore.All();
        return View("Plan", new PmPlanInput());
    }

    // ---------- API ----------

    /// <summary>Renders a self-contained static HTML PM hub (no server needed) for a saved program.</summary>
    [HttpGet("~/Pm/Export")]
    public IActionResult Export(string? slug)
    {
        var plan = programStore.Load(slug ?? "");
        if (plan is null)
            return NotFound(new { error = $"no program '{slug}'" });

        // inline the hub stylesheet so the export is a single portable file
        var cssPath = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"),
            "css", "pm-hub.css");
        if (System.IO.File.Exists(cssPath))
            ViewData["PmCss"] = System.IO.File.ReadAllText(cssPath);

        Response.Headers.CacheControl = "no-store";
        return View("Export", plan);
    }

    [HttpGet("~/api/pm/programs")]
    public ActionResult ListPrograms() => Ok(new
    {
        count = programStore.All().Count,
        programs = programStore.All().Select(p => new { p.Slug, p.Name, p.SourceName,
            p.Years, p.Sprints, p.UserStoryCount, p.TotalStoryPoints, p.Budget.TotalUsd, p.GeneratedUtc })
    });

    [HttpGet("~/api/pm/programs/{slug}")]
    public ActionResult GetProgram(string slug)
    {
        var plan = programStore.Load(slug);
        return plan is null ? NotFound(new { error = $"no program '{slug}'" }) : Ok(plan);
    }

    /// <summary>Generates a program from GitHub source or a prompt and returns the full plan JSON.</summary>
    [HttpPost("~/api/pm/program")]
    public async Task<ActionResult> GenerateProgram([FromBody] PmPlanInput input, CancellationToken ct)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Name))
            return BadRequest(new { error = "name is required" });
        if (string.IsNullOrWhiteSpace(input.GitHubUrl) && string.IsNullOrWhiteSpace(input.Prompt))
            return BadRequest(new { error = "provide a GitHub URL or a prompt" });

        try
        {
            var plan = await GenerateAsync(input, ct);
            programStore.Save(plan);
            return Ok(plan);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PM API generation failed");
            return BadRequest(new { error = ex.Message });
        }
    }

    // ---------- shared generation ----------

    private async Task<ProgramPlan> GenerateAsync(PmPlanInput input, CancellationToken ct)
    {
        var options = new PmOptions
        {
            Name = input.Name.Trim(), Years = input.Years, TeamSize = input.TeamSize,
            BudgetCapPerYearUsd = input.BudgetCapPerYearUsd, VelocityPerDev = input.VelocityPerDev
        };

        SourceSnapshot snapshot;
        if (!string.IsNullOrWhiteSpace(input.GitHubUrl))
        {
            var token = config["SmartAgent:GitHub:Token"] ?? Environment.GetEnvironmentVariable("GITHUB_ACCESS_TOKEN");
            snapshot = await gitHub.FetchAsync(new SourceRequest
            {
                SourceType = SourceType.GitHub, Url = input.GitHubUrl.Trim(), GitHubToken = token,
                CancellationToken = ct
            });
        }
        else
        {
            snapshot = new SourceSnapshot
            {
                SourceType = SourceType.ZipArchive, SourceName = "prompt",
                SourceDetail = input.Prompt ?? string.Empty, Files = new List<SourceFile>()
            };
        }

        var programsRoot = Path.Combine(env.ContentRootPath,
            config["SmartAgent:Pm:ProgramsRoot"] ?? "data/programs");
        var sw = Stopwatch.StartNew();
        var plan = await pmGovernor.GenerateAsync(snapshot, options,
            Path.Combine(env.ContentRootPath, config["SmartAgent:DatRoot"] ?? "data/dat", "programs"), ct);
        plan.SourceDetail += $" | generated in {sw.ElapsedMilliseconds}ms";
        return plan;
    }
}
