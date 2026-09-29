using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using PageForge.App.Wpf.Resources;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Print preview (FR-VIEW-05): shows the exact pages that will reach the
/// printer, in a <see cref="DocumentViewer"/> with its own zoom, fit and page
/// navigation. Printing is deliberately not done here — the viewer's Print
/// command (toolbar button and Ctrl+P) is rerouted to <c>onPrint</c>, which
/// receives the zero-based page the viewer is showing so the print dialog's
/// "Current page" choice means what the user sees. Fully offline.
/// </summary>
internal sealed class PrintPreviewWindow : Window
{
    public PrintPreviewWindow(FixedDocument document, string documentName, Action<int> onPrint)
    {
        Title = UiStrings.Format("Print_Preview_Title", documentName);
        Width = 900;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var viewer = new DocumentViewer { Document = document };
        viewer.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Print,
            (_, e) =>
            {
                e.Handled = true;
                onPrint(Math.Max(0, viewer.MasterPageNumber - 1));
            },
            (_, e) => e.CanExecute = true));

        Content = viewer;
    }
}
