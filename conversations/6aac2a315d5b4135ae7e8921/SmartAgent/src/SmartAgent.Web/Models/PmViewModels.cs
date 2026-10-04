using Microsoft.AspNetCore.Mvc.ViewFeatures;
using SmartAgent.Core.Pm;

namespace SmartAgent.Web.Models;

/// <summary>View page for one sprint with its full engineering detail.</summary>
public sealed record PmSprintPage(ProgramPlan Plan, PmSprint Sprint);

/// <summary>View page for a sprint-picked section (design docs / db scripts).</summary>
public sealed record PmSprintPickerPage(ProgramPlan Plan, int SprintNumber);


/// <summary>Layout metadata passed to the PM shell via ViewData.</summary>
public sealed record PmLayoutInfo(string Brand, string Slug, int Years, int Sprints, int Weeks,
    int Team, string PlatformShort);

/// <summary>Sets the PM shell layout info from a program plan.</summary>
public static class PmLayout
{
    public const string Key = "PmLayout";

    public static void Apply(ViewDataDictionary viewData, ProgramPlan plan)
    {
        viewData[Key] = new PmLayoutInfo(plan.Name, plan.Slug, plan.Years, plan.Sprints,
            plan.Weeks, plan.Resources.Count, plan.Platform?.ShortName ?? "Platform");
    }
}

public static partial class PmPhaseBadge
{
    public static (string Label, string Css) Phase(string phase) => phase switch
    {
        "framework" => ("framework", "phase-framework"),
        "build" => ("build", "phase-build"),
        "enhance" => ("enhance", "phase-enhance"),
        "release" => ("release", "phase-release"),
        "stabilization" => ("stabilization", "phase-stab"),
        _ => ("build", "phase-build")
    };

    public static (string Label, string Css) Layer(string layer) => layer switch
    {
        "Database" => ("Database", "layer-db"),
        "Data Adapter" => ("Data Adapter", "layer-da"),
        "Controllers" => ("Controllers", "layer-ctl"),
        "Frontend" => ("Frontend", "layer-fe"),
        "Server" => ("Server", "layer-srv"),
        "Framework" => ("Framework", "layer-fw"),
        _ => ("Server", "layer-srv")
    };
}
