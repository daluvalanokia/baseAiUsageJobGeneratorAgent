using System.Text.Json;
using SmartAgent.Core.Pm;

namespace SmartAgent.Web.Services;

/// <summary>Persists generated program plans as JSON under data/programs.</summary>
public sealed class ProgramStore(string rootDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = rootDirectory;

    public void Save(ProgramPlan plan)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, $"{plan.Slug}.json"), JsonSerializer.Serialize(plan, Json));
        // living artifacts: regenerated on every save so the solution's docs
        // folder (requirements, design document, sequence diagram, change
        // impact) always matches the current plan
        LivingArtifactsComposer.Write(plan, Path.Combine(Root, plan.Slug), plan.Prompt);
    }

    /// <summary>The living artifacts folder for a plan, if written.</summary>
    public string? ArtifactsDir(string slug)
    {
        var dir = Path.Combine(Root, slug, LivingArtifactsComposer.FolderName);
        return Directory.Exists(dir) ? dir : null;
    }

    public ProgramPlan? Load(string slug)
    {
        var path = Path.Combine(Root, $"{slug}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ProgramPlan>(File.ReadAllText(path)) : null;
    }

    public IReadOnlyList<ProgramPlan> All() =>
        Directory.Exists(Root)
            ? Directory.GetFiles(Root, "*.json")
                .Select(f => JsonSerializer.Deserialize<ProgramPlan>(File.ReadAllText(f)))
                .Where(p => p != null).Cast<ProgramPlan>().OrderByDescending(p => p.GeneratedUtc).ToList()
            : new List<ProgramPlan>();
}
