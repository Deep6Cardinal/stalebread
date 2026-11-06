using StaleBread.App.ViewModels;
using StaleBread.Core.Model;
using StaleBread.Core.Storage;

namespace StaleBread.Windows.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void CurrentCardDistinguishesSettlingAndHistoricalMeasurementTime()
    {
        var time = DateTimeOffset.Parse("2026-11-05T16:00:00Z");
        var finding = Finding() with { IsClearing = true, MeasurementsAtUtc = time, Members = [] };
        Assert.StartsWith("Settling", DiagnosticText.Title(finding));
        Assert.Contains("no associated processes at last trigger", DiagnosticText.Title(finding));
        Assert.Contains("last matching observation", DiagnosticText.MeasurementSummary(finding));
        Assert.Contains("performance impact not established", DiagnosticText.MeasurementSummary(finding));
        Assert.Contains("No cleanup", DiagnosticText.Cleanup(finding));
    }

    [Fact]
    public void NewResolvedHistoryExplainsTimedClearAndOldMeasurements()
    {
        var f = Finding(state: FindingState.Resolved) with { ClearAfterSeconds = 120, MeasurementsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var text = DiagnosticText.History(new(1, DateTimeOffset.UtcNow, HistoryKind.Finding, "Churn", f));
        Assert.Contains("120 seconds", text); Assert.Contains("last matching observation", text);
        Assert.DoesNotContain("at this observation", text); Assert.DoesNotContain("three scheduled scans", text);
    }

    [Fact]
    public void LegacyHistoryExplainsMissingNumericEvidence()
    {
        Assert.Contains("measurement time unavailable", DiagnosticText.MeasurementSummary(Finding()));
        Assert.Contains("not recorded", DiagnosticText.MeasurementDetails(Finding()));
    }

    private static Finding Finding(FindingKind kind = FindingKind.Churn, FindingState state = FindingState.Active) =>
        new("Churn/42:123", kind, EvidenceConfidence.Medium, [new(43, 124)], ["TwoWindows10NewIdentities"], false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Impact.Moderate, "1", new(42, 123), [], [], null,
            Coverage.SnapshotOnly, true, state);

    [Fact]
    public void AccessDeniedExplainsFallbackWithoutRequiringElevation()
    {
        var text = DiagnosticText.CollectorMessage("Process trace unavailable: AccessDenied");
        Assert.Contains("Windows denied access", text);
        Assert.Contains("short-lived processes", text);
        Assert.Contains("AccessDenied", text);
        Assert.DoesNotContain("Run as administrator", text);
    }

    [Theory]
    [InlineData("Process trace stopped")]
    [InlineData("Process trace unavailable: ManagementException")]
    public void OtherTraceFailuresDoNotClaimAccessDenied(string error)
    {
        var text = DiagnosticText.CollectorMessage(error);
        Assert.Contains("Live process events unavailable", text);
        Assert.DoesNotContain("Windows denied access", text);
        Assert.Contains(error, text);
    }

    [Fact]
    public void UnrelatedCollectorErrorsArePreserved() =>
        Assert.Equal("Collector failed: IOException", DiagnosticText.CollectorMessage("Collector failed: IOException"));

    [Fact]
    public void SnapshotStatusDoesNotClaimMonitoringStopped() =>
        Assert.Equal("Watching · snapshot monitoring (live process events unavailable)", DiagnosticText.MonitorStatus(Coverage.SnapshotOnly, false));

    [Fact]
    public void PauseTakesPrecedenceOverCoverage() =>
        Assert.Equal("Paused", DiagnosticText.MonitorStatus(Coverage.SnapshotOnly, true));

    [Fact]
    public void ChurnExplainsWhyElevationDoesNotEnableCleanup()
    {
        var text = DiagnosticText.Cleanup(Finding());
        Assert.Contains("Repeated starts", text);
        Assert.Contains("not proof", text);
        Assert.Contains("Administrator mode does not enable cleanup", text);
    }

    [Fact]
    public void DegradedCoverageTakesPrecedenceOverEligibleFlag() =>
        Assert.Contains("incomplete", DiagnosticText.Cleanup(Finding() with { Coverage = Coverage.Degraded, CleanupEligible = true }));

    [Fact]
    public void EligibleLeftoverExplainsManualReview() =>
        Assert.Contains("Review cleanup", DiagnosticText.Cleanup(Finding(FindingKind.ObservedOrphan) with { CleanupEligible = true }));

    [Theory]
    [InlineData(FindingKind.PossibleOrphan, "owner's exit was not observed")]
    [InlineData(FindingKind.HeavyWorkload, "busy work")]
    [InlineData(FindingKind.Growth, "alone do not establish")]
    [InlineData(FindingKind.Accumulation, "alone do not establish")]
    [InlineData(FindingKind.ActiveLeftover, "still shows activity")]
    [InlineData(FindingKind.ObservedOrphan, "Open Details")]
    public void ReviewOnlyReasonsMatchFindingType(FindingKind kind, string explanation) =>
        Assert.Contains(explanation, DiagnosticText.Cleanup(Finding(kind)));

    [Fact]
    public void ResolvedFindingNeverInvitesCleanup() =>
        Assert.Contains("cannot authorize cleanup", DiagnosticText.Cleanup(Finding(state: FindingState.Resolved) with { CleanupEligible = true }));

    [Theory]
    [InlineData(FindingState.Active, "Active when recorded")]
    [InlineData(FindingState.Resolved, "Resolved")]
    public void HistoryUsesReadableTypeAndRecordedState(FindingState state, string expected)
    {
        var text = DiagnosticText.History(new(1, DateTimeOffset.UtcNow, HistoryKind.Finding, "Churn", Finding(state: state)));
        Assert.Contains("Repeated subprocess activity", text);
        Assert.Contains(expected, text);
        Assert.Contains("42", text);
        if (state == FindingState.Resolved)
        {
            Assert.Contains("three scheduled scans", text);
            Assert.Contains("not a cleanup result", text);
            Assert.DoesNotContain("No processes stopped", text);
        }
    }

    [Fact]
    public void HistoryPreservesActionsAndLegacyEntriesWithoutInventingState()
    {
        foreach (var kind in new[] { HistoryKind.Action, HistoryKind.Finding })
        {
            var text = DiagnosticText.History(new(1, DateTimeOffset.UtcNow, kind, "Original summary"));
            Assert.Contains("Original summary", text);
            Assert.DoesNotContain("Resolved", text);
        }
    }
}
