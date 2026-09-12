// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PageForge.App.Wpf.Controls;

/// <summary>
/// A section heading rendered like bold caption text but exposed to assistive
/// technology with real heading semantics (WCAG 1.3.1 / 2.4.6). WPF (.NET 8)
/// ships no declarative heading pattern and no override hook for the UIA
/// HeadingLevel property, so <see cref="HeadingTextBlock"/> pairs a
/// <see cref="HeadingLevel"/> property with a custom automation peer that
/// promotes the caption to a real "Heading" control element in the automation
/// tree — screen readers can enumerate it, announce its role, and read its name
/// ("Organize tools", "Annotate tools", "Edit tools").
/// </summary>
public sealed class HeadingTextBlock : TextBlock
{
    /// <summary>The 1-based heading level (1–9) exposed through UI Automation.</summary>
    public static readonly DependencyProperty HeadingLevelProperty =
        DependencyProperty.Register(
            nameof(HeadingLevel),
            typeof(int),
            typeof(HeadingTextBlock),
            new FrameworkPropertyMetadata(2));

    /// <summary>Gets or sets the 1-based heading level exposed through automation
    /// (WCAG heading levels 1–9, no UI effect).</summary>
    public int HeadingLevel
    {
        get => (int)GetValue(HeadingLevelProperty);
        set => SetValue(HeadingLevelProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeadingTextBlockAutomationPeer(this);
}