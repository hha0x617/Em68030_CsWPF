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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Em68030.Properties;
using Em68030.ViewModels;

namespace Em68030.Views;

public partial class BreakpointsWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>
    /// ブレークポイントのアドレスへ逆アセンブリペインをスクロールする要求。
    /// </summary>
    public event Action<uint>? ScrollToAddressRequested;

    public BreakpointsWindow(MainViewModel vm)
    {
        InitializeComponent();
        var iconUri = new Uri("pack://application:,,,/Assets/Em68030.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        _vm = vm;
    }

    public void RefreshList()
    {
        BreakpointList.Items.Clear();

        // Sort breakpoints by address for stable display
        var sorted = _vm.Breakpoints.OrderBy(kv => kv.Key).ToList();

        foreach (var (addr, bp) in sorted)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Margin = new Thickness(2, 1, 2, 1);
            grid.Tag = addr; // Store address for double-click

            // Enable checkbox
            var cb = new CheckBox
            {
                IsChecked = bp.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            uint capturedAddr = addr;
            cb.Checked += (s, e) =>
            {
                _vm.EnableBreakpoint(capturedAddr, true);
                RefreshList();
            };
            cb.Unchecked += (s, e) =>
            {
                _vm.EnableBreakpoint(capturedAddr, false);
                RefreshList();
            };
            Grid.SetColumn(cb, 0);

            // Address text
            var addrText = new TextBlock
            {
                Text = $"${addr:X8}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(addrText, 1);

            // Delete button (right-aligned)
            var delBtn = new Button
            {
                Content = Strings.Breakpoints_Delete,
                Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 4, 0)
            };
            delBtn.Click += (s, e) =>
            {
                _vm.RemoveBreakpoint(capturedAddr);
                RefreshList();
            };
            Grid.SetColumn(delBtn, 2);

            grid.Children.Add(cb);
            grid.Children.Add(addrText);
            grid.Children.Add(delBtn);

            BreakpointList.Items.Add(grid);
        }
    }

    private void BreakpointList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BreakpointList.SelectedItem is Grid grid && grid.Tag is uint addr)
        {
            ScrollToAddressRequested?.Invoke(addr);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearAllBreakpoints();
        RefreshList();
    }
}
