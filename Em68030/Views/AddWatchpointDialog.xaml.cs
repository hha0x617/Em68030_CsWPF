// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Globalization;
using System.Windows;
using Em68030.Properties;
using Em68030.ViewModels;

namespace Em68030.Views;

public partial class AddWatchpointDialog : Window
{
    public uint WatchAddress { get; private set; }
    public WatchpointSize WatchSize { get; private set; } = WatchpointSize.Word;
    public WatchpointType WatchType { get; private set; } = WatchpointType.Write;
    public string WatchCondition { get; private set; } = "";

    public AddWatchpointDialog()
    {
        InitializeComponent();
        AddressBox.Focus();
    }

    /// Edit mode: pre-fill all fields with existing watchpoint values.
    public AddWatchpointDialog(uint address, WatchpointSize size, WatchpointType type, string condition)
    {
        InitializeComponent();
        Title = Strings.Watchpoint_EditTitle;
        AddressBox.Text = $"0x{address:X}";
        SizeCombo.SelectedIndex = size == WatchpointSize.Byte ? 0 : size == WatchpointSize.Long ? 2 : 1;
        TypeCombo.SelectedIndex = type == WatchpointType.Read ? 1 : type == WatchpointType.ReadWrite ? 2 : 0;
        ConditionBox.Text = condition;
        AddressBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string addrStr = AddressBox.Text.Trim();
        if (string.IsNullOrEmpty(addrStr)) return;

        // Parse address: 0x prefix, $ prefix, or plain hex
        bool parsed = false;
        if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            parsed = uint.TryParse(addrStr.AsSpan(2), NumberStyles.HexNumber, null, out uint a) && (WatchAddress = a) == a;
        else if (addrStr.StartsWith('$'))
            parsed = uint.TryParse(addrStr.AsSpan(1), NumberStyles.HexNumber, null, out uint b) && (WatchAddress = b) == b;
        else
            parsed = uint.TryParse(addrStr, NumberStyles.HexNumber, null, out uint c) && (WatchAddress = c) == c;

        if (!parsed) return;

        WatchSize = SizeCombo.SelectedIndex switch
        {
            0 => WatchpointSize.Byte,
            2 => WatchpointSize.Long,
            _ => WatchpointSize.Word,
        };

        WatchType = TypeCombo.SelectedIndex switch
        {
            1 => WatchpointType.Read,
            2 => WatchpointType.ReadWrite,
            _ => WatchpointType.Write,
        };

        WatchCondition = ConditionBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
