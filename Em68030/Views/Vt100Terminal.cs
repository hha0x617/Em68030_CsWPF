namespace Em68030.Views;

/// <summary>
/// Minimal VT100/ANSI terminal emulator with character cell screen buffer.
/// Supports cursor movement, screen/line clearing, scrolling regions,
/// and line-drawing characters — sufficient for curses-based applications
/// like NetBSD sysinst.
/// </summary>
public class Vt100Terminal
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private char[,] _screen;
    private int _cursorRow;
    private int _cursorCol;
    private bool _dirty = true;

    // Scrollback buffer: circular ring buffer of lines that scrolled off the top.
    // Using a fixed array + head/count avoids O(N) RemoveAt(0) on every scroll.
    private string[] _scrollback;
    private int _scrollbackHead;  // Index of oldest line
    private int _scrollbackCount; // Number of lines stored
    private int _maxScrollback;

    // Parser state machine
    private enum State { Normal, Esc, Csi, EscParen }
    private State _state = State.Normal;
    private readonly List<int> _csiParams = new();
    private int _currentParam;
    private bool _hasCurrentParam;
    private bool _csiQuestion; // CSI ? prefix

    // Scroll region (0-based, inclusive)
    private int _scrollTop;
    private int _scrollBottom;

    // Saved cursor position (DECSC / DECRC)
    private int _savedRow;
    private int _savedCol;

    // DEC Cursor Key Mode (DECCKM): when true, arrow keys send ESC O x instead of ESC [ x
    public bool ApplicationCursorKeys { get; private set; }

    // Alternate character set (line drawing)
    private bool _alternateCharset;

    // VT100 line drawing characters mapped from ASCII
    private static readonly Dictionary<char, char> LineDrawingMap = new()
    {
        ['j'] = '\u2518', // ┘
        ['k'] = '\u2510', // ┐
        ['l'] = '\u250C', // ┌
        ['m'] = '\u2514', // └
        ['n'] = '\u253C', // ┼
        ['q'] = '\u2500', // ─
        ['t'] = '\u251C', // ├
        ['u'] = '\u2524', // ┤
        ['v'] = '\u2534', // ┴
        ['w'] = '\u252C', // ┬
        ['x'] = '\u2502', // │
        ['a'] = '\u2592', // ▒
        ['f'] = '\u00B0', // °
        ['g'] = '\u00B1', // ±
        ['~'] = '\u00B7', // ·
        ['y'] = '\u2264', // ≤
        ['z'] = '\u2265', // ≥
    };

    public Vt100Terminal(int cols = 80, int rows = 25, int maxScrollback = 2000)
    {
        Cols = cols;
        Rows = rows;
        _maxScrollback = Math.Clamp(maxScrollback, 0, 100000);
        _scrollback = new string[_maxScrollback > 0 ? _maxScrollback : 1];
        _screen = new char[rows, cols];
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        ClearScreen();
    }

    public bool IsDirty => _dirty;

    public void ClearDirty() => _dirty = false;

    public void SetDirty() => _dirty = true;

    /// <summary>
    /// Resize the scrollback ring buffer, preserving the most recent lines.
    /// </summary>
    public void ResizeScrollback(int newMax)
    {
        newMax = Math.Clamp(newMax, 0, 100000);
        if (newMax == _maxScrollback) return;

        var newBuf = new string[newMax > 0 ? newMax : 1];
        if (newMax > 0 && _scrollbackCount > 0)
        {
            // Copy the most recent lines into the new buffer
            int copyCount = Math.Min(_scrollbackCount, newMax);
            int srcStart = (_scrollbackHead + _scrollbackCount - copyCount) % _maxScrollback;
            for (int i = 0; i < copyCount; i++)
                newBuf[i] = _scrollback[(srcStart + i) % _maxScrollback];
            _scrollbackHead = 0;
            _scrollbackCount = copyCount;
        }
        else
        {
            _scrollbackHead = 0;
            _scrollbackCount = 0;
        }

        _scrollback = newBuf;
        _maxScrollback = newMax;
        _dirty = true;
    }

    /// <summary>
    /// Resize the terminal screen to new dimensions, preserving existing content.
    /// Rows that overflow the top are saved to scrollback.
    /// </summary>
    public void Resize(int newCols, int newRows)
    {
        if (newCols == Cols && newRows == Rows) return;

        var newScreen = new char[newRows, newCols];
        for (int r = 0; r < newRows; r++)
            for (int c = 0; c < newCols; c++)
                newScreen[r, c] = ' ';

        // If shrinking rows, save overflow lines to scrollback
        int overflow = Rows - newRows;
        if (overflow > 0 && _maxScrollback > 0)
        {
            int linesToSave = Math.Min(overflow, Rows);
            for (int r = 0; r < linesToSave; r++)
            {
                var line = new char[Cols];
                for (int c = 0; c < Cols; c++)
                    line[c] = _screen[r, c];
                int writeIdx = (_scrollbackHead + _scrollbackCount) % _maxScrollback;
                _scrollback[writeIdx] = new string(line).TrimEnd();
                if (_scrollbackCount < _maxScrollback)
                    _scrollbackCount++;
                else
                    _scrollbackHead = (_scrollbackHead + 1) % _maxScrollback;
            }
        }

        // Copy existing content (shifted if rows shrunk)
        int srcStartRow = overflow > 0 ? overflow : 0;
        int copyRows = Math.Min(Rows - Math.Max(overflow, 0), newRows);
        int copyCols = Math.Min(Cols, newCols);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newScreen[r, c] = _screen[srcStartRow + r, c];

        _screen = newScreen;
        Cols = newCols;
        Rows = newRows;

        // Clamp cursor
        _cursorRow = Math.Clamp(_cursorRow - Math.Max(overflow, 0), 0, newRows - 1);
        _cursorCol = Math.Clamp(_cursorCol, 0, newCols - 1);

        // Reset scroll region
        _scrollTop = 0;
        _scrollBottom = newRows - 1;

        _dirty = true;
    }

    public void Write(char ch)
    {
        switch (_state)
        {
            case State.Normal:
                ProcessNormal(ch);
                break;
            case State.Esc:
                ProcessEsc(ch);
                break;
            case State.Csi:
                ProcessCsi(ch);
                break;
            case State.EscParen:
                // ESC ( or ESC ) — character set designation, consume one more char
                _state = State.Normal;
                break;
        }
    }

    public void Write(string s)
    {
        foreach (char ch in s)
            Write(ch);
    }

    /// <summary>
    /// Render the screen buffer as a single string with newline-separated rows.
    /// Trailing spaces on each row are preserved to maintain column alignment.
    /// </summary>
    public string Render()
    {
        var sb = new System.Text.StringBuilder(Rows * (Cols + 1));
        for (int r = 0; r < Rows; r++)
        {
            if (r > 0) sb.Append('\n');
            for (int c = 0; c < Cols; c++)
                sb.Append(_screen[r, c]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render the screen buffer with a block cursor at the current cursor position.
    /// </summary>
    public string RenderWithCursor()
    {
        var sb = new System.Text.StringBuilder(Rows * (Cols + 1));
        for (int r = 0; r < Rows; r++)
        {
            if (r > 0) sb.Append('\n');
            for (int c = 0; c < Cols; c++)
            {
                if (r == _cursorRow && c == _cursorCol)
                    sb.Append('\u2588'); // Full block cursor
                else
                    sb.Append(_screen[r, c]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render the full terminal output: scrollback history followed by the live screen.
    /// </summary>
    public string RenderFull()
    {
        var sb = new System.Text.StringBuilder(
            _scrollbackCount * (Cols + 1) + Rows * (Cols + 1));
        for (int i = 0; i < _scrollbackCount; i++)
        {
            sb.Append(_scrollback[(_scrollbackHead + i) % _maxScrollback]);
            sb.Append('\n');
        }
        for (int r = 0; r < Rows; r++)
        {
            if (r > 0) sb.Append('\n');
            for (int c = 0; c < Cols; c++)
                sb.Append(_screen[r, c]);
        }
        return sb.ToString();
    }

    public int ScrollbackLineCount => _scrollbackCount;

    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;

    // --- Normal character processing ---

    private void ProcessNormal(char ch)
    {
        switch (ch)
        {
            case '\x1B': // ESC
                _state = State.Esc;
                break;
            case '\r': // CR
                _cursorCol = 0;
                _dirty = true;
                break;
            case '\n': // LF — also do CR (the write bypass skips tty ONLCR processing)
                _cursorCol = 0;
                LineFeed();
                break;
            case '\b': // BS
                if (_cursorCol > 0) _cursorCol--;
                _dirty = true;
                break;
            case '\t': // TAB
                _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, Cols - 1);
                _dirty = true;
                break;
            case '\x07': // BEL
                break;
            case '\x0E': // SO — Switch to alternate character set
                _alternateCharset = true;
                break;
            case '\x0F': // SI — Switch to standard character set
                _alternateCharset = false;
                break;
            default:
                if (ch >= ' ')
                    PutChar(ch);
                break;
        }
    }

    private void PutChar(char ch)
    {
        // Map line-drawing characters when in alternate charset
        if (_alternateCharset && LineDrawingMap.TryGetValue(ch, out char mapped))
            ch = mapped;

        if (_cursorCol >= Cols)
        {
            // Auto-wrap
            _cursorCol = 0;
            LineFeed();
        }

        _screen[_cursorRow, _cursorCol] = ch;
        _cursorCol++;
        _dirty = true;
    }

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
            ScrollUp(1);
        else if (_cursorRow < Rows - 1)
            _cursorRow++;
        _dirty = true;
    }

    // --- ESC sequence processing ---

    private void ProcessEsc(char ch)
    {
        switch (ch)
        {
            case '[': // CSI
                _state = State.Csi;
                _csiParams.Clear();
                _currentParam = 0;
                _hasCurrentParam = false;
                _csiQuestion = false;
                break;
            case '(' or ')': // Character set designation
                _state = State.EscParen;
                break;
            case '7': // DECSC — Save cursor
                _savedRow = _cursorRow;
                _savedCol = _cursorCol;
                _state = State.Normal;
                break;
            case '8': // DECRC — Restore cursor
                _cursorRow = Math.Clamp(_savedRow, 0, Rows - 1);
                _cursorCol = Math.Clamp(_savedCol, 0, Cols - 1);
                _dirty = true;
                _state = State.Normal;
                break;
            case 'M': // RI — Reverse Index (cursor up, scroll down if at top)
                if (_cursorRow == _scrollTop)
                    ScrollDown(1);
                else if (_cursorRow > 0)
                    _cursorRow--;
                _dirty = true;
                _state = State.Normal;
                break;
            case 'D': // IND — Index (cursor down, scroll up if at bottom)
                LineFeed();
                _state = State.Normal;
                break;
            case 'E': // NEL — Next Line
                _cursorCol = 0;
                LineFeed();
                _state = State.Normal;
                break;
            case '=' or '>': // Keypad modes — ignore
                _state = State.Normal;
                break;
            case 'c': // RIS — Full reset
                Reset();
                _state = State.Normal;
                break;
            default:
                // Unknown ESC sequence — ignore
                _state = State.Normal;
                break;
        }
    }

    // --- CSI sequence processing ---

    private void ProcessCsi(char ch)
    {
        if (ch == '?')
        {
            _csiQuestion = true;
            return;
        }

        if (ch >= '0' && ch <= '9')
        {
            _currentParam = _currentParam * 10 + (ch - '0');
            _hasCurrentParam = true;
            return;
        }

        if (ch == ';')
        {
            _csiParams.Add(_hasCurrentParam ? _currentParam : 0);
            _currentParam = 0;
            _hasCurrentParam = false;
            return;
        }

        // Final character — execute the CSI command
        if (_hasCurrentParam)
            _csiParams.Add(_currentParam);

        ExecuteCsi(ch);
        _state = State.Normal;
    }

    private int Param(int index, int defaultValue = 1)
    {
        if (index < _csiParams.Count && _csiParams[index] > 0)
            return _csiParams[index];
        return defaultValue;
    }

    private void ExecuteCsi(char cmd)
    {
        if (_csiQuestion)
        {
            // DEC private modes
            int mode = Param(0);
            if (cmd == 'h') // Set mode
            {
                if (mode == 1) ApplicationCursorKeys = true; // DECCKM
            }
            else if (cmd == 'l') // Reset mode
            {
                if (mode == 1) ApplicationCursorKeys = false; // DECCKM
            }
            // CSI ? 25 h/l = show/hide cursor (ignore)
            // CSI ? 1049 h/l = alternate screen buffer (ignore)
            return;
        }

        switch (cmd)
        {
            case 'A': // CUU — Cursor Up
                _cursorRow = Math.Max(_cursorRow - Param(0), 0);
                _dirty = true;
                break;

            case 'B': // CUD — Cursor Down
                _cursorRow = Math.Min(_cursorRow + Param(0), Rows - 1);
                _dirty = true;
                break;

            case 'C': // CUF — Cursor Forward (Right)
                _cursorCol = Math.Min(_cursorCol + Param(0), Cols - 1);
                _dirty = true;
                break;

            case 'D': // CUB — Cursor Backward (Left)
                _cursorCol = Math.Max(_cursorCol - Param(0), 0);
                _dirty = true;
                break;

            case 'H' or 'f': // CUP — Cursor Position (1-based)
                _cursorRow = Math.Clamp(Param(0) - 1, 0, Rows - 1);
                _cursorCol = Math.Clamp(Param(1, 1) - 1, 0, Cols - 1);
                _dirty = true;
                break;

            case 'G': // CHA — Cursor Horizontal Absolute (1-based)
                _cursorCol = Math.Clamp(Param(0) - 1, 0, Cols - 1);
                _dirty = true;
                break;

            case 'd': // VPA — Cursor Vertical Absolute (1-based)
                _cursorRow = Math.Clamp(Param(0) - 1, 0, Rows - 1);
                _dirty = true;
                break;

            case 'J': // ED — Erase in Display
                EraseInDisplay(Param(0, 0));
                break;

            case 'K': // EL — Erase in Line
                EraseInLine(Param(0, 0));
                break;

            case 'L': // IL — Insert Lines
                InsertLines(Param(0));
                break;

            case 'M': // DL — Delete Lines
                DeleteLines(Param(0));
                break;

            case 'P': // DCH — Delete Characters
                DeleteChars(Param(0));
                break;

            case '@': // ICH — Insert Blank Characters
                InsertChars(Param(0));
                break;

            case 'X': // ECH — Erase Characters
                EraseChars(Param(0));
                break;

            case 'r': // DECSTBM — Set Scrolling Region (1-based)
                _scrollTop = Math.Clamp(Param(0) - 1, 0, Rows - 1);
                _scrollBottom = Math.Clamp(Param(1, Rows) - 1, 0, Rows - 1);
                if (_scrollTop > _scrollBottom)
                    (_scrollTop, _scrollBottom) = (_scrollBottom, _scrollTop);
                _cursorRow = 0;
                _cursorCol = 0;
                _dirty = true;
                break;

            case 'S': // SU — Scroll Up
                ScrollUp(Param(0));
                break;

            case 'T': // SD — Scroll Down
                ScrollDown(Param(0));
                break;

            case 'm': // SGR — Select Graphic Rendition (colors/bold — ignore for now)
                break;

            case 'h' or 'l': // SM/RM — Set/Reset Mode (ignore)
                break;

            case 'n': // DSR — Device Status Report (ignore)
                break;

            case 's': // SCP — Save Cursor Position
                _savedRow = _cursorRow;
                _savedCol = _cursorCol;
                break;

            case 'u': // RCP — Restore Cursor Position
                _cursorRow = Math.Clamp(_savedRow, 0, Rows - 1);
                _cursorCol = Math.Clamp(_savedCol, 0, Cols - 1);
                _dirty = true;
                break;
        }
    }

    // --- Erase operations ---

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // Erase from cursor to end of screen
                ClearRange(_cursorRow, _cursorCol, Rows - 1, Cols - 1);
                break;
            case 1: // Erase from start of screen to cursor
                ClearRange(0, 0, _cursorRow, _cursorCol);
                break;
            case 2: // Erase entire screen
                ClearScreen();
                break;
        }
        _dirty = true;
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: // Erase from cursor to end of line
                for (int c = _cursorCol; c < Cols; c++)
                    _screen[_cursorRow, c] = ' ';
                break;
            case 1: // Erase from start of line to cursor
                for (int c = 0; c <= _cursorCol && c < Cols; c++)
                    _screen[_cursorRow, c] = ' ';
                break;
            case 2: // Erase entire line
                for (int c = 0; c < Cols; c++)
                    _screen[_cursorRow, c] = ' ';
                break;
        }
        _dirty = true;
    }

    private void EraseChars(int n)
    {
        for (int i = 0; i < n && _cursorCol + i < Cols; i++)
            _screen[_cursorRow, _cursorCol + i] = ' ';
        _dirty = true;
    }

    // --- Insert/Delete operations ---

    private void InsertLines(int n)
    {
        int bottom = _scrollBottom;
        for (int i = 0; i < n; i++)
        {
            // Shift lines down within scroll region
            for (int r = bottom; r > _cursorRow; r--)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r - 1, c];
            // Clear the inserted line
            for (int c = 0; c < Cols; c++)
                _screen[_cursorRow, c] = ' ';
        }
        _dirty = true;
    }

    private void DeleteLines(int n)
    {
        int bottom = _scrollBottom;
        for (int i = 0; i < n; i++)
        {
            // Shift lines up within scroll region
            for (int r = _cursorRow; r < bottom; r++)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r + 1, c];
            // Clear the bottom line
            for (int c = 0; c < Cols; c++)
                _screen[bottom, c] = ' ';
        }
        _dirty = true;
    }

    private void DeleteChars(int n)
    {
        for (int i = _cursorCol; i < Cols; i++)
        {
            int src = i + n;
            _screen[_cursorRow, i] = src < Cols ? _screen[_cursorRow, src] : ' ';
        }
        _dirty = true;
    }

    private void InsertChars(int n)
    {
        for (int i = Cols - 1; i >= _cursorCol + n; i--)
            _screen[_cursorRow, i] = _screen[_cursorRow, i - n];
        for (int i = 0; i < n && _cursorCol + i < Cols; i++)
            _screen[_cursorRow, _cursorCol + i] = ' ';
        _dirty = true;
    }

    // --- Scrolling ---

    private void ScrollUp(int n)
    {
        for (int i = 0; i < n; i++)
        {
            // Save the top line to scrollback ring buffer before it's overwritten
            if (_scrollTop == 0 && _maxScrollback > 0)
            {
                var line = new char[Cols];
                for (int c = 0; c < Cols; c++)
                    line[c] = _screen[_scrollTop, c];
                int writeIdx = (_scrollbackHead + _scrollbackCount) % _maxScrollback;
                _scrollback[writeIdx] = new string(line).TrimEnd();
                if (_scrollbackCount < _maxScrollback)
                    _scrollbackCount++;
                else
                    _scrollbackHead = (_scrollbackHead + 1) % _maxScrollback; // Overwrite oldest
            }

            for (int r = _scrollTop; r < _scrollBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r + 1, c];
            for (int c = 0; c < Cols; c++)
                _screen[_scrollBottom, c] = ' ';
        }
        _dirty = true;
    }

    private void ScrollDown(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollBottom; r > _scrollTop; r--)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r - 1, c];
            for (int c = 0; c < Cols; c++)
                _screen[_scrollTop, c] = ' ';
        }
        _dirty = true;
    }

    // --- Helpers ---

    private void ClearScreen()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _screen[r, c] = ' ';
        _dirty = true;
    }

    private void ClearRange(int r1, int c1, int r2, int c2)
    {
        for (int r = r1; r <= r2 && r < Rows; r++)
        {
            int startC = (r == r1) ? c1 : 0;
            int endC = (r == r2) ? c2 : Cols - 1;
            for (int c = startC; c <= endC && c < Cols; c++)
                _screen[r, c] = ' ';
        }
    }

    private void Reset()
    {
        ClearScreen();
        _cursorRow = 0;
        _cursorCol = 0;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _alternateCharset = false;
        _state = State.Normal;
        _dirty = true;
    }
}
