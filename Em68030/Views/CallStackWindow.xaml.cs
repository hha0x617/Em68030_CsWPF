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

public partial class CallStackWindow : Window
{
    private readonly MainViewModel _vm;

    public event Action<uint>? NavigateToAddressRequested;

    public CallStackWindow(MainViewModel vm)
    {
        InitializeComponent();
        var iconUri = new Uri("pack://application:,,,/Assets/Em68030.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        _vm = vm;
        UpdateTitle();
    }

    /// <summary>
    /// Refresh the title bar so it reflects the current Call Stack mode
    /// (Shadow Stack vs. A6 Frame Chain). Called from the constructor and
    /// from RefreshList so the title stays in sync when the user toggles
    /// the mode via Settings (which triggers a RefreshList afterwards).
    /// </summary>
    private void UpdateTitle()
    {
        string modeLabel = _vm.Config.CallStackMode == "A6Chain"
            ? Strings.CallStack_TitleModeA6
            : Strings.CallStack_TitleModeShadow;
        Title = $"{Strings.Window_CallStack}  [{modeLabel}]";
    }

    public void RefreshList(bool isRunning)
    {
        UpdateTitle();
        CallStackList.Items.Clear();

        var consolasFont = new FontFamily("Consolas");
        var normalFg = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        var currentFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x80));
        var fpFg = new SolidColorBrush(Color.FromRgb(0x80, 0xB0, 0xFF));
        var heuristicFg = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
        var dimFg = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));

        if (isRunning)
        {
            CallStackList.Items.Add(new TextBlock
            {
                Text = Strings.CallStack_Running,
                Foreground = dimFg, FontSize = 13, Margin = new Thickness(8, 8, 0, 0)
            });
            return;
        }

        var entries = _vm.GetCallStack();
        if (entries.Count == 0)
        {
            CallStackList.Items.Add(new TextBlock
            {
                Text = Strings.CallStack_Empty,
                Foreground = dimFg, FontSize = 13, Margin = new Thickness(8, 8, 0, 0)
            });
            return;
        }

        for (int idx = 0; idx < entries.Count; idx++)
        {
            var (addr, fp, label) = entries[idx];
            bool isCurrent = (idx == 0);
            bool isHeuristic = (label == "?");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Margin = new Thickness(2, 1, 2, 1);
            grid.Tag = addr;

            // Frame index
            var idxText = new TextBlock
            {
                Text = $"#{idx}", FontFamily = consolasFont, FontSize = 13,
                Foreground = isCurrent ? currentFg : normalFg
            };
            Grid.SetColumn(idxText, 0);

            // Address
            var addrText = new TextBlock
            {
                Text = $"${addr:X8}", FontFamily = consolasFont, FontSize = 13,
                Foreground = isCurrent ? currentFg : (isHeuristic ? heuristicFg : normalFg)
            };
            Grid.SetColumn(addrText, 1);

            // Info
            var infoText = new TextBlock
            {
                FontFamily = consolasFont, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isCurrent)
            {
                infoText.Text = $"PC  A6=${fp:X8}";
                infoText.Foreground = currentFg;
            }
            else if (isHeuristic)
            {
                infoText.Text = "(heuristic)";
                infoText.Foreground = heuristicFg;
            }
            else
            {
                infoText.Text = $"FP=${fp:X8}";
                infoText.Foreground = fpFg;
            }
            Grid.SetColumn(infoText, 2);

            grid.Children.Add(idxText);
            grid.Children.Add(addrText);
            grid.Children.Add(infoText);
            CallStackList.Items.Add(grid);
        }
    }

    private void CallStackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CallStackList.SelectedItem is Grid grid && grid.Tag is uint addr)
        {
            NavigateToAddressRequested?.Invoke(addr);
        }
    }
}
