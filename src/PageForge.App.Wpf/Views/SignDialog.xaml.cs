// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.IO;
using System.Windows;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// FR-SEC-03 dialog: collects the signing certificate, its password and the
/// signature's identity, and exposes the resulting
/// <see cref="PdfSignatureRequest"/> when the user confirms.
/// </summary>
/// <remarks>
/// Where the signature goes is decided before this dialog opens, on the page
/// itself (see <see cref="SignPlaceView"/>), so the position is shown here
/// rather than asked for. It used to be a page-number box with a fixed
/// rectangle, which made the one genuinely visual decision in signing the one
/// thing the user could not see.
/// </remarks>
public partial class SignDialog : Window
{
    private readonly PdfRect _bounds;

    /// <param name="pageIndex">Zero-based page the signature was placed on.</param>
    /// <param name="bounds">The placed box, in PDF points.</param>
    public SignDialog(int pageIndex, PdfRect bounds)
    {
        InitializeComponent();
        PageIndex = pageIndex;
        _bounds = bounds;

        PlacementText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "Page {0}, a {1:F0} by {2:F0} point box at ({3:F0}, {4:F0}). Cancel and drag again to move it.",
            pageIndex + 1,
            bounds.X1 - bounds.X0,
            bounds.Y1 - bounds.Y0,
            bounds.X0,
            bounds.Y0);
    }

    /// <summary>The validated request, or null when the user cancelled.</summary>
    public PdfSignatureRequest? Request { get; private set; }

    /// <summary>Zero-based page the signature goes on.</summary>
    public int PageIndex { get; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Certificate files (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            CertificateBox.Text = dialog.FileName;
        }
    }

    private void Inputs_Changed(object sender, RoutedEventArgs e) => Revalidate();

    private void Revalidate()
    {
        // Field change events fire while InitializeComponent is still building
        // the tree, so guard rather than assume every control exists.
        if (OkButton is null || ValidationText is null || CertificateBox is null)
        {
            return;
        }

        string? problem = Validate();
        ValidationText.Text = problem ?? string.Empty;
        OkButton.IsEnabled = problem is null && CertificateBox.Text.Trim().Length > 0;
    }

    /// <summary>
    /// Returns the reason the input cannot be signed with, or null when it can.
    /// One method, so the message shown while typing and the decision to enable
    /// the button can never disagree.
    /// </summary>
    private string? Validate()
    {
        string certificate = CertificateBox.Text.Trim();
        if (certificate.Length == 0)
        {
            // Nothing chosen yet is not an error worth shouting about; the
            // button stays disabled on its own.
            return null;
        }

        if (!File.Exists(certificate))
        {
            return "That certificate file does not exist.";
        }

        if (FieldNameBox.Text.Trim().Length == 0)
        {
            return "A signature field name is required.";
        }

        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? problem = Validate();
        if (problem is not null || CertificateBox.Text.Trim().Length == 0)
        {
            ValidationText.Text = problem ?? "Choose a signing certificate.";
            return;
        }

        Request = new PdfSignatureRequest(
            FieldName: FieldNameBox.Text.Trim(),
            Bounds: _bounds,
            CertificatePath: CertificateBox.Text.Trim(),
            CertificatePassword: CertificatePasswordBox.Password.Length == 0
                ? null
                : CertificatePasswordBox.Password,
            Reason: NullIfBlank(ReasonBox.Text),
            Location: NullIfBlank(LocationBox.Text));
        DialogResult = true;

        static string? NullIfBlank(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
