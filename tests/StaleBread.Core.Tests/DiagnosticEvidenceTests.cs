using System.Text.Json;
using StaleBread.Core.Detection;
using StaleBread.Core.Model;
using StaleBread.Core.Storage;
using static StaleBread.Core.Tests.Fixtures;

namespace StaleBread.Core.Tests;

public sealed class DiagnosticEvidenceTests
{
    [Fact]
    public void CpuAndMemoryTriggersRetainSeparateMeasuredEvidence()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        Finding? finding = null;
        for (int t = 0; t <= 120; t += 5)
            finding = engine.Evaluate(graph.Apply(B(t, P(2) with { CpuTime = TimeSpan.FromSeconds(t), PrivateBytes = 2UL << 30 })
                with { System = new(90, 1, 10) }), new()).SingleOrDefault(f => f.Kind == FindingKind.HeavyWorkload);
        Assert.NotNull(finding);
        var cpu = Assert.Single(finding.Measurements, m => m.Trigger == TriggerKind.Cpu);
        Assert.Equal(10, cpu.CpuMachinePercent); Assert.Equal(1, cpu.CpuCoreEquivalents);
        Assert.Equal(EvidenceScope.Process, cpu.Scope); Assert.Equal(new ProcessIdentity(2, 1), cpu.Subject);
        var memory = Assert.Single(finding.Measurements, m => m.Trigger == TriggerKind.MemoryPressure);
        Assert.Equal(90, memory.SystemMemoryLoadPercent); Assert.Equal(2UL << 30, memory.PrivateBytes);
        Assert.Contains("SustainedCpu", finding.ReasonCodes); Assert.Contains("PrivateMemoryAndSystemPressure", finding.ReasonCodes);
    }

    [Fact]
    public void GroupCpuEvidenceIsNotMisrepresentedAsIndividualCpu()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 60; t += 5)
            found = engine.Evaluate(graph.Apply(B(t, P(1, name: "claude.exe", path: @"C:\Users\test\.local\bin\claude.exe"),
                P(2, 1) with { CpuTime = TimeSpan.FromSeconds(t * .4) }, P(3, 1) with { CpuTime = TimeSpan.FromSeconds(t * .4) })), new());
        var measurement = Assert.Single(Assert.Single(found).Measurements);
        Assert.Equal(EvidenceScope.HelperGroup, measurement.Scope); Assert.Equal(2, measurement.MemberCount);
        Assert.Null(measurement.Subject); Assert.Equal(.8, measurement.CpuCoreEquivalents);
    }

    [Fact]
    public void SimultaneousMemoryAndHandleGrowthKeepBothBaselines()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 600; t += 5)
            found = engine.Evaluate(graph.Apply(B(t, P(2) with { PrivateBytes = (2UL << 30) + (ulong)((512UL << 20) * (t / 600d)),
                HandleCount = 4000 + (int)(2000 * (t / 600d)) })), new());
        var growth = Assert.Single(found, f => f.Kind == FindingKind.Growth);
        Assert.Equal(2UL << 30, Assert.Single(growth.Measurements, m => m.Trigger == TriggerKind.MemoryGrowth).BaselinePrivateBytes);
        var handles = Assert.Single(growth.Measurements, m => m.Trigger == TriggerKind.HandleGrowth);
        Assert.Equal(4000, handles.BaselineHandleCount); Assert.Equal(6000, handles.HandleCount);
    }

    private static Finding Sample() => new("private-chat-title/12345:987654321", FindingKind.Churn, EvidenceConfidence.Medium,
        [new(12346, 987654322)], ["TwoWindowsSnapshotAppearances", "private-chat-title"], false, B(0).Utc, B(0).Utc,
        Impact.Moderate, "2", new(12345, 987654321), [], [], 100, Coverage.SnapshotOnly, true, FindingState.Active)
        { MeasurementsAtUtc = B(0).Utc, Measurements = [new(TriggerKind.Churn, EvidenceScope.AssociatedFamily, 1,
            new(12346, 987654322), 60, PreviousWindowStarts: 12, RecentWindowStarts: 14, ThresholdCount: 10)] };

    [Fact]
    public void ExportCorrelatesEpisodesAndOwnersWithoutLeakingIdentitiesOrFreeText()
    {
        var f = Sample();
        var result = DiagnosticExporter.Export(new(new(), [
            new(1, B(0).Utc, HistoryKind.Finding, "private-chat-title", f),
            new(1, B(200).Utc, HistoryKind.Finding, "private-chat-title", f with { State = FindingState.Resolved }),
            new(1, B(300).Utc, HistoryKind.Finding, "private-chat-title", f with { FirstSeenUtc = B(300).Utc })],
            Coverage.SnapshotOnly, @"C:\Users\secret", "secret"));
        foreach (string text in new[] { result.Json, result.Csv })
        {
            Assert.DoesNotContain("private-chat-title", text); Assert.DoesNotContain("987654321", text);
            Assert.DoesNotContain("987654322", text); Assert.DoesNotContain("12345", text); Assert.DoesNotContain("secret", text);
        }
        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var rows = json.RootElement.GetProperty("entries");
        Assert.Equal(rows[0].GetProperty("episode").GetString(), rows[1].GetProperty("episode").GetString());
        Assert.NotEqual(rows[0].GetProperty("episode").GetString(), rows[2].GetProperty("episode").GetString());
        Assert.Equal(rows[0].GetProperty("owner").GetString(), rows[2].GetProperty("owner").GetString());
        Assert.Equal(rows[0].GetProperty("members")[0].GetString(), rows[0].GetProperty("measurements")[0].GetProperty("subject").GetString());
        Assert.Equal(12, rows[0].GetProperty("measurements")[0].GetProperty("previousWindowStarts").GetInt32());
        Assert.Equal(B(0).Utc, rows[1].GetProperty("measurementsAt").GetDateTimeOffset());
        Assert.Equal("LastMatchingObservation", rows[1].GetProperty("measurementBasis").GetString());
        Assert.Contains("Resolved", result.Csv);
    }

    [Fact]
    public void LegacyHistoryDoesNotInventMeasurementTimes()
    {
        // Old history lacks all newly added properties.
        var old = JsonSerializer.Serialize(Sample() with { MeasurementsAtUtc = null, Measurements = [] });
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(old)!;
        foreach (string key in new[] { "MeasurementsAtUtc", "Measurements", "IsClearing", "ClearAfterSeconds" }) fields.Remove(key);
        var restored = JsonSerializer.Deserialize<Finding>(JsonSerializer.Serialize(fields))!;
        Assert.Null(restored.MeasurementsAtUtc); Assert.Empty(restored.Measurements);
        var export = DiagnosticExporter.Export(new(new(), [new(1, B(10).Utc, HistoryKind.Finding, "old", restored)], Coverage.SnapshotOnly, "", ""));
        Assert.Contains("LegacyTimestampUnavailable", export.Json);
        var roundTrip = JsonSerializer.Deserialize<Finding>(JsonSerializer.Serialize(Sample()))!;
        Assert.Equal(Sample().MeasurementsAtUtc, roundTrip.MeasurementsAtUtc);
        Assert.Equal(Sample().Measurements[0], Assert.Single(roundTrip.Measurements));
    }
}
