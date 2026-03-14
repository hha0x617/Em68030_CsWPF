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

using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

public class InputDeviceTests
{
    private const uint Base = InputDevice.BaseAddress;
    private readonly InputDevice _device = new(640, 480);

    // ============================================================================
    // Magic register
    // ============================================================================

    [Fact]
    public void ReadMagic_ReturnsEMKM()
    {
        uint magic = _device.ReadLong(Base + 0x00);
        Assert.Equal(0x454D4B4Du, magic);
    }

    // ============================================================================
    // Initial state (empty FIFO)
    // ============================================================================

    [Fact]
    public void InitialEventCount_IsZero()
    {
        Assert.Equal(0, _device.ReadByte(Base + 0x04));
    }

    [Fact]
    public void InitialEventType_IsZero()
    {
        Assert.Equal(0, _device.ReadByte(Base + 0x05));
    }

    [Fact]
    public void InitialEventCode_IsZero()
    {
        Assert.Equal(0, _device.ReadWord(Base + 0x06));
    }

    [Fact]
    public void InitialIrqEnable_IsZero()
    {
        Assert.Equal(0, _device.ReadByte(Base + 0x10));
    }

    [Fact]
    public void InitialIrqStatus_IsZero()
    {
        Assert.Equal(0, _device.ReadByte(Base + 0x11));
    }

    [Fact]
    public void InitialMouseMode_IsAbsolute()
    {
        Assert.Equal(1, _device.ReadByte(Base + 0x18));
    }

    // ============================================================================
    // Screen size registers
    // ============================================================================

    [Fact]
    public void ReadScreenWidth_Returns640()
    {
        Assert.Equal(640, _device.ReadWord(Base + 0x1C));
    }

    [Fact]
    public void ReadScreenHeight_Returns480()
    {
        Assert.Equal(480, _device.ReadWord(Base + 0x1E));
    }

    [Fact]
    public void SetScreenSize_UpdatesRegisters()
    {
        _device.SetScreenSize(800, 600);
        Assert.Equal(800, _device.ReadWord(Base + 0x1C));
        Assert.Equal(600, _device.ReadWord(Base + 0x1E));
    }

    // ============================================================================
    // Key events
    // ============================================================================

    [Fact]
    public void PushKeyEvent_IncrementsEventCount()
    {
        _device.PushKeyEvent(30, 1); // KEY_A = 30, press
        Assert.Equal(1, _device.ReadByte(Base + 0x04));
    }

    [Fact]
    public void PushKeyEvent_SetsEventType()
    {
        _device.PushKeyEvent(30, 1);
        Assert.Equal(InputDevice.EventKey, _device.ReadByte(Base + 0x05));
    }

    [Fact]
    public void PushKeyEvent_SetsEventCode()
    {
        _device.PushKeyEvent(30, 1);
        Assert.Equal(30, _device.ReadWord(Base + 0x06));
    }

    [Fact]
    public void PushKeyEvent_Press_SetsValue1()
    {
        _device.PushKeyEvent(30, 1);
        Assert.Equal(1, _device.ReadWord(Base + 0x08));
    }

    [Fact]
    public void PushKeyEvent_Release_SetsValue0()
    {
        _device.PushKeyEvent(30, 0);
        Assert.Equal(0, _device.ReadWord(Base + 0x08));
    }

    [Fact]
    public void PushKeyEvent_Value2IsZero()
    {
        _device.PushKeyEvent(30, 1);
        Assert.Equal(0, _device.ReadWord(Base + 0x0A));
    }

    // ============================================================================
    // Event ACK (dequeue)
    // ============================================================================

    [Fact]
    public void EventAck_DequeueFrontEvent()
    {
        _device.PushKeyEvent(30, 1);
        _device.PushKeyEvent(31, 1);
        Assert.Equal(2, _device.ReadByte(Base + 0x04));

        _device.WriteByte(Base + 0x0C, 0xFF); // ACK
        Assert.Equal(1, _device.ReadByte(Base + 0x04));
        Assert.Equal(31, _device.ReadWord(Base + 0x06)); // second event is now front
    }

    [Fact]
    public void EventAck_EmptyFifo_DoesNotCrash()
    {
        _device.WriteByte(Base + 0x0C, 0xFF); // ACK on empty FIFO
        Assert.Equal(0, _device.ReadByte(Base + 0x04));
    }

    [Fact]
    public void EventAck_AllEvents_FifoBecomesEmpty()
    {
        _device.PushKeyEvent(30, 1);
        _device.PushKeyEvent(30, 0);
        _device.WriteByte(Base + 0x0C, 0); // ACK
        _device.WriteByte(Base + 0x0C, 0); // ACK
        Assert.Equal(0, _device.ReadByte(Base + 0x04));
        Assert.Equal(0, _device.ReadByte(Base + 0x05)); // type=0 when empty
    }

    // ============================================================================
    // Mouse move events
    // ============================================================================

    [Fact]
    public void PushMouseMoveEvent_SetsType()
    {
        _device.PushMouseMoveEvent(10, -5);
        Assert.Equal(InputDevice.EventMouseMove, _device.ReadByte(Base + 0x05));
    }

    [Fact]
    public void PushMouseMoveEvent_SetsDeltaX()
    {
        _device.PushMouseMoveEvent(10, -5);
        short dx = (short)_device.ReadWord(Base + 0x08);
        Assert.Equal(10, dx);
    }

    [Fact]
    public void PushMouseMoveEvent_SetsDeltaY()
    {
        _device.PushMouseMoveEvent(10, -5);
        short dy = (short)_device.ReadWord(Base + 0x0A);
        Assert.Equal(-5, dy);
    }

    [Fact]
    public void PushMouseMoveEvent_NegativeDelta()
    {
        _device.PushMouseMoveEvent(-100, -200);
        short dx = (short)_device.ReadWord(Base + 0x08);
        short dy = (short)_device.ReadWord(Base + 0x0A);
        Assert.Equal(-100, dx);
        Assert.Equal(-200, dy);
    }

    // ============================================================================
    // Mouse button events
    // ============================================================================

    [Fact]
    public void PushMouseButtonEvent_SetsType()
    {
        _device.PushMouseButtonEvent(0x110, 1); // BTN_LEFT
        Assert.Equal(InputDevice.EventMouseBtn, _device.ReadByte(Base + 0x05));
    }

    [Fact]
    public void PushMouseButtonEvent_SetsButtonCode()
    {
        _device.PushMouseButtonEvent(0x110, 1);
        Assert.Equal(0x110, _device.ReadWord(Base + 0x06));
    }

    [Fact]
    public void PushMouseButtonEvent_Press_SetsValue1()
    {
        _device.PushMouseButtonEvent(0x110, 1);
        Assert.Equal(1, _device.ReadWord(Base + 0x08));
    }

    [Fact]
    public void PushMouseButtonEvent_Release_SetsValue0()
    {
        _device.PushMouseButtonEvent(0x110, 0);
        Assert.Equal(0, _device.ReadWord(Base + 0x08));
    }

    // ============================================================================
    // Mouse absolute position
    // ============================================================================

    [Fact]
    public void PushMouseAbsEvent_UpdatesAbsRegisters()
    {
        _device.PushMouseAbsEvent(320, 240);
        Assert.Equal(320, _device.ReadWord(Base + 0x14));
        Assert.Equal(240, _device.ReadWord(Base + 0x16));
    }

    [Fact]
    public void PushMouseAbsEvent_AlsoPushesEvent()
    {
        _device.PushMouseAbsEvent(100, 200);
        Assert.Equal(1, _device.ReadByte(Base + 0x04));
        Assert.Equal(InputDevice.EventMouseMove, _device.ReadByte(Base + 0x05));
    }

    // ============================================================================
    // Mouse mode register
    // ============================================================================

    [Fact]
    public void WriteMouseMode_SetsRelative()
    {
        _device.WriteByte(Base + 0x18, 0);
        Assert.Equal(0, _device.ReadByte(Base + 0x18));
    }

    [Fact]
    public void WriteMouseMode_SetsAbsolute()
    {
        _device.WriteByte(Base + 0x18, 0);
        _device.WriteByte(Base + 0x18, 1);
        Assert.Equal(1, _device.ReadByte(Base + 0x18));
    }

    // ============================================================================
    // IRQ registers
    // ============================================================================

    [Fact]
    public void WriteIrqEnable_SetsFlag()
    {
        _device.WriteByte(Base + 0x10, 1);
        Assert.Equal(1, _device.ReadByte(Base + 0x10));
    }

    [Fact]
    public void IrqStatus_ReflectsEventCount()
    {
        Assert.Equal(0, _device.ReadByte(Base + 0x11));
        _device.PushKeyEvent(30, 1);
        Assert.Equal(1, _device.ReadByte(Base + 0x11));
        _device.WriteByte(Base + 0x0C, 0); // ACK
        Assert.Equal(0, _device.ReadByte(Base + 0x11));
    }

    // ============================================================================
    // Event count clamping
    // ============================================================================

    [Fact]
    public void EventCount_ClampsAt255()
    {
        for (int i = 0; i < 300; i++)
            _device.PushKeyEvent(30, 1);
        Assert.Equal(255, _device.ReadByte(Base + 0x04));
    }

    // ============================================================================
    // Multiple event types in sequence
    // ============================================================================

    [Fact]
    public void MixedEvents_FifoOrder()
    {
        _device.PushKeyEvent(30, 1);                 // event 0: key press
        _device.PushMouseMoveEvent(5, -3);           // event 1: mouse move
        _device.PushMouseButtonEvent(0x110, 1);      // event 2: mouse button

        // Event 0: key
        Assert.Equal(InputDevice.EventKey, _device.ReadByte(Base + 0x05));
        Assert.Equal(30, _device.ReadWord(Base + 0x06));
        _device.WriteByte(Base + 0x0C, 0); // ACK

        // Event 1: mouse move
        Assert.Equal(InputDevice.EventMouseMove, _device.ReadByte(Base + 0x05));
        short dx = (short)_device.ReadWord(Base + 0x08);
        short dy = (short)_device.ReadWord(Base + 0x0A);
        Assert.Equal(5, dx);
        Assert.Equal(-3, dy);
        _device.WriteByte(Base + 0x0C, 0); // ACK

        // Event 2: mouse button
        Assert.Equal(InputDevice.EventMouseBtn, _device.ReadByte(Base + 0x05));
        Assert.Equal(0x110, _device.ReadWord(Base + 0x06));
        _device.WriteByte(Base + 0x0C, 0); // ACK

        Assert.Equal(0, _device.ReadByte(Base + 0x04));
    }

    // ============================================================================
    // Unknown offset
    // ============================================================================

    [Fact]
    public void ReadUnknownOffset_ReturnsZero()
    {
        Assert.Equal(0, _device.ReadByte(Base + 0x09));
    }

    [Fact]
    public void WriteUnknownOffset_DoesNotCrash()
    {
        _device.WriteByte(Base + 0x1F, 0xFF);
    }
}
