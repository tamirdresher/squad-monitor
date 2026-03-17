using System.Globalization;
using System.Text.RegularExpressions;

namespace SquadMonitor;

/// <summary>
/// Parses Copilot/Ralph session logs and tracks token consumption per agent, per model,
/// and per session. Provides estimated cost calculations based on model pricing.
/// </summary>
public sealed class TokenTracker
{
    // ── Model pricing per 1M tokens (input / output) ──
    private static readonly Dictionary<string, (double InputPer1M, double OutputPer1M)> ModelPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet"]     = (3.00,  15.00),
        ["claude-sonnet-4"]   = (3.00,  15.00),
        ["claude-haiku"]      = (0.25,   1.25),
        ["claude-haiku-4"]    = (0.25,   1.25),
        ["claude-opus"]       = (15.00, 75.00),
        ["claude-opus-4"]     = (15.00, 75.00),
        ["gpt-4"]             = (2.50,  10.00),
        ["gpt-4o"]            = (2.50,  10.00),
        ["gpt-4.1"]           = (2.00,   8.00),
        ["gpt-5"]             = (2.50,  10.00),
        ["gpt-5-mini"]        = (0.40,   1.60),
    };

    // Known agent type names from Copilot CLI sub-agents
    private static readonly HashSet<string> KnownAgentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explore", "task", "general-purpose", "code-review",
        "belanna", "data", "kes", "neelix", "picard", "podcaster",
        "q", "ralph", "scribe", "seven", "squad", "troi", "worf"
    };

    private readonly Dictionary<string, AgentTokenStats> _agentStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionTokenStats> _sessionStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelTokenStats> _modelStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenApiIds = new();
    private readonly List<TokenUsageEvent> _events = new();

    public IReadOnlyDictionary<string, AgentTokenStats> AgentStats => _agentStats;
    public IReadOnlyDictionary<string, SessionTokenStats> SessionStats => _sessionStats;
    public IReadOnlyDictionary<string, ModelTokenStats> ModelStats => _modelStats;
    public IReadOnlyList<TokenUsageEvent> Events => _events;

    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public long TotalCachedTokens { get; private set; }
    public double TotalEstimatedCost { get; private set; }

    /// <summary>
    /// Scans all recent Copilot log files under ~/.copilot/logs and aggregates token usage.
    /// </summary>
    public void ScanLogs(string userProfile, int maxLogFiles = 10)
    {
        var logDir = Path.Combine(userProfile, ".copilot", "logs");
        if (!Directory.Exists(logDir)) return;

        var logFiles = new DirectoryInfo(logDir)
            .GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTime)
            .Take(maxLogFiles);

        foreach (var file in logFiles)
        {
            try { ParseLogFile(file.FullName); }
            catch { /* skip unreadable files */ }
        }
    }

    /// <summary>
    /// Parses a single log file for token usage events.
    /// </summary>
    public void ParseLogFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains("\"kind\": \"assistant_usage\"") || line.Contains("cli.model_call:"))
            {
                ParseUsageBlock(reader, line);
            }
        }
    }

    private void ParseUsageBlock(StreamReader reader, string firstLine)
    {
        var block = ReadBlock(reader, firstLine);

        // Deduplicate via api_id
        var apiId = ExtractString(block, @"""api_call_id"":\s*""([^""]+)""")
                 ?? ExtractString(block, @"""api_id"":\s*""([^""]+)""");
        if (apiId != null && !_seenApiIds.Add(apiId))
            return;

        var model = ExtractString(block, @"""model"":\s*""([^""]+)""") ?? "unknown";
        var sessionId = ExtractString(block, @"""session_id"":\s*""([^""]+)""") ?? "unknown";

        // Try to extract agent type from the block
        var agentType = ExtractString(block, @"""agent_type"":\s*""([^""]+)""")
                     ?? InferAgentFromContext(block)
                     ?? "unknown";

        var inputTokens = ExtractLong(block, @"""input_tokens"":\s*(\d+)");
        if (inputTokens == 0) inputTokens = ExtractLong(block, @"""prompt_tokens_count"":\s*(\d+)");

        var outputTokens = ExtractLong(block, @"""output_tokens"":\s*(\d+)");
        if (outputTokens == 0) outputTokens = ExtractLong(block, @"""completion_tokens_count"":\s*(\d+)");

        var cachedTokens = ExtractLong(block, @"""cache_read_tokens"":\s*(\d+)");
        if (cachedTokens == 0) cachedTokens = ExtractLong(block, @"""cached_tokens_count"":\s*(\d+)");

        var reportedCost = ExtractDouble(block, @"""cost"":\s*([\d.]+)");
        var estimatedCost = reportedCost > 0 ? reportedCost : EstimateCost(model, inputTokens, outputTokens);

        var timestamp = ExtractTimestamp(block) ?? DateTime.UtcNow;

        // Update totals
        TotalInputTokens += inputTokens;
        TotalOutputTokens += outputTokens;
        TotalCachedTokens += cachedTokens;
        TotalEstimatedCost += estimatedCost;

        // Update per-model stats
        if (!_modelStats.TryGetValue(model, out var modelStat))
        {
            modelStat = new ModelTokenStats { ModelName = model };
            _modelStats[model] = modelStat;
        }
        modelStat.Calls++;
        modelStat.InputTokens += inputTokens;
        modelStat.OutputTokens += outputTokens;
        modelStat.CachedTokens += cachedTokens;
        modelStat.EstimatedCost += estimatedCost;

        // Update per-agent stats
        if (!_agentStats.TryGetValue(agentType, out var agentStat))
        {
            agentStat = new AgentTokenStats { AgentName = agentType };
            _agentStats[agentType] = agentStat;
        }
        agentStat.Calls++;
        agentStat.InputTokens += inputTokens;
        agentStat.OutputTokens += outputTokens;
        agentStat.CachedTokens += cachedTokens;
        agentStat.EstimatedCost += estimatedCost;

        // Update per-session stats
        if (!_sessionStats.TryGetValue(sessionId, out var sessionStat))
        {
            sessionStat = new SessionTokenStats { SessionId = sessionId, StartTime = timestamp };
            _sessionStats[sessionId] = sessionStat;
        }
        sessionStat.Calls++;
        sessionStat.InputTokens += inputTokens;
        sessionStat.OutputTokens += outputTokens;
        sessionStat.CachedTokens += cachedTokens;
        sessionStat.EstimatedCost += estimatedCost;
        sessionStat.LastActivity = timestamp;

        // Record event
        _events.Add(new TokenUsageEvent
        {
            Timestamp = timestamp,
            Model = model,
            AgentType = agentType,
            SessionId = sessionId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedTokens = cachedTokens,
            EstimatedCost = estimatedCost
        });
    }

    /// <summary>
    /// Estimates cost from model name and token counts using known pricing.
    /// </summary>
    public static double EstimateCost(string model, long inputTokens, long outputTokens)
    {
        var pricing = ResolvePricing(model);
        return (inputTokens / 1_000_000.0 * pricing.InputPer1M)
             + (outputTokens / 1_000_000.0 * pricing.OutputPer1M);
    }

    private static (double InputPer1M, double OutputPer1M) ResolvePricing(string model)
    {
        // Try exact match first
        if (ModelPricing.TryGetValue(model, out var exact))
            return exact;

        // Try prefix match (e.g., "claude-sonnet-4.5-20250514" → "claude-sonnet")
        foreach (var kvp in ModelPricing)
        {
            if (model.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // Default to mid-range pricing
        return (3.00, 15.00);
    }

    private static string? InferAgentFromContext(string block)
    {
        foreach (var agent in KnownAgentTypes)
        {
            // Look for agent_type patterns like "explore", "task" in surrounding context
            if (Regex.IsMatch(block, $@"""agent_type"":\s*""{Regex.Escape(agent)}""", RegexOptions.IgnoreCase))
                return agent;
            if (Regex.IsMatch(block, $@"""type"":\s*""{Regex.Escape(agent)}""", RegexOptions.IgnoreCase))
                return agent;
        }
        return null;
    }

    private static DateTime? ExtractTimestamp(string block)
    {
        var ts = ExtractString(block, @"""timestamp"":\s*""([^""]+)""")
              ?? ExtractString(block, @"""created_at"":\s*""([^""]+)""");
        if (ts != null && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        return null;
    }

    /// <summary>
    /// Resets all tracked data.
    /// </summary>
    public void Reset()
    {
        _agentStats.Clear();
        _sessionStats.Clear();
        _modelStats.Clear();
        _seenApiIds.Clear();
        _events.Clear();
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        TotalCachedTokens = 0;
        TotalEstimatedCost = 0;
    }

    // ── Helpers (same patterns used in Program.cs / SharpUI.cs) ──

    private static string ReadBlock(StreamReader reader, string firstLine)
    {
        var lines = new List<string> { firstLine };
        for (int i = 0; i < 80; i++)
        {
            var next = reader.ReadLine();
            if (next == null) break;
            lines.Add(next);
            if (next.Length > 0 && next[0] == '}') break;
        }
        return string.Join('\n', lines);
    }

    private static long ExtractLong(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static double ExtractDouble(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string? ExtractString(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Formats a token count for display (e.g., 1234567 → "1.23M").
    /// </summary>
    public static string FormatTokenCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:F2}M",
        >= 1_000     => $"{count / 1_000.0:F1}K",
        _            => count.ToString()
    };
}

// ── Data models ──

public sealed class AgentTokenStats
{
    public required string AgentName { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public double EstimatedCost { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed class SessionTokenStats
{
    public required string SessionId { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public double EstimatedCost { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime LastActivity { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
    public TimeSpan Duration => LastActivity - StartTime;
}

public sealed class ModelTokenStats
{
    public required string ModelName { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public double EstimatedCost { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed class TokenUsageEvent
{
    public DateTime Timestamp { get; set; }
    public required string Model { get; set; }
    public required string AgentType { get; set; }
    public required string SessionId { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public double EstimatedCost { get; set; }
}
