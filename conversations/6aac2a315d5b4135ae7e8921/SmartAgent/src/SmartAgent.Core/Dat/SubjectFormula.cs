namespace SmartAgent.Core.Dat;

/// <summary>
/// One subject formula row (comma-separated column layout):
///   FormulaName,Expression,Variables,Units,Category
/// Variables are "name:Type" entries joined by ';'.
/// </summary>
public sealed record SubjectFormula
{
    public required string FormulaName { get; init; }
    public required string Expression { get; init; }
    public string Variables { get; init; } = string.Empty;
    public string Units { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    internal string ToRow() => $"{FormulaName},{Escape(Expression)},{Variables},{Units},{Category}";

    internal static SubjectFormula Parse(ReadOnlySpan<char> line)
    {
        var cols = Split(line);
        if (cols.Count < 5) throw new FormatException("subject formula row must have 5 columns");
        return new SubjectFormula
        {
            FormulaName = cols[0].Trim(), Expression = Unescape(cols[1]), Variables = cols[2],
            Units = cols[3], Category = cols[4]
        };
    }

    public static IReadOnlyList<string> Header => new[] { "FormulaName", "Expression", "Variables", "Units", "Category" };

    // expressions contain commas inside function calls → quote-wrap when needed
    private static string Escape(string value) =>
        value.Contains(',') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string Unescape(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            return value[1..^1].Replace("\"\"", "\"");
        return value;
    }

    private static IReadOnlyList<string> Split(ReadOnlySpan<char> line)
    {
        var cols = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQuotes = false; }
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
