---
name: winui-net-conventions
description: Use when writing or reviewing C#/.NET 8 or WinUI 3 (Windows App SDK) code for PageForge — XAML, MVVM structure, naming, threading, and dependency rules.
---

# WinUI 3 / .NET house conventions (PageForge)

## Stack
WinUI 3 (Windows App SDK), C#/.NET 8, MVVM via CommunityToolkit.Mvvm.

## Project layout (TSD §10)
- `PageForge.App/` — WinUI 3 desktop app (Views, ViewModels, behaviors)
- `PageForge.MuPdfInterop/` — MuPDF binding layer implementing `IPdfEngine`
- `PageForge.Core/` — editing engine, command stack, shared models (no UI / no MuPDF dependency)

## Rules
- **Dependency direction:** App → Interop → Core. `Core` never references WinUI or MuPDF.
- **Engine swappability:** all PDF access goes through `IPdfEngine` (in Core); MuPDF is just one implementation, replaced by fakes in unit tests.
- **MVVM:** Views bind to ViewModels; code-behind only wires interactions; canvas selection/drag-resize live in behaviors.
- **Naming:** PascalCase types/members, `x:Name` in camelCase, file-scoped namespaces, nullable reference types enabled.
- **Threading:** XAML on the UI thread (DispatcherQueue); engine/PDF calls off-thread, serialized.
- **Edit logic is Core-domain logic:** overflow detection, collision warning, font-fidelity checks are in Core and unit-testable without UI (TSD §6, FR-EDIT-02/03).
- **Platform:** x64 and ARM64 both build; reference native libs per-arch.

When in doubt about an API, consult live docs via the `context7` MCP server before writing code.