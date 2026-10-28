// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Listing;

namespace Lumora.Core.Components.UI;

internal sealed class SettingsTextFieldTemplate : SettingsRowTemplate
{
    public SettingsTextFieldTemplate(SettingsFonts fonts) : base(fonts) { }

    public override ListingRow Build(ListingView view, UIBuilder builder, Slot row)
    {
        var shell = BuildShell(row, clickable: false);
        var host = SettingsUI.Band(shell.Control, "Field", SettingsMetrics.ControlBand, 0f, 0f);
        var input = DashTextField.Attach(host, Fonts.Body, DashTheme.FontBody, string.Empty, 0);
        // A click away keeps what was typed, the way Enter does; Escape still drops it.
        input.SubmitOnBlur.Value = true;
        var listingRow = new TextFieldRow { Shell = shell, Input = input };
        input.EditingStarted += _ => listingRow.EditingKey = listingRow.Item?.Key;
        input.EditingFinished += (_, text) => listingRow.Commit(text);
        input.FocusLost += (_, _) => listingRow.EndEdit();
        return listingRow;
    }

    private sealed class TextFieldRow : ListingRow
    {
        public required SettingsRowShell Shell;
        public required TextInput Input;
        // The row is pooled: an edit belongs to the item it started on, never to whatever the row shows by
        // the time it ends.
        public string? EditingKey;

        public void Commit(string text)
        {
            if (Item is ListingTextField field && field.Interactable && field.Key == EditingKey)
                field.Write(text);
            EditingKey = null;
            if (Item != null)
                Bind(Item);
        }

        public void EndEdit()
        {
            // FocusLost runs before EditingFinished; Escape and a blur that typed nothing only get this one,
            // and the field goes back to the value it really holds on the next bind.
            if (Item != null && !Input.IsFocused)
                Bind(Item);
        }

        public override void Bind(ListingItem item)
        {
            Shell.Bind(item, useDetailAsHint: true);
            if (item is not ListingTextField field)
                return;
            Input.ReadOnly.Value = !field.Interactable;
            if (!string.Equals(Input.Placeholder.Value, field.Placeholder, System.StringComparison.Ordinal))
                Input.Placeholder.Value = field.Placeholder;
            if (Input.MaxLength.Value != field.MaxLength)
                Input.MaxLength.Value = field.MaxLength;
            // Never under the caret: the view rebinds on a timer, and a rebind mid-edit would undo the typing.
            if (Input.IsFocused)
                return;
            string value = field.Read() ?? string.Empty;
            if (!string.Equals(Input.Text.Value, value, System.StringComparison.Ordinal))
                Input.Text.Value = value;
        }

        public override void Unbind()
        {
            if (Input.IsFocused)
                Input.Cancel();
            EditingKey = null;
        }
    }
}
