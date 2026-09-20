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
/// The widget rectangle is not asked for. Placing a signature by dragging it
/// on the page is the right interaction and it is not built yet; offering a
/// coordinate box instead would be worse than a sensible default, so the
/// signature goes in a fixed box near the bottom-left of the chosen page
/// (see <see cref="DefaultBounds"/>). The page number IS asked for, because
/// which page a signature sits on is a decision no default can make.
/// </remarks>
public partial class SignDialog : Window
{
    /// <summary>
    /// Where the signature widget is placed, in PDF points from the
    /// bottom-left: a band about 200x60 points above the bottom margin, clear
    /// of the footer on the corpus documents and large enough to be visible.
    /// </summary>
    public static readonly PdfRect DefaultBounds = new(72, 72, 272, 132);

    private readonly int _pageCount;

    public SignDialog(int pageCount, int initialPageIndex)
    {
        InitializeComponent();
        _pageCount = Math.Max(1, pageCount);
        PageBox.Text = (Math.Clamp(initialPageIndex, 0, _pageCount - 1) + 1)
            .ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The validated request, or null when the user cancelled.</summary>
    public PdfSignatureRequest? Request { get; private set; }

    /// <summary>Zero-based page the signature goes on; meaningful once <see cref="Request"/> is set.</summary>
    public int PageIndex { get; private set; }

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

        string? problem = Validate(out _);
        ValidationText.Text = problem ?? string.Empty;
        OkButton.IsEnabled = problem is null && CertificateBox.Text.Trim().Length > 0;
    }

    /// <summary>
    /// Returns the reason the input cannot be signed with, or null when it can.
    /// One method, so the message shown while typing and the decision to enable
    /// the button can never disagree.
    /// </summary>
    private string? Validate(out int pageIndex)
    {
        pageIndex = 0;

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

        if (!int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                          out int page) || page < 1 || page > _pageCount)
        {
            return $"Page must be a number between 1 and {_pageCount}.";
        }

        pageIndex = page - 1;
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? problem = Validate(out int pageIndex);
        if (problem is not null || CertificateBox.Text.Trim().Length == 0)
        {
            ValidationText.Text = problem ?? "Choose a signing certificate.";
            return;
        }

        PageIndex = pageIndex;
        Request = new PdfSignatureRequest(
            FieldName: FieldNameBox.Text.Trim(),
            Bounds: DefaultBounds,
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
