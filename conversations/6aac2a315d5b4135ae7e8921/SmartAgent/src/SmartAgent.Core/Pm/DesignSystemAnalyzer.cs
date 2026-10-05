using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartAgent.Core.Pm;

/// <summary>A design token captured from the live app, classified into a DTCG group.</summary>
public sealed record PmDesignToken
{
    public required string Name { get; init; }       // "primary" (from --primary)
    public required string Value { get; init; }     // "#d54309"
    public required string Type { get; init; }      // color | dimension | space | font
}

/// <summary>A responsive breakpoint observed in the app's stylesheets.</summary>
public sealed record PmDesignBreakpoint
{
    public required int MinWidthPx { get; init; }
    /// <summary>Material 3 layout tier this breakpoint falls into.</summary>
    public required string Tier { get; init; }      // compact | medium | expanded
    public required int ExpectedColumns { get; init; } // Material 3 column count for the tier
}

/// <summary>An interactive element whose declared size is below the touch-target standard.</summary>
public sealed record PmTouchTarget
{
    public required string Selector { get; init; }
    public required int DeclaredPx { get; init; }
}

/// <summary>
/// The design system spec captured from a running app, grounded in the
/// industry standards of expectation: Material Design 3 (layout grid,
/// 4dp/8dp spacing, 48x48dp touch targets), Fluent 2 (data density and
/// pagination limits) and Apple HIG (semantic color tokens), exported in
/// the W3C Design Tokens Community Group JSON format.
/// </summary>
public sealed record PmDesignSpec
{
    public required string Standard { get; init; }  // "Material 3 + Fluent 2 + HIG (W3C DTCG export)"
    public required IReadOnlyList<PmDesignBreakpoint> Breakpoints { get; init; }
    public required IReadOnlyList<PmDesignToken> Tokens { get; init; }
    /// <summary>Design tokens in the W3C Design Tokens Community Group JSON format.</summary>
    public required string DtcgJson { get; init; }
    public string Viewport { get; init; } = string.Empty;
    public required IReadOnlyList<PmTouchTarget> TouchTargetViolations { get; init; }
    /// <summary>Spacing declarations off the Material 4dp baseline grid (value, declaration).</summary>
    public required IReadOnlyList<string> OffGridSpacing { get; init; }
    /// <summary>Routes rendering more rows than the Fluent 2 dense-viewport density guideline.</summary>
    public required IReadOnlyList<string> DenseRoutes { get; init; }
    public int RawHexColors { get; init; }
    public required IReadOnlyList<string> Findings { get; init; }
    public int ViolationCount { get; init; }
    public required string Summary { get; init; }
}

/// <summary>
/// Extracts granular design-system specifications from the live app's CSS and
/// rendered pages and scores them against the standard of expectation:
/// Material 3 responsive grid rules (compact 4 columns/16dp margins at
/// 0-599dp, medium 8 columns at 600-904dp, expanded 12 columns at 905dp+),
/// the 4dp/8dp spacing scale, 48x48dp minimum touch targets, Fluent 2 data
/// density/pagination limits and HIG semantic color token requirements.
/// </summary>
public static partial class DesignSystemAnalyzer
{
    private const int MaxCssBytes = 512 * 1024;
    private const int MaxTokens = 200;
    /// <summary>Fluent 2 density guideline: paginate dense lists beyond this many rows per viewport.</summary>
    private const int DenseRowLimit = 15;
    private const int TouchTargetDp = 48;

    public static PmDesignSpec Analyze(string css, IReadOnlyList<PmFunctionalPage> pages, string viewport)
    {
        var findings = new List<string>();

        // ── design tokens: CSS custom properties, classified into DTCG groups ──
        var tokens = new List<PmDesignToken>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in TokenRegex().Matches(css).Cast<Match>())
        {
            var name = m.Groups["name"].Value.Trim();
            var value = m.Groups["value"].Value.Trim();
            if (name.Length == 0 || value.Length == 0) continue;
            // multi-word values are composed tokens (shorthands) — keep simple scalars
            if (value.Contains(' ') && !value.StartsWith('#')) continue;
            if (!seen.Add(name)) continue;
            tokens.Add(new PmDesignToken { Name = name, Value = value, Type = Classify(name, value) });
            if (tokens.Count >= MaxTokens) break;
        }

        // ── responsive breakpoints, mapped to the Material 3 tiers ──
        var widths = MediaQueryRegex().Matches(css).Cast<Match>()
            .Select(m => int.TryParse(m.Groups["px"].Value, out var px) ? px : 0)
            .Where(px => px > 0).Distinct().OrderBy(px => px).ToList();
        var breakpoints = widths.Select(px => new PmDesignBreakpoint
        {
            MinWidthPx = px,
            Tier = px < 600 ? "compact" : px < 905 ? "medium" : "expanded",
            ExpectedColumns = px < 600 ? 4 : px < 905 ? 8 : 12
        }).ToList();
        if (widths.Count == 0)
            findings.Add("no responsive breakpoints defined — the layout cannot reflow between the Material 3 tiers " +
                        "(compact 4 columns/16dp margins, medium 8 columns, expanded 12 columns)");
        else if (widths.All(w => w < 905))
            findings.Add($"no expanded (>=905px) breakpoint — desktop inherits the medium tier; Material 3 expects " +
                        $"a 12-column grid with 24dp margins from 905dp");

        // ── spacing scale: margin/padding/gap declarations off the 4dp grid ──
        var offGrid = new List<string>();
        foreach (var m in SpacingRegex().Matches(css).Cast<Match>())
        {
            var px = int.TryParse(m.Groups["px"].Value, out var v) ? v : 0;
            var prop = m.Groups["prop"].Value;
            // hairline borders are not spacing; 1-2px rules are intentional strokes
            if (px <= 2 || px % 4 == 0 || prop.StartsWith("border", StringComparison.OrdinalIgnoreCase)) continue;
            var decl = $"{m.Groups["prop"].Value.Trim()}: {m.Groups["px"].Value}px".Truncate(60);
            if (offGrid.Count > 0 && offGrid[^1].Contains(decl)) continue;
            offGrid.Add(decl);
            if (offGrid.Count >= 20) break;
        }
        if (offGrid.Count > 0)
            findings.Add($"{offGrid.Count} spacing declarations off the Material 4dp/8dp baseline grid " +
                        $"(e.g. {offGrid[0]}) — normalize to the 4dp scale");

        // ── touch targets: declared interactive sizes vs the 48x48dp standard ──
        var touchViolations = new List<PmTouchTarget>();
        foreach (var rule in RuleRegex().Matches(css).Cast<Match>())
        {
            var sel = rule.Groups["selector"].Value;
            if (!InteractiveRegex().IsMatch(sel)) continue;
            var h = HeightRegex().Match(rule.Groups["body"].Value);
            if (!h.Success || !int.TryParse(h.Groups["px"].Value, out var px) || px <= 0 || px >= TouchTargetDp) continue;
            touchViolations.Add(new PmTouchTarget { Selector = sel.Trim().Truncate(60), DeclaredPx = px });
            if (touchViolations.Count >= 20) break;
        }
        if (touchViolations.Count > 0)
            findings.Add($"{touchViolations.Count} interactive elements below the Material 48x48dp touch-target minimum " +
                        $"(e.g. {touchViolations[0].Selector} at {touchViolations[0].DeclaredPx}px) — add hit-slop padding or raise the height");

        // ── HIG semantic color tokens: raw hex colors bypassing the token layer ──
        var rawHex = HexColorRegex().Matches(css).Count;
        if (rawHex > 20)
            findings.Add($"{rawHex} raw hex colors bypass the token layer — HIG requires semantic color tokens " +
                        "(primary text vs disabled labels, dark/light themes) instead of literal values");

        // ── Fluent 2 data density: rows per rendered viewport ──
        var denseRoutes = new List<string>();
        foreach (var page in pages.Where(p => p.Status >= 200 && p.Status < 300))
        {
            var rows = page.RowCount;
            if (rows > DenseRowLimit)
            {
                denseRoutes.Add($"{page.Route}: {rows} rows");
                findings.Add($"{page.Route} renders {rows} rows in one viewport — beyond the Fluent 2 dense-list " +
                             $"guideline (~{DenseRowLimit} rows before forced scroll); add pagination or virtualization");
            }
        }

        if (string.IsNullOrWhiteSpace(viewport))
            findings.Add("no viewport meta tag — mobile devices cannot apply the compact grid tier");

        var violationCount = offGrid.Count + touchViolations.Count + denseRoutes.Count
                             + (widths.Count == 0 || widths.All(w => w < 905) ? 1 : 0)
                             + (rawHex > 20 ? 1 : 0) + (string.IsNullOrWhiteSpace(viewport) ? 1 : 0);
        return new PmDesignSpec
        {
            Standard = "Material 3 + Fluent 2 + Apple HIG (W3C DTCG export)",
            Breakpoints = breakpoints, Tokens = tokens, DtcgJson = BuildDtcg(tokens), Viewport = viewport,
            TouchTargetViolations = touchViolations, OffGridSpacing = offGrid, DenseRoutes = denseRoutes,
            RawHexColors = rawHex, Findings = findings, ViolationCount = violationCount,
            Summary = $"{tokens.Count} design tokens ({breakpoints.Count} breakpoints), {violationCount} design-standard violations: " +
                      $"{offGrid.Count} off-grid spacing, {touchViolations.Count} touch-target, {denseRoutes.Count} data-density" +
                      (findings.Count > 0 ? $", {findings.Count} findings" : "")
        };
    }

    /// <summary>Tokens in the W3C Design Tokens Community Group JSON format.</summary>
    public static string BuildDtcg(IReadOnlyList<PmDesignToken> tokens)
    {
        var groups = new Dictionary<string, Dictionary<string, object>>
        {
            ["color"] = new(), ["space"] = new(), ["font"] = new(), ["dimension"] = new()
        };
        foreach (var t in tokens)
        {
            var group = groups.TryGetValue(t.Type, out var g) ? g : groups["dimension"];
            group[t.Name] = new Dictionary<string, string> { ["$value"] = t.Value, ["$type"] = t.Type };
        }
        var root = new Dictionary<string, object>
        {
            ["$description"] = "Design tokens captured from the running application (W3C DTCG format)",
            ["$extension"] = new Dictionary<string, string>
            {
                ["standard"] = "Material 3 / Fluent 2 / Apple HIG",
                ["urn"] = "urn:design-tokens:standard-of-expectation"
            }
        };
        foreach (var (name, group) in groups)
            if (group.Count > 0)
                root[name] = group;
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Classify(string name, string value)
    {
        if (HexColorRegex().IsMatch(value) || RgbColorRegex().IsMatch(value)) return "color";
        if (name.Contains("font", StringComparison.OrdinalIgnoreCase)
            || name.Contains("size", StringComparison.OrdinalIgnoreCase)) return "font";
        if (name.Contains("space", StringComparison.OrdinalIgnoreCase)
            || name.Contains("padding", StringComparison.OrdinalIgnoreCase)
            || name.Contains("margin", StringComparison.OrdinalIgnoreCase)
            || name.Contains("gap", StringComparison.OrdinalIgnoreCase)) return "space";
        return "dimension";
    }

    [GeneratedRegex(@"--(?<name>[\w-]+)\s*:\s*(?<value>[^;]+)")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"@media[^{]*?min-width\s*:\s*(?<px>\d+)px")]
    private static partial Regex MediaQueryRegex();
    [GeneratedRegex(@"[{;\s](?<prop>(?:margin|padding|gap|top|bottom|left|right|inset)[\w-]*)\s*:\s*(?<px>\d+)px")]
    private static partial Regex SpacingRegex();
    [GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex RuleRegex();
    [GeneratedRegex(@"\b(button|btn|link|nav|chip|icon|toggle)[\w.\s#:-]*(?:,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex InteractiveRegex();
    [GeneratedRegex(@"(?:min-|max-)?height\s*:\s*(?<px>\d+)px")]
    private static partial Regex HeightRegex();
    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b")]
    private static partial Regex HexColorRegex();
    [GeneratedRegex(@"\brgba?\(")]
    private static partial Regex RgbColorRegex();
}

internal static class DesignStringExtensions
{
    public static string Truncate(this string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
