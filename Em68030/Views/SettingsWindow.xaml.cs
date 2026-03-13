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
using System.Windows.Media;
using Em68030.Config;
using Em68030.IO;
using Em68030.Properties;
using Microsoft.Win32;

namespace Em68030.Views;

public partial class SettingsWindow : Window
{
    public EmulatorConfig Config { get; private set; }

    private class DiskRowInfo
    {
        public Panel RowPanel { get; set; } = null!;
        public TextBox PathBox { get; set; } = null!;
        public ComboBox IdBox { get; set; } = null!;
        public Button BrowseBtn { get; set; } = null!;
        public Button RemoveBtn { get; set; } = null!;
        public Button DisklabelBtn { get; set; } = null!;
    }

    private readonly List<DiskRowInfo> _diskRows = new();
    private int _desiredCdromId = 3;
    private readonly Action? _unmountScsiDisks;

    private static string GetSelectedItemText(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem cbi && cbi.Content is string text)
            return text;
        return "";
    }

    private static void SelectItemByText(ComboBox box, string text)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem cbi && cbi.Content is string content && content == text)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    public SettingsWindow(EmulatorConfig config, Action? unmountScsiDisks = null)
    {
        InitializeComponent();
        Config = config;
        _unmountScsiDisks = unmountScsiDisks;
        LoadSettings();
    }

    private void LoadSettings()
    {
        // Board type
        SelectItemByText(BoardTypeBox, Config.BoardType);
        Mvme147RomBox.Text = Config.Mvme147RomPath;

        // SCSI Disks
        foreach (var disk in Config.Mvme147ScsiDisks)
            AddDiskRow(disk.Path, disk.ScsiId);
        if (_diskRows.Count == 0)
            AddDiskRow("", 0);

        ScsiCdromPathBox.Text = Config.Mvme147ScsiCdromPath;
        _desiredCdromId = Math.Clamp(Config.Mvme147ScsiCdromId, 0, 6);
        BootPartitionBox.SelectedIndex = Math.Clamp(Config.Mvme147BootPartition, 0, 1);
        SelectItemByText(TargetOSBox, Config.TargetOS);
        LinuxCommandLineBox.Text = Config.LinuxCommandLine;
        UpdateTargetOSVisibility();
        SelectItemByText(NetworkModeBox, Config.NetworkMode);
        NatGatewayIpBox.Text = Config.NatGatewayIp;
        NatGatewayMacBox.Text = Config.NatGatewayMac;
        UpdateNatGatewayEnabled();
        UpdateMvme147Visibility();
        RefreshScsiIdOptions();

        MemSizeBox.Text = (Config.MemorySize / (1024 * 1024)).ToString();
        ConsoleEnabledBox.IsChecked = Config.ConsoleEnabled;
        ConsoleAddrBox.Text = Config.ConsoleBaseAddress.ToString("X8");
        HddEnabledBox.IsChecked = Config.HddEnabled;
        HddAddrBox.Text = Config.HddBaseAddress.ToString("X8");
        HddPathBox.Text = Config.HddImagePath;
        ConsoleScrollbackBox.Text = Config.ConsoleScrollbackLines.ToString();
        ConsoleColumnsBox.Text = Config.ConsoleColumns.ToString();
        ConsoleRowsBox.Text = Config.ConsoleRows.ToString();
        FontFamilyBox.Text = Config.FontFamily;
        FontSizeBox.Text = Config.FontSize.ToString();
        JitEnabledBox.IsChecked = Config.JitEnabled;
        JitMinBlockLengthBox.Text = Config.JitMinBlockLength.ToString();
        JitCompileThresholdBox.Text = Config.JitCompileThreshold.ToString();

        // Framebuffer
        FramebufferEnabledBox.IsChecked = Config.FramebufferEnabled;
        int[][] presets = [[320,240],[640,480],[800,600],[1024,768],[1280,720],[1280,1024],[1920,1080]];
        int resIdx = 1; // default 640x480
        for (int i = 0; i < presets.Length; i++)
        {
            if (presets[i][0] == Config.FramebufferWidth && presets[i][1] == Config.FramebufferHeight)
            { resIdx = i; break; }
        }
        FbResolutionBox.SelectedIndex = resIdx;
        FbBppBox.SelectedIndex = Config.FramebufferBpp switch { 8 => 0, 32 => 2, _ => 1 };
    }

    // ========================================================================
    // Dynamic SCSI disk row management
    // ========================================================================

    private DiskRowInfo AddDiskRow(string path, int scsiId)
    {
        var row = new DiskRowInfo();

        var idBox = new ComboBox
        {
            Width = 50,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // Items will be populated by RefreshScsiIdOptions; store desired ID as Tag
        idBox.Tag = scsiId;
        idBox.SelectionChanged += IdBox_SelectionChanged;

        var pathBox = new TextBox
        {
            Text = path,
            MinWidth = 160,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            CaretBrush = Brushes.White,
            Padding = new Thickness(4, 2, 4, 2),
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var browseBtn = new Button
        {
            Content = "...",
            Width = 30,
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            VerticalAlignment = VerticalAlignment.Center
        };
        browseBtn.Tag = row;
        browseBtn.Click += BrowseScsiDisk_Click;

        var removeBtn = new Button
        {
            Content = "\u00D7", // multiplication sign
            Width = 24,
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            VerticalAlignment = VerticalAlignment.Center
        };
        removeBtn.Tag = row;
        removeBtn.Click += RemoveScsiDisk_Click;

        var disklabelBtn = new Button
        {
            Content = Strings.Settings_Disklabel,
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontSize = 11,
            ToolTip = "Write a NetBSD disklabel to this disk image",
            VerticalAlignment = VerticalAlignment.Center
        };
        disklabelBtn.Tag = row;
        disklabelBtn.Click += WriteDisklabel_Click;

        var idLabel = new TextBlock
        {
            Text = "ID:",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(idLabel, 0);
        Grid.SetColumn(idBox, 1);
        Grid.SetColumn(disklabelBtn, 2);
        Grid.SetColumn(pathBox, 3);
        Grid.SetColumn(browseBtn, 4);
        Grid.SetColumn(removeBtn, 5);
        grid.Children.Add(idLabel);
        grid.Children.Add(idBox);
        grid.Children.Add(disklabelBtn);
        grid.Children.Add(pathBox);
        grid.Children.Add(browseBtn);
        grid.Children.Add(removeBtn);

        row.RowPanel = grid;
        row.PathBox = pathBox;
        row.IdBox = idBox;
        row.BrowseBtn = browseBtn;
        row.RemoveBtn = removeBtn;
        row.DisklabelBtn = disklabelBtn;

        _diskRows.Add(row);
        ScsiDiskListPanel.Children.Add(grid);

        return row;
    }

    private void RemoveDiskRow(DiskRowInfo row)
    {
        ScsiDiskListPanel.Children.Remove(row.RowPanel);
        _diskRows.Remove(row);
        RefreshScsiIdOptions();
    }

    private void RefreshScsiIdOptions()
    {
        // Collect IDs used by each disk row
        var usedByDisk = new Dictionary<DiskRowInfo, int>();
        foreach (var row in _diskRows)
        {
            if (row.IdBox.SelectedItem is ComboBoxItem sel && int.TryParse(sel.Content?.ToString(), out int id))
                usedByDisk[row] = id;
            else if (row.IdBox.Tag is int tagId)
                usedByDisk[row] = tagId;
            else
                usedByDisk[row] = -1;
        }

        int cdromId = (ScsiCdromIdBox.SelectedItem is ComboBoxItem cdromSel
                       && int.TryParse(cdromSel.Content?.ToString(), out int parsedCdromId))
                      ? parsedCdromId
                      : _desiredCdromId;

        // Update each disk row's ComboBox
        foreach (var row in _diskRows)
        {
            int currentId = usedByDisk[row];
            var otherIds = new HashSet<int>();
            foreach (var kv in usedByDisk)
            {
                if (kv.Key != row && kv.Value >= 0)
                    otherIds.Add(kv.Value);
            }
            if (cdromId >= 0)
                otherIds.Add(cdromId);

            row.IdBox.SelectionChanged -= IdBox_SelectionChanged;
            row.IdBox.Items.Clear();
            int selectIndex = 0;
            for (int i = 0; i <= 6; i++)
            {
                if (i != currentId && otherIds.Contains(i))
                    continue;
                var item = new ComboBoxItem { Content = i.ToString() };
                row.IdBox.Items.Add(item);
                if (i == currentId)
                    selectIndex = row.IdBox.Items.Count - 1;
            }
            row.IdBox.SelectedIndex = selectIndex;
            row.IdBox.Tag = currentId; // update tag
            row.IdBox.SelectionChanged += IdBox_SelectionChanged;
        }

        // Update CD-ROM ComboBox: exclude disk IDs
        var diskIds = new HashSet<int>(usedByDisk.Values.Where(v => v >= 0));
        int currentCdromId = cdromId;
        ScsiCdromIdBox.SelectionChanged -= CdromIdBox_SelectionChanged;
        ScsiCdromIdBox.Items.Clear();
        int cdromSelectIndex = 0;
        for (int i = 0; i <= 6; i++)
        {
            if (i != currentCdromId && diskIds.Contains(i))
                continue;
            var item = new ComboBoxItem { Content = i.ToString() };
            ScsiCdromIdBox.Items.Add(item);
            if (i == currentCdromId)
                cdromSelectIndex = ScsiCdromIdBox.Items.Count - 1;
        }
        ScsiCdromIdBox.SelectedIndex = cdromSelectIndex;
        _desiredCdromId = currentCdromId;
        ScsiCdromIdBox.SelectionChanged += CdromIdBox_SelectionChanged;

        // Update Add button: disable if all IDs taken
        int totalUsed = diskIds.Count + (currentCdromId >= 0 ? 1 : 0);
        AddScsiDiskBtn.IsEnabled = totalUsed < 7;
    }

    private void IdBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshScsiIdOptions();
    }

    private void CdromIdBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshScsiIdOptions();
    }

    private int GetSelectedScsiId(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem sel && int.TryParse(sel.Content?.ToString(), out int id))
            return id;
        return 0;
    }

    // ========================================================================
    // Visibility and board type
    // ========================================================================

    private void UpdateMvme147Visibility()
    {
        bool isMvme = GetSelectedItemText(BoardTypeBox) == "MVME147";
        Mvme147Panel.Visibility = isMvme ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTargetOSVisibility()
    {
        bool isLinux = GetSelectedItemText(TargetOSBox) == "Linux";
        NetBsdPanel.Visibility = isLinux ? Visibility.Collapsed : Visibility.Visible;
        LinuxPanel.Visibility = isLinux ? Visibility.Visible : Visibility.Collapsed;

        // Disklabel buttons are only useful for NetBSD disk images
        foreach (var row in _diskRows)
        {
            row.DisklabelBtn.IsEnabled = !isLinux;
            row.DisklabelBtn.Opacity = isLinux ? 0.35 : 1.0;
        }
    }

    private void TargetOS_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NetBsdPanel != null)
            UpdateTargetOSVisibility();
    }

    private void UpdateNatGatewayEnabled()
    {
        bool isNat = GetSelectedItemText(NetworkModeBox).Contains("NAT");
        NatGatewayIpBox.IsEnabled = isNat;
        NatGatewayMacBox.IsEnabled = isNat;
        NatGatewayIpBox.Opacity = isNat ? 1.0 : 0.35;
        NatGatewayMacBox.Opacity = isNat ? 1.0 : 0.35;
    }

    private void BoardType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Mvme147Panel != null)
            UpdateMvme147Visibility();
    }

    private void NetworkMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NatGatewayIpBox != null)
            UpdateNatGatewayEnabled();
    }

    // ========================================================================
    // OK / Save
    // ========================================================================

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        // Board type
        Config.BoardType = GetSelectedItemText(BoardTypeBox);
        Config.Mvme147RomPath = Mvme147RomBox.Text;

        // SCSI Disks
        Config.Mvme147ScsiDisks.Clear();
        foreach (var row in _diskRows)
        {
            string path = row.PathBox.Text;
            int id = GetSelectedScsiId(row.IdBox);
            if (!string.IsNullOrEmpty(path))
                Config.Mvme147ScsiDisks.Add(new ScsiDiskConfig { Path = path, ScsiId = id });
        }

        Config.Mvme147ScsiCdromPath = ScsiCdromPathBox.Text;
        Config.Mvme147ScsiCdromId = GetSelectedScsiId(ScsiCdromIdBox);
        Config.Mvme147BootPartition = BootPartitionBox.SelectedIndex;
        Config.TargetOS = GetSelectedItemText(TargetOSBox);
        Config.LinuxCommandLine = LinuxCommandLineBox.Text;
        Config.NetworkMode = GetSelectedItemText(NetworkModeBox);

        // Validate and save gateway IP/MAC (fallback to default on invalid input)
        var parsedIp = SlirpNetworkHandler.ParseIpAddress(NatGatewayIpBox.Text);
        Config.NatGatewayIp = $"{parsedIp[0]}.{parsedIp[1]}.{parsedIp[2]}.{parsedIp[3]}";
        var parsedMac = SlirpNetworkHandler.ParseMacAddress(NatGatewayMacBox.Text);
        Config.NatGatewayMac = $"{parsedMac[0]:x2}:{parsedMac[1]:x2}:{parsedMac[2]:x2}:{parsedMac[3]:x2}:{parsedMac[4]:x2}:{parsedMac[5]:x2}";

        // Memory size
        if (int.TryParse(MemSizeBox.Text, out int memMB))
            Config.MemorySize = Math.Clamp(memMB, 4, 4096) * 1024 * 1024;

        Config.ConsoleEnabled = ConsoleEnabledBox.IsChecked == true;
        if (uint.TryParse(ConsoleAddrBox.Text, NumberStyles.HexNumber, null, out uint conAddr))
            Config.ConsoleBaseAddress = conAddr;

        Config.HddEnabled = HddEnabledBox.IsChecked == true;
        if (uint.TryParse(HddAddrBox.Text, NumberStyles.HexNumber, null, out uint hddAddr))
            Config.HddBaseAddress = hddAddr;

        Config.HddImagePath = HddPathBox.Text;
        if (int.TryParse(ConsoleScrollbackBox.Text, out int scrollback))
            Config.ConsoleScrollbackLines = Math.Clamp(scrollback, 0, 100000);
        if (int.TryParse(ConsoleColumnsBox.Text, out int cols))
            Config.ConsoleColumns = Math.Clamp(cols, 80, 320);
        if (int.TryParse(ConsoleRowsBox.Text, out int rows))
            Config.ConsoleRows = Math.Clamp(rows, 24, 80);
        Config.FontFamily = FontFamilyBox.Text;
        if (double.TryParse(FontSizeBox.Text, out double fontSize))
            Config.FontSize = fontSize;
        Config.JitEnabled = JitEnabledBox.IsChecked == true;
        if (int.TryParse(JitMinBlockLengthBox.Text, out int minBlock))
            Config.JitMinBlockLength = Math.Clamp(minBlock, 1, 64);
        if (int.TryParse(JitCompileThresholdBox.Text, out int threshold))
            Config.JitCompileThreshold = Math.Clamp(threshold, 1, 255);

        // Framebuffer
        Config.FramebufferEnabled = FramebufferEnabledBox.IsChecked == true;
        {
            int[][] presets = [[320,240],[640,480],[800,600],[1024,768],[1280,720],[1280,1024],[1920,1080]];
            int idx = FbResolutionBox.SelectedIndex;
            if (idx >= 0 && idx < presets.Length)
            {
                Config.FramebufferWidth = presets[idx][0];
                Config.FramebufferHeight = presets[idx][1];
            }
        }
        if (FbBppBox.SelectedItem is ComboBoxItem bppItem &&
            int.TryParse(bppItem.Content?.ToString(), out int bpp))
            Config.FramebufferBpp = bpp;

        // Validate: framebuffer enabled requires enough RAM for kernel (min 32MB usable)
        if (Config.FramebufferEnabled)
        {
            const uint minKernelRam = 32u * 1024 * 1024; // 32MB
            uint vramBase = Config.ComputeVramBase();
            if (vramBase < minKernelRam)
            {
                uint vramSize = (uint)(Config.FramebufferWidth * Config.FramebufferHeight * Config.FramebufferBpp / 8);
                int requiredMB = (int)((minKernelRam + vramSize + 0xFFFFF) / (1024 * 1024));
                Config.MemorySize = requiredMB * 1024 * 1024;
                MemSizeBox.Text = requiredMB.ToString();
            }
        }

        DialogResult = true;
    }

    // ========================================================================
    // Button handlers
    // ========================================================================

    private void AddScsiDisk_Click(object sender, RoutedEventArgs e)
    {
        // Find the first free SCSI ID
        var usedIds = new HashSet<int>();
        foreach (var row in _diskRows)
            usedIds.Add(GetSelectedScsiId(row.IdBox));
        usedIds.Add(GetSelectedScsiId(ScsiCdromIdBox));

        int freeId = 0;
        for (int i = 0; i <= 6; i++)
        {
            if (!usedIds.Contains(i)) { freeId = i; break; }
        }
        AddDiskRow("", freeId);
        RefreshScsiIdOptions();
    }

    private void RemoveScsiDisk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DiskRowInfo row)
            RemoveDiskRow(row);
    }

    private void WriteDisklabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DiskRowInfo row) return;
        var filePath = row.PathBox.Text;
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return;

        var result = MessageBox.Show(
            Strings.Settings_DisklabelConfirm,
            Strings.Settings_WriteDisklabel,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        try
        {
            ScsiDisk.WriteNetBsdDisklabel(filePath);
        }
        catch
        {
            // ignore write errors
        }
    }

    private void BrowseRom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ROM Image (*.bin;*.rom)|*.bin;*.rom|All files (*.*)|*.*",
            Title = Strings.Settings_SelectRomImage
        };
        if (dlg.ShowDialog() == true)
        {
            Mvme147RomBox.Text = dlg.FileName;
        }
    }

    private void BrowseHdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "HDD Image (*.img;*.hdd)|*.img;*.hdd|All files (*.*)|*.*",
            Title = Strings.Settings_SelectHddImage
        };
        if (dlg.ShowDialog() == true)
        {
            HddPathBox.Text = dlg.FileName;
        }
    }

    private void BrowseScsiDisk_Click(object sender, RoutedEventArgs e)
    {
        DiskRowInfo? targetRow = null;
        if (sender is Button btn && btn.Tag is DiskRowInfo row)
            targetRow = row;
        if (targetRow == null) return;

        var dlg = new OpenFileDialog
        {
            Filter = "Disk Image (*.img)|*.img|All files (*.*)|*.*",
            Title = Strings.Settings_SelectScsiDisk
        };
        if (dlg.ShowDialog() == true)
        {
            targetRow.PathBox.Text = dlg.FileName;
        }
    }

    private void BrowseScsiCdrom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ISO Image (*.iso)|*.iso|All files (*.*)|*.*",
            Title = Strings.Settings_SelectCdRomIso
        };
        if (dlg.ShowDialog() == true)
        {
            ScsiCdromPathBox.Text = dlg.FileName;
        }
    }

    private void CreateScsiImage_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewScsiImageSizeBox.Text, out int sizeMB) || sizeMB <= 0)
        {
            MessageBox.Show(Strings.Msg_InvalidSize, Strings.Msg_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        sizeMB = Math.Clamp(sizeMB, 100, 2097152);

        // Unmount currently mounted disks so the file is not locked
        _unmountScsiDisks?.Invoke();

        var dlg = new SaveFileDialog
        {
            Filter = "Disk Image (*.img)|*.img|All files (*.*)|*.*",
            Title = Strings.Settings_CreateScsiDisk
        };
        if (dlg.ShowDialog() == true)
        {
            long sizeBytes = (long)sizeMB * 1024 * 1024;
            using (var fs = new System.IO.FileStream(dlg.FileName, System.IO.FileMode.Create))
            {
                fs.SetLength(sizeBytes);
            }

            bool isNetBsd = GetSelectedItemText(TargetOSBox) != "Linux";
            if (isNetBsd)
                ScsiDisk.WriteNetBsdDisklabel(dlg.FileName);

            // Add a new row with the created image, or set the last empty row
            var emptyRow = _diskRows.FirstOrDefault(r => string.IsNullOrEmpty(r.PathBox.Text));
            if (emptyRow != null)
            {
                emptyRow.PathBox.Text = dlg.FileName;
            }
            else
            {
                var newRow = AddDiskRow(dlg.FileName, 0);
                RefreshScsiIdOptions();
            }

            string typeLabel = isNetBsd ? "NetBSD disklabel" : "empty";
            MessageBox.Show(string.Format(Strings.Settings_CreatedScsiDiskFormat, sizeMB, typeLabel, dlg.FileName),
                          Strings.Msg_Success, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CreateImage_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewImageSizeBox.Text, out int sizeMB) || sizeMB <= 0)
        {
            MessageBox.Show(Strings.Msg_InvalidSize, Strings.Msg_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        sizeMB = Math.Clamp(sizeMB, 100, 2097152);

        var dlg = new SaveFileDialog
        {
            Filter = "HDD Image (*.img)|*.img|All files (*.*)|*.*",
            Title = Strings.Settings_CreateHddImage
        };
        if (dlg.ShowDialog() == true)
        {
            HddDevice.CreateImage(dlg.FileName, (long)sizeMB * 1024 * 1024);
            HddPathBox.Text = dlg.FileName;
            MessageBox.Show(string.Format(Strings.Settings_CreatedHddImageFormat, sizeMB, dlg.FileName), Strings.Msg_Success,
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
