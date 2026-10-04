namespace SmartAgent.Core.Pm;

/// <summary>View-facing helpers for the PM Hub: squad colors, priority classes.</summary>
public static class PmHub
{
    public static readonly string[] SquadPalette =
    {
        "#2563EB", "#16A34A", "#0D9488", "#4F46E5", "#EA580C", "#DB2777", "#CA8A04", "#475569"
    };

    public static string SquadColor(string key)
    {
        var i = key is { Length: > 0 } && char.IsAsciiLetter(key[0]) ? char.ToUpperInvariant(key[0]) - 'A' : 0;
        return SquadPalette[Math.Clamp(i, 0, SquadPalette.Length - 1)];
    }

    /// <summary>Squad owning a module (via epic ownership).</summary>
    public static string SquadOfModule(ProgramPlan plan, string module)
    {
        var epic = plan.Epics.FirstOrDefault(e => e.Module.Equals(module, StringComparison.OrdinalIgnoreCase));
        if (epic is null) return "A";
        var squad = plan.Squads.FirstOrDefault(sq => sq.EpicKeys.Contains(epic.Key));
        return squad?.Key ?? "A";
    }

    /// <summary>MoSCoW class from the 0-100 priority score.</summary>
    public static (string Label, string Css) MoScow(int priority) => priority switch
    {
        >= 70 => ("Must", "must"),
        >= 40 => ("Should", "should"),
        _ => ("Could", "could")
    };
}
