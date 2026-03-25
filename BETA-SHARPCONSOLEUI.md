# SharpConsoleUI TUI Dashboard

This branch integrates **SharpConsoleUI** (v2.4.44), a modern .NET terminal UI framework with compositor-based multi-window architecture, providing a fully interactive dashboard experience.

## What's Been Done

✅ **Package Integration:**
- Upgraded `SharpConsoleUI` to v2.4.44
- `Spectre.Console` 0.54.0 (unchanged)

✅ **Polished Controls Layout:**
- `TableControl` — interactive GitHub Issues and PRs with fuzzy filtering (`/`) and column sorting (click header)
- `TabControl` — right panel with tabs: **1 Ralph** / **2 Tokens** / **3 Sessions**
- `SparklineControl` — agent activity chart with green→cyan gradient in the feed area
- `HorizontalSplitter` — drag-resizable split between main grid and feed area
- `StatusBarControl` — sticky bottom bar with labelled shortcuts

✅ **Gradient Background:**
- `WindowBuilder.WithBackgroundGradient(ColorGradient.FromColors([Navy, Black]), GradientDirection.Vertical)`
- Steel-blue border for the active window

✅ **Backward Compatibility:**
- Original Spectre.Console mode remains default
- SharpConsoleUI mode is opt-in via `--sharp-ui` / `--beta` flag

## Layout

```
Window (Maximized, Navy→Black gradient, SteelBlue border)
├── Header [StickyTop]  Squad Monitor v2 — TUI Dashboard  ⟳ HH:MM:SS
├── HorizontalSplitter (draggable)
│   ├── Top: HorizontalGrid (column splitter)
│   │   ├── Left (flex 6): ScrollablePanel
│   │   │   ├── TableControl: GitHub Issues  (filter with /, sort by header)
│   │   │   └── TableControl: Pull Requests  (filter with /, sort by header)
│   │   └── Right (flex 4): TabControl
│   │       ├── Tab "1 Ralph"    — Ralph heartbeat & recent rounds
│   │       ├── Tab "2 Tokens"   — Token usage & model stats
│   │       └── Tab "3 Sessions" — Live agent sessions
│   └── Bottom: ScrollablePanel
│       ├── SparklineControl: Agent Activity (green→cyan)
│       └── MarkupControl: Live agent feed entries
└── StatusBarControl [StickyBottom]
    q=Quit  /=Filter  r=Refresh  Tab=Next  |  1=Ralph  2=Tokens  3=Sessions
```

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `q` | Quit |
| `r` | Force refresh (invalidates all caches) |
| `1` / `2` / `3` | Switch to Ralph / Tokens / Sessions tab |
| `/` | Open fuzzy filter on focused table |
| `↑↓` | Sort / scroll |
| `Tab` | Navigate between panels |

## Usage

```bash
# Run standard mode (unchanged)
dotnet run

# Run SharpConsoleUI TUI dashboard
dotnet run -- --sharp-ui
# or
dotnet run -- --beta
```

## What SharpConsoleUI Provides

Unlike traditional TUI frameworks (Terminal.Gui, Spectre.Console), SharpConsoleUI uses a **multi-threaded compositor architecture** inspired by desktop GUI frameworks (WPF/Avalonia):

- **Multi-window system** with overlapping windows, Z-ordering, occlusion culling
- **Per-window async update threads** - each window can update independently in real-time
- **Compositor effects** - blur, animations, advanced rendering
- **Spectre.Console integration** - can embed Spectre widgets inside SharpConsoleUI windows
- **Measure → Arrange → Paint** rendering pipeline (DOM-based)
- Cross-platform: Windows, Linux, macOS

## Roadmap for Full Implementation

The current implementation is a **proof-of-concept placeholder**. Next steps:

1. **Multi-panel layout:**
   - Agent status panel (top-left)
   - Session log panel (top-right)
   - Decisions/activities panel (bottom)

2. **Async real-time updates:**
   - Each panel updates independently on its own thread
   - Live log streaming from ralph.log
   - Live activities feed from activities.jsonl
   - Token usage updates

3. **Compositor features:**
   - Window blur effects for inactive panels
   - Smooth animations for panel updates
   - Keyboard navigation between panels

4. **Spectre.Console integration:**
   - Embed existing Spectre widgets (tables, progress bars, etc.)
   - Maintain existing formatting and markup

## Resources

- **NuGet Package:** https://www.nuget.org/packages/SharpConsoleUI/2.4.44
- **GitHub:** https://github.com/nickprotop/ConsoleEx
- **Documentation:** https://dev.to/nikolaos_protopapas_d3bd6/building-terminal-uis-in-net-how-sharpconsoleui-complements-terminalgui-hb9

## Related

Issue: tamirdresher/microsoft/tamresearch1#311
