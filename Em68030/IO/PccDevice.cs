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

using System.Diagnostics;
using Em68030.Core;

/// <summary>
/// PCC (Peripheral Channel Controller) for MVME147.
/// Central I/O controller managing timers and interrupt routing.
/// Mapped at $FFFE1000, 48 bytes ($00-$2F).
///
/// Register map (from NetBSD pccreg.h):
///   $00-$03  DMA Table Address (32-bit)
///   $04-$07  DMA Data Address (32-bit)
///   $08-$0B  DMA Byte Count (32-bit)
///   $0C-$0F  DMA Data Hold (32-bit)
///   $10-$11  Timer 1 Preload (16-bit)
///   $12-$13  Timer 1 Count (16-bit)
///   $14-$15  Timer 2 Preload (16-bit)
///   $16-$17  Timer 2 Count (16-bit)
///   $18      Timer 1 Interrupt Control
///   $19      Timer 1 Control
///   $1A      Timer 2 Interrupt Control
///   $1B      Timer 2 Control
///   $1C      AC Fail ICR
///   $1D      Watchdog Timer ICR
///   $1E      Printer ICR
///   $1F      Printer Control
///   $20      DMA ICR
///   $21      DMA Control
///   $22      Bus Error ICR
///   $23      DMA Status
///   $24      Abort ICR
///   $25      Table Address Function Code
///   $26      Serial (SCC) ICR
///   $27      General Control
///   $28      LANCE ICR
///   $29      General Status
///   $2A      SCSI ICR
///   $2B      Slave Base Address
///   $2C      Software Interrupt 1 ICR
///   $2D      Interrupt Vector Base
///   $2E      Software Interrupt 2 ICR
///   $2F      Revision Level
///
/// Timer model (PCC_TIMERFREQ = 160,000 Hz):
///   Counter counts UP from preload value.
///   On 16-bit overflow (0xFFFF → 0x0000), interrupt is generated
///   and counter is reloaded with the preload value.
///   Counts per interrupt = 0x10000 - preload.
///   For 100 Hz: preload = 0x10000 - (160000/100) = 0xF9C0, counts = 1600.
///
/// All ICR bit layout (timer and device ICRs share same format):
///   Bit 7: INT (write 1 to clear = PCC_ICLEAR / PCC_TIMERACK = 0x80)
///   Bit 3: IEN (interrupt enable = PCC_IENABLE = 0x08)
///   Bits 2-0: IL (interrupt level = PCC_IMASK = 0x07)
///
/// Timer Control register bits:
///   Bit 2 (CEN): Count Enable — timer counts when set.
///   Bit 1 (COC): Clear On Compare — reload preload on overflow.
///   Bit 0 (CCI): Counter Clear Inhibit — when 0, writing resets counter to preload.
///   PCC_TIMERCLEAR  = 0x00: CCI=0 → reset counter, CEN=0 → stopped.
///   PCC_TIMERENABLE = 0x01: CCI=1 → preserve, CEN=0 → stopped.
///   PCC_TIMERSTOP   = 0x03: CCI=1 COC=1 CEN=0 → stopped, count preserved.
///   PCC_TIMERSTART  = 0x07: CCI=1 COC=1 CEN=1 → counting with reload.
///
/// Vectored interrupts:
///   PCC provides vector = Vector Base ($2D) + device offset (PCCV_xxx).
///   Default vector base = 0x40 (PCC_VECBASE).
///   PCCV: ACFAIL=0, BERR=1, ABORT=2, ZS=3, LE=4, SCSI=5,
///         DMA=6, PRINTER=7, TIMER1=8, TIMER2=9, SOFT1=10, SOFT2=11
/// </summary>
public class PccDevice : IMemoryMappedDevice
{
    private readonly MC68030 _cpu;
    private const uint BaseAddress = 0xFFFE1000;

    // DMA registers ($00-$0F) — stored but not functionally implemented
    private readonly byte[] _dmaRegs = new byte[16];

    // Timer registers (16-bit)
    private ushort _timer1Preload;       // $10-$11
    private uint _timer1Count;           // $12-$13 (uint for overflow detection)
    private ushort _timer2Preload;       // $14-$15
    private uint _timer2Count;           // $16-$17

    // Timer ICR and Control registers
    private byte _timer1Icr;             // $18
    private byte _timer1Control;         // $19
    private byte _timer2Icr;             // $1A
    private byte _timer2Control;         // $1B

    // Timer overflow counters (bits 7:4 of control register when read)
    // Real PCC hardware tracks how many times the timer has overflowed.
    // NetBSD's clock_pcc_profintr() uses: for (cr >>= PCC_TIMEROVFLSHIFT; cr; cr--) { hardclock(); }
    // If the overflow counter is 0, hardclock() is never called!
    private byte _timer1OverflowCount;
    private byte _timer2OverflowCount;

    // Wall-clock timer: real PCC timer runs at 160,000 Hz independent of CPU speed.
    // Using Stopwatch for high-resolution wall-clock timing to keep timer in sync with real time.
    private const int TimerFreq = 160000;
    private long _lastTimerTimestamp = Stopwatch.GetTimestamp();
    private long _timer1Fractional;
    private long _timer2Fractional;

    // Device ICR and control registers ($1C-$2F)
    private byte _acFailIcr;             // $1C
    private byte _wdogIcr;              // $1D
    private byte _printerIcr;           // $1E
    private byte _printerControl;       // $1F
    private byte _dmaIcr;               // $20
    private byte _dmaControl;           // $21
    private byte _busErrIcr;            // $22
    private byte _dmaStatus;            // $23
    private byte _abortIcr;             // $24
    private byte _tableAddrFc;          // $25
    private byte _sccIcr;               // $26
    private byte _generalControl;       // $27
    private byte _lanceIcr;             // $28
    private byte _generalStatus;        // $29
    private byte _scsiIcr;              // $2A
    private byte _slaveBaseAddr;        // $2B
    private byte _soft1Icr;             // $2C
    private byte _vectorBase = 0x40;    // $2D (PCC_VECBASE default)
    private byte _soft2Icr;             // $2E
    private byte _revision;             // $2F

    // Level-sensitive device assertion state.
    // External devices (SCC, SCSI, LANCE) drive their interrupt lines as levels.
    // The PCC ICR INT bit must reflect the device's current assertion, even after
    // the kernel writes PCC_ICLEAR. If the device is still asserting, INT re-latches.
    private bool _sccDeviceActive;
    private bool _scsiDeviceActive;
    private bool _lanceDeviceActive;

    // Reference to SCSI controller for Tick() deferred interrupt delivery
    private Wd33c93Device? _scsiDevice;

    /// <summary>
    /// Callback invoked when the watchdog timer is armed (0xA5 written to watchdog register).
    /// Used by MVME147 Linux kernel for hardware reboot (mvme147_reset).
    /// </summary>
    public Action? OnWatchdogReset;


    // PCC interrupt vector offsets
    private const int PCCV_ACFAIL = 0;
    private const int PCCV_BERR = 1;
    private const int PCCV_ABORT = 2;
    private const int PCCV_ZS = 3;
    private const int PCCV_LE = 4;
    private const int PCCV_SCSI = 5;
    private const int PCCV_DMA = 6;
    private const int PCCV_PRINTER = 7;
    private const int PCCV_TIMER1 = 8;
    private const int PCCV_TIMER2 = 9;
    private const int PCCV_SOFT1 = 10;
    private const int PCCV_SOFT2 = 11;

    public PccDevice(MC68030 cpu)
    {
        _cpu = cpu;
    }

    /// <summary>Set reference to WD33C93 for deferred interrupt delivery via Tick().</summary>
    public void SetScsiDevice(Wd33c93Device scsi) { _scsiDevice = scsi; }

    /// <summary>
    /// Hardware reset: called when the CPU executes the RESET instruction (0x4E70).
    /// Resets all PCC registers to power-on defaults: stops timers, clears all ICRs,
    /// de-asserts all interrupt lines, and clears the General Control register (MIEN=0).
    /// </summary>
    public void HardwareReset()
    {
        // Stop timers and clear their state
        _timer1Control = 0;
        _timer2Control = 0;
        _timer1Icr = 0;
        _timer2Icr = 0;
        _timer1OverflowCount = 0;
        _timer2OverflowCount = 0;
        _timer1Count = 0;
        _timer2Count = 0;
        _timer1Fractional = 0;
        _timer2Fractional = 0;
        _lastTimerTimestamp = Stopwatch.GetTimestamp();

        // Clear all device ICRs
        _acFailIcr = 0;
        _wdogIcr = 0;
        _printerIcr = 0;
        _printerControl = 0;
        _dmaIcr = 0;
        _dmaControl = 0;
        _busErrIcr = 0;
        _dmaStatus = 0;
        _abortIcr = 0;
        _sccIcr = 0;
        _generalControl = 0;
        _lanceIcr = 0;
        _scsiIcr = 0;
        _soft1Icr = 0;
        _soft2Icr = 0;

        // Clear device assertion state
        _sccDeviceActive = false;
        _scsiDeviceActive = false;
        _lanceDeviceActive = false;

        // Update IPL — all ICRs cleared so this sets IPL to 0
        UpdateIPL();
    }

    public void Tick()
    {
        // Calculate elapsed wall-clock time and convert to 160 kHz timer ticks.
        // Real PCC timer runs at a fixed 160,000 Hz crystal, independent of CPU speed.
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _lastTimerTimestamp;
        _lastTimerTimestamp = now;

        // Clamp: ignore negative or huge jumps (e.g., after pause/resume)
        if (elapsed <= 0) return;
        long maxElapsed = Stopwatch.Frequency / 10; // 100ms max
        if (elapsed > maxElapsed) elapsed = maxElapsed;

        // Timer 1: count-up from preload, overflow at 0x10000
        if ((_timer1Control & 0x04) != 0) // CEN (bit 2) = count enable
        {
            _timer1Fractional += elapsed * TimerFreq;
            int ticks = (int)(_timer1Fractional / Stopwatch.Frequency);
            _timer1Fractional -= (long)ticks * Stopwatch.Frequency;
            if (ticks > 0)
                AdvanceTimer(ref _timer1Count, _timer1Control, _timer1Preload,
                    ref _timer1OverflowCount, ref _timer1Icr, ticks);
        }

        // Timer 2: same model
        if ((_timer2Control & 0x04) != 0)
        {
            _timer2Fractional += elapsed * TimerFreq;
            int ticks = (int)(_timer2Fractional / Stopwatch.Frequency);
            _timer2Fractional -= (long)ticks * Stopwatch.Frequency;
            if (ticks > 0)
                AdvanceTimer(ref _timer2Count, _timer2Control, _timer2Preload,
                    ref _timer2OverflowCount, ref _timer2Icr, ticks);
        }

        // Fire deferred SCSI interrupts (Level I SEL_ATN follow-up)
        _scsiDevice?.Tick();
    }

    /// <summary>
    /// Get the current timer count with real-time interpolation.
    /// The PCC timer is a free-running hardware counter at 160 kHz.
    /// Between Tick() calls, the stored count is stale — this method computes
    /// the current value by adding wall-clock-elapsed ticks since the last Tick().
    /// Required for NetBSD's timecounter subsystem (clock_gettime CLOCK_MONOTONIC).
    /// </summary>
    private ushort GetCurrentTimerCount(uint count, long fractional, byte control)
    {
        if ((control & 0x04) == 0) // CEN not set, timer stopped
            return (ushort)(count & 0xFFFF);

        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _lastTimerTimestamp;
        if (elapsed <= 0) return (ushort)(count & 0xFFFF);

        long totalFrac = fractional + elapsed * TimerFreq;
        int additionalTicks = (int)(totalFrac / Stopwatch.Frequency);

        // Clamp: don't cross the 16-bit overflow boundary (0xFFFF).
        // Overflow handling (incrementing overflow count, setting ICR INT) is done
        // in Tick(). If we wrapped here, clock_pcc_getcount() would see a count
        // below preload while the overflow counter hasn't been incremented yet,
        // causing the monotonic clock to go backwards.
        int distToOverflow = 0x10000 - (int)(count & 0xFFFF);
        if (additionalTicks >= distToOverflow)
            additionalTicks = distToOverflow - 1; // clamp at 0xFFFF

        return (ushort)((count + (uint)additionalTicks) & 0xFFFF);
    }

    /// <summary>
    /// Advance a timer counter by the given number of 160 kHz ticks, handling
    /// overflow counting and preload reload efficiently (no per-tick loop).
    /// </summary>
    private void AdvanceTimer(ref uint count, byte control, ushort preload,
        ref byte overflowCount, ref byte icr, int ticks)
    {
        bool coc = (control & 0x02) != 0;
        int period = coc ? (0x10000 - preload) : 0x10000;
        if (period <= 0) period = 1;

        int distToOverflow = 0x10000 - (int)(count & 0xFFFF);

        if (ticks < distToOverflow)
        {
            count += (uint)ticks;
            return;
        }

        // At least one overflow
        ticks -= distToOverflow;
        int overflows = 1 + ticks / period;
        int remainder = ticks % period;
        count = (uint)((coc ? preload : 0) + remainder);

        int newOvf = overflowCount + overflows;
        overflowCount = (byte)(newOvf > 15 ? 15 : newOvf);
        icr |= 0x80; // Set INT pending
        UpdateIPL();
    }

    /// <summary>
    /// Called by external devices (SCC, SCSI, LANCE) to signal interrupt state.
    /// Sets/clears INT (bit 7) in the corresponding PCC ICR.
    /// </summary>
    public void SetDeviceInterrupt(string device, bool active)
    {
        switch (device)
        {
            case "scc":
                _sccDeviceActive = active;
                if (active) _sccIcr |= 0x80; else _sccIcr &= 0x7F;
                break;
            case "scsi":
                _scsiDeviceActive = active;
                if (active) _scsiIcr |= 0x80; else _scsiIcr &= 0x7F;
                break;
            case "lance":
                _lanceDeviceActive = active;
                if (active) _lanceIcr |= 0x80; else _lanceIcr &= 0x7F;
                break;
        }
        UpdateIPL();
        if (device == "scsi" && active)
        {
            // WD33C93 SAT commands complete synchronously during WriteByte,
            // but the Linux driver needs a few instructions after the command write
            // to set up hostdata->connected and hostdata->state before the ISR runs.
            // Real hardware takes milliseconds for SCSI selection/transfer.
            _cpu.SuppressInterrupt(8);
        }
    }

    private void UpdateIPL()
    {
        int maxLevel = 0;
        int maxVector = 0;

        // Check all ICRs in priority order (lowest PCCV = highest priority)
        CheckIcr(_acFailIcr, PCCV_ACFAIL, ref maxLevel, ref maxVector);
        CheckIcr(_busErrIcr, PCCV_BERR, ref maxLevel, ref maxVector);
        CheckIcr(_abortIcr, PCCV_ABORT, ref maxLevel, ref maxVector);
        CheckIcr(_sccIcr, PCCV_ZS, ref maxLevel, ref maxVector);
        CheckIcr(_lanceIcr, PCCV_LE, ref maxLevel, ref maxVector);
        CheckIcr(_scsiIcr, PCCV_SCSI, ref maxLevel, ref maxVector);
        CheckIcr(_dmaIcr, PCCV_DMA, ref maxLevel, ref maxVector);
        CheckIcr(_printerIcr, PCCV_PRINTER, ref maxLevel, ref maxVector);
        CheckIcr(_timer1Icr, PCCV_TIMER1, ref maxLevel, ref maxVector);
        CheckIcr(_timer2Icr, PCCV_TIMER2, ref maxLevel, ref maxVector);
        CheckIcr(_soft1Icr, PCCV_SOFT1, ref maxLevel, ref maxVector);
        CheckIcr(_soft2Icr, PCCV_SOFT2, ref maxLevel, ref maxVector);

        _cpu.SetIPL(maxLevel, maxLevel > 0 ? _vectorBase + maxVector : -1);
    }

    /// <summary>
    /// Check ICR for active interrupt.
    /// All PCC ICRs share the same layout: INT=bit7, IEN=bit3, IL=bits 2-0.
    /// At equal levels, higher-priority device (checked first) wins.
    /// </summary>
    private static void CheckIcr(byte icr, int pccVector, ref int maxLevel, ref int maxVector)
    {
        if ((icr & 0x88) == 0x88) // INT (bit 7) + IEN (bit 3)
        {
            int level = icr & 0x07; // IL from bits 2-0
            if (level == 0) level = 1;
            if (level > maxLevel)
            {
                maxLevel = level;
                maxVector = pccVector;
            }
        }
    }

    public byte ReadByte(uint address)
    {
        uint offset = address - BaseAddress;
        if (offset < 0x10) return _dmaRegs[(int)offset];
        return offset switch
        {
            0x10 => (byte)(_timer1Preload >> 8),
            0x11 => (byte)(_timer1Preload & 0xFF),
            0x12 => (byte)((GetCurrentTimerCount(_timer1Count, _timer1Fractional, _timer1Control) >> 8) & 0xFF),
            0x13 => (byte)(GetCurrentTimerCount(_timer1Count, _timer1Fractional, _timer1Control) & 0xFF),
            0x14 => (byte)(_timer2Preload >> 8),
            0x15 => (byte)(_timer2Preload & 0xFF),
            0x16 => (byte)((GetCurrentTimerCount(_timer2Count, _timer2Fractional, _timer2Control) >> 8) & 0xFF),
            0x17 => (byte)(GetCurrentTimerCount(_timer2Count, _timer2Fractional, _timer2Control) & 0xFF),
            0x18 => _timer1Icr,
            0x19 => (byte)(_timer1Control | (_timer1OverflowCount << 4)),
            0x1A => _timer2Icr,
            0x1B => (byte)(_timer2Control | (_timer2OverflowCount << 4)),
            0x1C => _acFailIcr,
            0x1D => _wdogIcr,
            0x1E => _printerIcr,
            0x1F => _printerControl,
            0x20 => _dmaIcr,
            0x21 => _dmaControl,
            0x22 => _busErrIcr,
            0x23 => _dmaStatus,
            0x24 => _abortIcr,
            0x25 => _tableAddrFc,
            0x26 => _sccIcr,
            0x27 => _generalControl,
            0x28 => _lanceIcr,
            0x29 => _generalStatus,
            0x2A => _scsiIcr,
            0x2B => _slaveBaseAddr,
            0x2C => _soft1Icr,
            0x2D => _vectorBase,
            0x2E => _soft2Icr,
            0x2F => _revision,
            _ => 0
        };
    }

    public ushort ReadWord(uint address)
    {
        uint offset = address - BaseAddress;
        return offset switch
        {
            0x10 => _timer1Preload,
            0x12 => GetCurrentTimerCount(_timer1Count, _timer1Fractional, _timer1Control),
            0x14 => _timer2Preload,
            0x16 => GetCurrentTimerCount(_timer2Count, _timer2Fractional, _timer2Control),
            _ => (ushort)((ReadByte(address) << 8) | ReadByte(address + 1))
        };
    }

    public uint ReadLong(uint address)
    {
        return (uint)((ReadWord(address) << 16) | ReadWord(address + 2));
    }

    public void WriteByte(uint address, byte value)
    {
        uint offset = address - BaseAddress;
        if (offset < 0x10)
        {
            _dmaRegs[(int)offset] = value;
            return;
        }
        switch (offset)
        {
            // Timer preload/count
            case 0x10: _timer1Preload = (ushort)((_timer1Preload & 0x00FF) | (value << 8)); break;
            case 0x11: _timer1Preload = (ushort)((_timer1Preload & 0xFF00) | value); break;
            case 0x12: _timer1Count = (_timer1Count & 0x00FFu) | ((uint)value << 8); break;
            case 0x13: _timer1Count = (_timer1Count & 0xFF00u) | value; break;
            case 0x14: _timer2Preload = (ushort)((_timer2Preload & 0x00FF) | (value << 8)); break;
            case 0x15: _timer2Preload = (ushort)((_timer2Preload & 0xFF00) | value); break;
            case 0x16: _timer2Count = (_timer2Count & 0x00FFu) | ((uint)value << 8); break;
            case 0x17: _timer2Count = (_timer2Count & 0xFF00u) | value; break;

            // Timer ICR and Control
            case 0x18: WriteIcr(ref _timer1Icr, value); break;
            case 0x19: WriteTimerControl(ref _timer1Control, ref _timer1Count, _timer1Preload, ref _timer1OverflowCount, value); break;
            case 0x1A: WriteIcr(ref _timer2Icr, value); break;
            case 0x1B: WriteTimerControl(ref _timer2Control, ref _timer2Count, _timer2Preload, ref _timer2OverflowCount, value); break;

            // Device ICR and control registers
            case 0x1C: WriteIcr(ref _acFailIcr, value); break;
            case 0x1D:
                if (value == 0xA5)
                {
                    // Watchdog armed with ~100ms timeout — trigger immediate reset in emulation
                    OnWatchdogReset?.Invoke();
                }
                else
                {
                    WriteIcr(ref _wdogIcr, value);
                }
                break;
            case 0x1E: WriteIcr(ref _printerIcr, value); break;
            case 0x1F: _printerControl = value; break;
            case 0x20: WriteIcr(ref _dmaIcr, value); break;
            case 0x21:
                _dmaControl = value;
                break;
            case 0x22: WriteIcr(ref _busErrIcr, value); break;
            case 0x23: _dmaStatus = value; break;
            case 0x24: WriteIcr(ref _abortIcr, value); break;
            case 0x25: _tableAddrFc = value; break;
            case 0x26: WriteDeviceIcr(ref _sccIcr, value, _sccDeviceActive); break;
            case 0x27: _generalControl = value; break;
            case 0x28: WriteDeviceIcr(ref _lanceIcr, value, _lanceDeviceActive); break;
            case 0x29: _generalStatus = value; break;
            case 0x2A: WriteDeviceIcr(ref _scsiIcr, value, _scsiDeviceActive); break;
            case 0x2B: _slaveBaseAddr = value; break;
            case 0x2C: WriteSoftIcr(ref _soft1Icr, value); break;
            case 0x2D: _vectorBase = value; break;
            case 0x2E: WriteSoftIcr(ref _soft2Icr, value); break;
            case 0x2F: _revision = value; break;
        }
    }

    public void WriteWord(uint address, ushort value)
    {
        uint offset = address - BaseAddress;
        switch (offset)
        {
            case 0x10: _timer1Preload = value; break;
            case 0x12: _timer1Count = value; break;
            case 0x14: _timer2Preload = value; break;
            case 0x16: _timer2Count = value; break;
            default:
                WriteByte(address, (byte)(value >> 8));
                WriteByte(address + 1, (byte)(value & 0xFF));
                break;
        }
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)(value & 0xFFFF));
    }

    /// <summary>
    /// Unified ICR write for all PCC interrupt control registers.
    /// Timer and device ICRs share the same bit layout:
    ///   Bit 7 (PCC_ICLEAR/PCC_TIMERACK): writing 1 clears INT.
    ///   Bit 3 (PCC_IENABLE): interrupt enable.
    ///   Bits 2-0 (PCC_IMASK): interrupt level.
    /// </summary>
    private void WriteIcr(ref byte icr, byte value)
    {
        byte intBit = (byte)(icr & 0x80); // Current INT state

        // PCC_ICLEAR / PCC_TIMERACK (bit 7): writing 1 clears INT
        if ((value & 0x80) != 0)
            intBit = 0;

        icr = (byte)(intBit | (value & 0x0F));
        UpdateIPL();
    }

    /// <summary>
    /// Write ICR for level-sensitive external device interrupts (SCC, SCSI, LANCE).
    /// PCC_ICLEAR (bit 7) requests clearing INT, but if the device is still asserting
    /// its interrupt line, INT immediately re-latches. This matches real PCC hardware
    /// where the INT bit reflects the device's level-sensitive output.
    /// </summary>
    private void WriteDeviceIcr(ref byte icr, byte value, bool deviceActive)
    {
        byte intBit = (byte)(icr & 0x80);

        if ((value & 0x80) != 0)
            intBit = 0;

        // Level-sensitive: if device is still asserting, INT re-latches immediately
        if (deviceActive)
            intBit = 0x80;

        icr = (byte)(intBit | (value & 0x0F));
        UpdateIPL();
    }

    /// <summary>
    /// Write a software interrupt ICR (SOFT1/SOFT2).
    /// Unlike device ICRs where writing bit 7 CLEARS INT,
    /// for software interrupt registers writing bit 7 SETS INT (triggers the interrupt).
    /// Writing 0 to bit 7 clears INT.
    /// </summary>
    private void WriteSoftIcr(ref byte icr, byte value)
    {
        icr = (byte)(value & 0x8F); // Store bit 7 as-is + bits 3-0
        UpdateIPL();
    }

    /// <summary>
    /// Write timer control register.
    /// Bit 2 (CEN): Count Enable — timer counts when set.
    /// Bit 1 (COC): Clear On Compare — reload preload on overflow.
    /// Bit 0 (CCI): Counter Clear Inhibit — when 0, resets counter to preload.
    ///
    /// PCC_TIMERCLEAR  (0x00): CCI=0 → reset counter, CEN=0 → stop.
    /// PCC_TIMERENABLE (0x01): CCI=1 → preserve counter, CEN=0 → still stopped.
    /// PCC_TIMERSTOP   (0x03): CCI=1 → preserve, COC=1, CEN=0 → stopped.
    /// PCC_TIMERSTART  (0x07): CCI=1, COC=1, CEN=1 → counting with reload.
    /// </summary>
    /// <summary>PCC DMA Data Address ($04-$07) as a 32-bit value.</summary>
    public uint GetDmaDataAddress()
    {
        return (uint)(_dmaRegs[4] << 24 | _dmaRegs[5] << 16 | _dmaRegs[6] << 8 | _dmaRegs[7]);
    }

    /// <summary>Update PCC DMA Data Address after a DMA transfer (auto-increment).</summary>
    public void SetDmaDataAddress(uint addr)
    {
        _dmaRegs[4] = (byte)(addr >> 24);
        _dmaRegs[5] = (byte)(addr >> 16);
        _dmaRegs[6] = (byte)(addr >> 8);
        _dmaRegs[7] = (byte)(addr);
    }

    /// <summary>PCC DMA Byte Count ($08-$0B), lower 24 bits.</summary>
    public uint GetDmaByteCount()
    {
        return (uint)((_dmaRegs[8] & 0x7F) << 24 | _dmaRegs[9] << 16 | _dmaRegs[10] << 8 | _dmaRegs[11]);
    }

    /// <summary>Signal DMA completion (sets DMAC_CSR_DONE bit in DMA control).</summary>
    public void SetDmaDone()
    {
        _dmaControl |= 0x80;   // DMAC_CSR_DONE
        _dmaIcr |= 0x80;       // DMA INT latch
        UpdateIPL();
    }

    private void WriteTimerControl(ref byte control, ref uint count, ushort preload, ref byte overflowCount, byte value)
    {
        // Bit 0 (CCI): when clear, reset counter to preload value
        if ((value & 0x01) == 0)
            count = preload;

        // Writing the control register clears the overflow counter (bits 7:4 in read value)
        overflowCount = 0;
        control = (byte)(value & 0x07); // Only store bits 2:0 (CCI, COC, CEN)
    }
}
