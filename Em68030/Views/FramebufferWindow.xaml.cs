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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Em68030.Core;
using Em68030.IO;
using InputDevice = Em68030.IO.InputDevice;

namespace Em68030.Views;

public partial class FramebufferWindow : Window
{
    private readonly Memory _memory;
    private readonly FramebufferDevice _device;
    private readonly InputDevice? _inputDevice;
    private readonly WriteableBitmap _bitmap;
    private readonly DispatcherTimer _renderTimer;
    private readonly byte[] _pixelBuffer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bpp;
    private readonly uint _vramOffset;
    private readonly int _vramBytes;

    public FramebufferWindow(Memory memory, FramebufferDevice device, InputDevice? inputDevice = null)
    {
        InitializeComponent();

        _memory = memory;
        _device = device;
        _inputDevice = inputDevice;
        _width = device.Width;
        _height = device.Height;
        _bpp = device.Bpp;
        _vramOffset = device.VramBase;
        _vramBytes = device.Stride * _height;

        Title = $"Em68030 Framebuffer - {_width}x{_height}x{_bpp}bpp";

        // Create bitmap — always render to Bgra32 for WPF compatibility
        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        _pixelBuffer = new byte[_width * _height * 4]; // BGRA32
        DisplayImage.Source = _bitmap;

        // Wire input event handlers.
        // Mouse: WPF has no MouseMiddleButtonDown event, so we use the generic
        // MouseDown/MouseUp and dispatch on e.ChangedButton. This also mirrors
        // the WinUI3 version's unified PointerPressed/PointerReleased structure.
        if (_inputDevice != null)
        {
            PreviewKeyDown += OnKeyDown;
            PreviewKeyUp += OnKeyUp;
            DisplayImage.MouseMove += OnMouseMove;
            DisplayImage.MouseDown += OnMouseDown;
            DisplayImage.MouseUp += OnMouseUp;
        }

        // 30fps render timer
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += (_, _) => RenderFrame();
        _renderTimer.Start();
    }

    private void RenderFrame()
    {
        if (!_device.Enabled) return;

        var ram = _memory.FastRam;
        if (ram == null) return;

        uint vramEnd = _vramOffset + (uint)_vramBytes;
        if (vramEnd > _memory.FastRamSize) return;

        switch (_bpp)
        {
            case 16:
                RenderFrame16bpp(ram);
                break;
            case 8:
                RenderFrame8bpp(ram);
                break;
            case 32:
                RenderFrame32bpp(ram);
                break;
            default:
                return;
        }

        _bitmap.Lock();
        _bitmap.WritePixels(
            new Int32Rect(0, 0, _width, _height),
            _pixelBuffer, _width * 4, 0);
        _bitmap.Unlock();
    }

    /// <summary>16bpp r5g6b5 big-endian → BGRA32</summary>
    private void RenderFrame16bpp(byte[] ram)
    {
        int srcOffset = (int)_vramOffset;
        int dstOffset = 0;

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                // Big-endian: high byte first
                int idx = srcOffset + (y * _width + x) * 2;
                ushort pixel = (ushort)((ram[idx] << 8) | ram[idx + 1]);

                // r5g6b5 → BGRA32
                int r = (pixel >> 11) & 0x1F;
                int g = (pixel >> 5) & 0x3F;
                int b = pixel & 0x1F;

                _pixelBuffer[dstOffset]     = (byte)((b << 3) | (b >> 2)); // B
                _pixelBuffer[dstOffset + 1] = (byte)((g << 2) | (g >> 4)); // G
                _pixelBuffer[dstOffset + 2] = (byte)((r << 3) | (r >> 2)); // R
                _pixelBuffer[dstOffset + 3] = 0xFF;                        // A
                dstOffset += 4;
            }
        }
    }

    /// <summary>8bpp palette index → BGRA32</summary>
    private void RenderFrame8bpp(byte[] ram)
    {
        int srcOffset = (int)_vramOffset;
        int dstOffset = 0;

        for (int i = 0; i < _width * _height; i++)
        {
            byte index = ram[srcOffset + i];
            var (r, g, b) = _device.GetPaletteEntry(index);

            _pixelBuffer[dstOffset]     = b; // B
            _pixelBuffer[dstOffset + 1] = g; // G
            _pixelBuffer[dstOffset + 2] = r; // R
            _pixelBuffer[dstOffset + 3] = 0xFF; // A
            dstOffset += 4;
        }
    }

    /// <summary>32bpp ARGB big-endian → BGRA32</summary>
    private void RenderFrame32bpp(byte[] ram)
    {
        int srcOffset = (int)_vramOffset;
        int dstOffset = 0;

        for (int i = 0; i < _width * _height; i++)
        {
            int idx = srcOffset + i * 4;
            // Big-endian ARGB: [A, R, G, B]
            byte a = ram[idx];
            byte r = ram[idx + 1];
            byte g = ram[idx + 2];
            byte b = ram[idx + 3];

            _pixelBuffer[dstOffset]     = b; // B
            _pixelBuffer[dstOffset + 1] = g; // G
            _pixelBuffer[dstOffset + 2] = r; // R
            _pixelBuffer[dstOffset + 3] = a; // A
            dstOffset += 4;
        }
    }

    // ========================================================================
    // Input event handlers
    // ========================================================================

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_inputDevice == null) return;

        // Ctrl+Shift+G: toggle mouse grab
        if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (_mouseGrabbed) UngrabMouse(); else GrabMouse();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+V: paste clipboard text as key events
        if (e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (Clipboard.ContainsText())
            {
                // Release Ctrl and Shift first — they were already sent to the guest as key presses,
                // so the guest would interpret pasted keys as modified keys without this.
                _inputDevice.PushKeyEvent(42, 0);  // KEY_LEFTSHIFT release
                _inputDevice.PushKeyEvent(29, 0);  // KEY_LEFTCTRL release
                _inputDevice.PushTextInput(Clipboard.GetText());
            }
            e.Handled = true;
            return;
        }

        var code = KeyMapping.WindowsVkToLinuxKey(KeyInterop.VirtualKeyFromKey(e.Key));
        if (code != 0)
        {
            _inputDevice.PushKeyEvent(code, 1);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_inputDevice == null) return;
        var code = KeyMapping.WindowsVkToLinuxKey(KeyInterop.VirtualKeyFromKey(e.Key));
        if (code != 0)
        {
            _inputDevice.PushKeyEvent(code, 0);
            e.Handled = true;
        }
    }

    private double _lastMouseX, _lastMouseY;
    private bool _lastMouseValid;
    private int _yDither = 1; // alternates +1/-1 to cancel gpm stale dy

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_inputDevice == null) return;
        var pos = e.GetPosition(DisplayImage);

        // Scale pointer position to framebuffer coordinates and update absolute registers.
        // The guest driver polls these registers directly for mouse position (tablet device).
        double actualW = DisplayImage.ActualWidth;
        double actualH = DisplayImage.ActualHeight;
        if (actualW <= 0 || actualH <= 0) return;

        var absX = (ushort)Math.Clamp(pos.X * _width / actualW, 0, _width - 1);
        var absY = (ushort)Math.Clamp(pos.Y * _height / actualH, 0, _height - 1);
        _inputDevice.SetMouseAbsPosition(absX, absY);

        // Relative deltas for gpm (relative mouse device).
        // gpm's evdev handler does NOT reset state->dy between events, and the
        // Linux kernel drops REL_Y=0 (EV_REL with value=0 is filtered).
        // Fix: when dy is 0 but dx is non-zero, send alternating +1/-1 in dy
        // to cancel out over 2 frames while preventing stale dy retention.
        if (_lastMouseValid)
        {
            var dx = (short)(pos.X - _lastMouseX);
            var dy = (short)(pos.Y - _lastMouseY);

            if (dx != 0 && dy == 0)
            {
                dy = (short)_yDither;
                _yDither = -_yDither;
            }

            if (dx != 0 || dy != 0)
                _inputDevice.PushMouseMoveEvent(dx, dy);
        }
        _lastMouseX = pos.X;
        _lastMouseY = pos.Y;
        _lastMouseValid = true;
    }

    // Unified mouse button handling. Uses e.ChangedButton to dispatch to the
    // appropriate Linux BTN_* code. Capture is held on any button press and
    // released only when all tracked buttons are up — this mirrors WinUI3's
    // PointerPressed/PointerReleased behavior and keeps events flowing even
    // when the user drags out of the DisplayImage bounds.
    //
    // Linux evdev codes: BTN_LEFT=0x110, BTN_RIGHT=0x111, BTN_MIDDLE=0x112
    private static ushort BtnCodeOf(MouseButton button) => button switch
    {
        MouseButton.Left => 0x110,
        MouseButton.Right => 0x111,
        MouseButton.Middle => 0x112,
        _ => 0 // XButton1/XButton2 not forwarded
    };

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_inputDevice == null) return;
        ushort btn = BtnCodeOf(e.ChangedButton);
        if (btn == 0) return;
        // Capture on first button pressed (idempotent if already captured).
        DisplayImage.CaptureMouse();
        _inputDevice.PushMouseButtonEvent(btn, 1);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_inputDevice == null) return;
        ushort btn = BtnCodeOf(e.ChangedButton);
        if (btn == 0) return;
        _inputDevice.PushMouseButtonEvent(btn, 0);
        // Release capture only when no tracked button remains pressed.
        if (e.LeftButton == MouseButtonState.Released &&
            e.RightButton == MouseButtonState.Released &&
            e.MiddleButton == MouseButtonState.Released)
        {
            DisplayImage.ReleaseMouseCapture();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UngrabMouse();
        _renderTimer.Stop();
        base.OnClosed(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        UngrabMouse();
        base.OnDeactivated(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        UpdateGrabRect();
        base.OnLocationChanged(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        UpdateGrabRect();
        base.OnRenderSizeChanged(sizeInfo);
    }

    // ========================================================================
    // Mouse grab (pointer confinement)
    // ========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr ptr); // null to release
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    private bool _mouseGrabbed;

    private void GrabMouse()
    {
        if (_mouseGrabbed) return;
        _mouseGrabbed = true;
        UpdateGrabRect();
        Cursor = Cursors.None;
        UpdateTitleGrabStatus();
    }

    private void UngrabMouse()
    {
        if (!_mouseGrabbed) return;
        _mouseGrabbed = false;
        ClipCursor(IntPtr.Zero);
        Cursor = Cursors.Arrow;
        UpdateTitleGrabStatus();
    }

    private void UpdateGrabRect()
    {
        if (!_mouseGrabbed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        GetClientRect(hwnd, out RECT clientRect);
        var topLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new POINT { X = clientRect.Right, Y = clientRect.Bottom };
        ClientToScreen(hwnd, ref topLeft);
        ClientToScreen(hwnd, ref bottomRight);

        var clipRect = new RECT
        {
            Left = topLeft.X, Top = topLeft.Y,
            Right = bottomRight.X, Bottom = bottomRight.Y
        };
        ClipCursor(ref clipRect);
    }

    private void UpdateTitleGrabStatus()
    {
        var baseTitle = $"Em68030 Framebuffer - {_width}x{_height}x{_bpp}bpp";
        if (_mouseGrabbed)
            baseTitle += " [Mouse Grabbed - Ctrl+Shift+G to release]";
        Title = baseTitle;
    }
}
