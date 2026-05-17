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

namespace Em68030.IO;

using Em68030.Core;

/// <summary>
/// Virtual keyboard/mouse input device for Em68030.
/// Mapped at $FFFE9000, 32 bytes.
///
/// The host UI pushes input events into a thread-safe FIFO.
/// The guest driver reads events from the front and acknowledges them.
///
/// Register map (offset from base $FFFE9000):
///   $00-$03  R    MAGIC         0x454D4B4D ("EMKM")
///   $04      R    EVENT_COUNT   Number of pending events (0-255, clamped)
///   $05      R    EVENT_TYPE    Front event type: 1=key, 2=mouse-move, 3=mouse-btn
///   $06-$07  R    EVENT_CODE    Scancode (Linux KEY_* code) or mouse button ID
///   $08-$09  R    EVENT_VALUE   Key: 0=release,1=press. Mouse-move: signed delta-X
///   $0A-$0B  R    EVENT_VALUE2  Mouse-move: signed delta-Y. Mouse-btn: 0=rel,1=press
///   $0C      W    EVENT_ACK     Write any value to dequeue front event
///   $10      RW   IRQ_ENABLE    Bit 0: enable interrupt on event available
///   $11      R    IRQ_STATUS    Bit 0: event available (EVENT_COUNT > 0)
///   $14-$15  R    MOUSE_ABS_X   Absolute mouse X
///   $16-$17  R    MOUSE_ABS_Y   Absolute mouse Y
///   $18      RW   MOUSE_MODE    0=relative, 1=absolute (default)
///   $1C-$1D  R    SCREEN_WIDTH  Framebuffer width
///   $1E-$1F  R    SCREEN_HEIGHT Framebuffer height
/// </summary>
public class InputDevice : IMemoryMappedDevice
{
    public const uint BaseAddress = 0xFFFE9000;
    public const uint DeviceSize = 32;
    public const uint Magic = 0x454D4B4D; // "EMKM"

    // Event types
    public const byte EventKey = 1;
    public const byte EventMouseMove = 2;
    public const byte EventMouseBtn = 3;

    private readonly object _lock = new();
    private readonly Queue<InputEvent> _fifo = new();

    private ushort _screenWidth;
    private ushort _screenHeight;
    private ushort _mouseAbsX;
    private ushort _mouseAbsY;
    private byte _mouseMode = 1; // 1 = absolute (default)
    private byte _irqEnable;

    private struct InputEvent
    {
        public byte Type;       // EventKey, EventMouseMove, EventMouseBtn
        public ushort Code;     // Scancode or button code
        public short Value;     // Key: 0/1, Mouse-move: delta-X, Mouse-btn: 0/1
        public short Value2;    // Mouse-move: delta-Y, otherwise 0
    }

    public InputDevice(ushort screenWidth, ushort screenHeight)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public byte ReadByte(uint address)
    {
        uint offset = address - BaseAddress;
        lock (_lock)
        {
            switch (offset)
            {
                // MAGIC ($00-$03)
                case 0x00: return unchecked((byte)(Magic >> 24));
                case 0x01: return unchecked((byte)(Magic >> 16));
                case 0x02: return unchecked((byte)(Magic >> 8));
                case 0x03: return unchecked((byte)Magic);

                // EVENT_COUNT ($04)
                case 0x04:
                {
                    int count = _fifo.Count;
                    return (byte)(count > 255 ? 255 : count);
                }

                // EVENT_TYPE ($05)
                case 0x05:
                    return _fifo.Count == 0 ? (byte)0 : _fifo.Peek().Type;

                // EVENT_CODE ($06-$07)
                case 0x06:
                    return _fifo.Count == 0 ? (byte)0 : (byte)(_fifo.Peek().Code >> 8);
                case 0x07:
                    return _fifo.Count == 0 ? (byte)0 : (byte)_fifo.Peek().Code;

                // EVENT_VALUE ($08-$09)
                case 0x08:
                    return _fifo.Count == 0 ? (byte)0 : (byte)((ushort)_fifo.Peek().Value >> 8);
                case 0x09:
                    return _fifo.Count == 0 ? (byte)0 : (byte)_fifo.Peek().Value;

                // EVENT_VALUE2 ($0A-$0B)
                case 0x0A:
                    return _fifo.Count == 0 ? (byte)0 : (byte)((ushort)_fifo.Peek().Value2 >> 8);
                case 0x0B:
                    return _fifo.Count == 0 ? (byte)0 : (byte)_fifo.Peek().Value2;

                // IRQ_ENABLE ($10)
                case 0x10:
                    return _irqEnable;

                // IRQ_STATUS ($11)
                case 0x11:
                    return _fifo.Count == 0 ? (byte)0 : (byte)1;

                // MOUSE_ABS_X ($14-$15)
                case 0x14: return (byte)(_mouseAbsX >> 8);
                case 0x15: return (byte)_mouseAbsX;

                // MOUSE_ABS_Y ($16-$17)
                case 0x16: return (byte)(_mouseAbsY >> 8);
                case 0x17: return (byte)_mouseAbsY;

                // MOUSE_MODE ($18)
                case 0x18:
                    return _mouseMode;

                // SCREEN_WIDTH ($1C-$1D)
                case 0x1C: return (byte)(_screenWidth >> 8);
                case 0x1D: return (byte)_screenWidth;

                // SCREEN_HEIGHT ($1E-$1F)
                case 0x1E: return (byte)(_screenHeight >> 8);
                case 0x1F: return (byte)_screenHeight;

                default: return 0;
            }
        }
    }

    public ushort ReadWord(uint address)
    {
        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address)
    {
        return ((uint)ReadWord(address) << 16) | ReadWord(address + 2);
    }

    public void WriteByte(uint address, byte value)
    {
        uint offset = address - BaseAddress;
        lock (_lock)
        {
            switch (offset)
            {
                // EVENT_ACK ($0C) - dequeue front event
                case 0x0C:
                    if (_fifo.Count > 0)
                        _fifo.Dequeue();
                    break;

                // IRQ_ENABLE ($10)
                case 0x10:
                    _irqEnable = (byte)(value & 1);
                    break;

                // MOUSE_MODE ($18)
                case 0x18:
                    _mouseMode = (byte)(value & 1);
                    break;
            }
        }
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)(value & 0xFF));
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)(value & 0xFFFF));
    }

    /// <summary>
    /// Push a key press/release event. code = Linux KEY_* code, value: 0=release, 1=press.
    /// </summary>
    public void PushKeyEvent(ushort code, byte value)
    {
        lock (_lock)
        {
            _fifo.Enqueue(new InputEvent { Type = EventKey, Code = code, Value = (short)value, Value2 = 0 });
        }
    }

    /// <summary>
    /// Push a relative mouse move event.
    /// </summary>
    public void PushMouseMoveEvent(short dx, short dy)
    {
        lock (_lock)
        {
            _fifo.Enqueue(new InputEvent { Type = EventMouseMove, Code = 0, Value = dx, Value2 = dy });
        }
    }

    /// <summary>
    /// Push an absolute mouse position update.
    /// </summary>
    public void PushMouseAbsEvent(ushort x, ushort y)
    {
        lock (_lock)
        {
            _mouseAbsX = x;
            _mouseAbsY = y;
            // Also push as event so the guest can detect position changes via polling
            _fifo.Enqueue(new InputEvent { Type = EventMouseMove, Code = 0, Value = (short)x, Value2 = (short)y });
        }
    }

    /// <summary>
    /// Push a mouse button press/release. button: Linux BTN_* code, value: 0=release, 1=press.
    /// </summary>
    public void PushMouseButtonEvent(ushort button, byte value)
    {
        lock (_lock)
        {
            _fifo.Enqueue(new InputEvent { Type = EventMouseBtn, Code = button, Value = (short)value, Value2 = 0 });
        }
    }

    public void SetScreenSize(ushort width, ushort height)
    {
        lock (_lock)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
    }

    /// <summary>Update absolute mouse position registers without pushing a FIFO event.</summary>
    public void SetMouseAbsPosition(ushort x, ushort y)
    {
        lock (_lock)
        {
            _mouseAbsX = x;
            _mouseAbsY = y;
        }
    }

    /// <summary>Push a string as a sequence of key press/release events (for paste).</summary>
    public void PushTextInput(string text)
    {
        const ushort KEY_LEFTSHIFT = 42;
        foreach (char ch in text)
        {
            if (ch == '\r') continue; // Skip CR in CRLF — LF alone produces KEY_ENTER
            var (keyCode, needShift) = KeyMapping.CharToScancode(ch);
            if (keyCode == 0) continue;

            if (needShift)
                PushKeyEvent(KEY_LEFTSHIFT, 1);
            PushKeyEvent(keyCode, 1);
            PushKeyEvent(keyCode, 0);
            if (needShift)
                PushKeyEvent(KEY_LEFTSHIFT, 0);
        }
    }
}
