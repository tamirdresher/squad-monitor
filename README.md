# Squad Monitor

Live terminal dashboard for monitoring AI agent orchestration. See what your Copilot agents and sub-agents are doing in real-time.

![.NET](https://img.shields.io/badge/.NET-10.0-blue)
![NuGet](https://img.shields.io/nuget/v/squad-monitor)
![CI](https://github.com/tamirdresher/squad-monitor/actions/workflows/ci.yml/badge.svg)
![License](https://img.shields.io/badge/license-MIT-green)

## What It Does

- **Ralph Watch** — monitors the autonomous orchestrator loop (heartbeat, round status, failure tracking)
- **GitHub Integration** — shows open issues, PRs with CI status, recently merged PRs
- **Orchestration Activity** — tracks which agents are working on what
- **Live Refresh** — updates every 5 seconds with flicker-free rendering via [Spectre.Console](https://spectreconsole.net/)
- **Dual View Mode** — press `O` to toggle between full dashboard and orchestration-only view

## Screenshots

<!-- TODO: Add screenshots -->

## Quick Start

### Installation

#### Option 1: Install as .NET Global Tool (Recommended)

```bash
dotnet tool install -g squad-monitor
```

Then run from anywhere:

```bash
squad-monitor
```

To update:

```bash
dotnet tool update -g squad-monitor
```

To uninstall:

```bash
dotnet tool uninstall -g squad-monitor
```

#### Option 2: Build from Source

```bash
git clone https://github.com/tamirdresher/squad-monitor.git
cd squad-monitor
dotnet run
```

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [GitHub CLI](https://cli.github.com/) (`gh`) — authenticated via `gh auth login`
- A `.squad/` directory in your repo (created by the Squad orchestrator)

### Options

When running `squad-monitor` (either as a global tool or via `dotnet run`):

| Flag | Description |
|------|-------------|
| `--interval N` | Refresh every N seconds (default: 5) |
| `--once` | Render once and exit (good for CI/scripts) |
| `--no-github` | Skip GitHub API calls (useful offline or without `gh` auth) |

## How It Works

The monitor reads several data sources and renders them into a live terminal dashboard:

1. **Heartbeat file** (`~/.squad/ralph-heartbeat.json`) — written every 30s by the orchestrator, contains status, round number, PID, and metrics
2. **Structured log** (`~/.squad/ralph-watch.log`) — one line per round with exit code, duration, and metrics
3. **GitHub CLI** — queries issues and PRs via `gh issue list` / `gh pr list` (works with any repo `gh` is pointed at)
4. **Orchestration log** (`.squad/orchestration-log/`) — markdown files tracking agent assignments, status, and outcomes
5. **Agency logs** (`~/.agency/logs/`) — real-time tool calls and sub-agent spawns

### Data Flow

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│ ralph-watch  │────▶│  heartbeat   │────▶│   squad      │
│   .ps1       │     │   .json      │     │   monitor    │
│  (loop)      │     │              │     │  (dashboard) │
└──────┬───────┘     └──────────────┘     └──────────────┘
       │                                         ▲
       ▼                                         │
┌──────────────┐     ┌──────────────┐            │
│   copilot    │────▶│  ralph-watch │────────────┘
│   CLI        │     │    .log      │
│  (session)   │     │              │
└──────────────┘     └──────────────┘
```

## ralph-watch.ps1

Included as a reference orchestrator loop. It:

- Runs Copilot CLI sessions in a configurable loop (default: every 5 minutes)
- Writes heartbeat JSON before/after each round
- Structured logging with exit code and duration
- Lockfile prevents duplicate instances per directory
- Automatic `git pull` between rounds to stay current
- Webhook alerting on consecutive failures (≥3)
- Log rotation (capped at 500 entries / 1MB)
- Background activity monitor that tails agent session logs

### Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `$intervalMinutes` | `5` | Minutes between rounds |
| `$maxLogEntries` | `500` | Max log entries before rotation |
| `$maxLogBytes` | `1MB` | Max log file size before rotation |
| `SQUAD_WEBHOOK_URL` | — | Env var for webhook alert URL |
| `~/.squad/webhook.url` | — | File containing webhook URL (fallback) |

## Dashboard Panels

### Ralph Watch Loop
Shows the orchestrator's current status, round number, last run time, consecutive failures, PID, and metrics from the last round. Also displays a countdown to the next expected round.

### Ralph Recent Rounds
Last 5 log entries showing start/end times, duration, and exit code for each round.

### GitHub Issues (squad)
Open issues labeled `squad` with author, labels, assignees, and age. Limited by terminal height for readability.

### GitHub Pull Requests (Open)
All open PRs with review status (✓ Approved, ✗ Changes Requested, ⏳ Pending), CI status rollup, and age.

### GitHub Pull Requests (Recently Merged)
Last 10 merged PRs with author, branch, and merge time.

### Orchestration Activity (24h)
Recent agent activity parsed from `.squad/orchestration-log/` markdown files, showing agent name, task, status, and outcome.

## Building

```bash
# Debug build
dotnet build

# Release build
dotnet publish -c Release

# Single-file executable (Windows x64)
dotnet publish -c Release -r win-x64 --self-contained
```

## License

MIT
