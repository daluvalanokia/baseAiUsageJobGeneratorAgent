using System.ComponentModel.DataAnnotations;

namespace SmartAgent.Web.Models;

/// <summary>Form driving multi-year program generation (user interaction layer).</summary>
public sealed class PmPlanInput
{
    [Required, MinLength(3), MaxLength(80)]
    public string Name { get; set; } = "Agile Delivery Programme";

    /// <summary>GitHub repo to ingest (sample: any .NET source in the account).</summary>
    public string? GitHubUrl { get; set; }

    /// <summary>Free-form prompt requirements (used when no GitHub URL is given).</summary>
    public string? Prompt { get; set; }

    [Range(1, 5)]
    public int Years { get; set; } = 2;

    /// <summary>Team size; blank = derived from detected modules.</summary>
    public int? TeamSize { get; set; }

    /// <summary>Annual budget cap in USD; 0/blank = unconstrained.</summary>
    public decimal BudgetCapPerYearUsd { get; set; }

    [Range(3, 25)]
    public int VelocityPerDev { get; set; } = 10;

    public string? SlugToOpen { get; set; }
}
