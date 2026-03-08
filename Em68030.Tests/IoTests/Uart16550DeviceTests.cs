using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

// ============================================================================
// Uart16550Device Tests
// ============================================================================

public class Uart16550DeviceTests
{
    private const uint Base = 0xFFFE4000;
    private readonly Uart16550Device _uart = new(Base);
    private readonly List<byte> _transmitted = new();
    private int _interruptCallCount;
    private bool _lastInterruptState;

    public Uart16550DeviceTests()
    {
        _uart.OnTransmit = ch => _transmitted.Add(ch);
        _uart.InterruptOutput = active => { _interruptCallCount++; _lastInterruptState = active; };
    }

    private byte ReadReg(uint reg) => _uart.ReadByte(Base + reg);
    private void WriteReg(uint reg, byte val) => _uart.WriteByte(Base + reg, val);
    private void EnableDLAB() => WriteReg(3, (byte)(ReadReg(3) | 0x80));
    private void DisableDLAB() => WriteReg(3, (byte)(ReadReg(3) & ~0x80));

    // ============================================================================
    // LSR default state
    // ============================================================================

    [Fact]
    public void LSR_DefaultState_TxReadyNoRxData()
    {
        byte lsr = ReadReg(5);
        Assert.Equal(0, lsr & 0x01);      // DR = 0
        Assert.NotEqual(0, lsr & 0x20);    // THRE = 1
        Assert.NotEqual(0, lsr & 0x40);    // TEMT = 1
    }

    // ============================================================================
    // THR / RBR (transmit / receive)
    // ============================================================================

    [Fact]
    public void WriteTHR_InvokesOnTransmit()
    {
        WriteReg(0, (byte)'A');
        Assert.Single(_transmitted);
        Assert.Equal((byte)'A', _transmitted[0]);
    }

    [Fact]
    public void WriteTHR_MultipleChars()
    {
        WriteReg(0, (byte)'H');
        WriteReg(0, (byte)'i');
        Assert.Equal(2, _transmitted.Count);
        Assert.Equal((byte)'H', _transmitted[0]);
        Assert.Equal((byte)'i', _transmitted[1]);
    }

    [Fact]
    public void ReceiveChar_SetsDataReady()
    {
        _uart.ReceiveChar((byte)'X');
        Assert.NotEqual(0, ReadReg(5) & 0x01);
    }

    [Fact]
    public void ReadRBR_ReturnsReceivedChar()
    {
        _uart.ReceiveChar((byte)'Z');
        Assert.Equal((byte)'Z', ReadReg(0));
    }

    [Fact]
    public void ReadRBR_ClearsDataReady()
    {
        _uart.ReceiveChar((byte)'A');
        ReadReg(0); // consume
        Assert.Equal(0, ReadReg(5) & 0x01);
    }

    [Fact]
    public void ReadRBR_EmptyFifo_ReturnsZero()
    {
        Assert.Equal(0, ReadReg(0));
    }

    [Fact]
    public void ReceiveChar_FIFO_PreservesOrder()
    {
        _uart.ReceiveChar((byte)'A');
        _uart.ReceiveChar((byte)'B');
        _uart.ReceiveChar((byte)'C');
        Assert.Equal((byte)'A', ReadReg(0));
        Assert.Equal((byte)'B', ReadReg(0));
        Assert.Equal((byte)'C', ReadReg(0));
    }

    [Fact]
    public void ReceiveChar_FifoFull_DropsCharacter()
    {
        for (int i = 0; i < 64; i++)
            _uart.ReceiveChar((byte)i);
        _uart.ReceiveChar(0xFF); // 65th dropped
        byte last = 0;
        for (int i = 0; i < 64; i++)
            last = ReadReg(0);
        Assert.Equal(63, last);
        Assert.Equal(0, ReadReg(5) & 0x01); // empty
    }

    // ============================================================================
    // IER
    // ============================================================================

    [Fact]
    public void IER_DefaultIsZero() => Assert.Equal(0, ReadReg(1));

    [Fact]
    public void IER_WriteAndReadBack()
    {
        WriteReg(1, 0x0F);
        Assert.Equal(0x0F, ReadReg(1));
    }

    [Fact]
    public void IER_MasksUpperBits()
    {
        WriteReg(1, 0xFF);
        Assert.Equal(0x0F, ReadReg(1));
    }

    // ============================================================================
    // IIR
    // ============================================================================

    [Fact]
    public void IIR_Default_NoInterrupt()
    {
        Assert.NotEqual(0, ReadReg(2) & 0x01);
    }

    [Fact]
    public void IIR_RxDataInterrupt()
    {
        WriteReg(1, 0x01); // Enable RDI
        _uart.ReceiveChar((byte)'A');
        byte iir = ReadReg(2);
        Assert.Equal(0, iir & 0x01);
        Assert.Equal(0x04, iir & 0x0E);
    }

    [Fact]
    public void IIR_TxEmptyInterrupt()
    {
        WriteReg(1, 0x02); // Enable THRI
        WriteReg(0, (byte)'A');
        byte iir = ReadReg(2);
        Assert.Equal(0, iir & 0x01);
        Assert.Equal(0x02, iir & 0x0E);
    }

    [Fact]
    public void IIR_RxPriorityOverTx()
    {
        WriteReg(1, 0x03); // Both
        _uart.ReceiveChar((byte)'A');
        WriteReg(0, (byte)'B');
        Assert.Equal(0x04, ReadReg(2) & 0x0E);
    }

    [Fact]
    public void IIR_FifoEnabledBits()
    {
        WriteReg(2, 0x01); // Enable FIFO
        Assert.Equal(0xC0, ReadReg(2) & 0xC0);
    }

    [Fact]
    public void IIR_FifoDisabledBits()
    {
        Assert.Equal(0x00, ReadReg(2) & 0xC0);
    }

    // ============================================================================
    // InterruptOutput callback
    // ============================================================================

    [Fact]
    public void InterruptOutput_FiredOnRxData()
    {
        WriteReg(1, 0x01);
        _uart.ReceiveChar((byte)'A');
        Assert.True(_lastInterruptState);
    }

    [Fact]
    public void InterruptOutput_ClearedWhenRxConsumed()
    {
        WriteReg(1, 0x01);
        _uart.ReceiveChar((byte)'A');
        ReadReg(0);
        Assert.False(_lastInterruptState);
    }

    [Fact]
    public void InterruptOutput_NotFiredWhenDisabled()
    {
        _interruptCallCount = 0;
        _uart.ReceiveChar((byte)'A');
        Assert.False(_lastInterruptState);
    }

    // ============================================================================
    // DLAB
    // ============================================================================

    [Fact]
    public void DLAB_AccessDivisorLatch()
    {
        EnableDLAB();
        WriteReg(0, 0x60);
        WriteReg(1, 0x00);
        Assert.Equal(0x60, ReadReg(0));
        Assert.Equal(0x00, ReadReg(1));
    }

    [Fact]
    public void DLAB_SwitchBackToNormal()
    {
        EnableDLAB();
        WriteReg(0, 0x60);
        DisableDLAB();
        WriteReg(0, (byte)'A');
        Assert.Single(_transmitted);
        Assert.Equal((byte)'A', _transmitted[0]);
    }

    [Fact]
    public void DLAB_DefaultDivisor()
    {
        EnableDLAB();
        Assert.Equal(0x01, ReadReg(0));
        Assert.Equal(0x00, ReadReg(1));
    }

    // ============================================================================
    // LCR / MCR / MSR / SCR
    // ============================================================================

    [Fact]
    public void LCR_WriteAndReadBack()
    {
        WriteReg(3, 0x1B);
        Assert.Equal(0x1B, ReadReg(3));
    }

    [Fact]
    public void MCR_WriteAndReadBack()
    {
        WriteReg(4, 0x0B);
        Assert.Equal(0x0B, ReadReg(4));
    }

    [Fact]
    public void MSR_Default_CTSAndDSRAsserted()
    {
        byte msr = ReadReg(6);
        Assert.NotEqual(0, msr & 0x10);
        Assert.NotEqual(0, msr & 0x20);
    }

    [Fact]
    public void SCR_WriteAndReadBack()
    {
        WriteReg(7, 0xA5);
        Assert.Equal(0xA5, ReadReg(7));
        WriteReg(7, 0x5A);
        Assert.Equal(0x5A, ReadReg(7));
    }

    // ============================================================================
    // Loopback mode
    // ============================================================================

    [Fact]
    public void Loopback_THRFeedsBackToRBR()
    {
        WriteReg(4, 0x10);
        WriteReg(0, (byte)'L');
        Assert.Empty(_transmitted);
        Assert.NotEqual(0, ReadReg(5) & 0x01);
        Assert.Equal((byte)'L', ReadReg(0));
    }

    [Fact]
    public void Loopback_MSR_ReflectsMCR()
    {
        WriteReg(4, 0x13); // Loopback + DTR + RTS
        byte msr = ReadReg(6);
        Assert.NotEqual(0, msr & 0x10); // CTS
        Assert.NotEqual(0, msr & 0x20); // DSR
    }

    [Fact]
    public void Loopback_MSR_NoDTR_NoDSR()
    {
        WriteReg(4, 0x10);
        byte msr = ReadReg(6);
        Assert.Equal(0, msr & 0x10);
        Assert.Equal(0, msr & 0x20);
    }

    [Fact]
    public void Loopback_MultipleChars()
    {
        WriteReg(4, 0x10);
        WriteReg(0, (byte)'A');
        WriteReg(0, (byte)'B');
        Assert.Equal((byte)'A', ReadReg(0));
        Assert.Equal((byte)'B', ReadReg(0));
    }

    // ============================================================================
    // FCR / Read-only / Out of range
    // ============================================================================

    [Fact]
    public void FCR_ClearRxFifo()
    {
        _uart.ReceiveChar((byte)'A');
        _uart.ReceiveChar((byte)'B');
        Assert.NotEqual(0, ReadReg(5) & 0x01);
        WriteReg(2, 0x02);
        Assert.Equal(0, ReadReg(5) & 0x01);
    }

    [Fact]
    public void LSR_IgnoresWrites()
    {
        WriteReg(5, 0x00);
        Assert.NotEqual(0, ReadReg(5) & 0x20);
    }

    [Fact]
    public void MSR_IgnoresWrites()
    {
        WriteReg(6, 0x00);
        Assert.NotEqual(0, ReadReg(6) & 0x30);
    }

    [Fact]
    public void ReadWord_CombinesTwoBytes()
    {
        WriteReg(7, 0xAB);
        ushort word = _uart.ReadWord(Base + 6);
        Assert.Equal(0xAB, word & 0xFF);
    }

    [Fact]
    public void OutOfRange_ReturnsZero()
    {
        Assert.Equal(0, _uart.ReadByte(Base + 8));
    }
}
