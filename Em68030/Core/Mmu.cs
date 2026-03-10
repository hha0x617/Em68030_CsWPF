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

namespace Em68030.Core;

public class Mmu
{
    // MMU Registers
    public ulong CRP { get; set; } // CPU Root Pointer (64-bit)
    public ulong SRP { get; set; } // Supervisor Root Pointer (64-bit)
    public ushort MMUSR { get; set; } // MMU Status Register

    // Last descriptor address (for PTEST A-register result)
    public uint LastDescriptorAddress { get; private set; }

    // --- TC register with cached derived fields ---
    private uint _tc;
    public uint TC
    {
        get => _tc;
        set
        {
            _tc = value;
            _enabled = (value & 0x80000000) != 0;
            _sre = (value & 0x02000000) != 0;
            int bits = (int)(value >> 20) & 0xF;
            _pageSizeCached = 1 << bits;
            _pageMaskCached = (uint)(_pageSizeCached - 1);
            _initialShiftCached = (int)((value >> 16) & 0xF);
            _tiaCached = (int)((value >> 12) & 0xF);
            _tibCached = (int)((value >> 8) & 0xF);
            _ticCached = (int)((value >> 4) & 0xF);
            _tidCached = (int)(value & 0xF);
        }
    }

    // --- TT registers with _ttEnabled flag ---
    private uint _tt0;
    public uint TT0
    {
        get => _tt0;
        set { _tt0 = value; _ttEnabled = (_tt0 & 0x8000) != 0 || (_tt1 & 0x8000) != 0; }
    }
    private uint _tt1;
    public uint TT1
    {
        get => _tt1;
        set { _tt1 = value; _ttEnabled = (_tt0 & 0x8000) != 0 || (_tt1 & 0x8000) != 0; }
    }

    // Cached TC-derived fields (updated on TC write)
    private bool _enabled;
    private bool _sre;
    private int _pageSizeCached = 4096;
    private uint _pageMaskCached = 0xFFF;
    private int _initialShiftCached;
    private int _tiaCached;
    private int _tibCached;
    private int _ticCached;
    private int _tidCached;
    private bool _ttEnabled;

    // Public accessors for cached values
    public bool Enabled => _enabled;
    public uint CachedPageMask => _pageMaskCached;

    // Pre-allocated array for TableWalk (avoids heap allocation per walk)
    private readonly int[] _tableIndexBits = new int[4];

    // ATC (Address Translation Cache) - direct-mapped, expanded for better hit rate
    private const int AtcSize = 4096;
    private const int AtcMask = AtcSize - 1;
    private readonly AtcEntry[] _atc = new AtcEntry[AtcSize];
    private readonly uint[] _atcDescriptorAddress = new uint[AtcSize]; // Separate for cache line packing

    // Shorthand accessors (use cached values)
    public bool SRE => _sre;

    private readonly Memory _physicalMemory;

    // Callback to invalidate CPU fetch cache on any TLB flush
    public Action? OnFlush { get; set; }

    public Mmu(Memory physicalMemory)
    {
        _physicalMemory = physicalMemory;
    }

    public void Reset()
    {
        CRP = 0;
        SRP = 0;
        TC = 0;  // setter updates all cached fields
        TT0 = 0;
        TT1 = 0;
        MMUSR = 0;
        LastDescriptorAddress = 0;
        Array.Clear(_atc, 0, AtcSize);
        Array.Clear(_atcDescriptorAddress, 0, AtcSize);
    }

    // --- ATC key management ---

    private ulong MakeAtcKey(uint logicalAddress, byte functionCode)
    {
        uint pageAddr = logicalAddress & ~_pageMaskCached;
        return ((ulong)(functionCode & 7) << 32) | pageAddr;
    }

    private static int MakeAtcIndex(ulong atcKey)
    {
        uint addr = (uint)(atcKey & 0xFFFFFFFF);
        uint fc = (uint)(atcKey >> 32);
        // Improved hash for better distribution across larger ATC
        uint hash = (addr >> 12) ^ (addr >> 18) ^ (addr >> 24) ^ (fc * 0x9E3779B9);
        return (int)(hash & AtcMask);
    }

    // --- SSW construction ---

    /// <summary>
    /// Determines if a function code represents a data access (vs instruction fetch).
    /// FC=1 (user data), FC=5 (supervisor data) → true
    /// FC=2 (user program), FC=6 (supervisor program) → false
    /// </summary>
    private static bool IsDataAccess(byte functionCode) => (functionCode & 2) == 0;

    private static ushort BuildSSW(byte functionCode, bool isRead)
    {
        // MC68030 SSW format:
        //   Bit 15: FC  (Fault on stage C of instruction pipe)
        //   Bit 14: FB  (Fault on stage B of instruction pipe)
        //   Bit 13: RC  (Rerun stage C)  — NetBSD SSW_RC = 0x2000
        //   Bit 12: RB  (Rerun stage B)  — NetBSD SSW_RB = 0x1000
        //   Bit  8: DF  (Data Fault — 1=data access, 0=instruction fetch)
        //   Bit  7: RM  (Read-Modify-Write)
        //   Bit  6: RW  (1=read, 0=write)
        //   Bits 2-0: FC (Function Code)
        //
        // NetBSD's busaddrerr2030 (busaddrerr.s) promotes RB→FB and RC→FC, then:
        //   DF=1: fault address from f_dcfa (frame offset +$10)
        //   DF=0, FB=1: fault address = saved PC + 4 (stage B prefetch)
        //   DF=0, FC=1: fault address = saved PC + 2 (stage C prefetch)
        //   DF=0, neither: fault address = saved PC (fallback)
        //
        // IMPORTANT: Always set DF=1 so the kernel reads the fault address from
        // the frame's Data Cycle Fault Address field (which our emulator always
        // fills with the correct faulting address).
        //
        // Without DF=1, instruction fetch faults use the saved PC as the fault
        // address. This is WRONG when a multi-word instruction spans a page
        // boundary: saved PC points to the instruction start (already mapped),
        // but the actual fault is on the NEXT page. The kernel would PTEST the
        // wrong (already-mapped) page, conclude it's a real bus error, and send
        // SIGBUS instead of paging in the correct page.
        ushort ssw = 0;
        ssw |= (ushort)(functionCode & 7);         // FC2-FC0 in bits 2-0
        ssw |= 0x0100;                              // DF bit 8 — always set
        if (isRead) ssw |= 0x0040;                 // RW bit 6 (1=read)
        return ssw;
    }

    // --- Main translation entry point ---

    public uint Translate(uint logicalAddress, bool supervisorMode, bool write, byte functionCode)
    {
        if (!_enabled)
            return logicalAddress;

        // Check Transparent Translation registers (skip if both disabled)
        if (_ttEnabled && (IsTransparent(logicalAddress, functionCode, _tt0, write) ||
                           IsTransparent(logicalAddress, functionCode, _tt1, write)))
            return logicalAddress;

        // Check ATC (Tag==0 means invalid, and atcKey is never 0 for valid FC)
        ulong atcKey = MakeAtcKey(logicalAddress, functionCode);
        int atcIdx = MakeAtcIndex(atcKey);
        ref AtcEntry entry = ref _atc[atcIdx];
        if (entry.Tag == atcKey)
        {
            // Write-protect check
            if (write && (entry.Flags & AtcFlagWriteProtected) != 0)
            {
                // MC68030: Normal translations do NOT modify MMUSR — only PTEST does.
                // Do not set MMUSR here; the kernel's bus error handler will PTEST
                // to get the correct MMUSR value.
                ushort ssw = BuildSSW(functionCode, isRead: false);
                throw new BusErrorException(logicalAddress, true, functionCode, ssw);
            }

            // If write and not yet modified, invalidate ATC and re-walk
            // to set the Modified bit in the page table descriptor
            if (write && (entry.Flags & AtcFlagModified) == 0)
            {
                entry.Tag = 0; // invalidate
                // Preserve MMUSR: only PTEST should modify it (MC68030 PRM §9.5.3)
                ushort savedMMUSR = MMUSR;
                uint result = TableWalk(logicalAddress, supervisorMode, write, functionCode);
                MMUSR = savedMMUSR;
                return result;
            }

            return entry.PhysicalPage | (logicalAddress & _pageMaskCached);
        }

        // Table walk — preserve MMUSR (only PTEST should modify it, MC68030 PRM §9.5.3)
        {
            ushort savedMMUSR = MMUSR;
            try
            {
                uint result = TableWalk(logicalAddress, supervisorMode, write, functionCode);
                MMUSR = savedMMUSR;
                return result;
            }
            catch
            {
                MMUSR = savedMMUSR;
                throw;
            }
        }
    }

    // Backward-compatible overload (for old call sites during transition)
    public uint Translate(uint logicalAddress, bool supervisorMode, bool write)
    {
        byte fc = supervisorMode ? (byte)5 : (byte)1; // Default: data access
        return Translate(logicalAddress, supervisorMode, write, fc);
    }

    /// <summary>
    /// Fast read-only ATC lookup. Returns physical address on hit, or uint.MaxValue on miss.
    /// Skips TT checks and table walks — caller must fall back to full Translate on miss.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public uint TranslateReadFast(uint logicalAddress, byte functionCode)
    {
        if (!_enabled)
            return logicalAddress;

        ulong atcKey = MakeAtcKey(logicalAddress, functionCode);
        int atcIdx = MakeAtcIndex(atcKey);
        ref AtcEntry entry = ref _atc[atcIdx];
        if (entry.Tag == atcKey)
            return entry.PhysicalPage | (logicalAddress & _pageMaskCached);

        return uint.MaxValue; // ATC miss — caller must use full Translate
    }

    // --- Transparent Translation ---

    /// <summary>
    /// MC68030 TTx register format (M68000 PRM Figure 1-9):
    ///   Bits 31-24: Logical Address Base
    ///   Bits 23-16: Logical Address Mask
    ///   Bit 15:     E (Enable)
    ///   Bits 14-11: Reserved (0)
    ///   Bit 10:     CI (Cache Inhibit)
    ///   Bit 9:      R/W (0=write transparent, 1=read transparent)
    ///   Bit 8:      RWM (0=use R/W, 1=ignore R/W → both transparent)
    ///   Bit 7:      Reserved (0)
    ///   Bits 6-4:   FC BASE (3 bits)
    ///   Bit 3:      Reserved (0)
    ///   Bits 2-0:   FC MASK (3 bits)
    /// </summary>
    private bool IsTransparent(uint address, byte functionCode, uint tt, bool isWrite)
    {
        if ((tt & 0x8000) == 0) return false; // E bit (bit 15)

        // Address match: compare high byte with base, ignoring masked bits
        byte addrBase = (byte)(tt >> 24);
        byte addrMask = (byte)(tt >> 16);
        byte addrHigh = (byte)(address >> 24);
        if ((addrHigh & ~addrMask) != (addrBase & ~addrMask))
            return false;

        // Function code match: compare FC with FC BASE, ignoring FC MASK bits
        int fcBase = (int)((tt >> 4) & 7);   // bits 6-4
        int fcMask = (int)(tt & 7);           // bits 2-0
        if ((functionCode & ~fcMask & 7) != (fcBase & ~fcMask & 7))
            return false;

        // R/W check: RWM (bit 8) masks the R/W field (bit 9)
        bool rwm = (tt & 0x0100) != 0;  // bit 8
        if (!rwm)
        {
            // RWM=0: R/W field is used. R/W=1 means read transparent, R/W=0 means write transparent.
            // For read-modify-write cycles, neither read nor write is transparent when RWM=0.
            bool rwBit = (tt & 0x0200) != 0; // bit 9: 1=read, 0=write
            if (isWrite && rwBit) return false;   // write access, but only reads are transparent
            if (!isWrite && !rwBit) return false;  // read access, but only writes are transparent
        }

        return true;
    }

    // --- Page Table Walk ---

    private uint TableWalk(uint logicalAddress, bool supervisorMode, bool write, byte functionCode)
    {
        // MC68030: Root pointer selection is based on FC2 bit of the function code,
        // NOT the CPU's current supervisor mode. This is critical for MOVES instruction
        // which accesses user space (FC=1) while the CPU is in supervisor mode.
        // FC2=1 (FC 4-7) = supervisor → use SRP if SRE; FC2=0 (FC 0-3) = user → use CRP
        bool isSupervisorAccess = (functionCode & 4) != 0;
        ulong rootPointer = (isSupervisorAccess && SRE) ? SRP : CRP;

        // Root pointer format (64-bit):
        // Upper long: bits 1-0 = DT (determines first-level entry size)
        // Lower long: bits 31-4 = table base address
        uint rpUpper = (uint)(rootPointer >> 32);
        uint rpLower = (uint)(rootPointer & 0xFFFFFFFF);

        int rpDT = (int)(rpUpper & 3);
        uint tableAddr = rpLower & 0xFFFFFFF0;

        // If root pointer DT is invalid (0), bus error
        if (rpDT == 0)
        {
            MMUSR = 0x0400; // I (Invalid) - bit 10
            ushort ssw = BuildSSW(functionCode, isRead: !write);
            throw new BusErrorException(logicalAddress, write, functionCode, ssw);
        }

        int shift = 32 - _initialShiftCached;
        _tableIndexBits[0] = _tiaCached;
        _tableIndexBits[1] = _tibCached;
        _tableIndexBits[2] = _ticCached;
        _tableIndexBits[3] = _tidCached;
        bool writeProtected = false;
        bool cacheInhibit = false;
        int levelsSearched = 0;

        // Entry size for the first table level is determined by root pointer DT
        int entrySize = (rpDT == 3) ? 8 : 4;

        for (int level = 0; level < 4; level++)
        {
            int indexBits = _tableIndexBits[level];
            if (indexBits == 0) continue;

            shift -= indexBits;
            int index = (int)((logicalAddress >> shift) & ((1 << indexBits) - 1));

            uint descAddr = tableAddr + (uint)(index * entrySize);
            LastDescriptorAddress = descAddr;

            // Read descriptor
            uint descriptorLo;
            uint descriptorHi = 0;
            if (entrySize == 8)
            {
                descriptorHi = _physicalMemory.ReadLong(descAddr);
                descriptorLo = _physicalMemory.ReadLong(descAddr + 4);
            }
            else
            {
                descriptorLo = _physicalMemory.ReadLong(descAddr);
            }

            int dt = (int)(descriptorLo & 3);
            levelsSearched++;

            switch (dt)
            {
                case 0: // Invalid descriptor
                {
                    MMUSR = (ushort)(
                        (levelsSearched & 7) |  // N (Number of levels, bits 2-0)
                        0x0400                   // I (Invalid, bit 10)
                    );
                    ushort ssw = BuildSSW(functionCode, isRead: !write);
                    throw new BusErrorException(logicalAddress, write, functionCode, ssw);
                }

                case 1: // Page descriptor (early termination)
                {
                    // Extract physical page address
                    // Page size determines which bits are the page offset
                    // pageBits not needed — shift already tracks the effective offset width
                    // For early termination, remaining index bits also become part of offset
                    // The effective page size is determined by the remaining shift value
                    uint pageMask = 0xFFFFFFFF << shift;
                    uint physPage = (descriptorLo & pageMask) & 0xFFFFFF00;

                    bool wp = (descriptorLo & 0x04) != 0; // Write Protect
                    bool u  = (descriptorLo & 0x08) != 0; // Used
                    bool m  = (descriptorLo & 0x10) != 0; // Modified
                    bool ci = (descriptorLo & 0x40) != 0; // Cache Inhibit

                    writeProtected |= wp;
                    cacheInhibit |= ci;

                    // Set Used bit if not already set
                    if (!u)
                    {
                        if (entrySize == 8)
                            _physicalMemory.WriteLong(descAddr + 4, descriptorLo | 0x08);
                        else
                            _physicalMemory.WriteLong(descAddr, descriptorLo | 0x08);
                    }

                    // Check write protect
                    if (write && writeProtected)
                    {
                        MMUSR = (ushort)(
                            (levelsSearched & 7) |           // N (Number of levels, bits 2-0)
                            0x0800 |                         // W (Write protected, bit 11)
                            (m ? 0x0200 : 0)                 // M (Modified, bit 9)
                        );
                        ushort ssw = BuildSSW(functionCode, isRead: false);
                        throw new BusErrorException(logicalAddress, true, functionCode, ssw);
                    }

                    // Set Modified bit on write
                    if (write && !m)
                    {
                        uint currentDesc;
                        uint mDescAddr;
                        if (entrySize == 8)
                        {
                            mDescAddr = descAddr + 4;
                            currentDesc = _physicalMemory.ReadLong(mDescAddr);
                        }
                        else
                        {
                            mDescAddr = descAddr;
                            currentDesc = _physicalMemory.ReadLong(mDescAddr);
                        }
                        _physicalMemory.WriteLong(mDescAddr, currentDesc | 0x10);
                        m = true;
                    }

                    uint pageOffset = (uint)(logicalAddress & ~pageMask);
                    uint physAddr = physPage | pageOffset;

                    // Update MMUSR
                    MMUSR = (ushort)(
                        (levelsSearched & 7) |               // N (Number of levels, bits 2-0)
                        (writeProtected ? 0x0800 : 0) |      // W (Write protected, bit 11)
                        (m ? 0x0200 : 0)                     // M (Modified, bit 9)
                    );

                    // Cache in ATC — for early-terminating descriptors that map
                    // regions larger than a single page, compute the correct
                    // physPage for the specific page within the larger region.
                    uint atcPhysPage = physAddr & ~CachedPageMask;
                    CacheInAtc(logicalAddress, functionCode, atcPhysPage,
                               writeProtected, cacheInhibit, m,
                               entrySize == 8 ? descAddr + 4 : descAddr);

                    return physAddr;
                }

                case 2: // Valid 4-byte table descriptor (short format)
                {
                    // Accumulate write-protect from table descriptors
                    writeProtected |= (descriptorLo & 0x04) != 0;

                    // Set Used bit if not set
                    if ((descriptorLo & 0x08) == 0)
                    {
                        if (entrySize == 8)
                            _physicalMemory.WriteLong(descAddr + 4, descriptorLo | 0x08);
                        else
                            _physicalMemory.WriteLong(descAddr, descriptorLo | 0x08);
                    }

                    // Next table address from descriptor
                    tableAddr = descriptorLo & 0xFFFFFFF0;
                    // DT=2 means next level uses 4-byte entries
                    entrySize = 4;
                    break;
                }

                case 3: // Valid 8-byte table descriptor (long format)
                {
                    // Accumulate write-protect
                    writeProtected |= (descriptorLo & 0x04) != 0;

                    // Set Used bit if not set
                    if ((descriptorLo & 0x08) == 0)
                    {
                        if (entrySize == 8)
                            _physicalMemory.WriteLong(descAddr + 4, descriptorLo | 0x08);
                        else
                            _physicalMemory.WriteLong(descAddr, descriptorLo | 0x08);
                    }

                    // Next table address
                    tableAddr = descriptorLo & 0xFFFFFFF0;
                    // DT=3 means next level uses 8-byte entries
                    entrySize = 8;
                    break;
                }
            }
        }

        // If we get here without finding a page descriptor, it's invalid
        MMUSR = (ushort)((levelsSearched & 7) | 0x0400);
        ushort sswFinal = BuildSSW(functionCode, isRead: !write);
        throw new BusErrorException(logicalAddress, write, functionCode, sswFinal);
    }

    // --- ATC cache management ---

    private void CacheInAtc(uint logicalAddress, byte functionCode,
                            uint physPage, bool writeProtected,
                            bool cacheInhibit, bool modified,
                            uint descriptorAddress)
    {
        ulong atcKey = MakeAtcKey(logicalAddress, functionCode);
        int idx = MakeAtcIndex(atcKey);

        byte flags = 0;
        if (writeProtected) flags |= AtcFlagWriteProtected;
        if (modified) flags |= AtcFlagModified;
        if (cacheInhibit) flags |= AtcFlagCacheInhibit;

        _atc[idx] = new AtcEntry
        {
            Tag = atcKey,
            PhysicalPage = physPage,
            Flags = flags,
            FunctionCode = functionCode
        };
        _atcDescriptorAddress[idx] = descriptorAddress;
    }

    // --- PFLUSH variants ---

    public void FlushAll()
    {
        Array.Clear(_atc, 0, AtcSize);
        Array.Clear(_atcDescriptorAddress, 0, AtcSize);
        OnFlush?.Invoke();
    }

    public void Flush(uint logicalAddress)
    {
        // Flush all FC variants for this address
        uint pageAddr = logicalAddress & ~_pageMaskCached;
        for (int i = 0; i < AtcSize; i++)
        {
            if (_atc[i].Tag != 0 && (uint)(_atc[i].Tag & 0xFFFFFFFF) == pageAddr)
                _atc[i].Tag = 0; // invalidate
        }
        OnFlush?.Invoke();
    }

    public void FlushByFC(byte functionCode, byte mask)
    {
        // PFLUSH FC,#mask: flush entries where (entryFC & mask) == (fc & mask)
        for (int i = 0; i < AtcSize; i++)
        {
            if (_atc[i].Tag != 0 && (_atc[i].FunctionCode & mask) == (functionCode & mask))
                _atc[i].Tag = 0; // invalidate
        }
        OnFlush?.Invoke();
    }

    public void FlushByFCAndAddress(byte functionCode, byte mask, uint logicalAddress)
    {
        // PFLUSH FC,#mask,(ea): flush matching FC+mask for specific page
        uint pageAddr = logicalAddress & ~_pageMaskCached;
        for (int i = 0; i < AtcSize; i++)
        {
            if (_atc[i].Tag != 0 &&
                (_atc[i].FunctionCode & mask) == (functionCode & mask) &&
                (uint)(_atc[i].Tag & 0xFFFFFFFF) == pageAddr)
                _atc[i].Tag = 0; // invalidate
        }
        OnFlush?.Invoke();
    }

    // --- PLOAD ---

    public void PLoad(uint logicalAddress, bool supervisorMode, bool write)
    {
        byte fc = supervisorMode ? (byte)5 : (byte)1;
        PLoad(logicalAddress, supervisorMode, write, fc);
    }

    public void PLoad(uint logicalAddress, bool supervisorMode, bool write, byte functionCode)
    {
        // Per MC68030 UM: "The PLOAD instruction does not alter the MMUSR."
        ushort savedMMUSR = MMUSR;
        try
        {
            TableWalk(logicalAddress, supervisorMode, write, functionCode);
        }
        catch (BusErrorException)
        {
            // PLOAD does not generate bus error exceptions
        }
        MMUSR = savedMMUSR;
    }

    // --- PTEST ---

    public void PTest(uint logicalAddress, bool supervisorMode, bool write)
    {
        byte fc = supervisorMode ? (byte)5 : (byte)1;
        PTest(logicalAddress, supervisorMode, write, fc, 7);
    }

    public void PTest(uint logicalAddress, bool supervisorMode, bool write,
                      byte functionCode, int maxLevel)
    {
        MMUSR = 0;
        LastDescriptorAddress = 0;

        if (!Enabled)
        {
            MMUSR = 0; // Valid translation (no flags set — no I bit)
            return;
        }

        // Per M68000 PRM: TTx check only applies to level 0 (ATC search).
        // For levels 1-7, T bit is always 0 and table walk is always performed.
        if (maxLevel == 0)
        {
            // Level 0: check TTx registers (with R/W sensitivity)
            if (_ttEnabled && (IsTransparent(logicalAddress, functionCode, _tt0, write) ||
                IsTransparent(logicalAddress, functionCode, _tt1, write)))
            {
                MMUSR = 0x0040; // T (Transparent, bit 6)
                return;
            }
            // Level 0: ATC-only search (not yet implemented — return I bit for ATC miss)
            // TODO: implement proper ATC search for level 0
            MMUSR = 0x0400; // I (Invalid) — no ATC entry found
            return;
        }

        // Levels 1-7: Non-destructive table walk for PTEST
        PTestTableWalk(logicalAddress, supervisorMode, write, functionCode, maxLevel);
    }

    private void PTestTableWalk(uint logicalAddress, bool supervisorMode,
                                 bool write, byte functionCode, int maxLevel)
    {
        // Root pointer selection based on FC2 bit (same as TableWalk)
        bool isSupervisorAccess = (functionCode & 4) != 0;
        ulong rootPointer = (isSupervisorAccess && SRE) ? SRP : CRP;

        uint rpUpper = (uint)(rootPointer >> 32);
        uint rpLower = (uint)(rootPointer & 0xFFFFFFFF);

        int rpDT = (int)(rpUpper & 3);
        uint tableAddr = rpLower & 0xFFFFFFF0;

        if (rpDT == 0)
        {
            MMUSR = 0x0400; // I (Invalid, bit 10)
            return;
        }

        int shift = 32 - _initialShiftCached;
        int tia = _tiaCached, tib = _tibCached, tic = _ticCached, tid = _tidCached;
        bool writeProtected = false;
        bool cacheInhibit = false;
        int levelsSearched = 0;
        int entrySize = (rpDT == 3) ? 8 : 4;
        Span<int> tableIndexBits = stackalloc int[] { tia, tib, tic, tid };

        for (int level = 0; level < 4; level++)
        {
            int indexBits = tableIndexBits[level];
            if (indexBits == 0) continue;

            if (levelsSearched >= maxLevel) break;

            shift -= indexBits;
            int index = (int)((logicalAddress >> shift) & ((1 << indexBits) - 1));

            uint descAddr = tableAddr + (uint)(index * entrySize);
            LastDescriptorAddress = descAddr;

            uint descriptorLo;
            if (entrySize == 8)
            {
                _physicalMemory.ReadLong(descAddr); // read and discard upper long
                descriptorLo = _physicalMemory.ReadLong(descAddr + 4);
            }
            else
            {
                descriptorLo = _physicalMemory.ReadLong(descAddr);
            }

            int dt = (int)(descriptorLo & 3);
            levelsSearched++;

            switch (dt)
            {
                case 0: // Invalid
                    MMUSR = (ushort)(
                        (levelsSearched & 7) |  // N (bits 2-0)
                        0x0400                   // I (Invalid, bit 10)
                    );
                    return;

                case 1: // Page descriptor
                {
                    bool wp = (descriptorLo & 0x04) != 0;
                    bool m  = (descriptorLo & 0x10) != 0;
                    bool ci = (descriptorLo & 0x40) != 0;

                    writeProtected |= wp;
                    cacheInhibit |= ci;

                    MMUSR = (ushort)(
                        (levelsSearched & 7) |               // N (bits 2-0)
                        (writeProtected ? 0x0800 : 0) |      // W (bit 11)
                        (m ? 0x0200 : 0)                     // M (bit 9)
                    );
                    return;
                }

                case 2: // Short table descriptor
                    writeProtected |= (descriptorLo & 0x04) != 0;
                    tableAddr = descriptorLo & 0xFFFFFFF0;
                    entrySize = 4;
                    break;

                case 3: // Long table descriptor
                    writeProtected |= (descriptorLo & 0x04) != 0;
                    tableAddr = descriptorLo & 0xFFFFFFF0;
                    entrySize = 8;
                    break;
            }
        }

        // Reached max level without finding page descriptor
        MMUSR = (ushort)(
            (levelsSearched & 7) |               // N (bits 2-0)
            (writeProtected ? 0x0800 : 0)        // W (bit 11)
        );
    }

    // --- Diagnostic level-A page table dump (non-destructive, for logging) ---

    public string DumpLevelATable(byte functionCode)
    {
        if (!Enabled) return "MMU disabled";

        bool isSupervisorAccess = (functionCode & 4) != 0;
        ulong rootPointer = (isSupervisorAccess && SRE) ? SRP : CRP;
        string rpName = (isSupervisorAccess && SRE) ? "SRP" : "CRP";

        uint rpUpper = (uint)(rootPointer >> 32);
        uint rpLower = (uint)(rootPointer & 0xFFFFFFFF);
        int rpDT = (int)(rpUpper & 3);
        uint tableAddr = rpLower & 0xFFFFFFF0;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[PTDUMP] {rpName}=${rootPointer:X16} DT={rpDT} tbl=${tableAddr:X8} TIA={_tiaCached}\n");

        if (rpDT == 0) { sb.Append("[PTDUMP] ROOT INVALID\n"); return sb.ToString(); }
        if (_tiaCached == 0) { sb.Append("[PTDUMP] TIA=0, no level-A table\n"); return sb.ToString(); }

        int numEntries = 1 << _tiaCached;
        int entrySize = (rpDT == 3) ? 8 : 4;
        int validCount = 0;

        for (int i = 0; i < numEntries; i++)
        {
            uint descAddr = tableAddr + (uint)(i * entrySize);
            uint descriptorLo;
            try
            {
                if (entrySize == 8)
                {
                    _physicalMemory.ReadLong(descAddr); // skip upper long
                    descriptorLo = _physicalMemory.ReadLong(descAddr + 4);
                }
                else
                {
                    descriptorLo = _physicalMemory.ReadLong(descAddr);
                }
            }
            catch { continue; }

            int dt = (int)(descriptorLo & 3);
            if (dt != 0) validCount++;

            // Only log first 8 entries and any valid entries (to keep output manageable)
            if (i < 8 || dt != 0)
            {
                uint shift = (uint)(32 - _tiaCached);
                uint vaStart = (uint)i << (int)shift;
                sb.Append($"[PTDUMP]   [{i}] @${descAddr:X8} desc=${descriptorLo:X8} DT={dt} VA=${vaStart:X8}-${vaStart + ((1u << (int)shift) - 1):X8}");
                if (dt == 2) sb.Append($" ->tbl4=${descriptorLo & 0xFFFFFFF0:X8}");
                if (dt == 3) sb.Append($" ->tbl8=${descriptorLo & 0xFFFFFFF0:X8}");
                sb.Append('\n');
            }
        }
        sb.Append($"[PTDUMP] Total: {validCount}/{numEntries} entries valid\n");
        return sb.ToString();
    }

    // --- Diagnostic page table walk (non-destructive, for logging) ---

    public string DiagnosticTableWalk(uint logicalAddress, byte functionCode)
    {
        if (!Enabled) return "MMU disabled";

        bool isSupervisorAccess = (functionCode & 4) != 0;
        ulong rootPointer = (isSupervisorAccess && SRE) ? SRP : CRP;
        string rpName = (isSupervisorAccess && SRE) ? "SRP" : "CRP";

        uint rpUpper = (uint)(rootPointer >> 32);
        uint rpLower = (uint)(rootPointer & 0xFFFFFFFF);
        int rpDT = (int)(rpUpper & 3);
        uint tableAddr = rpLower & 0xFFFFFFF0;

        var sb = new System.Text.StringBuilder();
        sb.Append($"{rpName}=${rootPointer:X16} DT={rpDT} tbl=${tableAddr:X8}");

        if (rpDT == 0) { sb.Append(" INVALID-ROOT"); return sb.ToString(); }

        int shift = 32 - _initialShiftCached;
        int[] tableIndexBits = { _tiaCached, _tibCached, _ticCached, _tidCached };
        string[] levelNames = { "A", "B", "C", "D" };
        int entrySize = (rpDT == 3) ? 8 : 4;
        bool wp = false;

        for (int level = 0; level < 4; level++)
        {
            int indexBits = tableIndexBits[level];
            if (indexBits == 0) continue;

            shift -= indexBits;
            int index = (int)((logicalAddress >> shift) & ((1 << indexBits) - 1));
            uint descAddr = tableAddr + (uint)(index * entrySize);

            uint descriptorLo, descriptorHi = 0;
            try
            {
                if (entrySize == 8)
                {
                    descriptorHi = _physicalMemory.ReadLong(descAddr);
                    descriptorLo = _physicalMemory.ReadLong(descAddr + 4);
                }
                else
                {
                    descriptorLo = _physicalMemory.ReadLong(descAddr);
                }
            }
            catch
            {
                sb.Append($" | L{levelNames[level]}: idx={index} @${descAddr:X8} READ-FAIL");
                return sb.ToString();
            }

            int dt = (int)(descriptorLo & 3);
            bool descWP = (descriptorLo & 0x04) != 0;
            bool descU = (descriptorLo & 0x08) != 0;
            bool descM = (descriptorLo & 0x10) != 0;
            wp |= descWP;

            string descStr = entrySize == 8
                ? $"${descriptorHi:X8}_{descriptorLo:X8}"
                : $"${descriptorLo:X8}";

            sb.Append($" | L{levelNames[level]}: idx={index} @${descAddr:X8} desc={descStr} DT={dt}");

            switch (dt)
            {
                case 0:
                    sb.Append(" INVALID");
                    return sb.ToString();
                case 1:
                {
                    uint pageMask = 0xFFFFFFFF << shift;
                    uint physPage = (descriptorLo & pageMask) & 0xFFFFFF00;
                    uint pageOffset = (uint)(logicalAddress & ~pageMask);
                    uint physAddr = physPage | pageOffset;
                    sb.Append($" PAGE WP={descWP} U={descU} M={descM} phys=${physAddr:X8} (accum-WP={wp})");
                    return sb.ToString();
                }
                case 2:
                    tableAddr = descriptorLo & 0xFFFFFFF0;
                    entrySize = 4;
                    sb.Append($" TBL4 WP={descWP}");
                    break;
                case 3:
                    tableAddr = descriptorLo & 0xFFFFFFF0;
                    entrySize = 8;
                    sb.Append($" TBL8 WP={descWP}");
                    break;
            }
        }

        sb.Append(" | NO-PAGE-FOUND");
        return sb.ToString();
    }

    // --- ATC entry ---
    // Compact layout: Tag==0 means invalid (no separate Valid field).
    // Bool fields packed into Flags byte for better cache density.

    private const byte AtcFlagWriteProtected = 1;
    private const byte AtcFlagModified = 2;
    private const byte AtcFlagCacheInhibit = 4;

    private struct AtcEntry
    {
        public ulong Tag;              // Full key (FC << 32 | pageAddr); 0 = invalid
        public uint PhysicalPage;
        public byte Flags;             // Packed: bit0=WP, bit1=M, bit2=CI
        public byte FunctionCode;
    }
}
