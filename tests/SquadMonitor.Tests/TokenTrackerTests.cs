using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SquadMonitor.Tests;

/// <summary>
/// Tests for <see cref="TokenTracker"/>: log parsing, deduplication,
/// per-session/model/agent accumulation, and cost estimation.
/// </summary>
public class TokenTrackerTests
{
    // ── FormatTokenCount ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.0K")]
    [InlineData(1_500, "1.5K")]
    [InlineData(999_999, "1000.0K")]
    [InlineData(1_000_000, "1.00M")]
    [InlineData(2_345_678, "2.35M")]
    public void FormatTokenCount_VariousValues_FormatsCorrectly(long input, string expected)
    {
        Assert.Equal(expected, TokenTracker.FormatTokenCount(input));
    }

    // ── EstimateCost ─────────────────────────────────────────────────────────

    [Fact]
    public void EstimateCost_ClaudeSonnet_UsesCorrectPricing()
    {
        // claude-sonnet: $3/M input, $15/M output
        double cost = TokenTracker.EstimateCost("claude-sonnet", inputTokens: 1_000_000, outputTokens: 1_000_000);
        Assert.Equal(18.00, cost, precision: 2);
    }

    [Fact]
    public void EstimateCost_ClaudeHaiku_UsesCorrectPricing()
    {
        // claude-haiku: $0.25/M input, $1.25/M output
        double cost = TokenTracker.EstimateCost("claude-haiku", inputTokens: 1_000_000, outputTokens: 1_000_000);
        Assert.Equal(1.50, cost, precision: 2);
    }

    [Fact]
    public void EstimateCost_PrefixModel_MatchesByContains()
    {
        // "claude-sonnet-4.5-20250514" should match "claude-sonnet" pricing
        double cost = TokenTracker.EstimateCost("claude-sonnet-4.5-20250514", 1_000_000, 0);
        Assert.Equal(3.00, cost, precision: 2);
    }

    [Fact]
    public void EstimateCost_UnknownModel_UsesMidRangeFallback()
    {
        // Falls back to $3.00/M input, $15.00/M output
        double cost = TokenTracker.EstimateCost("completely-unknown-llm", 1_000_000, 1_000_000);
        Assert.Equal(18.00, cost, precision: 2);
    }

    [Fact]
    public void EstimateCost_ZeroTokens_ReturnsZero()
    {
        Assert.Equal(0.0, TokenTracker.EstimateCost("claude-sonnet", 0, 0));
    }

    // ── ParseLogFile ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseLogFile_ValidUsageBlock_AccumulatesStats()
    {
        var tracker = new TokenTracker();
        var log = BuildTempLog($$"""
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "sess-001", "api_call_id": "call-1", "input_tokens": 1000, "output_tokens": 500, "timestamp": "{{DateTime.UtcNow:O}}"}
            }
            """);
        try
        {
            tracker.ParseLogFile(log);
            Assert.Equal(1000L, tracker.TotalInputTokens);
            Assert.Equal(500L, tracker.TotalOutputTokens);
            Assert.True(tracker.SessionStats.ContainsKey("sess-001"));
            Assert.Equal(1500L, tracker.SessionStats["sess-001"].TotalTokens);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void ParseLogFile_DuplicateApiCallId_DeduplicatesEntry()
    {
        var tracker = new TokenTracker();
        var sameId = "dup-call-xyz";
        var log = BuildTempLog($$"""
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "sess-002", "api_call_id": "{{sameId}}", "input_tokens": 100, "output_tokens": 50, "timestamp": "{{DateTime.UtcNow:O}}"}
            }
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "sess-002", "api_call_id": "{{sameId}}", "input_tokens": 100, "output_tokens": 50, "timestamp": "{{DateTime.UtcNow:O}}"}
            }
            """);
        try
        {
            tracker.ParseLogFile(log);
            // Should only count once despite appearing twice
            Assert.Equal(100L, tracker.TotalInputTokens);
            Assert.Equal(50L, tracker.TotalOutputTokens);
            Assert.Single(tracker.Events);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void ParseLogFile_MultipleDistinctCalls_AccumulatesAll()
    {
        var tracker = new TokenTracker();
        var log = BuildTempLog($$"""
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "sess-A", "api_call_id": "c1", "input_tokens": 200, "output_tokens": 100, "timestamp": "{{DateTime.UtcNow:O}}"}
            }
            {"kind": "assistant_usage", "model": "claude-haiku", "session_id": "sess-B", "api_call_id": "c2", "input_tokens": 50, "output_tokens": 25, "timestamp": "{{DateTime.UtcNow:O}}"}
            }
            """);
        try
        {
            tracker.ParseLogFile(log);
            Assert.Equal(250L, tracker.TotalInputTokens);
            Assert.Equal(125L, tracker.TotalOutputTokens);
            Assert.True(tracker.SessionStats.ContainsKey("sess-A"));
            Assert.True(tracker.SessionStats.ContainsKey("sess-B"));
            Assert.Equal(2, tracker.ModelStats.Count);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void ParseLogFile_PerSessionStats_TrackLastActivity()
    {
        var tracker = new TokenTracker();
        var ts1 = DateTime.UtcNow.AddMinutes(-30);
        var ts2 = DateTime.UtcNow;
        var log = BuildTempLog($$"""
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "s1", "api_call_id": "cx1", "input_tokens": 100, "output_tokens": 50, "timestamp": "{{ts1:O}}"}
            }
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "s1", "api_call_id": "cx2", "input_tokens": 200, "output_tokens": 75, "timestamp": "{{ts2:O}}"}
            }
            """);
        try
        {
            tracker.ParseLogFile(log);
            var stats = tracker.SessionStats["s1"];
            Assert.Equal(2, stats.Calls);
            Assert.Equal(300L, stats.InputTokens);
            Assert.Equal(125L, stats.OutputTokens);
            // LastActivity should reflect the later timestamp
            Assert.True(stats.LastActivity >= ts1);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Reset_ClearsAllData()
    {
        var tracker = new TokenTracker();
        var log = BuildTempLog($$"""
            {"kind": "assistant_usage", "model": "claude-sonnet", "session_id": "s1", "api_call_id": "r1", "input_tokens": 500, "output_tokens": 250, "timestamp": "{{DateTime.UtcNow:O}}"}
            }
            """);
        try
        {
            tracker.ParseLogFile(log);
            Assert.NotEqual(0L, tracker.TotalInputTokens);

            tracker.Reset();

            Assert.Equal(0L, tracker.TotalInputTokens);
            Assert.Equal(0L, tracker.TotalOutputTokens);
            Assert.Empty(tracker.SessionStats);
            Assert.Empty(tracker.AgentStats);
            Assert.Empty(tracker.ModelStats);
            Assert.Empty(tracker.Events);
        }
        finally { File.Delete(log); }
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static string BuildTempLog(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }
}
