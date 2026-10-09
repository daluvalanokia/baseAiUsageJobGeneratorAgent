using System.ComponentModel.DataAnnotations;

namespace SmartAgent.Core.Pm;

/// <summary>Main project page input: GitHub source, app URL, duration, budget, team.</summary>
public sealed class PmProjectInput
{
    [Required, MinLength(3), MaxLength(80)]
    public string Name { get; set; } = "My Project";

    /// <summary>GitHub repository holding the .NET solution to analyze.</summary>
    [Required]
    public string? GitHubUrl { get; set; }

    /// <summary>Live app URL to validate requirements against (optional).</summary>
    [Url]
    public string? FunctionalUrl { get; set; }
    public string? FunctionalUser { get; set; }
    public string? FunctionalPassword { get; set; }

    /// <summary>Project duration in weeks (converted to years for the engine).</summary>
    [Required, Range(2, 260)]
    public int Weeks { get; set; } = 52;

    /// <summary>Total budget in USD; 0 = unconstrained (split evenly across years).</summary>
    [Range(0, 100_000_000)]
    public decimal TotalBudgetUsd { get; set; }

    /// <summary>Team size; blank = derived from detected modules.</summary>
    [Range(0, 200)]
    public int? TeamSize { get; set; }

    /// <summary>Project weeks folded into whole engine years (min 1).</summary>
    public int YearsFor() => Math.Max(1, (int)Math.Ceiling(Weeks / 52.0));

    /// <summary>Total budget split evenly into per-year caps; 0 = unconstrained.</summary>
    public decimal PerYearBudgetFor() => TotalBudgetUsd > 0
        ? decimal.Round(TotalBudgetUsd / YearsFor(), MidpointRounding.AwayFromZero) : 0;

    /// <summary>The prompt the PM hub analysis runs on (Ingest intent).</summary>
    public string ComposePrompt() =>
        $"PM hub analysis of {GitHubUrl!.Trim()}"
        + (string.IsNullOrWhiteSpace(FunctionalUrl) ? "" : $" with live app at {FunctionalUrl!.Trim()}")
        + $", {Weeks} weeks, budget {(TotalBudgetUsd > 0 ? $"${TotalBudgetUsd:N0} USD" : "unconstrained")}"
        + (TeamSize is > 0 ? $", team of {TeamSize}" : "");
}

/// <summary>Result card for the main project page after a successful generation.</summary>
public sealed record PmProjectResult(
    string Slug, string Name, string ComposedPrompt,
    string ExportLink, string HubLink,
    int Sprints, int Weeks, int Stories, int StoryPoints, decimal BudgetUsd, int TeamSize, int TestCases);
