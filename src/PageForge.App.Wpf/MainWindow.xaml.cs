// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.Logging;
using PageForge.App.Wpf.ViewModels;
using PageForge.App.Wpf.Views;
using PageForge.Core.View;
using PageForge.MuPdfInterop;
using PageForge.App.Wpf.Resources;

namespace PageForge.App.Wpf;

/// <summary>
/// The shell window. Derives from WPF-UI's FluentWindow (itself a
/// <see cref="Window"/>) so the Mica backdrop, rounded corners and the
/// content-extended title bar declared in MainWindow.xaml are honoured; a plain
/// Window would silently ignore those properties.
///
/// The base is <see cref="Controls.FluentShellWindow"/> rather than FluentWindow
/// itself; that wrapper exists purely to dodge a namespace collision in the
/// generated MainWindow.g.cs, and its own documentation explains why.
/// </summary>
public partial class MainWindow : Controls.FluentShellWindow
{
    /// <summary>
    /// How often edited documents are copied aside (TRD §6).
    ///
    /// A compromise, and worth saying which way it errs. MuPDF's dirty flag does
    /// not clear when a document is saved, so it cannot answer "changed since the
    /// last autosave" - only "edited at some point". Each tick therefore rewrites
    /// every edited document rather than only the ones that moved. Thirty seconds
    /// keeps that cost small while bounding what a crash can take to half a
    /// minute of work.
    /// </summary>
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(30);

    private readonly Diagnostics.RecoverySession _recovery = Diagnostics.RecoverySession.Start();
    private System.Windows.Threading.DispatcherTimer? _autosaveTimer;
    private bool _autosaveRunning;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        // Release the recovery folder when the window closes. Application.OnExit also
        // does this, but by then Application.MainWindow may no longer point at this
        // window, and the folder was left behind on every clean exit. Disposing twice
        // is harmless.
        Closed += (_, _) => DisposeRecovery();
        Themes.ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => Themes.ThemeManager.ThemeChanged -= OnThemeChanged;
        UpdateThemeIcon();
        Loaded += async (_, _) =>
        {
            await OfferRecoveredDocumentsAsync();

            string? sample = App.FindSamplePdf();
            if (sample is not null)
            {
                await OpenDocumentAsync(sample);
            }
            else
            {
                // No sample to auto-open, so this is the empty state's real first
                // appearance. It is only evaluated here and after an open or close,
                // never before, so it cannot flash over a document still loading.
                UpdateEmptyState();
            }
        };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (string file in dialog.FileNames)
            {
                _ = OpenDocumentAsync(file);
            }
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateThemeIcon();

    /// <summary>Shows the theme the button will switch to: a sun while the dark theme
    /// is on screen, a moon while the light theme is.</summary>
    private void UpdateThemeIcon()
    {
        if (ThemeButton.Content is global::Wpf.Ui.Controls.SymbolIcon icon)
        {
            icon.Symbol = Themes.ThemeManager.Effective == Themes.AppTheme.Dark
                ? global::Wpf.Ui.Controls.SymbolRegular.WeatherSunny24
                : global::Wpf.Ui.Controls.SymbolRegular.WeatherMoon24;
        }
    }

    /// <summary>Opens the theme menu under its button, ticking the current choice.</summary>
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeSystemItem.IsChecked = Themes.ThemeManager.Requested == Themes.AppTheme.System;
        ThemeLightItem.IsChecked = Themes.ThemeManager.Requested == Themes.AppTheme.Light;
        ThemeDarkItem.IsChecked = Themes.ThemeManager.Requested == Themes.AppTheme.Dark;
        ThemeMenu.PlacementTarget = ThemeButton;
        ThemeMenu.IsOpen = true;
    }

    /// <summary>Applies the chosen theme now and remembers it for the next start.</summary>
    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
        {
            Themes.AppTheme theme = Themes.ThemeSettings.Parse(tag);
            Themes.ThemeManager.Apply(theme);
            Themes.ThemeSettings.Save(theme);
        }
    }

    /// <summary>Opens the public source repository in the default browser, satisfying
    /// the AGPL §13 source-availability obligation for the desktop client (TSD §7).
    /// Reads the same PAGEFORGE_REPO_URL used by the hosted /source endpoint so the
    /// two stay in sync.</summary>
    private void ViewSource_Click(object sender, RoutedEventArgs e)
    {
        string repoUrl = Environment.GetEnvironmentVariable("PAGEFORGE_REPO_URL")
            ?? "https://github.com/sarayunova/PageForge";
        try
        {
            Process.Start(new ProcessStartInfo(repoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.For(typeof(MainWindow)).LogWarning(
                ex, "Could not open the source repository link.");
            MessageBox.Show(UiStrings.Format("Error_CouldNotOpenTheSource", ex.Message),
                UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens a document into its own tab (FR-VIEW-04 multi-doc tabs).
    /// Each tab owns an <see cref="IPdfEngine"/> instance via its view-model.</summary>
    private async Task OpenDocumentAsync(string path)
    {
        try
        {
            DocumentTabViewModel vm = CreateTabViewModel();
            await vm.InitializeAsync(Path.GetFullPath(path));

            var tab = new TabItem();
            var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(new TextBlock { Text = vm.DisplayName, VerticalAlignment = VerticalAlignment.Center });
            var close = new Button
            {
                Content = "✕",
                Margin = new Thickness(6, 0, 0, 0),
                Background = null,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetName(close, UiStrings.Format("Tab_CloseDocument_Name", vm.DisplayName));
            close.ToolTip = UiStrings.Format("Tab_CloseDocument_Name", vm.DisplayName);
            close.Click += async (_, _) =>
            {
                if (await ConfirmDiscardAsync(vm))
                {
                    CloseTab(tab, vm);
                }
            };
            header.Children.Add(close);
            tab.Header = header;

            var view = new DocumentView();
            tab.Content = view;
            view.OpenDocumentRequested += path => _ = OpenDocumentAsync(path);
            view.SetTab(vm);

            // The tab carries what the autosave loop needs. TabControl.Items holds
            // TabItems, not view models, and a parallel dictionary keyed by tab
            // would be a second thing to keep in step with the tab list - which is
            // how the empty state got out of step before it was driven from the
            // two places that change the count.
            tab.Tag = new TabState(vm, Guid.NewGuid().ToString("N"));

            DocTabs.Items.Add(tab);
            DocTabs.SelectedItem = tab;
            UpdateEmptyState();
            StartAutosaveIfNeeded();
        }
        catch (Exception ex)
        {
            // Log before telling the user. The dialog carries only ex.Message, and
            // twice now a failure here has had to be diagnosed from a screenshot of
            // an empty window because the stack trace went nowhere - most recently a
            // NullReferenceException thrown inside DocumentView's
            // InitializeComponent, which the message alone did not begin to explain.
            Diagnostics.AppLog.For(typeof(MainWindow)).LogError(
                ex, "Failed to open {Path}.", path);
            MessageBox.Show(UiStrings.Format("Error_FailedToOpen", path, ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// True when the document has edits that exist nowhere but in memory. If the
    /// engine cannot say, the answer is "yes": asking once too often is cheap,
    /// silently discarding someone's work is not.
    /// </summary>
    private static async Task<bool> HasUnsavedAsync(DocumentTabViewModel vm)
    {
        try
        {
            return await vm.Core.Engine.HasUnsavedChangesAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.For(typeof(MainWindow)).LogWarning(
                ex, "Could not tell whether {Name} has unsaved changes; asking to be safe.", vm.DisplayName);
            return true;
        }
    }

    /// <summary>Closing a tab with unsaved edits discards them for good, so it
    /// needs an explicit yes. Returns true when closing may go ahead.</summary>
    private static async Task<bool> ConfirmDiscardAsync(DocumentTabViewModel vm)
    {
        if (!await HasUnsavedAsync(vm))
        {
            return true;
        }

        return MessageBox.Show(
            UiStrings.Format("Confirm_CloseUnsaved", vm.DisplayName),
            UiStrings.Get("Common_AppTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool _exitConfirmed;

    /// <summary>
    /// Quitting is the same irreversible discard as closing a tab, for every tab
    /// at once. The check is asynchronous, so the close is cancelled first and
    /// re-issued once the user has answered.
    /// </summary>
    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitConfirmed || App.SmokeMode)
        {
            return;
        }

        e.Cancel = true;
        var unsaved = new List<string>();
        foreach (TabItem tab in DocTabs.Items.OfType<TabItem>().ToList())
        {
            if (tab.Tag is TabState state && await HasUnsavedAsync(state.Vm))
            {
                unsaved.Add($"  • {state.Vm.DisplayName}");
            }
        }

        if (unsaved.Count > 0)
        {
            MessageBoxResult answer = MessageBox.Show(
                UiStrings.Format("Confirm_ExitUnsaved", unsaved.Count, string.Join(Environment.NewLine, unsaved)),
                UiStrings.Get("Common_AppTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        // Posted, never called inline. When nothing is unsaved the checks above finish
        // synchronously, so this line runs INSIDE the Closing event that is still being
        // handled, and WPF throws "Cannot ... Close ... while a Window is closing" - an
        // unhandled exception on the dispatcher that killed the app on every quit and
        // left its recovery folder behind to be offered as a "crash".
        _exitConfirmed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CloseTab(TabItem tab, DocumentTabViewModel vm)
    {
        // Closing a tab is a decision, so its recovery copy goes with it. Leaving
        // it behind would offer the document back after the next crash as though
        // the user had never dealt with it.
        if (tab.Tag is TabState state)
        {
            _recovery.Discard(state.Key);
        }

        DocTabs.Items.Remove(tab);
        _ = vm.Core.DisposeAsync().AsTask();
        UpdateEmptyState();
    }

    /// <summary>What the autosave loop needs about an open tab.</summary>
    private sealed record TabState(DocumentTabViewModel Vm, string Key);

    /// <summary>
    /// Offers back whatever a previous run left behind.
    ///
    /// Asks rather than reopening silently: the copies are mid-edit states the
    /// user never chose to save, and restoring them unannounced would be its own
    /// surprise. Declining clears them, so the question is asked once.
    ///
    /// Skipped entirely in smoke mode - the headless proofs launch and exit
    /// repeatedly, and a modal dialog there would hang CI rather than protect
    /// anyone.
    /// </summary>
    private async Task OfferRecoveredDocumentsAsync()
    {
        if (App.SmokeMode)
        {
            return;
        }

        IReadOnlyList<Diagnostics.RecoverableDocument> found =
            Diagnostics.RecoverySession.FindRecoverable();
        if (found.Count == 0)
        {
            // Nothing to offer, so drop the folders left by runs that were killed
            // with no unsaved work in them.
            Diagnostics.RecoverySession.ClearEmptyAbandoned();
            return;
        }

        string list = string.Join(
            Environment.NewLine,
            found.Select(d => $"  • {d.DisplayName} (autosaved {d.SavedAtUtc.ToLocalTime():t})"));

        MessageBoxResult answer = MessageBox.Show(
            UiStrings.Format("Recovery_Prompt", found.Count, Environment.NewLine, list),
            UiStrings.Get("Recovery_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            foreach (Diagnostics.RecoverableDocument document in found)
            {
                // Copied out before the folder is cleared: opening straight from
                // the recovery folder would leave the tab reading a file this
                // method is about to delete.
                string restored = Path.Combine(
                    Path.GetTempPath(),
                    $"recovered-{Path.GetFileNameWithoutExtension(document.DisplayName)}-{Guid.NewGuid():N}.pdf");
                try
                {
                    File.Copy(document.RecoveredFile, restored, overwrite: true);
                    await OpenDocumentAsync(restored);
                }
                catch (Exception ex)
                {
                    Diagnostics.AppLog.For(typeof(MainWindow)).LogError(
                        ex, "Could not reopen the recovered copy of {Name}.", document.DisplayName);
                }
            }
        }

        if (answer != MessageBoxResult.Yes
            && MessageBox.Show(
                UiStrings.Get("Recovery_ConfirmDiscard"),
                UiStrings.Get("Recovery_Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            // Kept, and offered again next launch: nothing is deleted without a yes.
            return;
        }

        // Cleared once the user has decided. Kept after an accepted discard, the
        // same documents would be offered again after every future crash.
        Diagnostics.RecoverySession.ClearAbandoned();
    }

    private void StartAutosaveIfNeeded()
    {
        if (_autosaveTimer is not null || !_recovery.IsActive)
        {
            return;
        }

        _autosaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = AutosaveInterval };
        _autosaveTimer.Tick += async (_, _) => await AutosaveOpenDocumentsAsync();
        _autosaveTimer.Start();
    }

    /// <summary>
    /// Copies every edited open document aside.
    ///
    /// Re-entrancy is guarded rather than left to chance: saving a large document
    /// can outlast the interval, and overlapping ticks would have two writes
    /// racing for the same staging file.
    /// </summary>
    private async Task AutosaveOpenDocumentsAsync()
    {
        if (_autosaveRunning)
        {
            return;
        }

        _autosaveRunning = true;
        try
        {
            foreach (object item in DocTabs.Items)
            {
                if (item is not TabItem { Tag: TabState state })
                {
                    continue;
                }

                await _recovery.AutosaveAsync(
                    state.Key,
                    state.Vm.Core.Engine,
                    state.Vm.Core.SourcePath,
                    state.Vm.DisplayName).ConfigureAwait(true);
            }
        }
        finally
        {
            _autosaveRunning = false;
        }
    }

    /// <summary>
    /// Releases the recovery buffer on a clean exit, which is what makes anything
    /// left behind mean "this session did not finish".
    /// </summary>
    public void DisposeRecovery()
    {
        _autosaveTimer?.Stop();
        _recovery.Dispose();
    }

    /// <summary>
    /// Shows the empty state only when there is nothing else to look at.
    ///
    /// Driven from the two places that change the tab count rather than bound to
    /// it: TabControl.Items is not an observable collection, so a binding on
    /// Items.Count would set itself once at load and never update.
    /// </summary>
    private void UpdateEmptyState()
    {
        bool empty = DocTabs.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DocTabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static DocumentTabViewModel CreateTabViewModel()
    {
        MuPdfEngine engine = MuPdfEngine.Create();
        return new DocumentTabViewModel(new DocumentViewModel(engine));
    }
}
