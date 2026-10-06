// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PageForge.App.Wpf.Controls;

/// <summary>
/// A section heading rendered like bold caption text but exposed to assistive
/// technology with real heading semantics (WCAG 1.3.1 / 2.4.6). The caption is a
/// real "Heading" control element in the automation tree (see
/// <see cref="HeadingTextBlockAutomationPeer"/>), and its level is published as
/// the native UIA HeadingLevel through <c>AutomationProperties.HeadingLevel</c>,
/// which WPF exposes from .NET 10. Screen readers can enumerate it, announce its
/// role and level, and read its name ("Organize tools", "Annotate tools", ...).
/// </summary>
public sealed class HeadingTextBlock : TextBlock
{
    /// <summary>The 1-based heading level (1–9) exposed through UI Automation.</summary>
    public static readonly DependencyProperty HeadingLevelProperty =
        DependencyProperty.Register(
            nameof(HeadingLevel),
            typeof(int),
            typeof(HeadingTextBlock),
            new FrameworkPropertyMetadata(2, OnHeadingLevelChanged));

    public HeadingTextBlock()
    {
        AutomationProperties.SetHeadingLevel(this, ToAutomationLevel(HeadingLevel));
    }

    /// <summary>Gets or sets the 1-based heading level exposed through automation
    /// (WCAG heading levels 1–9, no UI effect).</summary>
    public int HeadingLevel
    {
        get => (int)GetValue(HeadingLevelProperty);
        set => SetValue(HeadingLevelProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeadingTextBlockAutomationPeer(this);

    private static void OnHeadingLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeadingTextBlock heading)
        {
            AutomationProperties.SetHeadingLevel(heading, ToAutomationLevel((int)e.NewValue));
        }
    }

    /// <summary>Maps the 1-based level to the platform enum, clamped to the WCAG range.</summary>
    private static AutomationHeadingLevel ToAutomationLevel(int level) =>
        (AutomationHeadingLevel)Math.Clamp(level, 1, 9);
}