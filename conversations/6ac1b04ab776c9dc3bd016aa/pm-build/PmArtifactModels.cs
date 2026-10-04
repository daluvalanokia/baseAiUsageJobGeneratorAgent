using System.Text.Json.Serialization;

namespace SmartAgent.Core.Pm;

/// <summary>Unit test case grounded in a captured class/method.</summary>
public sealed record PmUnitTestCase
{
    public required string Id { get; init; }               // UT-S01-001
    public required string Cls { get; init; }
    public required string Method { get; init; }
    public required string Scenario { get; init; }
    public required string Expected { get; init; }
}

/// <summary>Feature-level system (acceptance) test.</summary>
public sealed record PmSystemTest
{
    public required string Id { get; init; }               // ST-S01-001
    public required string Feature { get; init; }
    public required string Precondition { get; init; }
    public required string Steps { get; init; }
    public required string Expected { get; init; }
    public required string Env { get; init; }
}

/// <summary>One logged defect in the issue register.</summary>
public sealed record PmIssue
{
    public required string Id { get; init; }               // BUG-S01-001
    public required int SprintNumber { get; init; }
    public required string Severity { get; init; }         // Critical / Major / Minor / Trivial
    public required string Component { get; init; }
    public required string Description { get; init; }
    public required string Reporter { get; init; }
    public required string Assignee { get; init; }
    public required string Status { get; init; }           // Open / In Progress / Resolved
}

/// <summary>Dual-provider database migration script for one sprint.</summary>
public sealed record PmDbScript
{
    public required string MigrationClass { get; init; }   // 001_project_bootstrap
    public required string PrimaryLabel { get; init; }
    public required string PrimaryDdl { get; init; }
    public required string SecondaryLabel { get; init; }
    public required string SecondaryDdl { get; init; }
    public required string RollbackPrimary { get; init; }
    public required string RollbackSecondary { get; init; }
    public required string SeedDmlPrimary { get; init; }
    public required string SeedDmlSecondary { get; init; }
}

/// <summary>Code artifact ledger: new vs modified vs cumulative file counts.</summary>
public sealed record PmCodeArtifact
{
    public IReadOnlyList<string> NewFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ModifiedFiles { get; init; } = Array.Empty<string>();
    public int CumulativeCount { get; init; }
}

/// <summary>One step of a sequence diagram.</summary>
public sealed record PmSequenceStep
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Label { get; init; }
    public bool IsReturn { get; init; }
}

/// <summary>High-level design for a sprint's module.</summary>
public sealed record PmHld
{
    public required string ModuleName { get; init; }
    public required string Purpose { get; init; }
    public IReadOnlyList<string> Components { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExternalDeps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Nfrs { get; init; } = Array.Empty<string>();
}

/// <summary>Detailed design: pseudo-code, sequence, data flow, error handling.</summary>
public sealed record PmDdd
{
    public required string PseudoCode { get; init; }
    public IReadOnlyList<PmSequenceStep> Sequence { get; init; } = Array.Empty<PmSequenceStep>();
    public required string DataFlow { get; init; }
    public required string ErrorHandling { get; init; }
    public IReadOnlyList<string> ConfigDeps { get; init; } = Array.Empty<string>();
}

/// <summary>Full engineering detail attached to one sprint.</summary>
public sealed record PmSprintDetail
{
    public required PmHld Hld { get; init; }
    public required PmDdd Ddd { get; init; }
    public IReadOnlyList<PmUnitTestCase> UnitTests { get; init; } = Array.Empty<PmUnitTestCase>();
    public IReadOnlyList<PmSystemTest> SystemTests { get; init; } = Array.Empty<PmSystemTest>();
    public IReadOnlyList<PmIssue> Issues { get; init; } = Array.Empty<PmIssue>();
    public PmDbScript? DbScript { get; init; }
    public PmCodeArtifact CodeArtifacts { get; init; } = new();
    public IReadOnlyList<string> ImplChecklist { get; init; } = Array.Empty<string>();
}

/// <summary>Release milestone with GO/NO-GO criteria.</summary>
public sealed record PmMilestone
{
    public required string Name { get; init; }
    public required int Sprint { get; init; }
    public required int Week { get; init; }
    public required string Scope { get; init; }
    public IReadOnlyList<string> GoNoGo { get; init; } = Array.Empty<string>();
}

/// <summary>Detected tech platform architecture profile.</summary>
public sealed record PmPlatformArch
{
    public IReadOnlyList<string> WebLayer { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ServiceLayer { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DataLayer { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DomainTables { get; init; } = Array.Empty<string>();
}

/// <summary>Tech platform profile detected from the source.</summary>
public sealed record PmPlatform
{
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required string Color { get; init; }
    public required string Os { get; init; }
    public required string Backend { get; init; }
    public required string BackendLang { get; init; }
    public required string PrimaryDb { get; init; }
    public required string SecondaryDb { get; init; }
    public IReadOnlyList<string> Infra { get; init; } = Array.Empty<string>();
    public required string BestFor { get; init; }
    public IReadOnlyList<string> Advantages { get; init; } = Array.Empty<string>();
    public required PmPlatformArch Arch { get; init; }
}
