using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Em68030.Config;
using Em68030.IO;
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
    }

    private readonly List<DiskRowInfo> _diskRows = new();
    private int _desiredCdromId = 3;

    public SettingsWindow(EmulatorConfig config)
    {
        InitializeComponent();
        Config = config;
        LoadSettings();
    }

    private void LoadSettings()
    {
        // Board type
        BoardTypeBox.SelectedIndex = Config.BoardType == "MVME147" ? 1 : 0;
        Mvme147RomBox.Text = Config.Mvme147RomPath;

        // SCSI Disks
        foreach (var disk in Config.Mvme147ScsiDisks)
            AddDiskRow(disk.Path, disk.ScsiId);
        if (_diskRows.Count == 0)
            AddDiskRow("", 0);

        ScsiCdromPathBox.Text = Config.Mvme147ScsiCdromPath;
        _desiredCdromId = Math.Clamp(Config.Mvme147ScsiCdromId, 0, 6);
        NetworkModeBox.SelectedIndex = Config.NetworkMode == "NAT" ? 1 : 0;
        UpdateMvme147Visibility();
        RefreshScsiIdOptions();

        MemSizeBox.Text = (Config.MemorySize / (1024 * 1024)).ToString();
        ConsoleEnabledBox.IsChecked = Config.ConsoleEnabled;
        ConsoleAddrBox.Text = Config.ConsoleBaseAddress.ToString("X8");
        HddEnabledBox.IsChecked = Config.HddEnabled;
        HddAddrBox.Text = Config.HddBaseAddress.ToString("X8");
        HddPathBox.Text = Config.HddImagePath;
        ConsoleScrollbackBox.Text = Config.ConsoleScrollbackLines.ToString();
        FontFamilyBox.Text = Config.FontFamily;
        FontSizeBox.Text = Config.FontSize.ToString();
        JitEnabledBox.IsChecked = Config.JitEnabled;
        JitMinBlockLengthBox.Text = Config.JitMinBlockLength.ToString();
        JitCompileThresholdBox.Text = Config.JitCompileThreshold.ToString();
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(idLabel, 0);
        Grid.SetColumn(idBox, 1);
        Grid.SetColumn(pathBox, 2);
        Grid.SetColumn(browseBtn, 3);
        Grid.SetColumn(removeBtn, 4);
        grid.Children.Add(idLabel);
        grid.Children.Add(idBox);
        grid.Children.Add(pathBox);
        grid.Children.Add(browseBtn);
        grid.Children.Add(removeBtn);

        row.RowPanel = grid;
        row.PathBox = pathBox;
        row.IdBox = idBox;
        row.BrowseBtn = browseBtn;
        row.RemoveBtn = removeBtn;

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
        bool isMvme = BoardTypeBox.SelectedIndex == 1;
        Mvme147Panel.Visibility = isMvme ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BoardType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Mvme147Panel != null)
            UpdateMvme147Visibility();
    }

    // ========================================================================
    // OK / Save
    // ========================================================================

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        // Board type
        Config.BoardType = BoardTypeBox.SelectedIndex == 1 ? "MVME147" : "Generic";
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
        Config.NetworkMode = NetworkModeBox.SelectedIndex == 1 ? "NAT" : "Virtual";

        // Memory size
        if (int.TryParse(MemSizeBox.Text, out int memMB))
            Config.MemorySize = memMB * 1024 * 1024;

        Config.ConsoleEnabled = ConsoleEnabledBox.IsChecked == true;
        if (uint.TryParse(ConsoleAddrBox.Text, NumberStyles.HexNumber, null, out uint conAddr))
            Config.ConsoleBaseAddress = conAddr;

        Config.HddEnabled = HddEnabledBox.IsChecked == true;
        if (uint.TryParse(HddAddrBox.Text, NumberStyles.HexNumber, null, out uint hddAddr))
            Config.HddBaseAddress = hddAddr;

        Config.HddImagePath = HddPathBox.Text;
        if (int.TryParse(ConsoleScrollbackBox.Text, out int scrollback) && scrollback >= 0)
            Config.ConsoleScrollbackLines = Math.Min(scrollback, 100000);
        Config.FontFamily = FontFamilyBox.Text;
        if (double.TryParse(FontSizeBox.Text, out double fontSize))
            Config.FontSize = fontSize;
        Config.JitEnabled = JitEnabledBox.IsChecked == true;
        if (int.TryParse(JitMinBlockLengthBox.Text, out int minBlock))
            Config.JitMinBlockLength = Math.Clamp(minBlock, 1, 64);
        if (int.TryParse(JitCompileThresholdBox.Text, out int threshold))
            Config.JitCompileThreshold = Math.Clamp(threshold, 1, 255);

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

    private void BrowseRom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ROM Image (*.bin;*.rom)|*.bin;*.rom|All files (*.*)|*.*",
            Title = "Select MVME147 ROM Image"
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
            Title = "Select HDD Image File"
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
            Title = "Select SCSI Disk Image"
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
            Title = "Select SCSI CD-ROM ISO Image"
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
            MessageBox.Show("Invalid size.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Disk Image (*.img)|*.img|All files (*.*)|*.*",
            Title = "Create SCSI Disk Image"
        };
        if (dlg.ShowDialog() == true)
        {
            long sizeBytes = (long)sizeMB * 1024 * 1024;
            using (var fs = new System.IO.FileStream(dlg.FileName, System.IO.FileMode.Create))
            {
                fs.SetLength(sizeBytes);
            }
            // Write a valid NetBSD disklabel with partition 'a' defined
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

            MessageBox.Show($"Created {sizeMB}MB SCSI disk image with NetBSD disklabel: {dlg.FileName}",
                          "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CreateImage_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewImageSizeBox.Text, out int sizeMB) || sizeMB <= 0)
        {
            MessageBox.Show("Invalid size.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "HDD Image (*.img)|*.img|All files (*.*)|*.*",
            Title = "Create HDD Image File"
        };
        if (dlg.ShowDialog() == true)
        {
            HddDevice.CreateImage(dlg.FileName, (long)sizeMB * 1024 * 1024);
            HddPathBox.Text = dlg.FileName;
            MessageBox.Show($"Created {sizeMB}MB image: {dlg.FileName}", "Success",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
