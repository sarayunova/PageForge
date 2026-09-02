// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// FR-SEC-01 proof-of-concept dialog: collects the open/permissions passwords,
/// the encryption method and the permission mask, and exposes the resulting
/// <see cref="PdfProtectionOptions"/> when the user confirms. At least one
/// password is required (the OK button stays disabled otherwise); leaving the
/// permissions password empty makes the owner password equal to the open
/// password (single-password semantics).
/// </summary>
public partial class ProtectDialog : Window
{
    public ProtectDialog()
    {
        InitializeComponent();
    }

    /// <summary>The validated protection options, or null when the user cancelled.</summary>
    public PdfProtectionOptions? Options { get; private set; }

    private void Passwords_Changed(object sender, RoutedEventArgs e)
    {
        bool any = OpenPasswordBox.Password.Length > 0 || OwnerPasswordBox.Password.Length > 0;
        OkButton.IsEnabled = any;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        PdfPermissions permissions = PdfPermissions.None;
        if (PermPrint.IsChecked == true)
        {
            permissions |= PdfPermissions.Print;
        }

        if (PermCopy.IsChecked == true)
        {
            permissions |= PdfPermissions.Copy;
        }

        if (PermModify.IsChecked == true)
        {
            permissions |= PdfPermissions.Modify;
        }

        if (PermAnnotate.IsChecked == true)
        {
            permissions |= PdfPermissions.Annotate;
        }

        if (PermAssemble.IsChecked == true)
        {
            permissions |= PdfPermissions.Assemble;
        }

        var method = (MethodCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() switch
        {
            "4" => PdfEncryptionMethod.Aes128,
            "3" => PdfEncryptionMethod.Rc4_128,
            "2" => PdfEncryptionMethod.Rc4_40,
            _ => PdfEncryptionMethod.Aes256,
        };

        Options = new PdfProtectionOptions(
            OpenPassword: OpenPasswordBox.Password,
            PermissionsPassword: OwnerPasswordBox.Password,
            Method: method,
            Permissions: permissions);
        DialogResult = true;
    }
}