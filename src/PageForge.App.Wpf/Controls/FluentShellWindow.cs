// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.App.Wpf.Controls;

/// <summary>
/// WPF-UI's FluentWindow, re-exported under this assembly's own namespace so it
/// can be used as a XAML root element.
///
/// This exists to work around a name collision, not to add behaviour. Using
/// <c>ui:FluentWindow</c> directly as the root of MainWindow.xaml makes the XAML
/// compiler emit <c>class MainWindow : Wpf.Ui.Controls.FluentWindow</c> into
/// MainWindow.g.cs, unqualified. That code sits inside namespace
/// <c>PageForge.App.Wpf</c>, and the enclosing namespace <c>PageForge.App</c>
/// already has a member named <c>Wpf</c> — so <c>Wpf</c> binds to
/// <c>PageForge.App.Wpf</c> and the build fails with "the type or namespace name
/// 'Ui' does not exist in the namespace 'PageForge.App.Wpf'". The generated file
/// cannot be told to write <c>global::</c>, so the root type has to be one whose
/// name does not collide.
///
/// The same trap applies to any <c>ui:</c> element given an <c>x:Name</c>: the
/// generated field declaration would be unqualified too. Leave WPF-UI elements
/// unnamed in XAML, or wrap them the way this class wraps FluentWindow.
/// </summary>
public class FluentShellWindow : global::Wpf.Ui.Controls.FluentWindow
{
}
