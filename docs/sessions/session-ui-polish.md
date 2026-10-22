# Session: interface polish, batch 1

**Date:** 2026-09-30

Started the UI work by capturing the running app in both themes (a PowerShell script
using PrintWindow; it needs no foreground window) and listing what looked unfinished.
The FrameForge colour tokens, type scale and Fluent chrome were already ported in
earlier phases, so this is polish, not a restyle.

Done: shared TabControl/TabItem style (Themes/Typography.xaml), pluralised status
line, search-box hint, tighter padding for the four sidebar tabs so they stay on one row.

Traps found:
- A custom TabControl template MUST name its content host `PART_SelectedContentHost`.
  Without it the tab's content vanished from the UI Automation tree (and would from
  screen readers); 21 of 22 UI tests failed with "Timed out waiting for PageIndicatorText".
- Naming a WPF-UI control with x:Name (e.g. `ui:TextBox`) breaks the build: the generated
  code writes `Wpf.Ui.Controls.TextBox`, which our own `PageForge.App.Wpf` namespace shadows.
  Use the plain control, or no x:Name.
- `AutomationProperties.AccessibilityView` is UWP-only; it does not exist in WPF.
- A transparent area shows the system Mica material, whose tint changes with window
  focus, so two screenshots of the same build can differ in background colour.

Not done yet (seen in the captures): "Skip to page" is permanently visible in the command
bar although it is a keyboard bypass; the Organize row repeats its tab's name as a label;
sidebar and toolbar surface levels differ in the light theme; Snip annotation is mouse-only.
