// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows.Automation.Peers;

namespace PageForge.App.Wpf.Controls;

/// <summary>
/// Automation peer for <see cref="HeadingTextBlock"/>: promotes the caption to a
/// real control element (so UIA clients can enumerate it and screen readers
/// announce its role) with the class name "Heading". The level itself is set by
/// the owner through <c>AutomationProperties.HeadingLevel</c>, which WPF on .NET
/// 10 publishes as the native UIA HeadingLevel. The WinUI 3 port (src/PageForge.App)
/// uses the same attached property (TSD §12.1).
/// </summary>
internal sealed class HeadingTextBlockAutomationPeer : TextBlockAutomationPeer
{
    public HeadingTextBlockAutomationPeer(HeadingTextBlock owner)
        : base(owner)
    {
    }

    /// <summary>Text blocks are invisible to content-view navigation by default;
    /// a heading must be a real element so AT can enumerate and jump to it.</summary>
    protected override bool IsControlElementCore() => true;

    protected override string GetClassNameCore() => "Heading";
}