using SmartAgent.Core.Dat;
using SmartAgent.Core.Pm;
using SmartAgent.Domain;
using Xunit;
using Xunit.Abstractions;

namespace SmartAgent.Tests;

public class ScratchTraceTests
{
    private readonly ITestOutputHelper _out;
    public ScratchTraceTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Trace_generation_and_save()
    {
        var ingestor = new RequirementIngestor();
        var planner = new SprintPlanner();
        var governor = new PmGovernor(new ThreadGovernor(), ingestor, planner, new PmQualityEngine(), new FunctionalSpecGenerator(), new PmThreadManager());
        var options = new PmOptions { Name = "Trace Run", Years = 1 };
        var snapshot = new SourceSnapshot
        {
            SourceType = SourceType.ZipArchive, SourceName = "prompt",
            SourceDetail = "Build a location tracking platform with auth and chat.",
            Files = new List<SourceFile>()
        };
        var plan = await governor.GenerateAsync(snapshot, options, Path.Combine(Path.GetTempPath(), "trace-" + Guid.NewGuid().ToString("N")));
        _out.WriteLine($"plan: {plan.Name} sprints={plan.Sprints} unitTests={plan.UnitTests.Count}");
        var json = System.Text.Json.JsonSerializer.Serialize(plan);
        _out.WriteLine("serialized ok, len=" + json.Length);
    }
}
