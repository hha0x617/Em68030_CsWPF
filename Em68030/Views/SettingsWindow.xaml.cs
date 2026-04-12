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
using System.IO;
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
    // Snapshot of the config values currently reflected in live hardware.
    // Fields that differ between Config and _appliedConfig are marked as "pending"
    // (orange foreground) in LoadSettings().
    private readonly EmulatorConfig _appliedConfig;

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

    public SettingsWindow(EmulatorConfig config, EmulatorConfig applied, Action? unmountScsiDisks = null)
    {
        InitializeComponent();
        Config = config;
        _appliedConfig = applied;
        _unmountScsiDisks = unmountScsiDisks;
        LoadSettings();
    }

    private void LoadSettings()
    {
        // Theme
        SelectItemByText(ThemeBox, Config.Theme);

        // Board type
        SelectItemByText(BoardTypeBox, Config.BoardType);
        Mvme147RomBox.Text = Config.Mvme147RomPath;
        NetBsdKernelImagePathBox.Text = Config.NetBsdKernelImagePath;
        LinuxKernelImagePathBox.Text = Config.LinuxKernelImagePath;

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
        // Enumerate TAP adapters
        var tapAdapters = TapNetworkHandler.EnumerateAdapters();
        TapAdapterBox.Items.Clear();
        if (tapAdapters.Count == 0)
        {
            TapModeItem.IsEnabled = false;
            TapModeItem.Opacity = 0.35;
        }
        else
        {
            foreach (var adapter in tapAdapters)
            {
                string displayName = string.IsNullOrEmpty(adapter.Name)
                    ? adapter.Description
                    : adapter.Name;
                TapAdapterBox.Items.Add(new ComboBoxItem
                {
                    Content = displayName,
                    Tag = adapter.Guid
                });
            }
            // Select the saved adapter
            int tapIdx = 0;
            for (int i = 0; i < TapAdapterBox.Items.Count; i++)
            {
                if (TapAdapterBox.Items[i] is ComboBoxItem ci && (string)ci.Tag == Config.TapAdapterGuid)
                { tapIdx = i; break; }
            }
            TapAdapterBox.SelectedIndex = tapIdx;
        }

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
        CallStackModeBox.SelectedIndex = Config.CallStackMode == "A6Chain" ? 1 : 0;

        // Debug
        EnableTraceButtonBox.IsChecked = Config.EnableTraceButton;
        TraceFilePathText.Text = $"Trace file: {Path.Combine(Em68030.Config.EmulatorConfig.DataDirectory, "tracelog.txt")}";

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

        MarkPendingFields();
    }

    // ========================================================================
    // Pending-field visualization (UX proposal B)
    //
    // Compare each "cold" (non-hot-swappable) field between Config and
    // _appliedConfig. If they differ, mark the corresponding control's
    // Foreground orange so the user can see at a glance that the value is
    // saved but not yet applied to live hardware.
    // ========================================================================

    private static Brush GetThemeBrush(string key) =>
        (Brush)Application.Current.FindResource(key);

    private static void MarkLabel(TextBlock? label, bool isPending)
    {
        if (label != null)
            label.Foreground = isPending ? GetThemeBrush("ThemeWarningFg") : GetThemeBrush("ThemeForeground");
    }

    private static void MarkCheckBox(CheckBox? cb, bool isPending)
    {
        if (cb != null)
            cb.Foreground = isPending ? GetThemeBrush("ThemeWarningFg") : GetThemeBrush("ThemeForeground");
    }

    private static void MarkSectionLabel(TextBlock? header, bool isPending)
    {
        if (header != null)
            header.Foreground = isPending ? GetThemeBrush("ThemeWarningFg") : GetThemeBrush("ThemeAccent");
    }

    private void MarkPendingFields()
    {
        var c = Config;
        var a = _appliedConfig;
        bool anyPending = false;
        bool Diff(bool d) { if (d) anyPending = true; return d; }

        // General tab — mark the LABEL TextBlock next to each control. For
        // CheckBoxes (whose label is the Content), mark the CheckBox itself.
        MarkLabel(LblBoard,                Diff(c.BoardType != a.BoardType));
        MarkLabel(LblMemorySizeMB,         Diff(c.MemorySize != a.MemorySize));
        MarkCheckBox(ConsoleEnabledBox,    Diff(c.ConsoleEnabled != a.ConsoleEnabled));
        MarkLabel(LblConsoleBaseAddr,      Diff(c.ConsoleBaseAddress != a.ConsoleBaseAddress));
        MarkLabel(LblTerminalSize,         Diff(c.ConsoleColumns != a.ConsoleColumns
                                             || c.ConsoleRows != a.ConsoleRows));
        MarkCheckBox(HddEnabledBox,        Diff(c.HddEnabled != a.HddEnabled));
        MarkLabel(LblHddBaseAddr,          Diff(c.HddBaseAddress != a.HddBaseAddress));
        MarkLabel(LblImageFile,            Diff(c.HddImagePath != a.HddImagePath));

        // MVME147 tab
        MarkLabel(LblRomImage,             Diff(c.Mvme147RomPath != a.Mvme147RomPath));
        MarkLabel(LblOperatingSystem,      Diff(c.TargetOS != a.TargetOS));
        MarkLabel(LblNetBsdKernelImage,    Diff(c.NetBsdKernelImagePath != a.NetBsdKernelImagePath));
        MarkLabel(LblBootPartition,        Diff(c.Mvme147BootPartition != a.Mvme147BootPartition));
        MarkLabel(LblLinuxKernelImage,     Diff(c.LinuxKernelImagePath != a.LinuxKernelImagePath));
        MarkLabel(LblCommandLine,          Diff(c.LinuxCommandLine != a.LinuxCommandLine));

        // SCSI disks list — mark section header
        bool scsiDisksDiffer = c.Mvme147ScsiDisks.Count != a.Mvme147ScsiDisks.Count;
        if (!scsiDisksDiffer)
        {
            for (int i = 0; i < c.Mvme147ScsiDisks.Count; i++)
            {
                if (c.Mvme147ScsiDisks[i].Path != a.Mvme147ScsiDisks[i].Path ||
                    c.Mvme147ScsiDisks[i].ScsiId != a.Mvme147ScsiDisks[i].ScsiId)
                {
                    scsiDisksDiffer = true; break;
                }
            }
        }
        MarkSectionLabel(LblScsiDisks, Diff(scsiDisksDiffer));

        // CD-ROM SCSI ID (path is hot-swappable, not tracked here)
        MarkLabel(LblScsiCdromId,          Diff(c.Mvme147ScsiCdromId != a.Mvme147ScsiCdromId));

        // Network
        MarkLabel(LblNetworkMode,          Diff(c.NetworkMode != a.NetworkMode));
        MarkLabel(LblTapAdapter,           Diff(c.TapAdapterGuid != a.TapAdapterGuid));
        MarkLabel(LblGatewayIP,            Diff(c.NatGatewayIp != a.NatGatewayIp));
        MarkLabel(LblGatewayMAC,           Diff(c.NatGatewayMac != a.NatGatewayMac));

        // Framebuffer
        MarkCheckBox(FramebufferEnabledBox, Diff(c.FramebufferEnabled != a.FramebufferEnabled));
        MarkLabel(LblFbResolution,         Diff(c.FramebufferWidth != a.FramebufferWidth
                                             || c.FramebufferHeight != a.FramebufferHeight));
        MarkLabel(LblFbBpp,                Diff(c.FramebufferBpp != a.FramebufferBpp));
        bool fbAnyDiff = c.FramebufferEnabled != a.FramebufferEnabled
                      || c.FramebufferWidth != a.FramebufferWidth
                      || c.FramebufferHeight != a.FramebufferHeight
                      || c.FramebufferBpp != a.FramebufferBpp;
        MarkSectionLabel(LblFramebuffer, fbAnyDiff);

        // Legend visibility
        if (PendingLegendText != null)
            PendingLegendText.Visibility = anyPending ? Visibility.Visible : Visibility.Collapsed;
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
            Padding = new Thickness(4, 2, 4, 2),
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        };
        pathBox.SetResourceReference(TextBox.BackgroundProperty, "ThemeInputBg");
        pathBox.SetResourceReference(TextBox.ForegroundProperty, "ThemeForeground");
        pathBox.SetResourceReference(TextBox.BorderBrushProperty, "ThemeBorder");
        pathBox.SetResourceReference(TextBox.CaretBrushProperty, "ThemeHighlightFg");

        var browseBtn = new Button
        {
            Content = "...",
            Width = 30,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        browseBtn.SetResourceReference(Button.BackgroundProperty, "ThemeControlBg");
        browseBtn.SetResourceReference(Button.ForegroundProperty, "ThemeForeground");
        browseBtn.Tag = row;
        browseBtn.Click += BrowseScsiDisk_Click;

        var removeBtn = new Button
        {
            Content = "\u00D7", // multiplication sign
            Width = 24,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        removeBtn.SetResourceReference(Button.BackgroundProperty, "ThemeControlBg");
        removeBtn.SetResourceReference(Button.ForegroundProperty, "ThemeForeground");
        removeBtn.Tag = row;
        removeBtn.Click += RemoveScsiDisk_Click;

        var disklabelBtn = new Button
        {
            Content = Strings.Settings_Disklabel,
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            ToolTip = "Write a NetBSD disklabel to this disk image",
            VerticalAlignment = VerticalAlignment.Center
        };
        disklabelBtn.SetResourceReference(Button.BackgroundProperty, "ThemeControlBg");
        disklabelBtn.SetResourceReference(Button.ForegroundProperty, "ThemeForeground");
        disklabelBtn.Tag = row;
        disklabelBtn.Click += WriteDisklabel_Click;

        var idLabel = new TextBlock
        {
            Text = "ID:",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        idLabel.SetResourceReference(TextBlock.ForegroundProperty, "ThemeForeground");

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
        TabMvme147.IsEnabled = isMvme;
        TabMvme147.Opacity = isMvme ? 1.0 : 0.35;
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
        string mode = GetSelectedItemText(NetworkModeBox);
        bool isNat = mode.Contains("NAT");
        bool isTap = mode.Contains("TAP");

        NatGatewayIpBox.IsEnabled = isNat;
        NatGatewayMacBox.IsEnabled = isNat;
        NatGatewayIpBox.Opacity = isNat ? 1.0 : 0.35;
        NatGatewayMacBox.Opacity = isNat ? 1.0 : 0.35;

        TapAdapterGrid.Visibility = isTap ? Visibility.Visible : Visibility.Collapsed;
        TapAdapterBox.IsEnabled = isTap;
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
        // Theme
        string newTheme = GetSelectedItemText(ThemeBox);
        if (!string.IsNullOrEmpty(newTheme))
        {
            string oldTheme = Config.Theme;
            Config.Theme = newTheme;
            if (oldTheme != newTheme)
            {
                bool dark = ThemeHelper.ResolveDarkMode(newTheme);
                App.ApplyTheme(dark);
                ThemeHelper.SetAppMode(dark);
                foreach (Window w in Application.Current.Windows)
                    ThemeHelper.ApplyTitleBar(w, dark);
            }
        }

        // Board type
        Config.BoardType = GetSelectedItemText(BoardTypeBox);
        Config.Mvme147RomPath = Mvme147RomBox.Text;
        Config.NetBsdKernelImagePath = NetBsdKernelImagePathBox.Text;
        Config.LinuxKernelImagePath = LinuxKernelImagePathBox.Text;

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

        // Save TAP adapter GUID
        if (TapAdapterBox.SelectedItem is ComboBoxItem tapItem && tapItem.Tag is string tapGuid)
            Config.TapAdapterGuid = tapGuid;

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
        if (CallStackModeBox.SelectedItem is ComboBoxItem csmItem && csmItem.Tag is string csmTag)
            Config.CallStackMode = csmTag;

        // Debug
        Config.EnableTraceButton = EnableTraceButtonBox.IsChecked == true;

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
        UpdateTargetOSVisibility();
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

    private void BrowseNetBsdKernel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = Strings.Settings_SelectKernelImage + "|*.*",
            Title = Strings.Settings_SelectKernelImage
        };
        if (dlg.ShowDialog() == true)
            NetBsdKernelImagePathBox.Text = dlg.FileName;
    }

    private void BrowseLinuxKernel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = Strings.Settings_SelectKernelImage + "|*.*",
            Title = Strings.Settings_SelectKernelImage
        };
        if (dlg.ShowDialog() == true)
            LinuxKernelImagePathBox.Text = dlg.FileName;
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
