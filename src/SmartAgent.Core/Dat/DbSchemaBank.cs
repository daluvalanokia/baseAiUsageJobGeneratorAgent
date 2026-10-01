using System.Collections.Concurrent;
using System.Diagnostics;

namespace SmartAgent.Core.Dat;

/// <summary>Database entity kinds resolved by the schema bank.</summary>
public enum DbEntityKind
{
    Table,
    View,
    Index,
    ClusteredIndex,
    Cluster,
    Partition,
    PrimaryKey,
    ForeignKey,
    UniqueKey
}

/// <summary>
/// One banked database entity: table, view, index, clustered index, cluster,
/// partition, primary/foreign/unique key. Stored as a pipe-delimited row in
/// the dbbank '.dat' catalog.
/// </summary>
public sealed record DbEntity
{
    public required string Key { get; init; }                // "dbo.Vehicles"
    public DbEntityKind Kind { get; set; } = DbEntityKind.Table;
    public string Schema { get; init; } = "dbo";
    public string Name { get; init; } = string.Empty;
    /// <summary>Numeric identifier used to partition entities across threads.</summary>
    public int Identifier { get; init; }
    /// <summary>Column list for tables/views: "Id:Int,Name:String".</summary>
    public string Columns { get; init; } = string.Empty;
    /// <summary>Key/index columns: "Id" or "DriverId".</summary>
    public string IndexColumns { get; init; } = string.Empty;
    public bool IsClustered { get; init; }
    public string PartitionScheme { get; init; } = string.Empty;
    /// <summary>Partition range value (e.g. "2024" for a yearly scheme).</summary>
    public string Range { get; init; } = string.Empty;
    /// <summary>Entity this key/index/partition is attached to ("dbo.Vehicles").</summary>
    public string Parent { get; init; } = string.Empty;
    /// <summary>Foreign key target: "dbo.Drivers(Id)".</summary>
    public string References { get; init; } = string.Empty;

    public const string Header =
        "# DbEntityCatalog — one database entity per row, pipe-delimited\n" +
        "# Key|Kind|Schema|Name|Identifier|Columns|IndexColumns|IsClustered|PartitionScheme|Range|Parent|References";

    public string ToRow() =>
        string.Join('|', Key, Kind.ToString(), Schema, Name, Identifier,
            Columns, IndexColumns, IsClustered, PartitionScheme, Range, Parent, References);

    public static DbEntity? FromRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) return null;
        var f = line.Split('|');
        if (f.Length < 12) return null;
        return new DbEntity
        {
            Key = f[0].Trim(), Kind = ParseKind(f[1]), Schema = f[2].Trim(), Name = f[3].Trim(),
            Identifier = int.TryParse(f[4], out var id) ? id : 0,
            Columns = f[5].Trim(), IndexColumns = f[6].Trim(), IsClustered = bool.TryParse(f[7], out var c) && c,
            PartitionScheme = f[8].Trim(), Range = f[9].Trim(), Parent = f[10].Trim(), References = f[11].Trim()
        };
    }

    public static DbEntityKind ParseKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "table" => DbEntityKind.Table,
        "view" => DbEntityKind.View,
        "index" => DbEntityKind.Index,
        "clusteredindex" or "clustered-index" => DbEntityKind.ClusteredIndex,
        "cluster" => DbEntityKind.Cluster,
        "partition" => DbEntityKind.Partition,
        "primarykey" or "pk" => DbEntityKind.PrimaryKey,
        "foreignkey" or "fk" => DbEntityKind.ForeignKey,
        "uniquekey" or "uk" => DbEntityKind.UniqueKey,
        _ => throw new ArgumentException($"unknown database entity kind '{kind}'")
    };
}

/// <summary>A key (PK/FK/unique) attached to a resolved data model.</summary>
public sealed record ResolvedKey(DbEntityKind Kind, string Name, IReadOnlyList<string> Columns, string References);

/// <summary>An index (clustered or non-clustered) attached to a resolved data model.</summary>
public sealed record ResolvedIndex(string Name, IReadOnlyList<string> Columns, bool IsClustered);

/// <summary>A partition attached to a cluster/table model.</summary>
public sealed record ResolvedPartition(string Name, string Scheme, string Range);

/// <summary>
/// A data model materialized from the schema bank: a table or view with its
/// columns typed-defaulted, plus the keys, indexes and partitions attached to
/// it. Foreign keys carry the resolved target model key.
/// </summary>
public sealed record ResolvedDbModel
{
    public required string Key { get; init; }
    public required DbEntityKind Kind { get; init; }
    public string Schema { get; init; } = "dbo";
    public string Name { get; init; } = string.Empty;
    public int Identifier { get; init; }
    public required IReadOnlyDictionary<string, object?> Columns { get; init; }
    public IReadOnlyList<ResolvedKey> Keys { get; set; } = Array.Empty<ResolvedKey>();
    public IReadOnlyList<ResolvedIndex> Indexes { get; set; } = Array.Empty<ResolvedIndex>();
    public IReadOnlyList<ResolvedPartition> Partitions { get; set; } = Array.Empty<ResolvedPartition>();
    /// <summary>FK targets that could not be resolved to a banked model.</summary>
    public IReadOnlyList<string> MissingReferences { get; set; } = Array.Empty<string>();
    public int ThreadId { get; init; }
    public DateTime ResolvedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Outcome of a threaded schema resolution.</summary>
public sealed record DbSchemaResolution(
    int Entities, int Resolved, int Threads, long ElapsedMs,
    IReadOnlyList<ResolvedDbModel> Models,
    IReadOnlyList<string> Orphans);        // keys/indexes/partitions whose parent is not banked

/// <summary>
/// Key-based hash dictionary of database entities loaded from the pipe-delimited
/// dbbank '.dat' catalog. Upserts are append-safe; the threaded Rebuild resolves
/// every entity into a fully-linked data model, partitioning work across threads
/// by identifier (contiguous ranges, one lane per chunk) so large schemas can be
/// processed in parallel by index or identifier.
/// </summary>
public sealed class DbSchemaBank(string filePath)
{
    private readonly ConcurrentDictionary<string, DbEntity> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();
    private bool _loaded;

    public int Count { get { EnsureLoaded(); return _entities.Count; } }

    public IReadOnlyDictionary<string, DbEntity> All()
    {
        EnsureLoaded();
        return _entities.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public DbEntity? Find(string key)
    {
        EnsureLoaded();
        return _entities.TryGetValue(key, out var e) ? e : null;
    }

    /// <summary>Appends the entity when its key does not exist, updates the row when it does.</summary>
    public (DbEntity Entity, string Action) Upsert(DbEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (string.IsNullOrWhiteSpace(entity.Key))
            throw new ArgumentException("Key is required (e.g. dbo.Vehicles)");

        lock (_fileLock)
        {
            EnsureLoaded();
            var action = _entities.ContainsKey(entity.Key) ? "updated" : "added";
            _entities[entity.Key] = entity;
            PersistUnlocked();
            return (_entities[entity.Key], action);
        }
    }

    /// <summary>
    /// Resolves every banked entity into a linked data model. The entity set is
    /// sorted by identifier, split into contiguous ranges (one per thread lane),
    /// and each lane builds its models into a shared key-based hash dictionary.
    /// A linking pass then attaches keys, indexes and partitions to their parent
    /// models and resolves foreign-key references.
    /// </summary>
    public DbSchemaResolution Rebuild(int maxThreads) => Rebuild(maxThreads, fromIdentifier: null, toIdentifier: null);

    /// <summary>Identifier-range rebuild: only entities with fromIdentifier..toIdentifier resolve.</summary>
    public DbSchemaResolution Rebuild(int maxThreads, int? fromIdentifier, int? toIdentifier)
    {
        EnsureLoaded();
        maxThreads = Math.Clamp(maxThreads, 1, 64);
        var sw = Stopwatch.StartNew();

        var selected = _entities.Values
            .Where(e => fromIdentifier is null || e.Identifier >= fromIdentifier)
            .Where(e => toIdentifier is null || e.Identifier <= toIdentifier)
            .OrderBy(e => e.Identifier)
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // contiguous identifier ranges → one lane per chunk (index/identifier-based threading)
        var lanes = Math.Min(maxThreads, Math.Max(1, selected.Count));
        var models = new ConcurrentDictionary<string, ResolvedDbModel>(StringComparer.OrdinalIgnoreCase);

        var lanesList = new List<(int From, int To)>();
        for (var i = 0; i < lanes; i++)
        {
            var from = (int)Math.Floor(i * selected.Count / (double)lanes);
            var to = (int)Math.Floor((i + 1) * selected.Count / (double)lanes) - 1;
            if (to >= from) lanesList.Add((from, to));
        }

        Parallel.ForEach(lanesList, lane =>
        {
            for (var i = lane.From; i <= lane.To; i++)
            {
                var e = selected[i];
                models[e.Key] = new ResolvedDbModel
                {
                    Key = e.Key, Kind = e.Kind, Schema = e.Schema, Name = e.Name,
                    Identifier = e.Identifier, Columns = ResolveColumns(e.Columns),
                    ThreadId = Environment.CurrentManagedThreadId
                };
            }
        });

        // linking pass: attach keys/indexes/partitions to parents, resolve FK references
        var orphans = new ConcurrentBag<string>();
        Parallel.ForEach(selected, e =>
        {
            if (e.Kind is not (DbEntityKind.PrimaryKey or DbEntityKind.ForeignKey or DbEntityKind.UniqueKey
                    or DbEntityKind.Index or DbEntityKind.ClusteredIndex or DbEntityKind.Partition))
                return;

            if (!models.TryGetValue(e.Parent, out var parent))
            {
                orphans.Add($"{e.Kind} '{e.Key}' references unbanked parent '{e.Parent}'");
                return;
            }

            var columns = SplitColumns(e.IndexColumns);
            // the same parent may be linked from several lanes: serialize per model
            lock (parent)
            {
                switch (e.Kind)
                {
                    case DbEntityKind.PrimaryKey or DbEntityKind.ForeignKey or DbEntityKind.UniqueKey:
                        var missingRef = string.Empty;
                        if (e.Kind == DbEntityKind.ForeignKey && !string.IsNullOrEmpty(e.References))
                        {
                            var targetKey = TargetKey(e.References);
                            if (!models.ContainsKey(targetKey)) missingRef = e.References;
                        }
                        parent.Keys = parent.Keys.Append(new ResolvedKey(e.Kind, e.Name, columns, e.References)).ToList();
                        if (!string.IsNullOrEmpty(missingRef))
                            parent.MissingReferences = parent.MissingReferences.Append(missingRef).ToList();
                        break;
                    case DbEntityKind.Index or DbEntityKind.ClusteredIndex:
                        parent.Indexes = parent.Indexes
                            .Append(new ResolvedIndex(e.Name, columns, e.Kind == DbEntityKind.ClusteredIndex || e.IsClustered))
                            .ToList();
                        break;
                    case DbEntityKind.Partition:
                        parent.Partitions = parent.Partitions
                            .Append(new ResolvedPartition(e.Name, e.PartitionScheme, e.Range))
                            .ToList();
                        break;
                }
            }
        });

        sw.Stop();
        var ordered = models.Values.OrderBy(m => m.Identifier).ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return new DbSchemaResolution(
            selected.Count, ordered.Count, lanesList.Count, sw.ElapsedMilliseconds,
            ordered, orphans.OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Foreign key "dbo.Drivers(Id)" → parent model key "dbo.Drivers".</summary>
    private static string TargetKey(string references)
    {
        var open = references.IndexOf('(');
        return open > 0 ? references[..open].Trim() : references.Trim();
    }

    private static IReadOnlyList<string> SplitColumns(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"Id:Int,Name:String" → typed-defaulted column dictionary.</summary>
    private static IReadOnlyDictionary<string, object?> ResolveColumns(string columns)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0) { result[pair] = null; continue; }
            var name = pair[..colon].Trim();
            var type = pair[(colon + 1)..].Trim();
            result[name] = type.ToLowerInvariant() switch
            {
                "int" or "int32" or "long" or "int64" or "short" or "int16" => 0,
                "double" or "float" or "single" or "decimal" => 0d,
                "bool" or "boolean" => false,
                "datetime" => default(DateTime),
                "string" or "text" or "nvarchar" or "varchar" => string.Empty,
                _ => null
            };
        }
        return result;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_fileLock)
        {
            if (_loaded) return;
            if (!File.Exists(filePath)) PersistSeed();   // first use: write the sample schema
            foreach (var line in File.ReadAllLines(filePath))
            {
                var entity = DbEntity.FromRow(line);
                if (entity != null) _entities[entity.Key] = entity;
            }
            _loaded = true;
        }
    }

    private void PersistUnlocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllLines(filePath,
            new[] { DbEntity.Header }.Concat(_entities.Values.OrderBy(e => e.Identifier).Select(e => e.ToRow())));
    }

    /// <summary>Seeds a small vehicle-fleet schema so the bank works out of the box.</summary>
    private void PersistSeed()
    {
        var seed = new[]
        {
            new DbEntity { Key = "dbo.Vehicles", Kind = DbEntityKind.Table, Name = "Vehicles", Identifier = 101,
                Columns = "Id:Int,Name:String,Model:String,DriverId:Int" },
            new DbEntity { Key = "dbo.Drivers", Kind = DbEntityKind.Table, Name = "Drivers", Identifier = 102,
                Columns = "Id:Int,FullName:String,LicenseNo:String" },
            new DbEntity { Key = "dbo.vw_FleetSummary", Kind = DbEntityKind.View, Name = "vw_FleetSummary", Identifier = 110,
                Columns = "Vehicle:String,Driver:String" },
            new DbEntity { Key = "dbo.pk_Vehicles", Kind = DbEntityKind.PrimaryKey, Name = "PK_Vehicles", Identifier = 103,
                IndexColumns = "Id", Parent = "dbo.Vehicles" },
            new DbEntity { Key = "dbo.cix_Vehicles", Kind = DbEntityKind.ClusteredIndex, Name = "CIX_Vehicles", Identifier = 104,
                IndexColumns = "Id", Parent = "dbo.Vehicles" },
            new DbEntity { Key = "dbo.ix_Vehicles_Name", Kind = DbEntityKind.Index, Name = "IX_Vehicles_Name", Identifier = 105,
                IndexColumns = "Name", Parent = "dbo.Vehicles" },
            new DbEntity { Key = "dbo.fk_Vehicles_Drivers", Kind = DbEntityKind.ForeignKey, Name = "FK_Vehicles_Drivers", Identifier = 106,
                IndexColumns = "DriverId", Parent = "dbo.Vehicles", References = "dbo.Drivers(Id)" },
            new DbEntity { Key = "dbo.uk_Drivers_License", Kind = DbEntityKind.UniqueKey, Name = "UK_Drivers_License", Identifier = 107,
                IndexColumns = "LicenseNo", Parent = "dbo.Drivers" },
            new DbEntity { Key = "dbo.clu_Fleet", Kind = DbEntityKind.Cluster, Name = "CLU_Fleet", Identifier = 108 },
            new DbEntity { Key = "dbo.ps_Vehicles_2024", Kind = DbEntityKind.Partition, Name = "PS_Vehicles_2024", Identifier = 109,
                PartitionScheme = "psVehicleYear", Range = "2024", Parent = "dbo.Vehicles" }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllLines(filePath, new[] { DbEntity.Header }.Concat(seed.OrderBy(e => e.Identifier).Select(e => e.ToRow())));
    }
}
