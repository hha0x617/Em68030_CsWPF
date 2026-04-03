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

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Em68030.Properties;

namespace Em68030.Views;

public partial class ConsoleWindow : Window
{
    // Win32 interop for focus-follows-mouse (bypass foreground lock)
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly Queue<char> _charBuffer = new();
    private readonly Queue<string> _lineBuffer = new();
    private readonly object _lock = new();

    private readonly Vt100Terminal _terminal;
    private readonly DispatcherTimer _renderTimer;
    private bool _autoScroll = true;
    private bool _suppressScrollEvent;
    private bool _rendering;

    // Thread-safe output queue: emulation thread enqueues, UI thread drains
    private readonly ConcurrentQueue<char> _outputQueue = new();

    // Inline input buffer for Generic mode line editing
    private readonly StringBuilder _inputLine = new();

    // Cursor blink state
    private int _blinkCounter;
    private bool _cursorVisible;

    // Character cell measurement for resize
    private double _charWidth;
    private double _charHeight;
    private bool _charMeasured;

    /// <summary>
    /// Callback to feed characters directly to the SCC device (Z8530 RX FIFO).
    /// Used by MVME147 mode where the kernel polls the SCC hardware for input.
    /// </summary>
    public Action<byte>? OnCharInput;

    public ConsoleWindow(int cols = 80, int rows = 24, int scrollbackLines = 2000)
    {
        _terminal = new Vt100Terminal(cols, rows, scrollbackLines);
        InitializeComponent();
        var iconUri = new Uri("pack://application:,,,/Assets/Em68030.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

        // Detect user scroll to toggle auto-scroll state
        OutputBox.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnOutputScrollChanged));

        // Render at low priority so emulation gets CPU time first.
        // 100ms interval (10fps) is sufficient for terminal display.
        _renderTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _renderTimer.Tick += (_, _) => RenderScreen();
        _renderTimer.Start();

        // Intercept Copy to trim trailing whitespace from terminal lines
        OutputBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy, OnCopyCommand,
            (_, args) => { args.CanExecute = OutputBox.SelectionLength > 0; args.Handled = true; }));

        // Intercept context-menu Paste so it routes through SCC/input instead of TextBox
        OutputBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste, OnPasteCommand,
            (_, args) => { args.CanExecute = true; args.Handled = true; }));

        // Explicitly enable Select All in context menu
        OutputBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.SelectAll,
            (_, _) => OutputBox.SelectAll(),
            (_, args) => { args.CanExecute = true; args.Handled = true; }));

        // Focus the output area for keyboard capture
        Loaded += (_, _) =>
        {
            OutputBox.Focus();
            MeasureCharCell();
            SetMinWindowSize();
            UpdateTitle();

            // Resize window to fit the configured terminal dimensions
            if (_charMeasured && _charWidth > 0 && _charHeight > 0)
            {
                double scrollBarW = SystemParameters.VerticalScrollBarWidth;
                double contentWidth = _terminal.Cols * _charWidth + 8 + scrollBarW;
                double contentHeight = _terminal.Rows * _charHeight + 8;
                double chromeWidth = ActualWidth - OutputBox.ActualWidth;
                double chromeHeight = ActualHeight - OutputBox.ActualHeight;
                Width = contentWidth + chromeWidth;
                Height = contentHeight + chromeHeight;
            }
        };
        Activated += (_, _) => OutputBox.Focus();
        SizeChanged += OnWindowSizeChanged;

        // Show/hide focus indicator bar based on keyboard focus
        OutputBox.GotKeyboardFocus += (_, _) => FocusIndicator.Visibility = Visibility.Visible;
        OutputBox.LostKeyboardFocus += (_, _) => FocusIndicator.Visibility = Visibility.Collapsed;

        // Search box key handling
        SearchBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                FindNext();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                CloseSearch();
                args.Handled = true;
            }
            else if (args.Key == Key.F3)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) FindPrev(); else FindNext();
                args.Handled = true;
            }
        };

        // Focus-follows-mouse: activate window and focus OutputBox when mouse enters
        OutputBox.MouseEnter += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd)
                return;
            // Bypass Windows foreground lock restriction by simulating Alt key
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            Activate();
            OutputBox.Focus();
        };
    }
    private void OnOutputScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Ignore scroll events caused by programmatic text updates
        if (_suppressScrollEvent) return;

        // Threshold: one line height to account for TextWrapping layout changes
        double threshold = _charHeight > 0 ? _charHeight : 20;
        bool atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - threshold;

        if (_autoScroll)
        {
            // Only leave auto-scroll when user explicitly scrolls up
            if (e.VerticalChange < 0 && !atBottom)
                _autoScroll = false;
        }
        else
        {
            // Return to auto-scroll when user scrolls to bottom
            if (atBottom)
            {
                _autoScroll = true;
                _terminal.SetDirty();
            }
        }
    }

    /// <summary>
    /// Resize the scrollback buffer. Must be called on the UI thread.
    /// </summary>
    public void SetScrollbackLines(int lines)
    {
        _terminal.ResizeScrollback(lines);
    }

    /// <summary>
    /// Resize the terminal and adjust window size to match.
    /// </summary>
    public void SetTerminalSize(int cols, int rows)
    {
        cols = Math.Max(cols, 80);
        rows = Math.Max(rows, 24);
        if (cols == _terminal.Cols && rows == _terminal.Rows) return;

        _terminal.Resize(cols, rows);
        UpdateTitle();

        // Adjust window size to fit the new terminal dimensions
        if (_charMeasured && _charWidth > 0 && _charHeight > 0)
        {
            double scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
            double contentWidth = cols * _charWidth + 8 + scrollBarWidth; // Padding + scrollbar
            double contentHeight = rows * _charHeight + 8; // Padding="4" top+bottom
            double chromeWidth = ActualWidth - OutputBox.ActualWidth;
            double chromeHeight = ActualHeight - OutputBox.ActualHeight;
            Width = contentWidth + chromeWidth;
            Height = contentHeight + chromeHeight;
        }
    }

    private void MeasureCharCell()
    {
        var ft = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(OutputBox.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            OutputBox.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _charWidth = ft.Width;
        _charHeight = ft.Height;
        _charMeasured = true;
    }

    private void SetMinWindowSize()
    {
        if (!_charMeasured) return;
        double chromeWidth = ActualWidth - OutputBox.ActualWidth;
        double chromeHeight = ActualHeight - OutputBox.ActualHeight;
        MinWidth = 80 * _charWidth + 8 + SystemParameters.VerticalScrollBarWidth + chromeWidth;
        MinHeight = 24 * _charHeight + 8 + chromeHeight; // Padding="4" top+bottom
    }

    private void UpdateTitle()
    {
        Title = string.Format(Strings.Window_ConsoleFormat, _terminal.Cols, _terminal.Rows);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_charMeasured || _charWidth <= 0 || _charHeight <= 0) return;

        // Subtract padding (4+4=8) and scrollbar width from text area
        double scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
        double availableWidth = OutputBox.ActualWidth - 8 - scrollBarWidth; // padding + scrollbar
        double availableHeight = OutputBox.ActualHeight - 8;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        int newCols = Math.Max(80, (int)(availableWidth / _charWidth));
        int newRows = Math.Max(24, (int)(availableHeight / _charHeight));

        if (newCols != _terminal.Cols || newRows != _terminal.Rows)
        {
            _terminal.Resize(newCols, newRows);
            UpdateTitle();
        }
    }

    /// <summary>
    /// Thread-safe: can be called from the emulation background thread.
    /// Characters are queued and processed on the UI thread by the render timer.
    /// </summary>
    public void AppendChar(char ch)
    {
        _outputQueue.Enqueue(ch);
    }

    /// <summary>
    /// Thread-safe: can be called from the emulation background thread.
    /// </summary>
    public void AppendString(string s)
    {
        foreach (char ch in s)
            _outputQueue.Enqueue(ch);
    }

    private void RenderScreen()
    {
        // Drain output queue into terminal (produced by emulation thread).
        // Always drain, even if we skip the UI update, so the terminal
        // state stays current.
        while (_outputQueue.TryDequeue(out char ch))
            _terminal.Write(ch);

        // Cursor blink: toggle every 5 ticks (500ms on, 500ms off)
        _blinkCounter++;
        bool newVisible = (_blinkCounter / 5) % 2 == 0;
        bool blinkChanged = newVisible != _cursorVisible;
        _cursorVisible = newVisible;

        if (!_terminal.IsDirty && !blinkChanged) return;

        // Suspend text updates while the user has a text selection active,
        // so mouse-drag selections aren't reset every 100ms render tick.
        if (OutputBox.SelectionLength > 0) return;

        // While the user is browsing scrollback history, do NOT update the
        // TextBox text — replacing text resets the scroll position, and all
        // attempts to restore it fight with the TextBox's internal caret-scroll.
        // The terminal still processes output (above), so when the user scrolls
        // back to the bottom, the latest content will be shown.
        if (!_autoScroll)
        {
            _terminal.ClearDirty();
            return;
        }

        // Skip if the previous render's UI update hasn't completed yet.
        // The terminal stays dirty so the next timer tick will retry.
        if (_rendering) return;

        _terminal.ClearDirty();
        _rendering = true;
        _suppressScrollEvent = true;

        OutputBox.Text = _cursorVisible
            ? _terminal.RenderFullWithCursor()
            : _terminal.RenderFull();

        OutputBox.CaretIndex = OutputBox.Text.Length;
        OutputBox.ScrollToEnd();

        // Clear flags at Input priority (5) — after Render (7) and Loaded (6)
        // process all deferred scroll events from the text/caret update.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _suppressScrollEvent = false;
            _rendering = false;
        });
    }

    public char ReadChar()
    {
        lock (_lock)
        {
            if (_charBuffer.Count > 0)
                return _charBuffer.Dequeue();
        }
        return '\0';
    }

    public string ReadString()
    {
        lock (_lock)
        {
            if (_lineBuffer.Count > 0)
                return _lineBuffer.Dequeue();
        }
        return "";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Resolve the actual key — IME may wrap arrow/function keys as ImeProcessed
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;


        // Ctrl+Shift+F: open search bar (works in all modes)
        if (key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        // Search mode: intercept F3/Shift+F3 and Escape
        if (_searchMode)
        {
            if (key == Key.F3)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) FindPrev(); else FindNext();
                e.Handled = true;
                return;
            }
            if (key == Key.Escape)
            {
                CloseSearch();
                e.Handled = true;
                return;
            }
        }

        // Don't process keys when SearchBox has focus (let the user type)
        if (SearchBox.IsFocused)
            return;

        if (OnCharInput != null)
        {
            // MVME147 mode: send VT100 sequences for special keys
            // Arrow keys use ESC O x in application mode (DECCKM), ESC [ x in normal mode
            string arrow = _terminal.ApplicationCursorKeys ? "\x1BO" : "\x1B[";
            string? seq = key switch
            {
                Key.Space    => " ",
                Key.Enter    => "\r",
                Key.Up       => arrow + "A",
                Key.Down     => arrow + "B",
                Key.Left     => arrow + "D",
                Key.Right    => arrow + "C",
                Key.Home     => "\x1B[H",
                Key.End      => "\x1B[F",
                Key.Back     => "\x7F",
                Key.Delete   => "\x1B[3~",
                Key.Tab      => "\t",
                Key.Escape   => "\x1B",
                Key.PageUp   => "\x1B[5~",
                Key.PageDown => "\x1B[6~",
                Key.Insert   => "\x1B[2~",
                Key.F1       => "\x1BOP",
                Key.F2       => "\x1BOQ",
                Key.F3       => "\x1BOR",
                Key.F4       => "\x1BOS",
                Key.F5       => "\x1B[15~",
                Key.F6       => "\x1B[17~",
                Key.F7       => "\x1B[18~",
                Key.F8       => "\x1B[19~",
                Key.F9       => "\x1B[20~",
                Key.F10      => "\x1B[21~",
                Key.F11      => "\x1B[23~",
                Key.F12      => "\x1B[24~",
                _ => null
            };

            if (seq != null)
            {
                SendRawBytes(seq);
                e.Handled = true;
                return;
            }

            // Ctrl+C: copy selected text (CommandBinding handles trimming), or send 0x03 (ETX) if no selection
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && key == Key.C)
            {
                if (OutputBox.SelectionLength == 0)
                {
                    OnCharInput(0x03);
                    e.Handled = true;
                }
                // Selection active: let CommandBinding (OnCopyCommand) handle it
                return;
            }

            // Ctrl+V: paste clipboard text into SCC
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && key == Key.V)
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    text = text.Replace("\r\n", "\r").Replace('\n', '\r');
                    SendRawBytes(text);
                }
                e.Handled = true;
                return;
            }

            // Ctrl+A..Z -> control codes 0x01..0x1A
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                key >= Key.A && key <= Key.Z)
            {
                byte code = (byte)(key - Key.A + 1);
                OnCharInput(code);
                e.Handled = true;
                return;
            }
        }
        else
        {
            // Ctrl+C: copy selected text (CommandBinding handles trimming)
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && key == Key.C)
            {
                // No selection: suppress (no action in Generic mode)
                // Selection: let CommandBinding (OnCopyCommand) handle it
                e.Handled = OutputBox.SelectionLength == 0;
                return;
            }

            // Ctrl+V: paste clipboard text into input buffer + local echo
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && key == Key.V)
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    text = text.Replace("\r\n", "\r").Replace('\n', '\r');
                    foreach (char c in text)
                    {
                        _inputLine.Append(c);
                        _terminal.Write(c);
                    }
                }
                e.Handled = true;
                return;
            }

            // Generic mode: handle special keys for inline input
            switch (key)
            {
                case Key.Enter:
                    SubmitGenericInput();
                    e.Handled = true;
                    return;
                case Key.Space:
                    _inputLine.Append(' ');
                    _terminal.Write(' ');
                    e.Handled = true;
                    return;
                case Key.Back:
                    if (_inputLine.Length > 0)
                    {
                        _inputLine.Remove(_inputLine.Length - 1, 1);
                        _terminal.Write("\b \b");
                    }
                    e.Handled = true;
                    return;
                // Suppress keys that would edit the TextBox
                case Key.Delete:
                case Key.Home:
                case Key.End:
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Insert:
                case Key.Tab:
                case Key.Escape:
                    e.Handled = true;
                    return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            base.OnPreviewTextInput(e);
            return;
        }

        // Don't intercept text input when SearchBox has focus
        if (SearchBox.IsFocused)
        {
            base.OnPreviewTextInput(e);
            return;
        }

        if (OnCharInput != null)
        {
            // MVME147 mode: send each character directly to SCC
            foreach (char c in e.Text)
                OnCharInput((byte)c);
        }
        else
        {
            // Generic mode: buffer and locally echo
            foreach (char c in e.Text)
            {
                _inputLine.Append(c);
                _terminal.Write(c);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Submit line-buffered input for Generic mode.
    /// </summary>
    private void SubmitGenericInput()
    {
        string text = _inputLine.ToString();
        _inputLine.Clear();

        lock (_lock)
        {
            _lineBuffer.Enqueue(text);
            foreach (char c in text)
                _charBuffer.Enqueue(c);
            _charBuffer.Enqueue('\n');
        }

        _terminal.Write('\n');
    }

    /// <summary>
    /// Copy selected text to clipboard with trailing whitespace trimmed per line.
    /// </summary>
    private void OnCopyCommand(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (OutputBox.SelectionLength == 0) return;

        try
        {
            string text = OutputBox.SelectedText;
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
                lines[i] = lines[i].TrimEnd(' ', '\t');
            string trimmed = string.Join("\r\n", lines);
            if (!string.IsNullOrWhiteSpace(trimmed))
                Clipboard.SetText(trimmed);
        }
        catch (Exception)
        {
            // Clipboard access can fail (e.g., locked by another process)
        }
    }

    /// <summary>
    /// Handle context-menu Paste (routes clipboard through SCC/input instead of TextBox).
    /// </summary>
    private void OnPasteCommand(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!Clipboard.ContainsText()) return;

        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;

        text = text.Replace("\r\n", "\r").Replace('\n', '\r');

        if (OnCharInput != null)
        {
            SendRawBytes(text);
        }
        else
        {
            foreach (char c in text)
            {
                _inputLine.Append(c);
                _terminal.Write(c);
            }
        }
    }

    /// <summary>
    /// Send raw bytes to the SCC RX FIFO (MVME147 hardware input path).
    /// No local echo — the kernel or application will echo via output.
    /// </summary>
    private void SendRawBytes(string data)
    {
        if (OnCharInput == null) return;
        foreach (char c in data)
            OnCharInput((byte)c);
    }

    // ========================================================================
    // Search
    // ========================================================================

    private bool _searchMode;
    private int _searchIndex = -1;
    private string _lastSearchText = "";

    private void SearchNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void SearchPrev_Click(object sender, RoutedEventArgs e) => FindPrev();
    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void OpenSearch()
    {
        _searchMode = true;
        SearchBar.Visibility = Visibility.Visible;

        // Pre-fill with selected text if any
        if (OutputBox.SelectionLength > 0)
            SearchBox.Text = OutputBox.SelectedText;

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        _searchMode = false;
        _searchIndex = -1;
        _lastSearchText = "";
        SearchBar.Visibility = Visibility.Collapsed;
        SearchStatus.Text = "";
        OutputBox.Focus();
    }

    private List<(int pos, int length)> CollectMatches(string text, string searchText,
        bool regexMode, bool caseSensitive)
    {
        var matches = new List<(int, int)>();
        if (string.IsNullOrEmpty(searchText) || string.IsNullOrEmpty(text)) return matches;

        if (regexMode)
        {
            try
            {
                var options = caseSensitive
                    ? System.Text.RegularExpressions.RegexOptions.None
                    : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                var regex = new System.Text.RegularExpressions.Regex(searchText, options);
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
                    matches.Add((m.Index, m.Length));
            }
            catch (ArgumentException) { /* invalid regex */ }
        }
        else
        {
            var comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            int pos = 0;
            while ((pos = text.IndexOf(searchText, pos, comparison)) >= 0)
            {
                matches.Add((pos, searchText.Length));
                pos += searchText.Length;
            }
        }
        return matches;
    }

    private void FindNext()
    {
        var searchText = SearchBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var text = OutputBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        bool regexMode = RegexToggle.IsChecked == true;

        if (searchText != _lastSearchText)
        {
            _searchIndex = -1;
            _lastSearchText = searchText;
        }

        bool caseSensitive = CaseSensitiveToggle.IsChecked == true;
        var matches = CollectMatches(text, searchText, regexMode, caseSensitive);
        if (matches.Count == 0)
        {
            _searchIndex = -1;
            SearchStatus.Text = regexMode ? "No match" : "Not found";
            return;
        }

        int nextIdx = -1;
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].pos > _searchIndex) { nextIdx = i; break; }
        }
        if (nextIdx < 0) nextIdx = 0; // wrap

        _searchIndex = matches[nextIdx].pos;
        HighlightMatch(matches[nextIdx].pos, matches[nextIdx].length,
                        nextIdx + 1, matches.Count);
    }

    private void FindPrev()
    {
        var searchText = SearchBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var text = OutputBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        bool regexMode = RegexToggle.IsChecked == true;

        if (searchText != _lastSearchText)
        {
            _searchIndex = text.Length;
            _lastSearchText = searchText;
        }

        bool caseSensitive = CaseSensitiveToggle.IsChecked == true;
        var matches = CollectMatches(text, searchText, regexMode, caseSensitive);
        if (matches.Count == 0)
        {
            _searchIndex = -1;
            SearchStatus.Text = regexMode ? "No match" : "Not found";
            return;
        }

        int prevIdx = -1;
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            if (matches[i].pos < _searchIndex) { prevIdx = i; break; }
        }
        if (prevIdx < 0) prevIdx = matches.Count - 1; // wrap

        _searchIndex = matches[prevIdx].pos;
        HighlightMatch(matches[prevIdx].pos, matches[prevIdx].length,
                        prevIdx + 1, matches.Count);
    }

    private void HighlightMatch(int pos, int length, int current, int total)
    {
        OutputBox.Focus();

        _suppressScrollEvent = true;
        OutputBox.Select(pos, length);
        _autoScroll = false;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _suppressScrollEvent = false);

        SearchStatus.Text = $"{current}/{total}";
    }
}
