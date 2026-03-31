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

// ============================================================================
// Z8530Channel Tests
// ============================================================================

public class Z8530ChannelTests
{
    private readonly Z8530Channel _channel = new();
    private int _interruptChangedCount;
    private readonly List<byte> _transmittedChars = new();

    public Z8530ChannelTests()
    {
        _channel.InterruptStateChanged = () => _interruptChangedCount++;
        _channel.CharTransmitted += ch => _transmittedChars.Add(ch);
    }

    /// Helper: write to a WR register via the register pointer mechanism.
    private void WriteReg(byte reg, byte value)
    {
        if (reg < 8)
            _channel.WriteControl(reg);
        else
            _channel.WriteControl((byte)(0x08 | (reg & 0x07)));
        _channel.WriteControl(value);
    }

    /// Helper: read from a RR register via the register pointer mechanism.
    private byte ReadReg(byte reg)
    {
        if (reg < 8)
            _channel.WriteControl(reg);
        else
            _channel.WriteControl((byte)(0x08 | (reg & 0x07)));
        return _channel.ReadControl();
    }

    // --- RR0 Default State ---

    [Fact]
    public void RR0_DefaultState_TxEmptyAndDCDAndCTS()
    {
        byte rr0 = _channel.ReadControl();
        Assert.Equal(0, rr0 & 0x01); // RxAvail = 0
        Assert.NotEqual(0, rr0 & 0x04); // TxEmpty = 1
        Assert.NotEqual(0, rr0 & 0x08); // DCD = 1
        Assert.NotEqual(0, rr0 & 0x20); // CTS = 1
    }

    // --- Register Pointer ---

    [Fact]
    public void RegisterPointer_ResetsAfterRead()
    {
        _channel.WriteControl(1); // Set pointer to RR1
        byte rr1 = _channel.ReadControl();
        Assert.Equal(0x01, rr1); // RR1: All Sent

        byte rr0 = _channel.ReadControl(); // Pointer reset → RR0
        Assert.NotEqual(0, rr0 & 0x04); // TxEmpty
    }

    [Fact]
    public void RegisterPointer_ResetsAfterWrite()
    {
        WriteReg(1, 0x00);
        byte rr0 = _channel.ReadControl();
        Assert.NotEqual(0, rr0 & 0x04);
    }

    // --- Point High (WR0 cmd=1) ---

    [Fact]
    public void PointHigh_NotImplemented()
    {
        // Point High (cmd=1) is not implemented because Linux uses 16550 UART
        // instead of Z8530 SCC for console output. The command is ignored.
        _channel.WriteControl(0x08); // cmd=1, regSelect=0 → ignored
        _channel.WriteControl(0x41); // This writes to WR0, not WR8

        Assert.Empty(_transmittedChars); // No data transmitted
    }

    // --- TX Operations ---

    [Fact]
    public void WriteData_TransmitsChar()
    {
        _channel.WriteData(0x42);
        Assert.Single(_transmittedChars);
        Assert.Equal(0x42, _transmittedChars[0]);
    }

    [Fact]
    public void WriteData_SetsTxInProgress()
    {
        _channel.WriteData(0x42);
        Assert.True(_channel.TxInProgress);
        Assert.False(_channel.TxIntPending);
    }

    [Fact]
    public void WriteData_RR0_TxEmptyClears()
    {
        _channel.WriteData(0x42);
        byte rr0 = _channel.ReadControl();
        Assert.Equal(0, rr0 & 0x04); // TxEmpty = 0
    }

    [Fact]
    public void Tick_CompletesTx_SetsTxIntPending()
    {
        _channel.WriteData(0x42);
        Assert.True(_channel.TxInProgress);

        _channel.Tick(false);
        Assert.False(_channel.TxInProgress);
        Assert.True(_channel.TxIntPending);
    }

    [Fact]
    public void Tick_AfterTxComplete_RR0_TxEmptyAsserts()
    {
        _channel.WriteData(0x42);
        _channel.Tick(false);

        byte rr0 = _channel.ReadControl();
        Assert.NotEqual(0, rr0 & 0x04); // TxEmpty = 1
    }

    // --- TX Interrupt Enable ---

    [Fact]
    public void WR1_EnableTxInt_ImmediatelyAssertsTxIntPending()
    {
        WriteReg(1, 0x02);
        Assert.True(_channel.TxIntPending);
    }

    [Fact]
    public void WR0_Cmd5_ResetsTxIntPending()
    {
        WriteReg(1, 0x02); // TIE → TxIntPending = true
        Assert.True(_channel.TxIntPending);

        _channel.WriteControl(0x28); // cmd=5
        Assert.False(_channel.TxIntPending);
    }

    // --- TX Prod Mechanism ---

    [Fact]
    public void TxProd_ReassertsAfter16Ticks()
    {
        WriteReg(1, 0x02); // TIE → TxIntPending = true
        _channel.WriteControl(0x28); // Reset TxIntPending
        Assert.False(_channel.TxIntPending);

        for (int i = 0; i < 15; i++)
            _channel.Tick(false);
        Assert.False(_channel.TxIntPending);

        _channel.Tick(false); // 16th tick
        Assert.True(_channel.TxIntPending);
    }

    // --- RX via ReceiveChar (hardware RX FIFO) ---

    [Fact]
    public void ReceiveChar_SetsRxAvailInRR0()
    {
        _channel.ReceiveChar(0x61);
        byte rr0 = _channel.ReadControl();
        Assert.NotEqual(0, rr0 & 0x01);
    }

    [Fact]
    public void ReceiveChar_ReadData_ReturnsChar()
    {
        _channel.ReceiveChar(0x61);
        Assert.Equal(0x61, _channel.ReadData());
    }

    [Fact]
    public void ReceiveChar_SetsRxIntPending()
    {
        _channel.ReceiveChar(0x61);
        Assert.True(_channel.RxIntPending);
    }

    [Fact]
    public void ReadData_ClearsRxIntPending()
    {
        _channel.ReceiveChar(0x61);
        Assert.True(_channel.RxIntPending);

        _channel.ReadData();
        Assert.False(_channel.RxIntPending);
    }

    [Fact]
    public void ReceiveChar_MultipleChars_FIFO()
    {
        _channel.ReceiveChar(0x61);
        _channel.ReceiveChar(0x62);
        _channel.ReceiveChar(0x63);

        Assert.Equal(0x61, _channel.ReadData());
        Assert.Equal(0x62, _channel.ReadData());
        Assert.Equal(0x63, _channel.ReadData());
    }

    // --- RX via QueueInput (polled-mode user input) ---

    [Fact]
    public void QueueInput_RR0_RxAvail_But_NoRxIntPending()
    {
        _channel.QueueInput(0x61);

        byte rr0 = _channel.ReadControl();
        Assert.NotEqual(0, rr0 & 0x01); // RxAvail = 1
        Assert.False(_channel.RxIntPending); // No interrupt
    }

    [Fact]
    public void QueueInput_ReadData_ReturnsCharDirectly()
    {
        _channel.QueueInput(0x42);
        Assert.Equal(0x42, _channel.ReadData());
    }

    [Fact]
    public void QueueInput_PromotedWhenRxInterruptEnabled_CpuRunning()
    {
        WriteReg(1, 0x10); // RX interrupt enabled
        _channel.QueueInput(0x42);

        _channel.Tick(false); // CPU running — still promoted (cpuStopped not required)
        Assert.True(_channel.RxIntPending);

        Assert.Equal(0x42, _channel.ReadData());
    }

    [Fact]
    public void QueueInput_PromotedWhenRxInterruptEnabled_CpuStopped()
    {
        WriteReg(1, 0x10); // RX interrupt enabled
        _channel.QueueInput(0x42);

        _channel.Tick(true); // CPU stopped
        Assert.True(_channel.RxIntPending);

        Assert.Equal(0x42, _channel.ReadData());
    }

    [Fact]
    public void QueueInput_NotPromotedWhenRxInterruptDisabled()
    {
        // RX interrupt NOT enabled (WR1 = 0)
        _channel.QueueInput(0x42);

        _channel.Tick(false); // CPU running
        Assert.False(_channel.RxIntPending);

        // Still readable via polled-mode ReadData
        Assert.Equal(0x42, _channel.ReadData());
    }

    // --- HW FIFO takes priority over pending input ---

    [Fact]
    public void ReadData_HwFifoTakesPriority()
    {
        _channel.QueueInput(0x41);
        _channel.ReceiveChar(0x42);

        Assert.Equal(0x42, _channel.ReadData()); // HW FIFO first
        Assert.Equal(0x41, _channel.ReadData()); // Then user input
    }

    // --- ReadData returns 0 when empty ---

    [Fact]
    public void ReadData_EmptyReturnsZero()
    {
        Assert.Equal(0, _channel.ReadData());
    }

    // --- Force TIE when CPU stopped ---

    [Fact]
    public void Tick_CpuStopped_ForcesTIE()
    {
        WriteReg(1, 0x02); // Enable TIE → TxIntPending = true
        WriteReg(1, 0x00); // Disable TIE

        Assert.True(_channel.TxIntPending);
        Assert.False(_channel.TxInterruptEnabled);

        _channel.Tick(true); // CPU stopped
        Assert.True(_channel.TxInterruptEnabled); // TIE forced on
    }
}

// ============================================================================
// Z8530Device Tests
// ============================================================================

public class Z8530DeviceTests
{
    private readonly Z8530Device _device = new();
    private readonly List<bool> _interruptHistory = new();

    private const uint Base = 0xFFFE3000;

    public Z8530DeviceTests()
    {
        _device.InterruptOutput = active => _interruptHistory.Add(active);
    }

    private void WriteChannelReg(uint ctrlAddr, byte reg, byte value)
    {
        if (reg < 8)
            _device.WriteByte(ctrlAddr, reg);
        else
            _device.WriteByte(ctrlAddr, (byte)(0x08 | (reg & 0x07)));
        _device.WriteByte(ctrlAddr, value);
    }

    // --- Address Map ---

    [Fact]
    public void AddressMap_ChannelB_Data()
    {
        _device.WriteByte(Base + 1, 0x42); // Channel B Data
        Assert.True(_device.ChannelB.TxInProgress);
    }

    [Fact]
    public void AddressMap_ChannelA_Data()
    {
        _device.WriteByte(Base + 3, 0x42); // Channel A Data
        Assert.True(_device.ChannelA.TxInProgress);
    }

    [Fact]
    public void AddressMap_ChannelA_Data_ReadAfterReceive()
    {
        _device.ChannelA.ReceiveChar(0x61);
        Assert.Equal(0x61, _device.ReadByte(Base + 3));
    }

    [Fact]
    public void AddressMap_ChannelB_Data_ReadAfterReceive()
    {
        _device.ChannelB.ReceiveChar(0x62);
        Assert.Equal(0x62, _device.ReadByte(Base + 1));
    }

    // --- RR3 (Interrupt Pending, Channel A only) ---

    [Fact]
    public void RR3_ChA_TxIntPending()
    {
        WriteChannelReg(Base + 2, 1, 0x02); // TIE on Ch A

        _device.WriteByte(Base + 2, 3); // register pointer = 3
        byte rr3 = _device.ReadByte(Base + 2);
        Assert.NotEqual(0, rr3 & 0x10); // Ch A TX IP
    }

    [Fact]
    public void RR3_ChA_RxIntPending()
    {
        WriteChannelReg(Base + 2, 1, 0x10); // RX int enable on Ch A
        _device.ChannelA.ReceiveChar(0x61);

        _device.WriteByte(Base + 2, 3);
        byte rr3 = _device.ReadByte(Base + 2);
        Assert.NotEqual(0, rr3 & 0x20); // Ch A RX IP
    }

    [Fact]
    public void RR3_ChB_TxIntPending()
    {
        WriteChannelReg(Base + 0, 1, 0x02); // TIE on Ch B

        _device.WriteByte(Base + 2, 3); // Read RR3 via Ch A
        byte rr3 = _device.ReadByte(Base + 2);
        Assert.NotEqual(0, rr3 & 0x02); // Ch B TX IP
    }

    [Fact]
    public void RR3_ChB_RxIntPending()
    {
        WriteChannelReg(Base + 0, 1, 0x10); // RX int enable on Ch B
        _device.ChannelB.ReceiveChar(0x62);

        _device.WriteByte(Base + 2, 3);
        byte rr3 = _device.ReadByte(Base + 2);
        Assert.NotEqual(0, rr3 & 0x04); // Ch B RX IP
    }

    // --- Composite Interrupt ---

    [Fact]
    public void CompositeInterrupt_Asserts_OnTxIntPending()
    {
        WriteChannelReg(Base + 2, 1, 0x02);
        Assert.NotEmpty(_interruptHistory);
        Assert.True(_interruptHistory.Last());
    }

    [Fact]
    public void CompositeInterrupt_Deasserts_WhenCleared()
    {
        WriteChannelReg(Base + 2, 1, 0x02); // TIE → interrupt asserts
        Assert.True(_interruptHistory.Last());

        _device.WriteByte(Base + 2, 0x28); // Reset TX Int Pending (cmd=5)
        WriteChannelReg(Base + 2, 1, 0x00); // Disable TIE

        Assert.False(_interruptHistory.Last());
    }

    [Fact]
    public void CompositeInterrupt_Asserts_OnRxIntPending()
    {
        WriteChannelReg(Base + 2, 1, 0x10); // RX int enable on Ch A
        _interruptHistory.Clear();

        _device.ChannelA.ReceiveChar(0x61);
        Assert.NotEmpty(_interruptHistory);
        Assert.True(_interruptHistory.Last());
    }

    // --- Word/Long Access ---

    [Fact]
    public void ReadWord_CombinesTwoBytes()
    {
        _device.ChannelB.ReceiveChar(0x42);

        ushort word = _device.ReadWord(Base);
        byte lo = (byte)(word & 0xFF); // Ch B Data
        Assert.Equal(0x42, lo);
    }

    // --- Tick delegates to both channels ---

    [Fact]
    public void Tick_TicksBothChannels()
    {
        _device.ChannelA.WriteData(0x41);
        _device.ChannelB.WriteData(0x42);

        Assert.True(_device.ChannelA.TxInProgress);
        Assert.True(_device.ChannelB.TxInProgress);

        _device.Tick(false);

        Assert.False(_device.ChannelA.TxInProgress);
        Assert.False(_device.ChannelB.TxInProgress);
        Assert.True(_device.ChannelA.TxIntPending);
        Assert.True(_device.ChannelB.TxIntPending);
    }
}
