using System.Text.Json;
using Xunit;
using SmartAgent.Core.Pm;

namespace SmartAgent.Tests;

public class DesignSystemAnalyzerTests
{
    private const string Css = @"
:root {
  --primary: #d54309;
  --secondary: rgb(59 130 246);
  --space-md: 13px;
  --font-size-body: 16px;
  --card-radius: 8px;
}
@media (min-width: 768px) { .container { padding: 24px; } }
.card { margin-top: 13px; padding: 12px; }
.btn { height: 34px; }
.btn-lg { min-height: 52px; }
.list-row { height: 40px; }
#hero { background: #123456; color: #ffffff; }
.page-a { background: #111111; }
.page-b { background: #222222; }
.page-c { background: #333333; }
.page-d { background: #444444; }
.page-e { background: #555555; }
.page-f { background: #666666; }
.page-g { background: #777777; }
.page-h { background: #888888; }
.page-i { background: #999999; }
.page-j { background: #aaaaaa; }
.page-k { background: #bbbbbb; }
.page-l { background: #cccccc; }
.page-m { background: #dddddd; }
.page-n { background: #eeeeee; }
.page-o { background: #efefef; }
.page-p { background: #f0f0f0; }
.page-q { background: #f1f1f1; }
.page-r { background: #f2f2f2; }
";

    private static IReadOnlyList<PmFunctionalPage> Pages(int governanceRows = 25) => new[]
    {
        new PmFunctionalPage { Route = "/Dashboard", Status = 200, Title = "Dashboard" },
        new PmFunctionalPage { Route = "/Governance", Status = 200, Title = "Governance", RowCount = governanceRows }
    };

    [Fact]
    public void Extracts_Tokens_And_Classifies_Them_Into_Dtcg_Groups()
    {
        var spec = DesignSystemAnalyzer.Analyze(Css, Pages(), "width=device-width");

        var primary = Assert.Single(spec.Tokens, t => t.Name == "primary");
        Assert.Equal("color", primary.Type);
        var space = Assert.Single(spec.Tokens, t => t.Name == "space-md");
        Assert.Equal("space", space.Type);
        var font = Assert.Single(spec.Tokens, t => t.Name == "font-size-body");
        Assert.Equal("font", font.Type);
        var radius = Assert.Single(spec.Tokens, t => t.Name == "card-radius");
        Assert.Equal("dimension", radius.Type);
    }

    [Fact]
    public void Emits_W3C_Dtcg_Json()
    {
        var spec = DesignSystemAnalyzer.Analyze(Css, Pages(), "width=device-width");
        using var doc = JsonDocument.Parse(spec.DtcgJson);
        var root = doc.RootElement;
        Assert.Equal("color", root.GetProperty("color").GetProperty("primary").GetProperty("$type").GetString());
        Assert.Equal("#d54309", root.GetProperty("color").GetProperty("primary").GetProperty("$value").GetString());
        Assert.Equal("13px", root.GetProperty("space").GetProperty("space-md").GetProperty("$value").GetString());
        Assert.Equal("16px", root.GetProperty("font").GetProperty("font-size-body").GetProperty("$value").GetString());
        Assert.Contains("DTCG", root.GetProperty("$description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_Breakpoints_To_Material_Tiers_And_Flags_Missing_Expanded()
    {
        var spec = DesignSystemAnalyzer.Analyze(Css, Pages(), "width=device-width");
        var bp = Assert.Single(spec.Breakpoints);
        Assert.Equal(768, bp.MinWidthPx);
        Assert.Equal("medium", bp.Tier);
        Assert.Equal(8, bp.ExpectedColumns);
        Assert.Contains(spec.Findings,
            f => f.Contains("no expanded (>=905px) breakpoint", StringComparison.Ordinal)
                 && f.Contains("12-column grid", StringComparison.Ordinal));

        var none = DesignSystemAnalyzer.Analyze(".card { padding: 12px; }", Pages(), "width=device-width");
        Assert.Contains(none.Findings,
            f => f.Contains("no responsive breakpoints defined", StringComparison.Ordinal));
    }

    [Fact]
    public void Flags_Off_Grid_Spacing_And_Touch_Targets()
    {
        var spec = DesignSystemAnalyzer.Analyze(Css, Pages(), "width=device-width");
        Assert.Contains(spec.OffGridSpacing, o => o.Contains("margin-top: 13px", StringComparison.Ordinal));
        Assert.Contains(spec.Findings,
            f => f.Contains("off the Material 4dp/8dp baseline grid", StringComparison.Ordinal));
        // .btn at 34px violates; .btn-lg at 52px does not; non-interactive .list-row is ignored
        var btn = Assert.Single(spec.TouchTargetViolations, t => t.Selector.Contains(".btn", StringComparison.Ordinal)
            && t.DeclaredPx == 34);
        Assert.DoesNotContain(spec.TouchTargetViolations, t => t.DeclaredPx >= 48);
        Assert.DoesNotContain(spec.TouchTargetViolations, t => t.Selector.Contains("list-row", StringComparison.Ordinal));
    }

    [Fact]
    public void Flags_Fluent2_Data_Density_And_Missing_Viewport()
    {
        var spec = DesignSystemAnalyzer.Analyze(Css, Pages(), "width=device-width");
        Assert.Contains(spec.DenseRoutes, r => r.Contains("/Governance: 25 rows", StringComparison.Ordinal));
        Assert.Contains(spec.Findings,
            f => f.Contains("beyond the Fluent 2 dense-list guideline", StringComparison.Ordinal));

        var noViewport = DesignSystemAnalyzer.Analyze(Css, Pages(), " ");
        Assert.Contains(noViewport.Findings,
            f => f.Contains("no viewport meta tag", StringComparison.Ordinal));

        var sparse = DesignSystemAnalyzer.Analyze(Css, Pages(9), "width=device-width");
        Assert.Empty(sparse.DenseRoutes);
    }

    [Fact]
    public void Flags_Raw_Hex_Bypassing_The_HIG_Token_Layer()
    {
        var spec = DesignSystemAnalyzer.Analyze(Css, Pages(), "width=device-width");
        Assert.True(spec.RawHexColors > 20, $"expected raw hex count > 20, got {spec.RawHexColors}");
        Assert.Contains(spec.Findings,
            f => f.Contains("semantic color tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureAsync_Populates_The_Design_Spec_From_Live_Pages()
    {
        var handler = new FakeAppHandler()
            .Add("/Dashboard",
                @"<html><head><meta name=""viewport"" content=""width=device-width"" />
                   <style>.btn { height: 34px; } .card { margin-top: 13px; }</style></head>
                   <body><a href=""/Governance"">g</a></body></html>")
            .Add("/Governance",
                @"<html><head><link rel=""stylesheet"" href=""/css/site.css"" /></head><body>" +
                string.Join("", Enumerable.Repeat("<tr><td>x</td></tr>", 20)) + "</body></html>")
            .Add("/css/site.css", ":root { --primary: #d54309; } @media (min-width: 905px) { .x { padding: 24px; } }");
        var gen = new FunctionalSpecGenerator(() => handler);

        var spec = await gen.CaptureAsync("https://app.test/Dashboard", "u", "p");

        Assert.NotNull(spec.Design);
        var d = spec.Design!;
        Assert.Contains(d.Tokens, t => t.Name == "primary" && t.Value == "#d54309");
        // inline style + fetched stylesheet both feed the analyzer
        Assert.Contains(d.Breakpoints, b => b.MinWidthPx == 905 && b.Tier == "expanded");
        Assert.Contains(d.TouchTargetViolations, t => t.Selector.Contains(".btn") && t.DeclaredPx == 34);
        Assert.Contains(d.OffGridSpacing, o => o.Contains("margin-top: 13px"));
        Assert.Contains(d.DenseRoutes, r => r.StartsWith("/Governance: 20 rows", StringComparison.Ordinal));
        Assert.Contains("/Governance", d.DenseRoutes[0]);
        Assert.Equal("width=device-width", d.Viewport);
    }

    [Fact]
    public async Task Design_Violations_Consolidate_Into_An_Evidence_Bound_Story()
    {
        var handler = new FakeAppHandler()
            .Add("/Dashboard",
                @"<html><head><style>.btn { height: 30px; } .card { margin: 13px; }</style></head><body></body></html>");
        var gen = new FunctionalSpecGenerator(() => handler);
        var spec = await gen.CaptureAsync("https://app.test/Dashboard", "u", "p");

        var result = gen.ValidateAndConsolidate(new List<PmEpic>(), new List<PmStory>(), spec);

        var designStory = result.Stories.SingleOrDefault(s => s.Module == "DesignSystem");
        Assert.NotNull(designStory);
        Assert.Contains("48x48dp touch-target minimum", designStory.AcceptanceCriteria[2]);
        Assert.Contains(designStory.AcceptanceCriteria, a => a.Contains("4dp and 8dp increments"));
        Assert.Contains(designStory.AcceptanceCriteria, a => a.Contains("W3C DTCG JSON"));
        Assert.Contains(result.Epics, e => e.Module == "DesignSystem");
        Assert.Contains(result.Spec.Findings, f => f.StartsWith("design system:", StringComparison.Ordinal));
        // the consolidated design story itself passes the quality engine standard
        var quality = new PmQualityEngine();
        Assert.InRange(designStory.AcceptanceCriteria.Count, 2, 6);
        Assert.Contains(designStory.AcceptanceCriteria, a => !AcComposer.IsGeneric(a));
        Assert.Contains(designStory.Points, new[] { 1, 2, 3, 5, 8, 13 });
    }
}
