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
