using System.Text;

namespace SmartAgent.Core.Dat;

/// <summary>
/// Object-oriented classification record for a single type. One comma-separated
/// row in the object bank '.dat' catalog. List-valued fields use '|' between
/// entries and ';' between inner items (e.g. method parameters) so the comma
/// stays a pure column separator.
/// </summary>
public sealed record ClassDefinition
{
    public required string ClassName { get; init; }
    public string Namespace { get; init; } = string.Empty;
    /// <summary>class | interface | abstract | record | struct | enum.</summary>
    public string Kind { get; init; } = "class";
    /// <summary>public | internal | private | protected.</summary>
    public string Access { get; init; } = "public";
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
    public bool IsStatic { get; init; }
    public string BaseClass { get; init; } = string.Empty;
    /// <summary>Implemented interface names, '|'-separated.</summary>
    public string Interfaces { get; init; } = string.Empty;
    /// <summary>Declared properties as "Type:Name", '|'-separated.</summary>
    public string Properties { get; init; } = string.Empty;
    /// <summary>Declared methods as "Name(ParamTypes;...):ReturnType", '|'-separated.</summary>
    public string Methods { get; init; } = string.Empty;
    /// <summary>Declared constructors as "(ParamTypes;...)", '|'-separated.</summary>
    public string Constructors { get; init; } = string.Empty;
    /// <summary>Declared events as "EventHandlerName", '|'-separated.</summary>
    public string Events { get; init; } = string.Empty;
    /// <summary>Generic parameters, '|'-separated.</summary>
    public string GenericParameters { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;

    public string Key => BankKey(Namespace, ClassName);

    public static string BankKey(string ns, string name) =>
        string.IsNullOrWhiteSpace(ns) ? name.Trim() : $"{ns.Trim()}.{name.Trim()}";

    internal static ClassDefinition Parse(ReadOnlySpan<char> line)
    {
        var cols = SplitRow(line);
        if (cols.Count < 16) throw new FormatException("classbank row must have 16 columns");
        return new ClassDefinition
        {
            ClassName = cols[0], Namespace = cols[1], Kind = cols[2], Access = cols[3],
            IsAbstract = cols[4] == "true", IsSealed = cols[5] == "true", IsStatic = cols[6] == "true",
            BaseClass = cols[7], Interfaces = cols[8], Properties = cols[9], Methods = cols[10],
            Constructors = cols[11], Events = cols[12], GenericParameters = cols[13],
            SourceFile = cols[14],
            AddedUtc = DateTime.TryParse(cols[15], out var t) ? t : DateTime.UtcNow
        };
    }

    internal string ToRow()
    {
        var sb = new StringBuilder();
        sb.Append(ClassName).Append(',')
          .Append(Namespace).Append(',')
          .Append(Kind).Append(',')
          .Append(Access).Append(',')
          .Append(IsAbstract ? "true" : "false").Append(',')
          .Append(IsSealed ? "true" : "false").Append(',')
          .Append(IsStatic ? "true" : "false").Append(',')
          .Append(BaseClass).Append(',')
          .Append(Interfaces).Append(',')
          .Append(Properties).Append(',')
          .Append(Methods).Append(',')
          .Append(Constructors).Append(',')
          .Append(Events).Append(',')
          .Append(GenericParameters).Append(',')
          .Append(SourceFile).Append(',')
          .Append(AddedUtc.ToString("O"));
        return sb.ToString();
    }

    public static IReadOnlyList<string> Header => new[]
    {
        "ClassName", "Namespace", "Kind", "Access", "IsAbstract", "IsSealed", "IsStatic",
        "BaseClass", "Interfaces", "Properties", "Methods", "Constructors", "Events",
        "GenericParameters", "SourceFile", "AddedUtc"
    };

    // Minimal CSV split: double-quote wrapping supported; quote chars doubled inside.
    private static IReadOnlyList<string> SplitRow(ReadOnlySpan<char> line)
    {
        var cols = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cols.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cols.Add(sb.ToString());
        return cols;
    }
}
