# SharpConsoleUI Beta Integration

This branch integrates **SharpConsoleUI** (v2.4.40), a modern .NET terminal UI framework with compositor-based multi-window architecture.

## What's Been Done

✅ **Package Integration:**
- Added `SharpConsoleUI` v2.4.40 NuGet package
- Upgraded `Spectre.Console` from 0.49.1 → 0.54.0 (required by SharpConsoleUI)

✅ **Beta Mode Entry Point:**
- New `--sharp-ui` or `--beta` command-line flag
- Proof-of-concept `SharpUI.cs` module demonstrating integration approach

✅ **Backward Compatibility:**
- Original Spectre.Console mode remains default
- SharpConsoleUI mode is opt-in via flag

## Usage

```bash
# Run standard mode (unchanged)
dotnet run

# Run SharpConsoleUI beta mode
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

- **NuGet Package:** https://www.nuget.org/packages/SharpConsoleUI/2.4.40
- **GitHub:** https://github.com/nickprotop/ConsoleEx
- **Documentation:** https://dev.to/nikolaos_protopapas_d3bd6/building-terminal-uis-in-net-how-sharpconsoleui-complements-terminalgui-hb9

## Related

Issue: tamirdresher/microsoft/tamresearch1#311
