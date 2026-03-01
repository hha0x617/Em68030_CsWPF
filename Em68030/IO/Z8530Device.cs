namespace Em68030.IO;

using System.Collections.Concurrent;
using Em68030.Core;

/// <summary>
/// Z8530 SCC channel — register model with RX FIFO, TX output, and interrupt support.
/// </summary>
public class Z8530Channel
{
    private byte _registerPointer;
    private readonly byte[] _writeRegs = new byte[16];
    private readonly byte[] _readRegs = new byte[16];
    private readonly Queue<byte> _rxFifo = new();
    private bool _txIntPending;
    private bool _txInProgress; // Character is being "transmitted" (simulated delay)
    private int _txIdleTicks;   // Ticks since TX became idle with TIE enabled

    // Staging queue for user console input. Characters are read directly from
    // here by ReadData (bypassing _rxFifo). They are visible to RR0 RxAvail
    // but invisible to RxIntPending, so the ISR cannot steal them.
    private readonly ConcurrentQueue<byte> _pendingInput = new();

    public event Action<byte>? CharTransmitted;

    /// <summary>Called when interrupt state may have changed. Parent device evaluates composite state.</summary>
    public Action? InterruptStateChanged;

    /// <summary>Callback to compute RR3 (set by Z8530Device on Channel A only).</summary>
    public Func<byte>? GetRR3;

    /// <summary>TX interrupt pending (TX buffer empty and not cleared by Reset TX Int cmd).</summary>
    public bool TxIntPending => _txIntPending;

    /// <summary>RX interrupt pending — only checks hardware FIFO, not user input queue.</summary>
    public bool RxIntPending => _rxFifo.Count > 0;

    /// <summary>Whether TX interrupt is enabled (WR1 bit 1).</summary>
    public bool TxInterruptEnabled => (_writeRegs[1] & 0x02) != 0;

    /// <summary>Whether RX interrupt is enabled (WR1 bits 4-3).</summary>
    public bool RxInterruptEnabled => (_writeRegs[1] & 0x18) != 0;

    /// <summary>Whether RX is enabled (WR3 bit 0).</summary>
    public bool RxEnabled => (_writeRegs[3] & 0x01) != 0;

    /// <summary>Whether TX is enabled (WR5 bit 3).</summary>
    public bool TxEnabled => (_writeRegs[5] & 0x08) != 0;

    /// <summary>Whether TX is in progress (character being serialized).</summary>
    public bool TxInProgress => _txInProgress;

    /// <summary>
    /// Hardware receive — adds to RX FIFO and triggers interrupt evaluation.
    /// Used for data arriving from actual emulated hardware sources.
    /// </summary>
    public void ReceiveChar(byte ch)
    {
        _rxFifo.Enqueue(ch);
        InterruptStateChanged?.Invoke();
    }

    /// <summary>
    /// Queue user console input. Characters are staged in _pendingInput and
    /// read on-demand by ReadData when the kernel reads the data register.
    /// Characters in _pendingInput are visible to RR0 RxAvail (for polling)
    /// but invisible to RxIntPending (preventing ISR from stealing them).
    /// </summary>
    public void QueueInput(byte ch)
    {
        _pendingInput.Enqueue(ch);
    }

    public byte ReadControl()
    {
        byte reg = _registerPointer;
        _registerPointer = 0; // Reset pointer after read

        return reg switch
        {
            // RR0: Bit 0=RxAvail, Bit 2=TxEmpty, Bit 3=DCD, Bit 5=CTS
            // DCD and CTS are always asserted (terminal connected).
            // TxEmpty reflects whether the transmit buffer is available (not mid-transmission).
            // RxAvail reflects data in _rxFifo OR _pendingInput (for polled mode).
            // Characters in _pendingInput don't trigger RxIntPending (no interrupt),
            // so the ISR cannot steal them — only explicit ReadData retrieves them.
            0 => (byte)((_rxFifo.Count > 0 || !_pendingInput.IsEmpty ? 0x01 : 0x00)
                        | (_txInProgress ? 0x00 : 0x04)  // TxEmpty when not transmitting
                        | 0x08   // DCD — carrier detect always active
                        | 0x20), // CTS — clear to send always active
            1 => 0x01, // RR1: All Sent
            2 => _readRegs[2], // RR2: Interrupt vector
            3 => GetRR3?.Invoke() ?? 0, // RR3: Interrupt pending (computed by device)
            _ => _readRegs[reg]
        };
    }

    public void WriteControl(byte value)
    {
        if (_registerPointer == 0)
        {
            byte regSelect = (byte)(value & 0x07);
            if (regSelect != 0)
            {
                _registerPointer = regSelect;
                return;
            }
            // WR0 command handling (bits 5-3)
            byte cmd = (byte)((value >> 3) & 0x07);
            switch (cmd)
            {
                case 0: break; // Null command
                case 2: // Reset Ext/Status interrupts
                    break;
                case 5: // Reset Tx interrupt pending
                    _txIntPending = false;
                    InterruptStateChanged?.Invoke();
                    break;
                case 6: // Error reset
                    break;
                case 7: // Reset highest IUS
                    InterruptStateChanged?.Invoke();
                    break;
            }
            _writeRegs[0] = value;
        }
        else
        {
            byte reg = _registerPointer;
            byte oldVal = _writeRegs[reg];
            _writeRegs[reg] = value;
            _registerPointer = 0;
            // WR1 affects interrupt enables — re-evaluate
            if (reg == 1)
            {
                // Z8530 spec: "The first interrupt occurs when the Transmit Interrupt
                // Enable bit is first set." When TX interrupt is enabled while the
                // transmit buffer is already empty, immediately assert TxIntPending
                // so the ISR can pick up the first byte from the tty output buffer.
                if (TxInterruptEnabled && !_txInProgress && !_txIntPending)
                {
                    _txIntPending = true;
                }
                InterruptStateChanged?.Invoke();
            }
        }
    }

    public byte ReadData()
    {
        // Read from hardware RX FIFO first (characters placed by Tick or ReceiveChar)
        if (_rxFifo.Count > 0)
        {
            byte ch = _rxFifo.Dequeue();
            if (_rxFifo.Count == 0)
                InterruptStateChanged?.Invoke();
            return ch;
        }
        // Polled-mode fallback: read directly from user input staging queue.
        // Characters here are invisible to RxIntPending (no interrupt triggered),
        // so the ISR never competes for them. Only explicit polling via
        // RR0 + ReadData retrieves these characters.
        if (_pendingInput.TryDequeue(out byte pending))
            return pending;
        return 0;
    }

    public void WriteData(byte value)
    {
        CharTransmitted?.Invoke(value);
        // Z8530 spec: "The Transmit Interrupt Pending flag is reset ... when a new
        // character is written to the Tx buffer."  Writing fills the TX buffer,
        // clearing the empty condition.  The TX interrupt re-asserts when the
        // character finishes transmitting (handled in Tick).
        _txInProgress = true;
        _txIntPending = false;
        _txIdleTicks = 0;
        InterruptStateChanged?.Invoke();
    }

    /// <summary>
    /// Called periodically to simulate TX/RX serial timing.
    /// TX: after a character is written, the next Tick completes transmission.
    /// RX: When the CPU is in STOP state (kernel sleeping in read() etc.)
    /// and RX interrupts are enabled, pending user input is promoted to
    /// _rxFifo to trigger an RX interrupt and wake the kernel.
    /// When the CPU is actively executing (e.g. cngetc polling loop),
    /// characters remain in _pendingInput and are consumed on-demand
    /// by ReadData via RR0 RxAvail polling.
    /// </summary>
    public void Tick(bool cpuStopped)
    {
        if (_txInProgress)
        {
            _txInProgress = false;
            _txIntPending = true;
            _txIdleTicks = 0;
            InterruptStateChanged?.Invoke();
        }
        else if (TxInterruptEnabled && !_txIntPending)
        {
            // TX prod: TIE is enabled but no TX interrupt is pending and no
            // transmission is in progress.  This state occurs when the ISR has
            // emptied its software output buffer and issued "Reset TX Int Pending",
            // but the softint subsequently refilled the buffer without toggling TIE
            // (because TIE was already set, zs_loadchannelregs skips the write).
            // On real Z8530 hardware, the only ways to generate a new TX interrupt
            // are: (a) write a char then wait for completion, or (b) toggle TIE 0→1.
            // Neither happens in this scenario, so we periodically prod the ISR by
            // reasserting TX Int Pending.  The ISR will find data in its software
            // buffer and resume the TX chain.  If no data is available, the ISR
            // simply resets TX Int Pending and returns (minimal overhead).
            if (++_txIdleTicks >= 16) // ~1024 instructions ≈ 100µs at 10 MHz
            {
                _txIdleTicks = 0;
                _txIntPending = true;
                InterruptStateChanged?.Invoke();
            }
        }
        else if (cpuStopped && _txIntPending && !TxInterruptEnabled)
        {
            // The CPU is idle (kernel sleeping in read() etc.), a TX interrupt
            // is pending, but TIE is false.  The NetBSD zstty driver's
            // cs_creg/cs_preg tracking can get out of sync with the actual
            // hardware WR1, causing zs_loadchannelregs to skip the TIE-enable
            // write when zstty_start tries to enable it.  Force TIE on so the
            // pending TX interrupt fires and the ISR can transmit queued data.
            // This only fires when the CPU is stopped (post-boot idle), never
            // during boot when the softint infrastructure isn't ready.
            _writeRegs[1] |= 0x02; // Set TIE bit
            _txIdleTicks = 0;
            InterruptStateChanged?.Invoke();
        }
        else
        {
            _txIdleTicks = 0;
        }

        // Promote pending user-input to the hardware RX FIFO only when the
        // CPU is stopped (idle/sleeping). This ensures:
        //  - Boot polled mode (cngetc): CPU is running, no promotion.
        //    Characters stay in _pendingInput, read via RR0 + ReadData.
        //  - Post-boot interrupt mode (read()): CPU is in STOP, promotion
        //    fires RX interrupt, ISR delivers chars, kernel wakes up.
        if (cpuStopped && RxInterruptEnabled && !_pendingInput.IsEmpty)
        {
            while (_pendingInput.TryDequeue(out byte b))
                _rxFifo.Enqueue(b);
            InterruptStateChanged?.Invoke();
        }
    }
}

/// <summary>
/// Z8530 SCC dual-channel serial controller for MVME147.
/// Channel A = console, Channel B = auxiliary.
/// Mapped at $FFFE3000, 4 bytes.
///
/// Address map:
///   $0 = Channel B Control
///   $1 = Channel B Data
///   $2 = Channel A Control
///   $3 = Channel A Data
/// </summary>
public class Z8530Device : IMemoryMappedDevice
{
    private const uint BaseAddress = 0xFFFE3000;

    public Z8530Channel ChannelA { get; } = new();
    public Z8530Channel ChannelB { get; } = new();
    public Action<bool>? InterruptOutput;

    public Z8530Device()
    {
        ChannelA.InterruptStateChanged = UpdateCompositeInterrupt;
        ChannelB.InterruptStateChanged = UpdateCompositeInterrupt;
        ChannelA.GetRR3 = ComputeRR3;
    }

    /// <summary>
    /// Tick both channels for TX/RX simulation.
    /// </summary>
    /// <param name="cpuStopped">True when the CPU is in STOP state (idle/sleeping).</param>
    public void Tick(bool cpuStopped)
    {
        ChannelA.Tick(cpuStopped);
        ChannelB.Tick(cpuStopped);
    }

    /// <summary>
    /// Evaluates composite interrupt state from both channels and asserts/deasserts the interrupt line.
    /// </summary>
    private void UpdateCompositeInterrupt()
    {
        bool active = (ChannelA.TxIntPending && ChannelA.TxInterruptEnabled)
                    || (ChannelA.RxIntPending && ChannelA.RxInterruptEnabled)
                    || (ChannelB.TxIntPending && ChannelB.TxInterruptEnabled)
                    || (ChannelB.RxIntPending && ChannelB.RxInterruptEnabled);
        InterruptOutput?.Invoke(active);
    }

    /// <summary>
    /// Computes RR3 (Interrupt Pending register, valid on Channel A only).
    /// Bit 5: Ch A RX IP, Bit 4: Ch A TX IP, Bit 3: Ch A Ext/Status IP,
    /// Bit 2: Ch B RX IP, Bit 1: Ch B TX IP, Bit 0: Ch B Ext/Status IP.
    /// </summary>
    private byte ComputeRR3()
    {
        byte rr3 = 0;
        if (ChannelA.RxIntPending && ChannelA.RxInterruptEnabled) rr3 |= 0x20;
        if (ChannelA.TxIntPending && ChannelA.TxInterruptEnabled) rr3 |= 0x10;
        if (ChannelB.RxIntPending && ChannelB.RxInterruptEnabled) rr3 |= 0x04;
        if (ChannelB.TxIntPending && ChannelB.TxInterruptEnabled) rr3 |= 0x02;
        return rr3;
    }

    public byte ReadByte(uint address)
    {
        uint offset = address - BaseAddress;
        return offset switch
        {
            0 => ChannelB.ReadControl(),
            1 => ChannelB.ReadData(),
            2 => ChannelA.ReadControl(),
            3 => ChannelA.ReadData(),
            _ => 0
        };
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
        uint offset = address - BaseAddress;
        switch (offset)
        {
            case 0: ChannelB.WriteControl(value); break;
            case 1: ChannelB.WriteData(value); break;
            case 2: ChannelA.WriteControl(value); break;
            case 3: ChannelA.WriteData(value); break;
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
}
