// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// One fillable form field in the side panel: its label, its current value, and
/// the means of writing that value back to the document.
///
/// This is the last part of the form surface to stop being built by hand, and
/// the reason it was left until last: unlike the outline drawn over the field -
/// which is geometry and nothing else, see <see cref="FormFieldBoxViewModel"/> -
/// a card carries live state and an action. Converting it needs a value and a
/// command, not a rectangle.
///
/// Checkbox and text fields differ in how a value reaches the document. A text
/// field has a Set button, because committing on every keystroke would rewrite
/// the PDF once per character. A checkbox has no such button: toggling it is
/// the commit.
/// </summary>
public sealed class FormFieldCardViewModel : ObservableObject
{
    private readonly Func<string, string, Task> _save;
    private string _value;
    private bool _isChecked;

    /// <param name="field">The field as the engine listed it.</param>
    /// <param name="save">Writes a value back, given the field id and the value.
    /// Injected rather than taking the tab view model, so this type needs to know
    /// nothing about documents and can be constructed in a test.</param>
    public FormFieldCardViewModel(PdfFormField field, Func<string, string, Task> save)
    {
        ArgumentNullException.ThrowIfNull(field);
        _save = save ?? throw new ArgumentNullException(nameof(save));

        Id = field.Id;
        Label = field.Label;
        KindLabel = field.Kind.ToString();
        IsCheckable = field.Kind is FormFieldKind.Checkbox or FormFieldKind.Radio;

        // Assigned to the backing fields, NOT through the properties. Going
        // through the setters here would look like a user edit and would write
        // every field back to the document just for opening the page.
        _value = field.Value;
        _isChecked = string.Equals(field.Value, "Yes", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(field.Value, "On", StringComparison.OrdinalIgnoreCase);

        // Names preserved exactly as the hand-built controls had them. They are
        // what UI Automation reports, so changing one silently breaks any test or
        // screen reader that looks the control up by name - the same trap as
        // dropping an x:Name during the earlier MVVM conversion.
        AccessibleName = $"{Label} ({KindLabel})";
        ValueAccessibleName = $"{Label} value";
        SetAccessibleName = $"Set {Label}";

        SetCommand = new AsyncRelayCommand(() => _save(Id, Value));
    }

    public string Id { get; }

    public string Label { get; }

    public string KindLabel { get; }

    /// <summary>True for checkbox and radio fields, which are toggled rather than
    /// typed into. The template shows one control or the other based on this.</summary>
    public bool IsCheckable { get; }

    public string AccessibleName { get; }

    public string ValueAccessibleName { get; }

    public string SetAccessibleName { get; }

    /// <summary>Commits <see cref="Value"/> for a text field.</summary>
    public IAsyncRelayCommand SetCommand { get; }

    /// <summary>The text field's value as edited, uncommitted until
    /// <see cref="SetCommand"/> runs.</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>
    /// A checkbox or radio field's state. Writing it saves immediately.
    ///
    /// Only when it actually changes: SetProperty returns false for an identical
    /// value, so re-binding the same state cannot trigger a write. The save is
    /// deliberately not awaited - a WPF two-way binding setter cannot await - and
    /// the save path reports its own failures.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _ = _save(Id, value ? "Yes" : "Off");
            }
        }
    }
}
