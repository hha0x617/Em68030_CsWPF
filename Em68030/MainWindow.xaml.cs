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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Em68030.Properties;
using Em68030.ViewModels;
using Em68030.Views;
using Microsoft.Win32;

namespace Em68030;

public partial class MainWindow : Window
{
    private MainViewModel _vm;
    private ConsoleWindow? _consoleWindow;
    private BreakpointsWindow? _breakpointsWindow;
    private FramebufferWindow? _framebufferWindow;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{Strings.MainWindow_Title} [{GitVersion.CommitHash}]";
        var iconUri = new Uri("pack://application:,,,/Assets/Em68030.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        _vm = new MainViewModel();
        DataContext = _vm;
        DisasmAddrBox.Text = _vm.PC.ToString("X8");
        MemAddrBox.Text = _vm.PC.ToString("X8");

        // Console output events may fire from the emulation background thread.
        // AppendChar/AppendString are thread-safe (use ConcurrentQueue internally).
        // EnsureConsoleWindow must only be called on the UI thread.
        _vm.ConsoleCharOutput += ch =>
        {
            if (_consoleWindow != null)
                _consoleWindow.AppendChar(ch);
            else
                Dispatcher.BeginInvoke(() => EnsureConsoleWindow().AppendChar(ch));
        };
        _vm.ConsoleStringOutput += s =>
        {
            if (_consoleWindow != null)
                _consoleWindow.AppendString(s);
            else
                Dispatcher.BeginInvoke(() => EnsureConsoleWindow().AppendString(s));
        };
        _vm.ConsoleCharInput = () =>
            EnsureConsoleWindow().ReadChar();
        _vm.ConsoleStringInput = () =>
            EnsureConsoleWindow().ReadString();
        _vm.ScrollToLineRequested += index =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (index >= 0 && index < DisasmList.Items.Count)
                    ScrollDisasmToCenter(index);
            });
        };

        _vm.OnFramebufferDeviceReset = () =>
        {
            Dispatcher.BeginInvoke(() => ReopenFramebufferWindow());
        };

        // Initialize menu state from saved config
        MenuShowFramebuffer.IsEnabled = _vm.FramebufferDevice != null;
    }

    private ConsoleWindow EnsureConsoleWindow()
    {
        if (_consoleWindow == null || !_consoleWindow.IsLoaded)
        {
            _consoleWindow = new ConsoleWindow(_vm.Config.ConsoleColumns, _vm.Config.ConsoleRows, _vm.Config.ConsoleScrollbackLines);
            _consoleWindow.Owner = this;
            _consoleWindow.OnCharInput = ch => _vm.SendConsoleChar(ch);
            _consoleWindow.Show();
        }
        return _consoleWindow;
    }

    private void OpenBinary_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
            Title = Strings.FileDialog_OpenBinary
        };
        if (dlg.ShowDialog() == true)
        {
            // Ask for load address using a simple input dialog
            var inputDlg = new Views.InputDialog(Strings.FileDialog_LoadAddress, Strings.FileDialog_EnterLoadAddress,
                _vm.Config.LastLoadAddress.ToString("X8"));
            inputDlg.Owner = this;
            if (inputDlg.ShowDialog() == true &&
                uint.TryParse(inputDlg.InputText, NumberStyles.HexNumber, null, out uint addr))
            {
                _vm.LoadBinaryFile(dlg.FileName, addr);

            }
        }
    }

    private void OpenSRecord_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "S-Record files (*.s19;*.s28;*.s37;*.srec;*.hex)|*.s19;*.s28;*.s37;*.srec;*.hex|All files (*.*)|*.*",
            Title = Strings.FileDialog_OpenSRecord
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.LoadSRecordFile(dlg.FileName);
        }
    }

    private void OpenElf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ELF files (*.elf;netbsd*;vmlinux*)|*.elf;netbsd*;vmlinux*|All files (*.*)|*.*",
            Title = Strings.FileDialog_OpenElf
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var result = _vm.LoadElfFile(dlg.FileName);
                ReopenFramebufferWindow();
                MenuShowFramebuffer.IsEnabled = _vm.FramebufferDevice != null;

                MessageBox.Show(
                    $"{Strings.Msg_ElfLoaded}\n\n" +
                    $"Machine: {result.MachineDescription}\n" +
                    $"Entry Point: ${result.EntryPoint:X8}\n" +
                    $"Load Range: ${result.StartAddress:X8} - ${result.EndAddress:X8}\n" +
                    $"Segments: {result.SegmentsLoaded}",
                    Strings.Msg_ElfLoaderTitle, MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Strings.Msg_FailedToLoadElf}\n{ex.Message}",
                    Strings.Msg_ElfLoaderError, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _vm.Cleanup();
        base.OnClosing(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RunToCursor_Click(object sender, RoutedEventArgs e)
    {
        if (DisasmList.SelectedItem is DisasmLineViewModel line && line.HasAddress)
        {
            _vm.RunToCursor(line.Address);
        }
    }

    private void SetPCToCursor_Click(object sender, RoutedEventArgs e)
    {
        if (DisasmList.SelectedItem is DisasmLineViewModel line && line.HasAddress)
        {
            _vm.SetPCToCursor(line.Address);
        }
    }

    private void ShowConsole_Click(object sender, RoutedEventArgs e)
    {
        EnsureConsoleWindow();
    }

    private void ShowFramebuffer_Click(object sender, RoutedEventArgs e)
    {
        EnsureFramebufferWindow();
    }

    private void ShowBreakpoints_Click(object sender, RoutedEventArgs e)
    {
        EnsureBreakpointsWindow();
    }

    private void EnsureBreakpointsWindow()
    {
        if (_breakpointsWindow == null || !_breakpointsWindow.IsLoaded)
        {
            _breakpointsWindow = new BreakpointsWindow(_vm);
            _breakpointsWindow.Owner = this;
            _breakpointsWindow.ScrollToAddressRequested += addr => _vm.ScrollToAddress(addr);
            _breakpointsWindow.Show();
        }
        else
        {
            _breakpointsWindow.Activate();
        }
        _breakpointsWindow.RefreshList();
    }

    private void EnsureFramebufferWindow()
    {
        if (_vm.FramebufferDevice == null) return;
        if (_framebufferWindow == null || !_framebufferWindow.IsLoaded)
        {
            _framebufferWindow = new FramebufferWindow(_vm.Memory, _vm.FramebufferDevice, _vm.InputDevice);
            _framebufferWindow.Owner = this;
            _framebufferWindow.Closed += (_, _) => _framebufferWindow = null;
            _framebufferWindow.Show();
        }
        else
        {
            _framebufferWindow.Activate();
        }
    }

    private void ReopenFramebufferWindow()
    {
        if (_framebufferWindow == null || !_framebufferWindow.IsLoaded) return;
        var left = _framebufferWindow.Left;
        var top = _framebufferWindow.Top;
        _framebufferWindow.Close();
        _framebufferWindow = null;
        EnsureFramebufferWindow();
        if (_framebufferWindow != null)
        {
            _framebufferWindow.Left = left;
            _framebufferWindow.Top = top;
        }
    }

    private void ToggleLst_Click(object sender, RoutedEventArgs e)
    {
        _vm.ShowLst = !_vm.ShowLst;
    }

    private void MhzText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _vm.ToggleMhzDisplayMode();
    }

    private void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToolBar toolBar)
        {
            if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflow)
                overflow.Visibility = Visibility.Collapsed;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_vm.Config.Clone(), () => _vm.UnmountAllScsiDisks());
        settings.Owner = this;
        if (settings.ShowDialog() == true)
        {
            _vm.ApplyConfig(settings.Config);
            MenuShowFramebuffer.IsEnabled = settings.Config.FramebufferEnabled;
            _consoleWindow?.SetScrollbackLines(settings.Config.ConsoleScrollbackLines);
            _consoleWindow?.SetTerminalSize(settings.Config.ConsoleColumns, settings.Config.ConsoleRows);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void DisasmAddrBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateDisassembly();
        }
    }

    private void DisasmGo_Click(object sender, RoutedEventArgs e)
    {
        NavigateDisassembly();
    }

    private void DisasmFollowPC_Click(object sender, RoutedEventArgs e)
    {
        // TwoWay binding already updated DisasmFollowPC from ToggleButton.IsChecked
        if (_vm.DisasmFollowPC)
            _vm.ResetDisasmFollowPC();
    }

    private uint ParseDisasmSize()
    {
        uint sizeBytes = 1024;
        string sizeText = DisasmSizeBox.Text.Trim();
        if (sizeText.StartsWith("$"))
        {
            uint.TryParse(sizeText[1..], NumberStyles.HexNumber, null, out sizeBytes);
        }
        else if (!string.IsNullOrEmpty(sizeText))
        {
            uint.TryParse(sizeText, NumberStyles.Integer, null, out sizeBytes);
        }
        return sizeBytes == 0 ? 1024 : sizeBytes;
    }

    private void NavigateDisassembly()
    {
        string text = DisasmAddrBox.Text.Trim();
        if (text.StartsWith("$")) text = text[1..];
        else if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) text = text[2..];

        if (string.IsNullOrEmpty(text))
        {
            if (_vm.HasProgramLoaded)
            {
                _vm.NavigateToProgram();
            }
            return;
        }

        if (uint.TryParse(text, NumberStyles.HexNumber, null, out uint addr))
        {
            uint sizeBytes = ParseDisasmSize();
            _vm.NavigateDisassembly(addr, sizeBytes);
            DisasmAddrBox.Text = addr.ToString("X8");
        }
        else
        {
            MessageBox.Show(string.Format(Strings.Msg_InvalidHexAddress, DisasmAddrBox.Text), Strings.Msg_Error,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextRunToCursor_Click(object sender, RoutedEventArgs e)
    {
        if (DisasmList.SelectedItem is DisasmLineViewModel line && line.HasAddress)
        {
            _vm.RunToCursor(line.Address);
        }
    }

    private void ContextSetPC_Click(object sender, RoutedEventArgs e)
    {
        if (DisasmList.SelectedItem is DisasmLineViewModel line && line.HasAddress)
        {
            _vm.SetPCToCursor(line.Address);
        }
    }

    private void DisasmList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DisasmList.SelectedItem is DisasmLineViewModel line && line.HasAddress)
        {
            _vm.ToggleBreakpoint(line.Address);
            _breakpointsWindow?.RefreshList();
        }
    }

    private void DisasmCopy_Click(object sender, RoutedEventArgs e)
    {
        CopyDisasmSelection();
    }

    private void DisasmList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CopyDisasmSelection();
            e.Handled = true;
        }
    }

    private void ScrollDisasmToCenter(int index)
    {
        DisasmList.ScrollIntoView(DisasmList.Items[index]);
        DisasmList.UpdateLayout();

        // Try to center the item in the viewport
        var scrollViewer = FindVisualChild<ScrollViewer>(DisasmList);
        if (scrollViewer != null)
        {
            double itemHeight = scrollViewer.ExtentHeight / DisasmList.Items.Count;
            double viewportMiddle = scrollViewer.ViewportHeight / 2;
            double targetOffset = index * itemHeight - viewportMiddle + itemHeight / 2;
            targetOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableHeight));
            scrollViewer.ScrollToVerticalOffset(targetOffset);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;
            var found = FindVisualChild<T>(child);
            if (found != null)
                return found;
        }
        return null;
    }

    private void CopyDisasmSelection()
    {
        if (DisasmList.SelectedItems.Count == 0) return;
        var texts = new List<string>();
        foreach (var item in DisasmList.SelectedItems)
        {
            if (item is DisasmLineViewModel line)
                texts.Add(line.Text);
        }
        if (texts.Count == 0) return;
        try
        {
            Clipboard.SetDataObject(string.Join(Environment.NewLine, texts));
        }
        catch { /* clipboard locked by another app */ }
    }

    private void MemAddrBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateMemory();
        }
    }

    private void MemGo_Click(object sender, RoutedEventArgs e)
    {
        NavigateMemory();
    }

    private void NavigateMemory()
    {
        string text = MemAddrBox.Text.Trim();
        // Remove common hex prefixes
        if (text.StartsWith("$")) text = text[1..];
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];

        if (uint.TryParse(text, NumberStyles.HexNumber, null, out uint addr))
        {
            // Parse size (decimal by default, $ prefix = hex)
            uint size = 256;
            string sizeText = MemSizeBox.Text.Trim();
            if (sizeText.StartsWith("$"))
            {
                uint.TryParse(sizeText[1..], NumberStyles.HexNumber, null, out size);
            }
            else
            {
                uint.TryParse(sizeText, NumberStyles.Integer, null, out size);
            }
            if (size == 0) size = 256;

            _vm.NavigateMemoryDump(addr, size);
            MemAddrBox.Text = addr.ToString("X8");
        }
        else
        {
            MessageBox.Show(string.Format(Strings.Msg_InvalidHexAddress, MemAddrBox.Text), Strings.Msg_Error,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MemCell_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.SelectAll();
        }
    }

    private void MemCell_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb &&
            tb.DataContext is ViewModels.MemoryByteCell cell)
        {
            // Find the parent row and refresh ASCII
            foreach (var row in _vm.MemoryDumpRows)
            {
                if (row.Cells.Contains(cell))
                {
                    row.RefreshAscii();
                    break;
                }
            }

            // Auto-advance to next cell when 2 hex digits are typed
            if (!tb.IsReadOnly && tb.Text.Length == 2 && tb.CaretIndex == 2)
            {
                MoveToCellOffset(cell, 0, 1);
            }
        }
    }

    private void MemCell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb &&
            tb.DataContext is ViewModels.MemoryByteCell cell)
        {
            int dRow = 0, dCol = 0;
            switch (e.Key)
            {
                case Key.Right:
                    if (tb.CaretIndex >= tb.Text.Length)
                    {
                        dCol = 1;
                        e.Handled = true;
                    }
                    break;
                case Key.Left:
                    if (tb.CaretIndex == 0)
                    {
                        dCol = -1;
                        e.Handled = true;
                    }
                    break;
                case Key.Up:
                    dRow = -1;
                    e.Handled = true;
                    break;
                case Key.Down:
                    dRow = 1;
                    e.Handled = true;
                    break;
                case Key.Tab:
                    dCol = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                    e.Handled = true;
                    break;
                case Key.Enter:
                    dCol = 1;
                    e.Handled = true;
                    break;
                default:
                    return;
            }

            if (dRow != 0 || dCol != 0)
            {
                MoveToCellOffset(cell, dRow, dCol);
            }
        }
    }

    private void MoveToCellOffset(ViewModels.MemoryByteCell fromCell, int dRow, int dCol)
    {
        int newRow = fromCell.Row + dRow;
        int newCol = fromCell.Column + dCol;

        // Wrap columns
        if (newCol >= 16) { newCol = 0; newRow++; }
        else if (newCol < 0) { newCol = 15; newRow--; }

        // Bounds check rows
        if (newRow < 0 || newRow >= _vm.MemoryDumpRows.Count) return;

        var targetCell = _vm.MemoryDumpRows[newRow].Cells[newCol];
        FocusCell(targetCell);
    }

    private void FocusCell(ViewModels.MemoryByteCell cell)
    {
        // Walk the visual tree to find the TextBox bound to this cell
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var tb = FindTextBoxForCell(MemoryDumpList, cell);
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static System.Windows.Controls.TextBox? FindTextBoxForCell(DependencyObject parent, ViewModels.MemoryByteCell cell)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.TextBox tb && tb.DataContext == cell)
                return tb;
            var result = FindTextBoxForCell(child, cell);
            if (result != null) return result;
        }
        return null;
    }
}

// Value converter for breakpoint indicator color (red for enabled, gray for disabled, transparent for none)
public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? Brushes.Red : Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class DisabledBreakpointToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? Brushes.Gray : Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
