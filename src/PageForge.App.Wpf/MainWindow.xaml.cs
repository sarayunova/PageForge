// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PageForge.App.Wpf.ViewModels;
using PageForge.App.Wpf.Views;
using PageForge.Core.View;
using PageForge.MuPdfInterop;

namespace PageForge.App.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            string? sample = App.FindSamplePdf();
            if (sample is not null)
            {
                await OpenDocumentAsync(sample);
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
            MessageBox.Show($"Could not open the source repository.\n\n{ex.Message}",
                "PageForge", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            close.Click += (_, _) => CloseTab(tab, vm);
            header.Children.Add(close);
            tab.Header = header;

            var view = new DocumentView();
            tab.Content = view;
            view.OpenDocumentRequested += path => _ = OpenDocumentAsync(path);
            view.SetTab(vm);

            DocTabs.Items.Add(tab);
            DocTabs.SelectedItem = tab;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open:\n{path}\n\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseTab(TabItem tab, DocumentTabViewModel vm)
    {
        DocTabs.Items.Remove(tab);
        _ = vm.Core.DisposeAsync().AsTask();
    }

    private static DocumentTabViewModel CreateTabViewModel()
    {
        MuPdfEngine engine = MuPdfEngine.Create();
        return new DocumentTabViewModel(new DocumentViewModel(engine));
    }
}
