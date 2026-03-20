using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace SquadMonitor.Tests;

/// <summary>
/// Tests for <see cref="TokenReport"/>, <see cref="TokenReportExporter"/>,
/// and export-to-disk functionality.
/// </summary>
public class TokenReportTests
{
    // ── GenerateDailyReport ──────────────────────────────────────────────────

    [Fact]
    public void GenerateDailyReport_NoEvents_ReturnsEmptyReport()
    {
        var tracker = new TokenTracker();
        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        Assert.Equal(0, summary.TotalCalls);
        Assert.Equal(0L, summary.TotalInputTokens);
        Assert.Equal(0L, summary.TotalOutputTokens);
        Assert.Equal(0.0, summary.TotalEstimatedCost);
        Assert.Empty(summary.ModelBreakdown);
        Assert.Empty(summary.AgentBreakdown);
        Assert.Empty(summary.SessionBreakdown);
    }

    [Fact]
    public void GenerateDailyReport_WithEvents_BreaksDownByModel()
    {
        var tracker = BuildTrackerWithEvents(
            ("claude-sonnet", "session-A", 1000, 500),
            ("claude-haiku", "session-B", 200, 100));

        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        Assert.Equal(2, summary.TotalCalls);
        Assert.Equal(2, summary.ModelBreakdown.Count);
        Assert.Contains(summary.ModelBreakdown, m => m.Model == "claude-sonnet");
        Assert.Contains(summary.ModelBreakdown, m => m.Model == "claude-haiku");
    }

    [Fact]
    public void GenerateDailyReport_WithEvents_BreaksDownBySession()
    {
        var tracker = BuildTrackerWithEvents(
            ("claude-sonnet", "session-X", 1000, 500),
            ("claude-sonnet", "session-Y", 800, 400));

        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        Assert.Equal(2, summary.SessionBreakdown.Count);
        var sessionIds = summary.SessionBreakdown.Select(s => s.SessionId).ToHashSet();
        Assert.Contains("session-X", sessionIds);
        Assert.Contains("session-Y", sessionIds);
    }

    [Fact]
    public void GenerateDailyReport_WithEvents_SumsTotalsCorrectly()
    {
        var tracker = BuildTrackerWithEvents(
            ("claude-sonnet", "s1", 1000, 500),
            ("claude-haiku", "s2", 200, 100));

        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        Assert.Equal(1200L, summary.TotalInputTokens);
        Assert.Equal(600L, summary.TotalOutputTokens);
    }

    // ── GenerateWeeklyReport ─────────────────────────────────────────────────

    [Fact]
    public void GenerateWeeklyReport_OnlyIncludesCurrentWeekEvents()
    {
        // Events added to the tracker are timestamped now (current week).
        var tracker = BuildTrackerWithEvents(("claude-sonnet", "s1", 500, 250));
        var report = new TokenReport(tracker);
        var weekly = report.GenerateWeeklyReport(DateTime.UtcNow);

        Assert.Equal(1, weekly.TotalCalls);
    }

    // ── RenderAsText ─────────────────────────────────────────────────────────

    [Fact]
    public void RenderAsText_IncludesSummarySection()
    {
        var tracker = BuildTrackerWithEvents(("claude-sonnet", "s1", 1000, 500));
        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        var text = TokenReport.RenderAsText(summary);

        Assert.Contains("SUMMARY", text);
        Assert.Contains("Estimated Cost:", text);
        Assert.Contains("MODEL BREAKDOWN", text);
    }

    [Fact]
    public void RenderAsText_EmptyReport_DoesNotThrow()
    {
        var tracker = new TokenTracker();
        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        var exception = Record.Exception(() => TokenReport.RenderAsText(summary));
        Assert.Null(exception);
    }

    // ── RenderAsMarkupLines ──────────────────────────────────────────────────

    [Fact]
    public void RenderAsMarkupLines_ReturnsNonEmptyList()
    {
        var tracker = new TokenTracker();
        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        var lines = TokenReport.RenderAsMarkupLines(summary);
        Assert.NotEmpty(lines);
    }

    // ── ExportDailyReport / TokenReportExporter ──────────────────────────────

    [Fact]
    public void ExportDailyReport_WritesJsonFile_ToExpectedPath()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"trt-{Guid.NewGuid():N}");
        try
        {
            var tracker = BuildTrackerWithEvents(("claude-sonnet", "s1", 1000, 500));
            var report = new TokenReport(tracker);

            var path = report.ExportDailyReport(tempHome, DateTime.UtcNow);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            // Validate JSON structure
            var json = File.ReadAllText(path!);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out _));
            Assert.True(doc.RootElement.TryGetProperty("report", out _));
        }
        finally
        {
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void ExportDailyReport_CalledTwice_MergesIntoSingleFile()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"trt-{Guid.NewGuid():N}");
        try
        {
            var today = DateTime.UtcNow;

            // First export
            var tracker1 = BuildTrackerWithEvents(("claude-sonnet", "s1", 1000, 500));
            var report1 = new TokenReport(tracker1);
            report1.ExportDailyReport(tempHome, today);

            // Second export on same day
            var tracker2 = BuildTrackerWithEvents(("claude-haiku", "s2", 200, 100));
            var report2 = new TokenReport(tracker2);
            report2.ExportDailyReport(tempHome, today);

            // Should still be a single file
            var exports = TokenReportExporter.ListExports(tempHome);
            Assert.Single(exports);

            // Load and verify merged totals
            var merged = TokenReportExporter.LoadExport(exports[0]);
            Assert.NotNull(merged);
            Assert.Equal(2, merged!.TotalCalls);
        }
        finally
        {
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void TokenReportExporter_ListExports_ReturnsFilesDescendingByName()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"trt-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(tempHome, ".squad", "token-reports");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "2025-01-01.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "2025-03-15.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "2025-02-10.json"), "{}");

            var exports = TokenReportExporter.ListExports(tempHome);

            Assert.Equal(3, exports.Count);
            // Sorted descending by filename
            Assert.True(
                string.Compare(Path.GetFileName(exports[0]), Path.GetFileName(exports[1]), StringComparison.Ordinal) > 0,
                "Exports should be sorted descending by name");
        }
        finally
        {
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void TokenReportExporter_ListExports_NonExistentDir_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz");
        var result = TokenReportExporter.ListExports(missing);
        Assert.Empty(result);
    }

    [Fact]
    public void TokenReportExporter_LoadExport_InvalidJson_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "NOT_JSON{{{{");
            var result = TokenReportExporter.LoadExport(tempFile);
            Assert.Null(result);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void TokenReportExporter_LoadExport_NonExistentFile_ReturnsNull()
    {
        var result = TokenReportExporter.LoadExport("/no/such/file.json");
        Assert.Null(result);
    }

    // ── ReportSummary – model/agent/session ordering ─────────────────────────

    [Fact]
    public void GenerateDailyReport_ModelBreakdown_OrderedByCostDescending()
    {
        var tracker = BuildTrackerWithEvents(
            ("claude-haiku", "s1", 100, 50),     // cheap
            ("claude-opus", "s2", 1000, 500),   // expensive
            ("claude-sonnet", "s3", 500, 250)); // mid

        var report = new TokenReport(tracker);
        var summary = report.GenerateDailyReport(DateTime.UtcNow);

        var costs = summary.ModelBreakdown.Select(m => m.EstimatedCost).ToList();
        for (int i = 0; i < costs.Count - 1; i++)
            Assert.True(costs[i] >= costs[i + 1], "Model breakdown should be ordered by cost descending");
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static TokenTracker BuildTrackerWithEvents(
        params (string model, string sessionId, long inputTokens, long outputTokens)[] entries)
    {
        var tracker = new TokenTracker();
        var sb = new System.Text.StringBuilder();
        var ts = DateTime.UtcNow;

        for (int i = 0; i < entries.Length; i++)
        {
            var (model, sessionId, input, output) = entries[i];
            sb.AppendLine($$"""{"kind": "assistant_usage", "model": "{{model}}", "session_id": "{{sessionId}}", "api_call_id": "test-call-{{i}}", "input_tokens": {{input}}, "output_tokens": {{output}}, "timestamp": "{{ts:O}}"}""");
            sb.AppendLine("}");
        }

        var tempLog = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempLog, sb.ToString());
            tracker.ParseLogFile(tempLog);
        }
        finally { File.Delete(tempLog); }

        return tracker;
    }
}
