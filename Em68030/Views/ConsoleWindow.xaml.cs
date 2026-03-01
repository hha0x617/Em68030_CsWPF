using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Em68030.Views;

public partial class ConsoleWindow : Window
{
    private readonly Queue<char> _charBuffer = new();
    private readonly Queue<string> _lineBuffer = new();
    private readonly object _lock = new();

    private readonly Vt100Terminal _terminal;
    private readonly DispatcherTimer _renderTimer;
    private bool _autoScroll = true;
    private bool _showScrollback; // When true, display full scrollback + screen

    // Thread-safe output queue: emulation thread enqueues, UI thread drains
    private readonly ConcurrentQueue<char> _outputQueue = new();

    // Inline input buffer for Generic mode line editing
    private readonly StringBuilder _inputLine = new();

    // Cursor blink state
    private int _blinkCounter;
    private bool _cursorVisible;

    /// <summary>
    /// Callback to feed characters directly to the SCC device (Z8530 RX FIFO).
    /// Used by MVME147 mode where the kernel polls the SCC hardware for input.
    /// </summary>
    public Action<byte>? OnCharInput;

    public ConsoleWindow(int scrollbackLines = 2000)
    {
        _terminal = new Vt100Terminal(80, 24, scrollbackLines);
        InitializeComponent();

        // Track user scroll position to determine auto-scroll behavior
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

        // Focus the output area for keyboard capture
        Loaded += (_, _) => OutputBox.Focus();
        Activated += (_, _) => OutputBox.Focus();
    }

    private void OnOutputScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Ignore scroll changes caused by content updates (new text added)
        if (e.ExtentHeightChange != 0) return;

        // User scrolled: auto-scroll if at (or near) the bottom
        _autoScroll = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2;
    }

    /// <summary>
    /// Resize the scrollback buffer. Must be called on the UI thread.
    /// </summary>
    public void SetScrollbackLines(int lines)
    {
        _terminal.ResizeScrollback(lines);
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
        // Drain output queue into terminal (produced by emulation thread)
        while (_outputQueue.TryDequeue(out char ch))
            _terminal.Write(ch);

        // Cursor blink: toggle every 5 ticks (500ms on, 500ms off)
        _blinkCounter++;
        bool newVisible = (_blinkCounter / 5) % 2 == 0;
        bool blinkChanged = newVisible != _cursorVisible;
        _cursorVisible = newVisible;

        if (!_terminal.IsDirty && !blinkChanged) return;
        _terminal.ClearDirty();

        if (_showScrollback)
        {
            OutputBox.Text = _terminal.RenderFull();
            if (_autoScroll)
                OutputBox.ScrollToEnd();
        }
        else
        {
            OutputBox.Text = _cursorVisible
                ? _terminal.RenderWithCursor()
                : _terminal.Render();
        }
    }

    private void ScrollbackToggle_Click(object sender, RoutedEventArgs e)
    {
        _showScrollback = !_showScrollback;
        ScrollbackButton.Content = _showScrollback ? "Live" : "Log";

        // Force re-render with the new mode
        _terminal.SetDirty();
        RenderScreen();

        if (_showScrollback && _autoScroll)
            OutputBox.ScrollToEnd();
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
                Key.Back     => "\b",
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
    /// Send raw bytes to the SCC RX FIFO (MVME147 hardware input path).
    /// No local echo — the kernel or application will echo via output.
    /// </summary>
    private void SendRawBytes(string data)
    {
        if (OnCharInput == null) return;
        foreach (char c in data)
            OnCharInput((byte)c);
    }
}
