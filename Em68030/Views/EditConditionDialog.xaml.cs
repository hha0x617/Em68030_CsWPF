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

using System.Windows;
using Em68030.Properties;

namespace Em68030.Views;

public partial class EditConditionDialog : Window
{
    public string Condition { get; private set; } = "";

    public EditConditionDialog(uint address, string currentCondition)
    {
        InitializeComponent();
        AddressLabel.Text = $"{Strings.Breakpoints_ConditionFor} ${address:X8}";
        HintLabel.Text = Strings.Breakpoints_ConditionHint;
        ConditionBox.Text = currentCondition;
        ConditionBox.Focus();
        ConditionBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Condition = ConditionBox.Text.Trim();
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Condition = "";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
