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

        var consolasFont = new FontFamily("Consolas");
        var normalFg = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        var condFg = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0x80));
        var deleteFg = new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60));
        var headerFg = new SolidColorBrush(Color.FromRgb(0x80, 0xB0, 0xFF));
        var watchFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x60));

        // ---- Breakpoints section ----
        if (_vm.Breakpoints.Count > 0)
        {
            BreakpointList.Items.Add(new TextBlock
            {
                Text = Strings.Breakpoints_SectionBreakpoints,
                Foreground = headerFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 4, 0, 2)
            });
        }

        var editFg = new SolidColorBrush(Color.FromRgb(0x80, 0xC0, 0xFF));
        var btnBg = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
        var btnBorder = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        foreach (var (addr, bp) in _vm.Breakpoints.OrderBy(kv => kv.Key))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Margin = new Thickness(2, 1, 2, 1);
            grid.Tag = addr;

            var cb = new CheckBox
            {
                IsChecked = bp.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            uint capturedAddr = addr;
            cb.Checked += (s, e) => { _vm.EnableBreakpoint(capturedAddr, true); RefreshList(); };
            cb.Unchecked += (s, e) => { _vm.EnableBreakpoint(capturedAddr, false); RefreshList(); };
            Grid.SetColumn(cb, 0);

            var textStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = $"${addr:X8}", FontFamily = consolasFont, FontSize = 13,
                Foreground = normalFg, Margin = new Thickness(4, 0, 0, 0)
            });
            if (!string.IsNullOrEmpty(bp.Condition))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = $"  if {bp.Condition}", FontFamily = consolasFont, FontSize = 11,
                    Foreground = condFg, Margin = new Thickness(4, 0, 0, 0)
                });
            }
            Grid.SetColumn(textStack, 1);

            // Edit condition button
            string capturedCond = bp.Condition;
            var editBtn = new Button
            {
                Content = Strings.Breakpoints_EditCondition,
                Background = btnBg, Foreground = editFg, BorderBrush = btnBorder,
                Padding = new Thickness(6, 2, 6, 2), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            editBtn.Click += (s, e) =>
            {
                var dialog = new EditConditionDialog(capturedAddr, capturedCond) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    _vm.SetBreakpointCondition(capturedAddr, dialog.Condition);
                    RefreshList();
                }
            };
            Grid.SetColumn(editBtn, 2);

            var delBtn = new Button
            {
                Content = Strings.Breakpoints_Delete,
                Background = btnBg, Foreground = deleteFg, BorderBrush = btnBorder,
                Padding = new Thickness(6, 2, 6, 2), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(4, 0, 4, 0)
            };
            delBtn.Click += (s, e) => { _vm.RemoveBreakpoint(capturedAddr); RefreshList(); };
            Grid.SetColumn(delBtn, 3);

            grid.Children.Add(cb);
            grid.Children.Add(textStack);
            grid.Children.Add(editBtn);
            grid.Children.Add(delBtn);
            BreakpointList.Items.Add(grid);
        }

        // ---- Watchpoints section ----
        if (_vm.Watchpoints.Count > 0)
        {
            BreakpointList.Items.Add(new TextBlock
            {
                Text = Strings.Breakpoints_SectionWatchpoints,
                Foreground = headerFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 8, 0, 2)
            });
        }

        foreach (var (addr, wp) in _vm.Watchpoints.OrderBy(kv => kv.Key))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Margin = new Thickness(2, 1, 2, 1);
            grid.Tag = addr;

            var cb = new CheckBox
            {
                IsChecked = wp.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            uint capturedAddr = addr;
            cb.Checked += (s, e) => { _vm.EnableWatchpoint(capturedAddr, true); RefreshList(); };
            cb.Unchecked += (s, e) => { _vm.EnableWatchpoint(capturedAddr, false); RefreshList(); };
            Grid.SetColumn(cb, 0);

            string sizeStr = wp.Size == WatchpointSize.Byte ? ".B" :
                             wp.Size == WatchpointSize.Long ? ".L" : ".W";
            string typeStr = wp.Type == WatchpointType.Read ? "R" :
                             wp.Type == WatchpointType.Write ? "W" : "RW";

            var textStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = $"${addr:X8}{sizeStr} [{typeStr}]", FontFamily = consolasFont, FontSize = 13,
                Foreground = watchFg, Margin = new Thickness(4, 0, 0, 0)
            });
            if (!string.IsNullOrEmpty(wp.Condition))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = $"  if {wp.Condition}", FontFamily = consolasFont, FontSize = 11,
                    Foreground = condFg, Margin = new Thickness(4, 0, 0, 0)
                });
            }
            Grid.SetColumn(textStack, 1);

            // Edit condition button
            string capturedCond = wp.Condition;
            var wpEditBtn = new Button
            {
                Content = Strings.Breakpoints_EditCondition,
                Background = btnBg, Foreground = editFg, BorderBrush = btnBorder,
                Padding = new Thickness(6, 2, 6, 2), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            wpEditBtn.Click += (s, e) =>
            {
                var dialog = new EditConditionDialog(capturedAddr, capturedCond) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    _vm.SetWatchpointCondition(capturedAddr, dialog.Condition);
                    RefreshList();
                }
            };
            Grid.SetColumn(wpEditBtn, 2);

            var delBtn = new Button
            {
                Content = Strings.Breakpoints_Delete,
                Background = btnBg, Foreground = deleteFg, BorderBrush = btnBorder,
                Padding = new Thickness(6, 2, 6, 2), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(4, 0, 4, 0)
            };
            delBtn.Click += (s, e) => { _vm.RemoveWatchpoint(capturedAddr); RefreshList(); };
            Grid.SetColumn(delBtn, 3);

            grid.Children.Add(cb);
            grid.Children.Add(textStack);
            grid.Children.Add(wpEditBtn);
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
        _vm.ClearAllWatchpoints();
        RefreshList();
    }

    private void AddWatchpoint_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddWatchpointDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.AddWatchpoint(dialog.WatchAddress, dialog.WatchSize, dialog.WatchType, dialog.WatchCondition);
            RefreshList();
        }
    }
}
