# SharpConsoleUI TUI Dashboard

This branch integrates **SharpConsoleUI** (v2.4.44), a modern .NET terminal UI framework with compositor-based multi-window architecture, providing a fully interactive dashboard experience.

## What's Been Done

Γ£à **Package Integration:**
- Upgraded `SharpConsoleUI` to v2.4.44
- `Spectre.Console` 0.54.0 (unchanged)

Γ£à **Polished Controls Layout:**
- `TableControl` ΓÇö interactive GitHub Issues and PRs with fuzzy filtering (`/`) and column sorting (click header)
- `TabControl` ΓÇö right panel with tabs: **1 Ralph** / **2 Tokens** / **3 Sessions**
- `SparklineControl` ΓÇö agent activity chart with greenΓåÆcyan gradient in the feed area
- `HorizontalSplitter` ΓÇö drag-resizable split between main grid and feed area
- `StatusBarControl` ΓÇö sticky bottom bar with labelled shortcuts

Γ£à **Gradient Background:**
- `WindowBuilder.WithBackgroundGradient(ColorGradient.FromColors([Navy, Black]), GradientDirection.Vertical)`
- Steel-blue border for the active window

Γ£à **Backward Compatibility:**
- Original Spectre.Console mode remains default
- SharpConsoleUI mode is opt-in via `--sharp-ui` / `--beta` flag

## Layout

```
Window (Maximized, NavyΓåÆBlack gradient, SteelBlue border)
Γö£ΓöÇΓöÇ Header [StickyTop]  Squad Monitor v2 ΓÇö TUI Dashboard  Γƒ│ HH:MM:SS
Γö£ΓöÇΓöÇ HorizontalSplitter (draggable)
Γöé   Γö£ΓöÇΓöÇ Top: HorizontalGrid (column splitter)
Γöé   Γöé   Γö£ΓöÇΓöÇ Left (flex 6): ScrollablePanel
Γöé   Γöé   Γöé   Γö£ΓöÇΓöÇ TableControl: GitHub Issues  (filter with /, sort by header)
Γöé   Γöé   Γöé   ΓööΓöÇΓöÇ TableControl: Pull Requests  (filter with /, sort by header)
Γöé   Γöé   ΓööΓöÇΓöÇ Right (flex 4): TabControl
Γöé   Γöé       Γö£ΓöÇΓöÇ Tab "1 Ralph"    ΓÇö Ralph heartbeat & recent rounds
Γöé   Γöé       Γö£ΓöÇΓöÇ Tab "2 Tokens"   ΓÇö Token usage & model stats
Γöé   Γöé       ΓööΓöÇΓöÇ Tab "3 Sessions" ΓÇö Live agent sessions
Γöé   ΓööΓöÇΓöÇ Bottom: ScrollablePanel
Γöé       Γö£ΓöÇΓöÇ SparklineControl: Agent Activity (greenΓåÆcyan)
Γöé       ΓööΓöÇΓöÇ MarkupControl: Live agent feed entries
ΓööΓöÇΓöÇ StatusBarControl [StickyBottom]
    q=Quit  /=Filter  r=Refresh  Tab=Next  |  1=Ralph  2=Tokens  3=Sessions
```

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `q` | Quit |
| `r` | Force refresh (invalidates all caches) |
| `1` / `2` / `3` | Switch to Ralph / Tokens / Sessions tab |
| `/` | Open fuzzy filter on focused table |
| `ΓåæΓåô` | Sort / scroll |
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
- **Measure ΓåÆ Arrange ΓåÆ Paint** rendering pipeline (DOM-based)
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

- **NuGet Package:** https://www.nuget.org/packages/SharpConsoleUI/2.4.40
- **GitHub:** https://github.com/nickprotop/ConsoleEx
- **Documentation:** https://dev.to/nikolaos_protopapas_d3bd6/building-terminal-uis-in-net-how-sharpconsoleui-complements-terminalgui-hb9

## Related

Issue: tamirdresher/microsoft/tamresearch1#311
