using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SquadMonitor;

/// <summary>
/// Monitors token usage against configurable thresholds and raises alerts
/// when limits are exceeded. Supports per-session, daily, and per-agent thresholds.
/// </summary>
public sealed class TokenAlertService
{
    private readonly TokenAlertConfig _config;
    private readonly List<TokenAlert> _activeAlerts = new();
    private readonly HashSet<string> _suppressedAlertKeys = new();

    public TokenAlertService(TokenAlertConfig? config = null)
    {
        _config = config ?? TokenAlertConfig.Default;
    }

    public IReadOnlyList<TokenAlert> ActiveAlerts => _activeAlerts;
    public TokenAlertConfig Config => _config;

    /// <summary>
    /// Evaluates current token usage against configured thresholds and returns any new alerts.
    /// </summary>
    public List<TokenAlert> Evaluate(TokenTracker tracker)
    {
        var newAlerts = new List<TokenAlert>();

        // Check daily cost threshold
        var todayEvents = tracker.Events
            .Where(e => e.Timestamp.Date == DateTime.UtcNow.Date)
            .ToList();
        var dailyCost = todayEvents.Sum(e => e.EstimatedCost);

        if (dailyCost >= _config.DailyCostLimitUsd)
        {
            TryAddAlert(newAlerts, new TokenAlert
            {
                Level = AlertLevel.Critical,
                Category = AlertCategory.DailyCost,
                Message = $"Daily cost ${dailyCost:F2} exceeds limit ${_config.DailyCostLimitUsd:F2}",
                CurrentValue = dailyCost,
                ThresholdValue = _config.DailyCostLimitUsd,
                Key = $"daily-cost-{DateTime.UtcNow:yyyy-MM-dd}"
            });
        }
        else if (dailyCost >= _config.DailyCostWarningUsd)
        {
            TryAddAlert(newAlerts, new TokenAlert
            {
                Level = AlertLevel.Warning,
                Category = AlertCategory.DailyCost,
                Message = $"Daily cost ${dailyCost:F2} approaching limit ${_config.DailyCostLimitUsd:F2}",
                CurrentValue = dailyCost,
                ThresholdValue = _config.DailyCostWarningUsd,
                Key = $"daily-cost-warn-{DateTime.UtcNow:yyyy-MM-dd}"
            });
        }

        // Check per-session token threshold
        foreach (var session in tracker.SessionStats.Values)
        {
            if (session.TotalTokens >= _config.SessionTokenLimit)
            {
                TryAddAlert(newAlerts, new TokenAlert
                {
                    Level = AlertLevel.Warning,
                    Category = AlertCategory.SessionTokens,
                    Message = $"Session {ShortId(session.SessionId)} used {TokenTracker.FormatTokenCount(session.TotalTokens)} tokens (limit: {TokenTracker.FormatTokenCount(_config.SessionTokenLimit)})",
                    CurrentValue = session.TotalTokens,
                    ThresholdValue = _config.SessionTokenLimit,
                    Key = $"session-tokens-{session.SessionId}"
                });
            }

            if (session.EstimatedCost >= _config.SessionCostLimitUsd)
            {
                TryAddAlert(newAlerts, new TokenAlert
                {
                    Level = AlertLevel.Critical,
                    Category = AlertCategory.SessionCost,
                    Message = $"Session {ShortId(session.SessionId)} cost ${session.EstimatedCost:F2} exceeds limit ${_config.SessionCostLimitUsd:F2}",
                    CurrentValue = session.EstimatedCost,
                    ThresholdValue = _config.SessionCostLimitUsd,
                    Key = $"session-cost-{session.SessionId}"
                });
            }
        }

        // Check per-agent cost threshold
        foreach (var agent in tracker.AgentStats.Values)
        {
            if (agent.EstimatedCost >= _config.AgentCostWarningUsd)
            {
                var level = agent.EstimatedCost >= _config.AgentCostLimitUsd
                    ? AlertLevel.Critical
                    : AlertLevel.Warning;

                TryAddAlert(newAlerts, new TokenAlert
                {
                    Level = level,
                    Category = AlertCategory.AgentCost,
                    Message = $"Agent '{agent.AgentName}' cost ${agent.EstimatedCost:F2} " +
                              (level == AlertLevel.Critical ? "exceeds" : "approaching") +
                              $" limit ${_config.AgentCostLimitUsd:F2}",
                    CurrentValue = agent.EstimatedCost,
                    ThresholdValue = level == AlertLevel.Critical ? _config.AgentCostLimitUsd : _config.AgentCostWarningUsd,
                    Key = $"agent-cost-{agent.AgentName}"
                });
            }
        }

        // Check total token usage
        var totalTokens = tracker.TotalInputTokens + tracker.TotalOutputTokens;
        if (totalTokens >= _config.TotalTokenLimit)
        {
            TryAddAlert(newAlerts, new TokenAlert
            {
                Level = AlertLevel.Critical,
                Category = AlertCategory.TotalTokens,
                Message = $"Total token usage {TokenTracker.FormatTokenCount(totalTokens)} exceeds limit {TokenTracker.FormatTokenCount(_config.TotalTokenLimit)}",
                CurrentValue = totalTokens,
                ThresholdValue = _config.TotalTokenLimit,
                Key = "total-tokens"
            });
        }

        // ── Budget alerts (SQUAD_TOKEN_BUDGET) ──
        if (_config.MaxTokenBudget > 0)
        {
            var budgetPct = (double)totalTokens / _config.MaxTokenBudget;

            if (budgetPct >= 1.0)
            {
                TryAddAlert(newAlerts, new TokenAlert
                {
                    Level = AlertLevel.Critical,
                    Category = AlertCategory.Budget,
                    Message = $"Token budget EXCEEDED: {TokenTracker.FormatTokenCount(totalTokens)} / {TokenTracker.FormatTokenCount(_config.MaxTokenBudget)} ({budgetPct:P0} used)",
                    CurrentValue = totalTokens,
                    ThresholdValue = _config.MaxTokenBudget,
                    Key = "budget-100"
                });
            }
            else if (budgetPct >= _config.BudgetWarningPct)
            {
                TryAddAlert(newAlerts, new TokenAlert
                {
                    Level = AlertLevel.Warning,
                    Category = AlertCategory.Budget,
                    Message = $"Token budget at {budgetPct:P0}: {TokenTracker.FormatTokenCount(totalTokens)} / {TokenTracker.FormatTokenCount(_config.MaxTokenBudget)}",
                    CurrentValue = totalTokens,
                    ThresholdValue = _config.MaxTokenBudget * _config.BudgetWarningPct,
                    Key = "budget-80"
                });
            }
        }

        return newAlerts;
    }

    /// <summary>
    /// Fires new alerts to the console and optionally to a configured webhook URL.
    /// Call this after Evaluate() to dispatch notifications.
    /// </summary>
    public async Task FireAlertsAsync(IReadOnlyList<TokenAlert> newAlerts)
    {
        foreach (var alert in newAlerts)
        {
            // Console notification
            var prefix = alert.Level == AlertLevel.Critical ? "🔴 [BUDGET ALERT]" : "🟡 [BUDGET WARNING]";
            Console.WriteLine($"{prefix} {alert.Message}");

            // Optional webhook POST
            if (!string.IsNullOrEmpty(_config.WebhookUrl))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var payload = JsonSerializer.Serialize(new
                    {
                        level = alert.Level.ToString(),
                        category = alert.Category.ToString(),
                        message = alert.Message,
                        currentValue = alert.CurrentValue,
                        thresholdValue = alert.ThresholdValue,
                        timestamp = alert.Timestamp.ToString("o")
                    });
                    await http.PostAsync(
                        _config.WebhookUrl,
                        new StringContent(payload, Encoding.UTF8, "application/json"));
                }
                catch
                {
                    // Webhook is optional — swallow errors silently
                }
            }
        }
    }

    /// <summary>
    /// Suppresses an alert so it won't be raised again (until Reset is called).
    /// </summary>
    public void Suppress(string alertKey) => _suppressedAlertKeys.Add(alertKey);

    /// <summary>
    /// Clears all active alerts and suppressions.
    /// </summary>
    public void Reset()
    {
        _activeAlerts.Clear();
        _suppressedAlertKeys.Clear();
    }

    /// <summary>
    /// Returns Spectre.Console markup lines for displaying alerts in the dashboard.
    /// </summary>
    public List<string> RenderAlertLines()
    {
        var lines = new List<string>();
        if (_activeAlerts.Count == 0) return lines;

        lines.Add("");
        lines.Add("[red bold] ⚠ TOKEN ALERTS[/]");
        foreach (var alert in _activeAlerts.OrderByDescending(a => a.Level))
        {
            var icon = alert.Level == AlertLevel.Critical ? "🔴" : "🟡";
            var color = alert.Level == AlertLevel.Critical ? "red" : "yellow";
            lines.Add($" {icon} [{color}]{Spectre.Console.Markup.Escape(alert.Message)}[/]");
        }
        return lines;
    }

    private void TryAddAlert(List<TokenAlert> newAlerts, TokenAlert alert)
    {
        if (_suppressedAlertKeys.Contains(alert.Key)) return;
        if (_activeAlerts.Any(a => a.Key == alert.Key)) return;

        alert.Timestamp = DateTime.UtcNow;
        _activeAlerts.Add(alert);
        newAlerts.Add(alert);
    }

    private static string ShortId(string id) =>
        id.Length > 12 ? id[..8] + "..." : id;
}

// ── Configuration ──

public sealed class TokenAlertConfig
{
    /// <summary>Daily cost warning threshold in USD.</summary>
    public double DailyCostWarningUsd { get; set; } = 25.00;

    /// <summary>Daily cost critical threshold in USD.</summary>
    public double DailyCostLimitUsd { get; set; } = 50.00;

    /// <summary>Per-session token limit.</summary>
    public long SessionTokenLimit { get; set; } = 2_000_000;

    /// <summary>Per-session cost limit in USD.</summary>
    public double SessionCostLimitUsd { get; set; } = 20.00;

    /// <summary>Per-agent cost warning threshold in USD.</summary>
    public double AgentCostWarningUsd { get; set; } = 15.00;

    /// <summary>Per-agent cost critical threshold in USD.</summary>
    public double AgentCostLimitUsd { get; set; } = 30.00;

    /// <summary>Total token usage limit across all sessions.</summary>
    public long TotalTokenLimit { get; set; } = 10_000_000;

    /// <summary>
    /// Maximum token budget for the current session/day.
    /// Read from the SQUAD_TOKEN_BUDGET environment variable. Default: 100,000.
    /// Alerts fire at <see cref="BudgetWarningPct"/> (default 80%) and 100%.
    /// </summary>
    public long MaxTokenBudget { get; set; } = 100_000;

    /// <summary>
    /// Fraction of <see cref="MaxTokenBudget"/> at which a warning alert fires.
    /// Default: 0.80 (80%).
    /// </summary>
    public double BudgetWarningPct { get; set; } = 0.80;

    /// <summary>
    /// Optional webhook URL to POST alert payloads to.
    /// Read from the SQUAD_WEBHOOK_URL environment variable.
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Default configuration with reasonable thresholds.</summary>
    public static TokenAlertConfig Default => new();

    /// <summary>
    /// Creates a configuration from environment variables, falling back to defaults.
    /// </summary>
    public static TokenAlertConfig FromEnvironment()
    {
        var config = new TokenAlertConfig();

        if (double.TryParse(Environment.GetEnvironmentVariable("SQUAD_DAILY_COST_WARNING"), out var dcw))
            config.DailyCostWarningUsd = dcw;
        if (double.TryParse(Environment.GetEnvironmentVariable("SQUAD_DAILY_COST_LIMIT"), out var dcl))
            config.DailyCostLimitUsd = dcl;
        if (long.TryParse(Environment.GetEnvironmentVariable("SQUAD_SESSION_TOKEN_LIMIT"), out var stl))
            config.SessionTokenLimit = stl;
        if (double.TryParse(Environment.GetEnvironmentVariable("SQUAD_SESSION_COST_LIMIT"), out var scl))
            config.SessionCostLimitUsd = scl;
        if (double.TryParse(Environment.GetEnvironmentVariable("SQUAD_AGENT_COST_WARNING"), out var acw))
            config.AgentCostWarningUsd = acw;
        if (double.TryParse(Environment.GetEnvironmentVariable("SQUAD_AGENT_COST_LIMIT"), out var acl))
            config.AgentCostLimitUsd = acl;
        if (long.TryParse(Environment.GetEnvironmentVariable("SQUAD_TOTAL_TOKEN_LIMIT"), out var ttl))
            config.TotalTokenLimit = ttl;
        if (long.TryParse(Environment.GetEnvironmentVariable("SQUAD_TOKEN_BUDGET"), out var budget))
            config.MaxTokenBudget = budget;
        if (double.TryParse(Environment.GetEnvironmentVariable("SQUAD_BUDGET_WARNING_PCT"), out var bwp))
            config.BudgetWarningPct = bwp;
        config.WebhookUrl = Environment.GetEnvironmentVariable("SQUAD_WEBHOOK_URL");

        return config;
    }
}

// ── Alert model ──

public sealed class TokenAlert
{
    public AlertLevel Level { get; set; }
    public AlertCategory Category { get; set; }
    public required string Message { get; set; }
    public double CurrentValue { get; set; }
    public double ThresholdValue { get; set; }
    public required string Key { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum AlertLevel
{
    Info,
    Warning,
    Critical
}

public enum AlertCategory
{
    DailyCost,
    SessionTokens,
    SessionCost,
    AgentCost,
    TotalTokens,
    /// <summary>Token budget threshold (80% warning / 100% critical) from SQUAD_TOKEN_BUDGET.</summary>
    Budget
}
