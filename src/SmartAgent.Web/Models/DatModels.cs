using SmartAgent.Core.Dat;

namespace SmartAgent.Web.Models;

public sealed class ClassDefinitionRequest
{
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Kind { get; set; } = "class";
    public string Access { get; set; } = "public";
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    public bool IsStatic { get; set; }
    public string BaseClass { get; set; } = string.Empty;
    public string Interfaces { get; set; } = string.Empty;
    public string Properties { get; set; } = string.Empty;
    public string Methods { get; set; } = string.Empty;
    public string Constructors { get; set; } = string.Empty;
    public string Events { get; set; } = string.Empty;
    public string GenericParameters { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;

    public ClassDefinition ToDefinition(DateTime? addedUtc = null) => new()
    {
        ClassName = ClassName, Namespace = Namespace, Kind = Kind, Access = Access,
        IsAbstract = IsAbstract, IsSealed = IsSealed, IsStatic = IsStatic, BaseClass = BaseClass,
        Interfaces = Interfaces, Properties = Properties, Methods = Methods, Constructors = Constructors,
        Events = Events, GenericParameters = GenericParameters, SourceFile = SourceFile,
        AddedUtc = addedUtc ?? DateTime.UtcNow
    };
}

public sealed class RebuildRequest
{
    /// <summary>Upper bound of spawned rebuild threads (1..64). Default 4.</summary>
    public int? MaxThreads { get; set; }
}

public sealed class SubjectFormulaRequest
{
    public string Subject { get; set; } = string.Empty;
    public string FormulaName { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public string Variables { get; set; } = string.Empty;
    public string Units { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public sealed class EvaluateFormulaRequest
{
    public string Subject { get; set; } = string.Empty;
    public string FormulaName { get; set; } = string.Empty;
    public Dictionary<string, double> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DistributeRequest
{
    /// <summary>Consolidation subject; file becomes data/dat/consolidations/&lt;subject&gt;.dat.</summary>
    public string Subject { get; set; } = "consolidation";
    /// <summary>Number of governor tasks to run (1..500). Default 12.</summary>
    public int? TaskCount { get; set; }
    /// <summary>Worker thread pool size (1..64). Default from RunOptions.MaxThreads or 4.</summary>
    public int? MaxThreads { get; set; }
    /// <summary>Simulated work per task in ms (0..5000). Default 20.</summary>
    public int? WorkMs { get; set; }
    /// <summary>How many of the tasks should fail on their first attempt (tests redistribution).</summary>
    public int? FailFirstAttempts { get; set; }
    /// <summary>Assign descending priorities so earlier tasks are processed first (priority processing).</summary>
    public bool Prioritize { get; set; }
    /// <summary>Each task allocates this many KB to simulate memory-heavy work (0 = off).</summary>
    public int? MemoryHogKb { get; set; }
    /// <summary>Per-lane allocation budget in KB; a lane crossing it is GC-trimmed and recycled.</summary>
    public int? LaneMemoryBudgetKb { get; set; }
    /// <summary>Run-wide allocation budget in KB; lane spawning stops beyond it.</summary>
    public int? TotalMemoryBudgetKb { get; set; }
}

public sealed class DistributeResponse
{
    public int Tasks { get; set; }
    public int Threads { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public long ElapsedMs { get; set; }
    public int LanesSpawned { get; set; }
    public int LanesRecycled { get; set; }
    public long AllocatedBytes { get; set; }
    public string ConsolidationFile { get; set; } = string.Empty;
    public List<object> Results { get; set; } = new();
}

public sealed class ResolvedObjectDto
{
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Dictionary<string, object?> Properties { get; set; } = new();
    public IReadOnlyList<string> MissingTypes { get; set; } = Array.Empty<string>();
    public int ThreadId { get; set; }
    public DateTime ResolvedUtc { get; set; }
}
