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
/// Virtual 16550A UART for Linux console support on MVME147.
/// Memory-mapped at a configurable address, 8 bytes.
///
/// Register map (byte offsets, DLAB=0):
///   +0  RBR (read) / THR (write) - Receive Buffer / Transmit Holding
///   +1  IER - Interrupt Enable Register
///   +2  IIR (read) / FCR (write) - Interrupt ID / FIFO Control
///   +3  LCR - Line Control Register
///   +4  MCR - Modem Control Register
///   +5  LSR - Line Status Register (read-only)
///   +6  MSR - Modem Status Register (read-only)
///   +7  SCR - Scratch Register
///
/// Register map (DLAB=1, LCR bit 7 set):
///   +0  DLL - Divisor Latch Low
///   +1  DLM - Divisor Latch High
/// </summary>
public class Uart16550Device : IMemoryMappedDevice
{
    private readonly uint _baseAddress;

    // LSR bit definitions
    private const byte LSR_DR   = 0x01; // Data Ready
    private const byte LSR_THRE = 0x20; // Transmit Holding Register Empty
    private const byte LSR_TEMT = 0x40; // Transmitter Empty

    // IER bit definitions
    private const byte IER_RDI  = 0x01; // Receive Data Available
    private const byte IER_THRI = 0x02; // Transmitter Holding Register Empty

    // IIR values
    private const byte IIR_NO_INT    = 0x01; // No interrupt pending
    private const byte IIR_RDI       = 0x04; // Receive Data Available
    private const byte IIR_THRI      = 0x02; // Transmitter Holding Register Empty
    private const byte IIR_FIFO_MASK = 0xC0; // FIFO enabled bits

    // RX FIFO
    private const int RxFifoSize = 64;
    private readonly byte[] _rxFifo = new byte[RxFifoSize];
    private int _rxHead;
    private int _rxTail;
    private int _rxCount;
    private readonly object _rxLock = new();

    // Registers
    private byte _ier;
    private byte _fcr;
    private byte _lcr;
    private byte _mcr;
    private byte _scr;
    private byte _dll = 0x01; // Default divisor = 1
    private byte _dlm;
    private bool _fifoEnabled;
    private bool _thrEmpty = true;

    /// <summary>Called when a character is transmitted (THR write).</summary>
    public Action<byte>? OnTransmit;

    /// <summary>Interrupt output callback (active high).</summary>
    public Action<bool>? InterruptOutput;

    public Uart16550Device(uint baseAddress)
    {
        _baseAddress = baseAddress;
    }

    public byte ReadByte(uint address)
    {
        uint reg = address - _baseAddress;
        switch (reg)
        {
            case 0: // RBR or DLL
                if ((_lcr & 0x80) != 0)
                    return _dll;
                lock (_rxLock)
                {
                    if (_rxCount > 0)
                    {
                        byte ch = _rxFifo[_rxHead];
                        _rxHead = (_rxHead + 1) % RxFifoSize;
                        _rxCount--;
                        UpdateInterrupt();
                        return ch;
                    }
                    return 0;
                }

            case 1: // IER or DLM
                return (_lcr & 0x80) != 0 ? _dlm : _ier;

            case 2: // IIR (read)
                return ComputeIIR();

            case 3: return _lcr;
            case 4: return _mcr;

            case 5: // LSR
            {
                byte lsr = LSR_THRE | LSR_TEMT; // TX always ready
                lock (_rxLock)
                {
                    if (_rxCount > 0)
                        lsr |= LSR_DR;
                }
                return lsr;
            }

            case 6: // MSR
                if ((_mcr & 0x10) != 0) // Loopback mode
                {
                    byte msr = 0;
                    if ((_mcr & 0x02) != 0) msr |= 0x10; // RTS -> CTS
                    if ((_mcr & 0x01) != 0) msr |= 0x20; // DTR -> DSR
                    return msr;
                }
                return 0x30; // CTS + DSR asserted

            case 7: return _scr;
            default: return 0;
        }
    }

    public ushort ReadWord(uint address)
    {
        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address)
    {
        return (uint)((ReadWord(address) << 16) | ReadWord(address + 2));
    }

    public void WriteByte(uint address, byte value)
    {
        uint reg = address - _baseAddress;
        switch (reg)
        {
            case 0: // THR or DLL
                if ((_lcr & 0x80) != 0)
                {
                    _dll = value;
                }
                else
                {
                    if ((_mcr & 0x10) != 0)
                    {
                        // Loopback mode: feed THR data back to RX FIFO
                        lock (_rxLock)
                        {
                            if (_rxCount < RxFifoSize)
                            {
                                _rxFifo[_rxTail] = value;
                                _rxTail = (_rxTail + 1) % RxFifoSize;
                                _rxCount++;
                            }
                        }
                    }
                    else
                    {
                        OnTransmit?.Invoke(value);
                    }
                    _thrEmpty = true;
                    UpdateInterrupt();
                }
                break;

            case 1: // IER or DLM
                if ((_lcr & 0x80) != 0)
                    _dlm = value;
                else
                {
                    _ier = (byte)(value & 0x0F);
                    UpdateInterrupt();
                }
                break;

            case 2: // FCR (write)
                _fcr = value;
                _fifoEnabled = (value & 0x01) != 0;
                if ((value & 0x02) != 0)
                {
                    lock (_rxLock)
                    {
                        _rxHead = _rxTail = _rxCount = 0;
                    }
                }
                break;

            case 3: _lcr = value; break;
            case 4: _mcr = value; break;
            case 5: break; // LSR read-only
            case 6: break; // MSR read-only
            case 7: _scr = value; break;
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

    /// <summary>Push a received character into the RX FIFO.</summary>
    public void ReceiveChar(byte ch)
    {
        lock (_rxLock)
        {
            if (_rxCount < RxFifoSize)
            {
                _rxFifo[_rxTail] = ch;
                _rxTail = (_rxTail + 1) % RxFifoSize;
                _rxCount++;
            }
        }
        UpdateInterrupt();
    }

    private byte ComputeIIR()
    {
        byte fifoFlag = _fifoEnabled ? IIR_FIFO_MASK : (byte)0;

        // Priority: RX data > TX empty
        if ((_ier & IER_RDI) != 0 && _rxCount > 0)
            return (byte)(IIR_RDI | fifoFlag);

        if ((_ier & IER_THRI) != 0 && _thrEmpty)
            return (byte)(IIR_THRI | fifoFlag);

        return (byte)(IIR_NO_INT | fifoFlag);
    }

    private void UpdateInterrupt()
    {
        if (InterruptOutput == null) return;
        byte iir = ComputeIIR();
        bool active = (iir & IIR_NO_INT) == 0; // bit 0 clear = interrupt pending
        InterruptOutput(active);
    }
}
