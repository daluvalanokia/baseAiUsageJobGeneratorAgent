using System.Collections.Concurrent;

namespace SmartAgent.Core.Dat;

/// <summary>
/// A class instance rebuilt from the bank with every declared property
/// populated (type-defaulted primitives, recursively resolved nested objects
/// for properties whose type is itself banked).
/// </summary>
public sealed record ResolvedObject
{
    public required string ClassName { get; init; }
    public string Namespace { get; init; } = string.Empty;
    public string Kind { get; init; } = "class";
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }
    public IReadOnlyList<string> MissingTypes { get; init; } = Array.Empty<string>();
    public int ThreadId { get; init; }
    public DateTime ResolvedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// In-memory hash dictionary of every class definition loaded from the
/// comma-separated classbank '.dat' file. Upserts are append-safe: a class is
/// added when it does not exist and updated in place when it does.
/// </summary>
public sealed class ObjectBank(string filePath)
{
    private readonly ConcurrentDictionary<string, ClassDefinition> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();
    private bool _loaded;

    public event Action<string, string>? ClassChanged;   // (key, action: added|updated)

    public int Count
    {
        get { EnsureLoaded(); return _classes.Count; }
    }

    public IReadOnlyDictionary<string, ClassDefinition> All()
    {
        EnsureLoaded();
        return _classes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public ClassDefinition? Find(string ns, string name)
    {
        EnsureLoaded();
        return _classes.TryGetValue(ClassDefinition.BankKey(ns, name), out var def) ? def : null;
    }

    /// <summary>Appends the class when it does not exist, updates the row when it does.</summary>
    public (ClassDefinition Definition, string Action) Upsert(ClassDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.ClassName))
            throw new ArgumentException("ClassName is required");

        lock (_fileLock)
        {
            EnsureLoaded();
            var key = definition.Key;
            var action = _classes.ContainsKey(key) ? "updated" : "added";
            _classes[key] = definition with { AddedUtc = action == "added" ? DateTime.UtcNow : definition.AddedUtc };
            PersistUnlocked();
            ClassChanged?.Invoke(key, action);
            return (_classes[key], action);
        }
    }

    /// <summary>
    /// Rebuilds materialized objects for every banked class with spawned
    /// worker threads (bounded by maxThreads). Each object is fully
    /// property-populated; properties typed as another banked class are
    /// recursively resolved (cycle-safe to depth 4).
    /// </summary>
    public IReadOnlyList<ResolvedObject> Rebuild(int maxThreads)
    {
        EnsureLoaded();
        maxThreads = Math.Clamp(maxThreads, 1, 64);
        var results = new ConcurrentBag<ResolvedObject>();

        Parallel.ForEach(_classes.Values,
            new ParallelOptions { MaxDegreeOfParallelism = maxThreads },
            def =>
            {
                results.Add(BuildObject(def, depth: 0));
            });

        return results.ToList();
    }

    private ResolvedObject BuildObject(ClassDefinition def, int depth)
    {
        var missing = new List<string>();
        var props = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in SplitList(def.Properties))
        {
            var parts = raw.Split(':', 2);
            var type = parts[0].Trim();
            var name = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
            props[name] = ResolveValue(type, name, depth, missing);
        }
        foreach (var name in SplitList(def.Events)) props[name] = default(string);

        return new ResolvedObject
        {
            ClassName = def.ClassName, Namespace = def.Namespace, Kind = def.Kind,
            Properties = props, MissingTypes = missing, ThreadId = Environment.CurrentManagedThreadId
        };
    }

    private object? ResolveValue(string type, string name, int depth, List<string> missing)
    {
        var clean = type.TrimEnd('?').Trim();

        // nested banked class → recursively rebuild a complete object
        if (depth < 4 && _classes.TryGetValue(ClassDefinition.BankKey(string.Empty, clean), out var nested))
            return BuildObject(nested, depth + 1);

        switch (clean.ToLowerInvariant())
        {
            case "bool" or "boolean": return false;
            case "byte" or "sbyte": return (byte)0;
            case "short" or "ushort": return (short)0;
            case "int" or "int32": return 0;
            case "long" or "int64": return 0L;
            case "float" or "single": return 0f;
            case "double": return 0d;
            case "decimal": return 0m;
            case "string": return string.Empty;
            case "char": return '\0';
            case "guid": return Guid.Empty;
            case "datetime" or "datetimeoffset": return DateTime.MinValue;
            case "timespan": return TimeSpan.Zero;
            default:
                if (clean.Length > 2 && (clean.EndsWith("[]") || clean.StartsWith("List<") || clean.StartsWith("IList<") ||
                    clean.StartsWith("IEnumerable<") || clean.StartsWith("ICollection<")))
                    return Array.Empty<object>();
                missing.Add($"{name}:{clean}");
                return null;
        }
    }

    private static IEnumerable<string> SplitList(string value) =>
        string.IsNullOrWhiteSpace(value) ? Enumerable.Empty<string>()
            : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_fileLock)
        {
            if (_loaded) return;
            foreach (var def in ReadFile())
                _classes[def.Key] = def;
            _loaded = true;
        }
    }

    private IEnumerable<ClassDefinition> ReadFile()
    {
        if (!File.Exists(filePath)) yield break;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            ClassDefinition def;
            try { def = ClassDefinition.Parse(line.AsSpan()); }
            catch (FormatException) { continue; }   // header or malformed row
            yield return def;
        }
    }

    private void PersistUnlocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllLines(filePath,
            new[] { "# " + string.Join(",", ClassDefinition.Header) }
                .Concat(_classes.Values.OrderBy(c => c.Key).Select(c => c.ToRow())));
    }
}
