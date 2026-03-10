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
/// AM7990 LANCE ethernet controller emulation for MVME147.
/// Phase 2: RX path + virtual network backend (ARP/ICMP/TCP/UDP echo).
/// Mapped at $FFFE1800, 4 bytes.
///
/// Registers (16-bit):
///   offset $0 = RDP (Register Data Port) — read/write selected CSR
///   offset $2 = RAP (Register Address Port) — selects CSR number
/// </summary>
public class LanceDevice : IMemoryMappedDevice
{
    private const uint BaseAddress = 0xFFFE1800;

    // Memory reference for DMA (same pattern as Wd33c93Device)
    private Memory? _memory;

    private ushort _rap; // Selected CSR number
    private readonly ushort[] _csr = new ushort[4];
    public Action<bool>? InterruptOutput;

    // CSR0 bit constants
    private const ushort CSR0_ERR  = 0x8000;
    private const ushort CSR0_BABL = 0x4000;
    private const ushort CSR0_CERR = 0x2000;
    private const ushort CSR0_MISS = 0x1000;
    private const ushort CSR0_MERR = 0x0800;
    private const ushort CSR0_RINT = 0x0400;
    private const ushort CSR0_TINT = 0x0200;
    private const ushort CSR0_IDON = 0x0100;
    private const ushort CSR0_INTR = 0x0080;
    private const ushort CSR0_INEA = 0x0040;
    private const ushort CSR0_RXON = 0x0020;
    private const ushort CSR0_TXON = 0x0010;
    private const ushort CSR0_TDMD = 0x0008;
    private const ushort CSR0_STOP = 0x0004;
    private const ushort CSR0_STRT = 0x0002;
    private const ushort CSR0_INIT = 0x0001;
    private const ushort W1C_MASK  = 0x7F00; // BABL|CERR|MISS|MERR|RINT|TINT|IDON

    // Initialization block state (read from DMA)
    private ushort _mode;
    private readonly byte[] _macAddress = new byte[6];
    private uint _rxRingAddr;
    private uint _txRingAddr;
    private int _rxRingLen;
    private int _txRingLen;

    // Chip state
    private bool _initialized;
    private bool _running;
    private int _txRingIndex;
    private bool _txPending;
    private int _rxRingIndex;
    private INetworkHandler _networkHandler = new VirtualNetworkHandler();

    public LanceDevice()
    {
        _csr[0] = CSR0_STOP;
    }

    public void SetNetworkHandler(INetworkHandler handler)
    {
        _networkHandler.Dispose();
        _networkHandler = handler;
        if (_initialized)
            _networkHandler.SetGuestMac(_macAddress);
    }

    public void AttachMemory(Memory memory)
    {
        _memory = memory;
    }

    public void Tick()
    {
        if (_txPending)
            ProcessTxRing();
        if (_running && _networkHandler.HasPendingPacket())
            ProcessRxRing();
    }

    public byte ReadByte(uint address)
    {
        uint offset = address - BaseAddress;
        ushort word = ReadWord(address & 0xFFFFFFFE);
        return (offset & 1) == 0 ? (byte)(word >> 8) : (byte)(word & 0xFF);
    }

    public ushort ReadWord(uint address)
    {
        uint offset = address - BaseAddress;
        return offset switch
        {
            0 => ReadCsr(_rap & 0x03),
            2 => _rap,
            _ => 0
        };
    }

    public uint ReadLong(uint address)
    {
        return (uint)((ReadWord(address) << 16) | ReadWord(address + 2));
    }

    public void WriteByte(uint address, byte value)
    {
        // LANCE is 16-bit only; byte writes are unusual but handle gracefully
    }

    public void WriteWord(uint address, ushort value)
    {
        uint offset = address - BaseAddress;
        switch (offset)
        {
            case 0: // RDP — write to selected CSR
                WriteCsr(_rap & 0x03, value);
                break;
            case 2: // RAP
                _rap = value;
                break;
        }
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)(value & 0xFFFF));
    }

    /// <summary>
    /// Read CSR register. CSR0 has dynamically computed bits.
    /// </summary>
    private ushort ReadCsr(int csrNum)
    {
        if (csrNum != 0)
            return _csr[csrNum];

        // CSR0: compute dynamic bits on top of stored bits
        ushort val = _csr[0];

        // ERR (bit 15) = BABL | CERR | MISS | MERR
        if ((val & (CSR0_BABL | CSR0_CERR | CSR0_MISS | CSR0_MERR)) != 0)
            val |= CSR0_ERR;
        else
            val = (ushort)(val & ~CSR0_ERR);

        // RXON (bit 5) and TXON (bit 4) reflect running state
        if (_running)
            val |= CSR0_RXON | CSR0_TXON;
        else
            val = (ushort)(val & ~(CSR0_RXON | CSR0_TXON));

        // INTR (bit 7) = (any W1C bit set) AND INEA
        if (((val & W1C_MASK) != 0) && ((val & CSR0_INEA) != 0))
            val |= CSR0_INTR;
        else
            val = (ushort)(val & ~CSR0_INTR);

        return val;
    }

    /// <summary>
    /// Write CSR register. CSR0 has special write semantics.
    /// CSR1/2/3 are only writable when STOP is set.
    /// </summary>
    private void WriteCsr(int csrNum, ushort value)
    {
        if (csrNum != 0)
        {
            // CSR1/2/3: only writable in STOP state (AM7990 spec)
            if ((_csr[0] & CSR0_STOP) != 0)
                _csr[csrNum] = value;
            return;
        }

        // CSR0 write — process in priority order

        // 1. STOP: full reset
        if ((value & CSR0_STOP) != 0)
        {
            _csr[0] = CSR0_STOP;
            _running = false;
            _initialized = false;
            _txPending = false;
            _networkHandler.Reset();
            UpdateInterrupt();
            return;
        }

        // 2. W1C: clear status bits where write value has 1
        ushort w1cBits = (ushort)(value & W1C_MASK);
        _csr[0] = (ushort)(_csr[0] & ~w1cBits);

        // 3. INEA: writing 1 sets it; writing 0 does NOT clear it (AM7990 spec)
        if ((value & CSR0_INEA) != 0)
            _csr[0] |= CSR0_INEA;

        // 4. TDMD: trigger transmit demand
        if ((value & CSR0_TDMD) != 0)
            _txPending = true;

        // 5. INIT: start initialization
        if ((value & CSR0_INIT) != 0)
            DoInit();

        // 6. STRT: start if initialized
        if ((value & CSR0_STRT) != 0)
        {
            if (_initialized)
            {
                _running = true;
                _csr[0] = (ushort)(_csr[0] & ~(CSR0_STOP | CSR0_INIT));
            }
        }

        UpdateInterrupt();
    }

    /// <summary>
    /// Read the 24-byte initialization block via DMA and configure the chip.
    /// </summary>
    private void DoInit()
    {
        if (_memory == null) return;

        // Build 24-bit physical address from CSR1 (low 16) + CSR2 (low 8)
        uint iadr = (uint)(_csr[1] & 0xFFFF) | (uint)((_csr[2] & 0x00FF) << 16);

        // Read initialization block (24 bytes at offset 0x00-0x17)
        // +0x00: mode (16-bit)
        _mode = _memory.PeekWord(iadr + 0x00);

        // +0x02: padr[0..2] (3 × 16-bit) → MAC address 6 bytes
        // LANCE stores MAC in little-endian word pairs, but with BSWP
        // the CPU writes them in its natural big-endian order.
        // PeekWord returns big-endian, so byte order is preserved.
        ushort padr0 = _memory.PeekWord(iadr + 0x02);
        ushort padr1 = _memory.PeekWord(iadr + 0x04);
        ushort padr2 = _memory.PeekWord(iadr + 0x06);
        _macAddress[0] = (byte)(padr0 >> 8);
        _macAddress[1] = (byte)(padr0 & 0xFF);
        _macAddress[2] = (byte)(padr1 >> 8);
        _macAddress[3] = (byte)(padr1 & 0xFF);
        _macAddress[4] = (byte)(padr2 >> 8);
        _macAddress[5] = (byte)(padr2 & 0xFF);

        // +0x08: ladrf[0..3] (4 × 16-bit) → multicast filter (read but not used in Phase 1)
        // Skip reading for now

        // +0x10: rdra (16-bit) + rlen|rhi (16-bit) → RX ring address & size
        ushort rdraLo = _memory.PeekWord(iadr + 0x10);
        ushort rlenRhi = _memory.PeekWord(iadr + 0x12);
        _rxRingAddr = (uint)(rdraLo & 0xFFFF) | (uint)((rlenRhi & 0x00FF) << 16);
        int rxLog2 = (rlenRhi >> 13) & 0x07;
        _rxRingLen = 1 << rxLog2;

        // +0x14: tdra (16-bit) + tlen|thi (16-bit) → TX ring address & size
        ushort tdraLo = _memory.PeekWord(iadr + 0x14);
        ushort tlenThi = _memory.PeekWord(iadr + 0x16);
        _txRingAddr = (uint)(tdraLo & 0xFFFF) | (uint)((tlenThi & 0x00FF) << 16);
        int txLog2 = (tlenThi >> 13) & 0x07;
        _txRingLen = 1 << txLog2;

        // Init successful: clear STOP, set IDON
        _csr[0] = (ushort)(_csr[0] & ~CSR0_STOP);
        _csr[0] |= CSR0_IDON;
        _initialized = true;
        _txRingIndex = 0;
        _rxRingIndex = 0;
        _networkHandler.SetGuestMac(_macAddress);
    }

    /// <summary>
    /// Process TX descriptor ring. Reads packet data from OWN descriptors,
    /// sends to virtual network handler, and marks descriptors done.
    /// </summary>
    private void ProcessTxRing()
    {
        if (_memory == null || !_running) return;

        bool processed = false;

        while (true)
        {
            // Each descriptor is 8 bytes:
            //   +0: tmd0 (16-bit) — buffer address low 16
            //   +2: tmd1 (16-bit) — high byte = flags (OWN|ERR|...), low byte = hadr (buffer addr bits 23:16)
            //   +4: tmd2 (16-bit) — buffer byte count (2's complement, upper 4 bits = 0xF)
            //   +6: tmd3 (16-bit) — error flags
            uint descAddr = _txRingAddr + (uint)(_txRingIndex * 8);

            ushort tmd0 = _memory.PeekWord(descAddr);
            ushort tmd1 = _memory.PeekWord(descAddr + 2);
            ushort tmd2 = _memory.PeekWord(descAddr + 4);
            byte tmd1Flags = (byte)(tmd1 >> 8);

            // Check OWN bit (bit 7 of flags byte)
            if ((tmd1Flags & 0x80) == 0)
                break;

            // Extract buffer address and byte count
            uint bufAddr = (uint)(tmd0 & 0xFFFF) | (uint)((tmd1 & 0xFF) << 16);
            int byteCount = -(short)(tmd2 | 0xF000);

            // Read packet data from DMA
            if (byteCount > 0 && byteCount <= 1536)
            {
                byte[] packet = new byte[byteCount];
                for (int i = 0; i < byteCount; i++)
                    packet[i] = _memory.PeekByte(bufAddr + (uint)i);

                // Check STP/ENP flags (single-buffer packet)
                bool stp = (tmd1Flags & 0x02) != 0;
                bool enp = (tmd1Flags & 0x01) != 0;
                if (stp && enp)
                    _networkHandler.ProcessPacket(packet, byteCount);
            }

            // Clear OWN bit, write back tmd1 and clear tmd3 (no errors)
            tmd1Flags = (byte)(tmd1Flags & ~0x80);
            ushort newTmd1 = (ushort)((tmd1Flags << 8) | (tmd1 & 0xFF));
            _memory.PokeWord(descAddr + 2, newTmd1);
            _memory.PokeWord(descAddr + 6, 0x0000);

            // Advance ring index
            _txRingIndex = (_txRingIndex + 1) % _txRingLen;
            processed = true;
        }

        _txPending = false;

        if (processed)
        {
            _csr[0] |= CSR0_TINT;
            UpdateInterrupt();
        }
    }

    /// <summary>
    /// Process RX descriptor ring. Injects pending packets from virtual network
    /// into guest memory via RX descriptors.
    /// </summary>
    private void ProcessRxRing()
    {
        if (_memory == null || !_running) return;

        while (_networkHandler.HasPendingPacket())
        {
            uint descAddr = _rxRingAddr + (uint)(_rxRingIndex * 8);

            ushort rmd1 = _memory.PeekWord(descAddr + 2);
            byte rmd1Flags = (byte)(rmd1 >> 8);

            // Check OWN bit — must be 1 (LANCE owns = empty buffer)
            if ((rmd1Flags & 0x80) == 0)
            {
                // No available buffer — set MISS
                _csr[0] |= CSR0_MISS;
                UpdateInterrupt();
                return;
            }

            // Get buffer address and size
            ushort rmd0 = _memory.PeekWord(descAddr);
            ushort rmd2 = _memory.PeekWord(descAddr + 4);
            uint bufAddr = (uint)(rmd0 & 0xFFFF) | (uint)((rmd1 & 0xFF) << 16);
            int bufSize = -(short)(rmd2 | 0xF000);

            byte[] packet = _networkHandler.DequeuePacket();

            if (packet.Length > bufSize)
            {
                // Packet too large — set ERR + BUFF, clear OWN
                ushort errFlags = (ushort)(0x40 | 0x04); // ERR | BUFF (within flags byte)
                ushort newRmd1 = (ushort)((errFlags << 8) | (rmd1 & 0xFF));
                _memory.PokeWord(descAddr + 2, newRmd1);
                _memory.PokeWord(descAddr + 6, 0);
            }
            else
            {
                // Write packet data via DMA
                for (int i = 0; i < packet.Length; i++)
                    _memory.PokeByte(bufAddr + (uint)i, packet[i]);

                // Write back RMD1: OWN=0, STP=1, ENP=1
                ushort newRmd1 = (ushort)(0x03 << 8 | (rmd1 & 0xFF));
                _memory.PokeWord(descAddr + 2, newRmd1);

                // RMD3: message byte count (packet length + 4 for FCS)
                ushort rmd3 = (ushort)((packet.Length + 4) & 0x0FFF);
                _memory.PokeWord(descAddr + 6, rmd3);
            }

            // Advance ring index
            _rxRingIndex = (_rxRingIndex + 1) % _rxRingLen;

            // Set RINT
            _csr[0] |= CSR0_RINT;
            UpdateInterrupt();
        }
    }

    /// <summary>
    /// Update interrupt output based on CSR0 status and INEA.
    /// </summary>
    private void UpdateInterrupt()
    {
        bool intr = ((_csr[0] & W1C_MASK) != 0) && ((_csr[0] & CSR0_INEA) != 0);
        InterruptOutput?.Invoke(intr);
    }
}
