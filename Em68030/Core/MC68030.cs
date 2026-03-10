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

using System.Runtime.CompilerServices;
using Em68030.Core.Jit;

namespace Em68030.Core;

public class MC68030
{
    // Data registers D0-D7
    public uint[] D { get; } = new uint[8];

    // Address registers A0-A7 (A7 = USP in user mode)
    public uint[] A { get; } = new uint[8];

    // Program Counter
    public uint PC { get; set; }

    // Status Register (16-bit)
    // Bits 15-8: System byte (T1,T0,S,M,0,I2,I1,I0)
    // Bits 7-0: CCR (0,0,0,X,N,Z,V,C)
    public ushort SR { get; set; }

    // Supervisor Stack Pointer
    public uint SSP { get; set; }

    // User Stack Pointer (explicit, separate from SSP)
    public uint USP { get; set; }

    // Vector Base Register
    public uint VBR { get; set; }

    // Cache Control Register
    public uint CACR { get; set; }

    // Cache Address Register
    public uint CAAR { get; set; }

    // Source Function Code / Destination Function Code registers
    public uint SFC { get; set; }
    public uint DFC { get; set; }

    // Memory, MMU, and FPU
    public Memory Memory { get; }
    public Mmu Mmu { get; }
    public Fpu Fpu { get; }

    // Execution state
    public bool Halted { get; set; }
    public bool Stopped { get; set; }
    public string? StopReason { get; set; }
    public long CycleCount { get; set; }
    public long InstructionCount { get; set; }

    // Double bus fault detection
    private bool _processingBusError;

    // Register snapshot for bus error recovery: on the real MC68030, the format $A
    // frame saves internal pipeline state allowing the CPU to resume mid-instruction.
    // The emulator re-executes the entire instruction from scratch on RTE, so we must
    // undo any register side-effects (e.g. postincrement/predecrement in EA resolution)
    // that occurred before the faulting bus cycle.
    private readonly uint[] _savedA = new uint[8];
    private readonly uint[] _savedD = new uint[8];
    private ushort _savedSR;
    private bool _regSnapshotNeeded;

    // Fetch page cache: avoid MMU translation for sequential fetches within same page
    private uint _fetchPageVA;    // VA base of cached page (page-aligned)
    private uint _fetchPagePA;    // PA base of cached page
    private uint _fetchPageMask;  // Page size - 1 (e.g., 0xFFF for 4KB)
    private bool _fetchCacheValid;

    // Data page cache: avoid MMU translation for data reads within same page
    private uint _dataPageVA;     // VA base of cached data page (page-aligned)
    private uint _dataPagePA;     // PA base of cached data page
    private uint _dataPageMask;   // Page size - 1
    private bool _dataCacheValid;

    // JIT bailout side-channel: set by compiled delegates for non-register-only blocks
    internal int _jitExecutedCount;
    internal int _jitExecutedCycles;
    internal uint _jitReadResult;

    /// <summary>Bailout blacklist threshold: blocks exceeding this bailout count are evicted.</summary>
    public const ushort JitBailoutBlacklistThreshold = 64;

    // External interrupt support
    internal int _pendingIPL = 0;
    internal int _pendingVector = -1; // -1 = use autovector
    private readonly List<Action> _tickHandlers = new();
    private Action[] _tickHandlerArray = Array.Empty<Action>(); // cached for fast iteration
    private int _tickDivider;
    private int _interruptSuppress;

    private const int TickInterval = 256;

    // Exception handling
    public event Action<int>? TrapExecuted;
    public event Action<string>? ExceptionOccurred;

    /// <summary>
    /// Diagnostic callback for fatal user-mode exceptions (illegal instruction, address error, etc.).
    /// Written to console output so the user can see what killed a process.
    /// </summary>
    public Action<string>? DiagnosticOutput;

    /// <summary>
    /// Callback invoked when the CPU executes the RESET instruction (0x4E70) in supervisor mode.
    /// On real hardware, RESET asserts the RSTO signal to reset all external devices.
    /// </summary>
    public Action? OnResetInstruction;

    // Enable verbose tracing (bus errors, syscalls) — toggled from UI
    public bool VerboseTrace;

    /// <summary>Enable FPU operation tracing for user-mode code (diagnostic).</summary>
    public bool FpuTraceEnabled;

    public bool TrapHandled { get; set; }

    public void LogException(string message) => ExceptionOccurred?.Invoke(message);

    // SR flag accessors
    public bool FlagC { get => (SR & 0x0001) != 0; set => SR = (ushort)(value ? SR | 0x0001 : SR & ~0x0001); }
    public bool FlagV { get => (SR & 0x0002) != 0; set => SR = (ushort)(value ? SR | 0x0002 : SR & ~0x0002); }
    public bool FlagZ { get => (SR & 0x0004) != 0; set => SR = (ushort)(value ? SR | 0x0004 : SR & ~0x0004); }
    public bool FlagN { get => (SR & 0x0008) != 0; set => SR = (ushort)(value ? SR | 0x0008 : SR & ~0x0008); }
    public bool FlagX { get => (SR & 0x0010) != 0; set => SR = (ushort)(value ? SR | 0x0010 : SR & ~0x0010); }

    public byte CCR { get => (byte)(SR & 0xFF); set => SR = (ushort)((SR & 0xFF00) | value); }

    public bool SupervisorMode
    {
        get => (SR & 0x2000) != 0;
        set
        {
            bool wasSuper = (SR & 0x2000) != 0;
            SR = (ushort)(value ? SR | 0x2000 : SR & ~0x2000);
            // Invalidate fetch/data cache when privilege level changes.
            // With SRE enabled, supervisor uses SRP and user uses CRP — different
            // root pointers mean VA→PA mappings differ between modes.
            if (wasSuper != value)
            {
                _fetchCacheValid = false;
                _dataCacheValid = false;
                if (JitEnabled) JitCache.InvalidateAll();
            }
        }
    }

    public int InterruptMask
    {
        get => (SR >> 8) & 7;
        set => SR = (ushort)((SR & 0xF8FF) | ((value & 7) << 8));
    }

    public bool TraceT1
    {
        get => (SR & 0x8000) != 0;
        set => SR = (ushort)(value ? SR | 0x8000 : SR & ~0x8000);
    }

    public bool TraceT0
    {
        get => (SR & 0x4000) != 0;
        set => SR = (ushort)(value ? SR | 0x4000 : SR & ~0x4000);
    }

    public bool MasterMode
    {
        get => (SR & 0x1000) != 0;
        set => SR = (ushort)(value ? SR | 0x1000 : SR & ~0x1000);
    }

    private InstructionDecoder _decoder;

    // JIT compiler
    public bool JitEnabled { get; set; }
    public readonly JitCache JitCache = new();
    private readonly JitCompiler _jitCompiler = new();
    public int JitCompileThreshold { get; set; } = 32;
    public int JitMinBlockLength { get; set; } = 3;

    // Last PC before instruction execution (for caller-side bus error recovery)
    internal uint _lastPC;

    /// <summary>
    /// Handle a BusErrorException thrown during ExecuteNextFast().
    /// Restores PC and all registers to their pre-instruction state, then raises the bus error.
    /// </summary>
    public void HandleBusError(BusErrorException ex)
    {
        PC = _lastPC;
        _savedA.AsSpan().CopyTo(A);
        _savedD.AsSpan().CopyTo(D);
        SR = _savedSR;
        _fetchCacheValid = false;
        _dataCacheValid = false;
        var (faultAddr, isWrite, fc, ssw) = FixupPhysicalBusError(ex);
        RaiseBusError(faultAddr, isWrite, fc, ssw);
    }

    /// <summary>
    /// When Memory.cs (physical layer) throws BusErrorException, it uses SSW=0 and FC=0
    /// because it has no MMU context. Reconstruct proper SSW from CPU state so the
    /// kernel's bus error handler receives correct DF, FC, and RW fields.
    /// </summary>
    private (uint faultAddress, bool isWrite, byte functionCode, ushort ssw) FixupPhysicalBusError(BusErrorException ex)
    {
        byte fc = ex.FunctionCode;
        ushort ssw = ex.SpecialStatusWord;

        if (ssw == 0 && fc == 0)
        {
            fc = GetFunctionCode(false); // Assume data access; DF=1 handles instruction fetches correctly too
            ssw = (ushort)(fc & 7);      // FC bits 2-0
            ssw |= 0x0100;               // DF bit 8 (always set so kernel reads DCFA field)
            if (!ex.IsWrite) ssw |= 0x0040; // RW bit 6 (1=read, 0=write)
        }

        return (ex.FaultAddress, ex.IsWrite, fc, ssw);
    }

    // External device IPL control and tick handlers
    public void SetIPL(int level, int vector = -1) { _pendingIPL = level; _pendingVector = vector; }
    public void AddTickHandler(Action handler) { _tickHandlers.Add(handler); _tickHandlerArray = _tickHandlers.ToArray(); }
    public void ClearTickHandlers() { _tickHandlers.Clear(); _tickHandlerArray = Array.Empty<Action>(); }
    public bool HasExternalDevices => _tickHandlerArray.Length > 0;

    /// <summary>
    /// Suppress interrupt processing for the given number of instructions.
    /// Used by PCC to defer SCSI interrupt delivery so the driver can finish
    /// setting up state (hostdata-&gt;connected, hostdata-&gt;state) before the ISR runs.
    /// </summary>
    public void SuppressInterrupt(int instructions)
    {
        _interruptSuppress = instructions;
    }

    public MC68030(Memory memory)
    {
        Memory = memory;
        Mmu = new Mmu(memory);
        Mmu.OnFlush = () => { _fetchCacheValid = false; _dataCacheValid = false; if (JitEnabled) JitCache.InvalidateAll(); };
        Fpu = new Fpu();
        _decoder = new InstructionDecoder(this);
    }

    public void Reset()
    {
        for (int i = 0; i < 8; i++) { D[i] = 0; A[i] = 0; }

        // Read initial SSP and PC from reset vectors (physical memory, no MMU)
        SSP = Memory.ReadLong(0);
        PC = Memory.ReadLong(4);
        A[7] = SSP;
        USP = 0;

        SR = 0x2700; // Supervisor mode, interrupt mask 7
        VBR = 0;
        CACR = 0;
        CAAR = 0;
        SFC = 0;
        DFC = 0;
        FunctionCodeOverride = -1;

        Mmu.Reset();
        _fetchCacheValid = false;
        _dataCacheValid = false;
        JitCache.InvalidateAll();
        _processingBusError = false;
        _pendingIPL = 0;
        _pendingVector = -1;
        _tickDivider = 0;
        Halted = false;
        Stopped = false;
        StopReason = null;
        CycleCount = 0;
        InstructionCount = 0;
    }

    // --- Function Code generation ---

    // MOVES instruction override: when >= 0, GetFunctionCode returns this value
    // instead of computing from supervisor mode. Used for DFC/SFC.
    public int FunctionCodeOverride { get; set; } = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetFunctionCode(bool isProgram)
    {
        // If MOVES override is active, use the specified FC
        if (FunctionCodeOverride >= 0)
            return (byte)(FunctionCodeOverride & 7);

        // User Data=1, User Program=2, Supervisor Data=5, Supervisor Program=6
        return (byte)((SR & 0x2000) != 0 ? (isProgram ? 6 : 5) : (isProgram ? 2 : 1));
    }

    // --- SR change with USP/SSP swap ---

    public void SetSR(ushort newSR)
    {
        bool wasSuper = SupervisorMode;
        bool willBeSuper = (newSR & 0x2000) != 0;

        if (wasSuper && !willBeSuper)
        {
            // Supervisor -> User: save SSP, restore USP
            SSP = A[7];
            A[7] = USP;
        }
        else if (!wasSuper && willBeSuper)
        {
            // User -> Supervisor: save USP, restore SSP
            USP = A[7];
            A[7] = SSP;
        }

        // Invalidate fetch/data cache on privilege mode change.
        // With SRE enabled, supervisor and user modes use different root pointers
        // (SRP vs CRP), so cached VA→PA translations become invalid.
        if (wasSuper != willBeSuper)
        {
            _fetchCacheValid = false;
            _dataCacheValid = false;
            if (JitEnabled) JitCache.InvalidateAll();
        }

        SR = newSR;
    }

    // --- Address translation (Read / Write / Fetch separated) ---

    public uint TranslateRead(uint logicalAddr, bool isProgram = false)
    {
        byte fc = GetFunctionCode(isProgram);
        return Mmu.Translate(logicalAddr, SupervisorMode, false, fc);
    }

    /// <summary>
    /// Fast path for data reads: check ATC directly, fall back to full Translate on miss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint TranslateReadFast(uint logicalAddr)
    {
        byte fc = GetFunctionCode(false);
        uint pa = Mmu.TranslateReadFast(logicalAddr, fc);
        if (pa != uint.MaxValue)
            return pa;
        return Mmu.Translate(logicalAddr, SupervisorMode, false, fc);
    }

    public uint TranslateWrite(uint logicalAddr)
    {
        byte fc = GetFunctionCode(false); // writes are always data
        return Mmu.Translate(logicalAddr, SupervisorMode, true, fc);
    }

    // Keep for backward compatibility (non-MMU contexts like ViewModel)
    public uint TranslateAddress(uint logicalAddr)
    {
        return TranslateRead(logicalAddr);
    }

    public byte ReadByte(uint addr)
    {
        // Data cache fast path
        if (_dataCacheValid && (addr & ~_dataPageMask) == _dataPageVA && FunctionCodeOverride < 0)
            return Memory.ReadByte(_dataPagePA + (addr & _dataPageMask));
        uint pa = TranslateReadFast(addr);
        if (Mmu.Enabled)
        {
            _dataPageMask = Mmu.CachedPageMask;
            _dataPageVA = addr & ~_dataPageMask;
            _dataPagePA = pa & ~_dataPageMask;
            _dataCacheValid = true;
        }
        return Memory.ReadByte(pa);
    }

    public ushort ReadWord(uint addr)
    {
        // A word read can cross a page boundary when addr is at the last byte of a page.
        if (Mmu.Enabled && (addr & Mmu.CachedPageMask) == Mmu.CachedPageMask)
        {
            byte hi = Memory.ReadByte(TranslateRead(addr));
            byte lo = Memory.ReadByte(TranslateRead(addr + 1));
            return (ushort)((hi << 8) | lo);
        }
        // Data cache fast path
        if (_dataCacheValid && (addr & ~_dataPageMask) == _dataPageVA
            && (addr & _dataPageMask) + 1 < _dataPageMask && FunctionCodeOverride < 0)
            return Memory.ReadWord(_dataPagePA + (addr & _dataPageMask));
        uint pa = TranslateReadFast(addr);
        if (Mmu.Enabled)
        {
            _dataPageMask = Mmu.CachedPageMask;
            _dataPageVA = addr & ~_dataPageMask;
            _dataPagePA = pa & ~_dataPageMask;
            _dataCacheValid = true;
        }
        return Memory.ReadWord(pa);
    }

    public uint ReadLong(uint addr)
    {
        // A longword read can cross a page boundary when within 3 bytes of page end.
        if (Mmu.Enabled)
        {
            uint offset = addr & Mmu.CachedPageMask;
            if (offset + 3 > Mmu.CachedPageMask)
            {
                ushort hi = ReadWord(addr);
                ushort lo = ReadWord(addr + 2);
                return ((uint)hi << 16) | lo;
            }
        }
        // Data cache fast path
        if (_dataCacheValid && (addr & ~_dataPageMask) == _dataPageVA
            && (addr & _dataPageMask) + 3 < _dataPageMask && FunctionCodeOverride < 0)
            return Memory.ReadLong(_dataPagePA + (addr & _dataPageMask));
        uint pa = TranslateReadFast(addr);
        if (Mmu.Enabled)
        {
            _dataPageMask = Mmu.CachedPageMask;
            _dataPageVA = addr & ~_dataPageMask;
            _dataPagePA = pa & ~_dataPageMask;
            _dataCacheValid = true;
        }
        return Memory.ReadLong(pa);
    }

    public void WriteByte(uint addr, byte val)
    {
        _dataCacheValid = false;
        Memory.WriteByte(TranslateWrite(addr), val);
    }

    public void WriteWord(uint addr, ushort val)
    {
        _dataCacheValid = false;
        if (Mmu.Enabled && (addr & Mmu.CachedPageMask) == Mmu.CachedPageMask)
        {
            Memory.WriteByte(TranslateWrite(addr), (byte)(val >> 8));
            Memory.WriteByte(TranslateWrite(addr + 1), (byte)val);
            return;
        }
        Memory.WriteWord(TranslateWrite(addr), val);
    }

    public void WriteLong(uint addr, uint val)
    {
        _dataCacheValid = false;
        if (Mmu.Enabled)
        {
            uint offset = addr & Mmu.CachedPageMask;
            if (offset + 3 > Mmu.CachedPageMask)
            {
                WriteWord(addr, (ushort)(val >> 16));
                WriteWord(addr + 2, (ushort)val);
                return;
            }
        }
        Memory.WriteLong(TranslateWrite(addr), val);
    }

    public ushort FetchWord()
    {
        uint pc = PC;
        if (_fetchCacheValid && (pc & ~_fetchPageMask) == _fetchPageVA
            && (pc & _fetchPageMask) + 1 < _fetchPageMask)
        {
            ushort val = Memory.ReadWord(_fetchPagePA + (pc & _fetchPageMask));
            PC = pc + 2;
            return val;
        }
        uint pa = TranslateRead(pc, isProgram: true);
        if (Mmu.Enabled)
        {
            _fetchPageMask = Mmu.CachedPageMask;
            _fetchPageVA = pc & ~_fetchPageMask;
            _fetchPagePA = pa & ~_fetchPageMask;
            _fetchCacheValid = true;
        }
        ushort v = Memory.ReadWord(pa);
        PC = pc + 2;
        return v;
    }

    public uint FetchLong()
    {
        uint pc = PC;
        if (_fetchCacheValid && (pc & ~_fetchPageMask) == _fetchPageVA
            && (pc & _fetchPageMask) + 3 < _fetchPageMask)
        {
            uint val = Memory.ReadLong(_fetchPagePA + (pc & _fetchPageMask));
            PC = pc + 4;
            return val;
        }
        // A longword fetch can cross a page boundary (e.g., PC=$xxFFE reads
        // from two different virtual pages that may map to non-contiguous
        // physical pages).  Always split into two word fetches for correctness.
        uint hi = FetchWord();
        uint lo = FetchWord();
        return (hi << 16) | lo;
    }

    public void InvalidateFetchCache()
    {
        _fetchCacheValid = false;
        _dataCacheValid = false;
    }

    public void InvalidateDataCache() { _dataCacheValid = false; }

    /// <summary>Set up data page cache for testing.</summary>
    public void SetupDataCache(uint va, uint pa, uint mask)
    {
        _dataPageVA = va & ~mask;
        _dataPagePA = pa & ~mask;
        _dataPageMask = mask;
        _dataCacheValid = true;
    }

    /// <summary>
    /// Try to read a longword via data page cache. Used by JIT-compiled code.
    /// Returns true on cache hit (result in _jitReadResult), false on miss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadLongCached(uint addr)
    {
        if (_dataCacheValid
            && (addr & ~_dataPageMask) == _dataPageVA
            && (addr & _dataPageMask) + 3 <= _dataPageMask)
        {
            _jitReadResult = Memory.ReadLong(_dataPagePA + (addr & _dataPageMask));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Take the deferred register snapshot (called by group decoders before register modification).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureRegSnapshot()
    {
        if (_regSnapshotNeeded)
        {
            Array.Copy(A, _savedA, 8);
            Array.Copy(D, _savedD, 8);
            _regSnapshotNeeded = false;
        }
    }

    public void SetCCR(byte ccr)
    {
        CCR = ccr;
    }

    public void UpdateCCR(byte flags, byte mask)
    {
        CCR = (byte)((CCR & ~mask) | (flags & mask));
    }

    // --- Instruction execution with bus error handling ---

    public void ExecuteStep()
    {
        if (Halted) return;

        // Tick external device handlers at reduced frequency (timers run even when CPU is stopped)
        if (++_tickDivider >= TickInterval)
        {
            _tickDivider = 0;
            var handlers = _tickHandlerArray;
            for (int i = 0; i < handlers.Length; i++)
                handlers[i]();
        }

        // Check for pending interrupts (checked even during STOP)
        if (_pendingIPL > 0 && (_pendingIPL == 7 || _pendingIPL > InterruptMask))
        {
            if (_interruptSuppress > 0)
            {
                --_interruptSuppress;
            }
            else
            {
                Stopped = false;
                StopReason = null;
                // Save state before interrupt processing for bus error recovery
                _lastPC = PC;
                A.AsSpan().CopyTo(_savedA);
                D.AsSpan().CopyTo(_savedD);
                _savedSR = SR;
                try
                {
                    ProcessInterrupt(_pendingIPL);
                }
                catch (BusErrorException ex)
                {
                    PC = _lastPC;
                    _savedA.AsSpan().CopyTo(A);
                    _savedD.AsSpan().CopyTo(D);
                    SR = _savedSR;
                    _fetchCacheValid = false;
                    _dataCacheValid = false;
                    var (faultAddr, isWrite, fc, ssw) = FixupPhysicalBusError(ex);
                    RaiseBusError(faultAddr, isWrite, fc, ssw);
                }
                CycleCount += 34;
                InstructionCount++;
                return;
            }
        }

        if (Stopped) return;

        uint savedPC = PC;
        ushort savedSR = SR;
        A.AsSpan().CopyTo(_savedA);
        D.AsSpan().CopyTo(_savedD);
        try
        {
            var opcode = _decoder.ExecuteNext();
            CycleCount += InstructionDecoder.GetCycles(opcode);
            InstructionCount++;
        }
        catch (BusErrorException ex)
        {
            PC = savedPC;
            _savedA.AsSpan().CopyTo(A);
            _savedD.AsSpan().CopyTo(D);
            SR = savedSR;
            _fetchCacheValid = false;
            _dataCacheValid = false;
            var (faultAddr, isWrite, fc, ssw) = FixupPhysicalBusError(ex);
            RaiseBusError(faultAddr, isWrite, fc, ssw);
        }
    }

    /// <summary>
    /// Fast execution without internal try-catch. Caller handles BusErrorException.
    /// Returns true if instruction was executed, false if CPU is stopped.
    /// Defers register snapshot (A[]/D[] copy) until first data memory access.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ExecuteNextFast()
    {
        if (++_tickDivider >= TickInterval)
        {
            _tickDivider = 0;
            var handlers = _tickHandlerArray;
            for (int i = 0; i < handlers.Length; i++)
                handlers[i]();
        }

        if (_pendingIPL > 0 && (_pendingIPL == 7 || _pendingIPL > InterruptMask))
        {
            if (_interruptSuppress > 0)
            {
                --_interruptSuppress;
            }
            else
            {
                Stopped = false;
                StopReason = null;
                _lastPC = PC;
                A.AsSpan().CopyTo(_savedA);
                D.AsSpan().CopyTo(_savedD);
                _savedSR = SR;
                ProcessInterrupt(_pendingIPL);
                CycleCount += 34;
                InstructionCount++;
                return !Halted;
            }
        }

        if (Stopped) return false;

        _lastPC = PC;
        _savedSR = SR;
        Array.Copy(A, _savedA, 8);
        Array.Copy(D, _savedD, 8);

        var opcode = _decoder.ExecuteNext();

        CycleCount += InstructionDecoder.GetCycles(opcode);
        InstructionCount++;
        return true;
    }

    /// <summary>
    /// JIT-enabled execution path — kept separate from ExecuteNextFast to avoid
    /// bloating the interpreter method body, which impairs .NET JIT inlining.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ExecuteNextFastJit()
    {
        if (++_tickDivider >= TickInterval)
        {
            _tickDivider = 0;
            var handlers = _tickHandlerArray;
            for (int i = 0; i < handlers.Length; i++)
                handlers[i]();

            JitSamplePC();
        }

        if (_pendingIPL > 0 && (_pendingIPL == 7 || _pendingIPL > InterruptMask))
        {
            if (_interruptSuppress > 0)
            {
                --_interruptSuppress;
            }
            else
            {
                Stopped = false;
                StopReason = null;
                _lastPC = PC;
                A.AsSpan().CopyTo(_savedA);
                D.AsSpan().CopyTo(_savedD);
                _savedSR = SR;
                ProcessInterrupt(_pendingIPL);
                CycleCount += 34;
                InstructionCount++;
                return !Halted;
            }
        }

        if (Stopped) return false;

        _lastPC = PC;
        _savedSR = SR;
        Array.Copy(A, _savedA, 8);
        Array.Copy(D, _savedD, 8);

        // Inline block lookup — avoid NoInlining call overhead when no JIT block exists
        if (_fetchCacheValid && (PC & ~_fetchPageMask) == _fetchPageVA)
        {
            uint physPC = _fetchPagePA + (PC & _fetchPageMask);
            var block = JitCache.TryGetBlock(physPC);
            if (block != null)
                return ExecuteNextJit(block);
        }

        // Interpreter fallback (inline — no function call overhead)
        var opcode = _decoder.ExecuteNext();
        CycleCount += InstructionDecoder.GetCycles(opcode);
        InstructionCount++;
        return true;
    }

    /// <summary>JIT block execution — called only on block hit to keep ExecuteNextFastJit small.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ExecuteNextJit(CompiledBlock block)
    {
        PC = block.Execute(this);

        int executedCount, executedCycles;
        if (block.RegisterOnly)
        {
            executedCount = block.InstructionCount;
            executedCycles = block.TotalCycles;
        }
        else
        {
            executedCount = _jitExecutedCount;
            executedCycles = _jitExecutedCycles;
        }

        CycleCount += executedCycles;
        InstructionCount += executedCount;
        _tickDivider += executedCount - 1;

        if (!block.RegisterOnly && executedCount < block.InstructionCount)
        {
            // Bailout — track frequency and blacklist if too frequent
            if (++block.BailoutCount >= JitBailoutBlacklistThreshold)
            {
                JitCache.RemoveBlock(block.PhysicalAddress);
                JitCache.MarkUncompilable(block.PhysicalAddress);
            }

            if (executedCount == 0)
            {
                // First instruction bailed out — fall through to interpreter
                _tickDivider++;
                var opcode = _decoder.ExecuteNext();
                CycleCount += InstructionDecoder.GetCycles(opcode);
                InstructionCount++;
            }
        }
        return true;
    }

    /// <summary>JIT PC sampling — called once per TickInterval from tick handler.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JitSamplePC()
    {
        if (!_fetchCacheValid || (PC & ~_fetchPageMask) != _fetchPageVA)
            return;
        uint physPC = _fetchPagePA + (PC & _fetchPageMask);
        if (JitCache.TryGetBlock(physPC) != null || JitCache.IsUncompilable(physPC))
            return;
        byte count = JitCache.IncrementAndGetCount(physPC);
        if (count == JitCompileThreshold)
        {
            var compiled = _jitCompiler.TryCompile(this, PC, physPC);
            if (compiled != null && compiled.InstructionCount >= JitMinBlockLength)
                JitCache.AddBlock(physPC, compiled);
            else
                JitCache.MarkUncompilable(physPC);
        }
    }

    // --- Exception handling ---

    public void RaiseException(int vector)
    {
        // Save SR and switch to supervisor mode
        ushort oldSR = SR;
        SupervisorMode = true;

        // Swap to SSP if not already in supervisor mode
        if ((oldSR & 0x2000) == 0)
        {
            USP = A[7];
            A[7] = SSP;
        }

        // Push Format 0 exception frame: SR, PC, Format/Vector
        // Stack layout (ascending from SP): SR(2), PC(4), Format/Vector(2)
        // If a bus error occurs during frame push or vector read, it propagates
        // to the execution loop which calls HandleBusError → RaiseBusError.
        // RaiseBusError has double-fault protection, so cascading failures halt the CPU.
        ushort formatVector = (ushort)(0x0000 | ((vector * 4) & 0x0FFF)); // Format 0
        try
        {
            PushWord(formatVector);
            PushLong(PC);
            PushWord(oldSR);
        }
        catch (BusErrorException)
        {
            // Bus error during exception frame push.
            // Restore to pre-exception state and re-throw so HandleBusError
            // can process it properly (may result in double fault → halt).
            SR = oldSR;
            if ((oldSR & 0x2000) == 0)
                A[7] = USP;
            throw;
        }

        // Read vector address from vector table (supervisor data mode, through MMU)
        uint vectorAddr = VBR + (uint)(vector * 4);
        PC = ReadLong(vectorAddr);

        ExceptionOccurred?.Invoke($"Exception vector {vector} at ${vectorAddr:X8}, new PC=${PC:X8}");

        // Diagnostic: log fatal user-mode exceptions to console
        if ((oldSR & 0x2000) == 0 && DiagnosticOutput != null)
        {
            uint savedPC = Memory.ReadLong(TranslateRead(A[7] + 2));

            switch (vector)
            {
                case 3: // Address error
                    DiagnosticOutput($"\n[EMU] ADDRESS ERROR at PC=${savedPC:X8}, SR=${oldSR:X4}\n");
                    break;
                case 4: // Illegal instruction
                {
                    ushort opcode = 0;
                    try { opcode = Memory.ReadWord(Mmu.Translate(savedPC, false, false, 2)); } catch { }
                    DiagnosticOutput($"\n[EMU] ILLEGAL INSTRUCTION at PC=${savedPC:X8}, opcode=${opcode:X4}, SR=${oldSR:X4}\n");
                    break;
                }
                case 8: // Privilege violation
                {
                    ushort opcode = 0;
                    try { opcode = Memory.ReadWord(TranslateRead(savedPC)); } catch { }
                    DiagnosticOutput($"\n[EMU] PRIVILEGE VIOLATION at PC=${savedPC:X8}, opcode=${opcode:X4}, SR=${oldSR:X4}\n");
                    break;
                }
                case 10: // Line-A emulator
                {
                    ushort opcode = 0;
                    try { opcode = Memory.ReadWord(TranslateRead(savedPC)); } catch { }
                    DiagnosticOutput($"\n[EMU] LINE-A TRAP at PC=${savedPC:X8}, opcode=${opcode:X4}\n");
                    break;
                }
                case 11: // Line-F emulator (coprocessor)
                {
                    ushort opcode = 0;
                    try { opcode = Memory.ReadWord(TranslateRead(savedPC)); } catch { }
                    DiagnosticOutput($"\n[EMU] LINE-F TRAP at PC=${savedPC:X8}, opcode=${opcode:X4}\n");
                    break;
                }
                default:
                {
                    // Catch-all: log ANY user-mode exception not already handled above
                    if (VerboseTrace && vector != 32) // skip TRAP#0 (syscall, traced separately)
                    {
                        uint crpLo = (uint)(Mmu.CRP & 0xFFFFFFFF);
                        ushort opcode = 0;
                        try { opcode = Memory.ReadWord(TranslateRead(savedPC)); } catch { }
                        DiagnosticOutput($"[EX] V={vector} PC=${savedPC:X8} OP=${opcode:X4} CRP=${crpLo:X8}\n");
                    }
                    break;
                }
            }
        }

        // Compact syscall trace: log key process-lifecycle syscalls from user mode.
        if (VerboseTrace && vector == 32 && (oldSR & 0x2000) == 0 && DiagnosticOutput != null)
        {
            uint sn = D[0];
            uint crpLo = (uint)(Mmu.CRP & 0xFFFFFFFF);
            DiagnosticOutput($"[s]{sn} ${crpLo:X8}\n");
        }

        // Syscall interception: when TRAP #0 (vector 32) fires from user mode,
        // rewrite blocking tty ioctls to non-blocking equivalents to avoid
        // output drain stalls on the emulated SCC serial port.
        if (vector == 32 && (oldSR & 0x2000) == 0 && D[0] == 54) // ioctl
        {
            try
            {
                uint pa = Mmu.Translate(USP + 8, false, false, 1);
                uint cmd = Memory.ReadLong(pa);
                if (cmd == 0x802C7415 || cmd == 0x802C7416) // TIOCSETAW / TIOCSETAF
                {
                    uint paW = Mmu.Translate(USP + 8, false, true, 1);
                    Memory.WriteLong(paW, 0x802C7414); // → TIOCSETA (no drain wait)
                }
                else if (cmd == 0x2000745E) // TIOCDRAIN (tcdrain)
                {
                    // Short-circuit: pop exception frame, return success immediately.
                    ushort savedSR = PopWord();
                    uint savedPC = PopLong();
                    PopWord(); // Format/Vector
                    SetSR(savedSR);
                    PC = savedPC;
                    D[0] = 0;      // Return 0 (success)
                    FlagC = false;  // No error
                    return;         // Skip syscall entirely
                }
            }
            catch { /* MMU translation failure — let syscall proceed normally */ }
        }
    }

    public void RaiseBusError(uint faultAddress, bool isWrite, byte functionCode, ushort ssw)
    {
        if (_processingBusError)
        {
            // Double bus fault -> halt the processor
            Halted = true;
            StopReason = "Double bus fault";
            ExceptionOccurred?.Invoke($"DOUBLE BUS FAULT at ${faultAddress:X8} - CPU halted");
            return;
        }

        _processingBusError = true;

        // Compute diagnostic MMUSR via PTest (Translate no longer modifies MMUSR,
        // so the global MMUSR may be stale from the last PTEST instruction).
        Mmu.PTest(faultAddress, (SR & 0x2000) != 0, isWrite, functionCode, 7);
        ushort savedMMUSR = Mmu.MMUSR;

        // Compact one-line log for ALL bus errors
        if (VerboseTrace && DiagnosticOutput != null)
        {
            uint crpLo = (uint)(Mmu.CRP & 0xFFFFFFFF);
            uint srpLo = (uint)(Mmu.SRP & 0xFFFFFFFF);
            bool isMovesFault = (SR & 0x2000) != 0 && functionCode >= 1 && functionCode <= 2;
            string mode = isMovesFault ? "MOVES" : ((SR & 0x2000) != 0 ? "S" : "U");
            bool isSupervisorAccess = (functionCode & 4) != 0;
            string rpInfo = (isSupervisorAccess && Mmu.SRE)
                ? $"SRP=${srpLo:X8} (SRE=1)"
                : $"CRP=${crpLo:X8}";
            DiagnosticOutput($"[BE:{mode}] PC=${PC:X8} FA=${faultAddress:X8} SSW=${ssw:X4} MMUSR=${savedMMUSR:X4} {(isWrite ? "W" : "R")} FC={functionCode} {rpInfo} USP={((SR & 0x2000) == 0 ? A[7] : USP):X8}\n");
        }

        try
        {
            uint oldPC = PC; // PC of faulting instruction (restored by ExecuteStep)
            ushort oldSR = SR;
            SupervisorMode = true;

            // Swap to SSP if coming from user mode
            if ((oldSR & 0x2000) == 0)
            {
                USP = A[7];
                A[7] = SSP;
            }

            // Format $A stack frame (MC68030 Short Bus Cycle Fault)
            // Total: 16 words = 32 bytes
            // Push from bottom of frame upward (stack pre-decrement)

            // +$1C: Internal registers (4 bytes)
            PushLong(0);
            // +$18: Data output buffer (4 bytes)
            PushLong(0);
            // +$14: Internal register (2 bytes)
            PushWord(0);
            // +$16: Internal register (2 bytes) -- note: push order is reversed
            PushWord(0);
            // +$10: Data cycle fault address (4 bytes)
            PushLong(faultAddress);
            // +$0E: Instruction pipe stage B (2 bytes)
            PushWord(0);
            // +$0C: Instruction pipe stage C (2 bytes)
            PushWord(0);
            // +$0A: Special Status Word (2 bytes)
            PushWord(ssw);
            // +$08: Internal register (2 bytes)
            PushWord(0);

            // +$06: Format/Vector word: format=$A, vector offset = 2*4 = 8
            ushort formatVector = (ushort)(0xA000 | (2 * 4));
            PushWord(formatVector);
            // +$02: PC (4 bytes)
            PushLong(PC);
            // +$00: SR (2 bytes)
            PushWord(oldSR);

            // Read vector from vector table (supervisor data mode, through MMU)
            // If this faults, the catch below handles it as a double bus fault
            uint vectorAddr = VBR + (2 * 4); // Vector 2 = bus error
            uint vectorPhys = TranslateRead(vectorAddr);
            PC = Memory.ReadLong(vectorPhys);

            string modeTag = (oldSR & 0x2000) == 0 ? " [USER-FAULT]" : "";
            uint vectorPhysDbg = vectorPhys; // capture for message
            ExceptionOccurred?.Invoke(
                $"Bus Error at ${faultAddress:X8}, SSW=${ssw:X4}, SR=${oldSR:X4}{modeTag}, MMUSR=${savedMMUSR:X4}, " +
                $"vectorVA=${vectorAddr:X8}>PA=${vectorPhysDbg:X8}, new PC=${PC:X8} (faulting PC=${oldPC:X8}, VBR=${VBR:X8})");
        }
        catch (BusErrorException ex2)
        {
            // Bus error during bus error frame construction = double fault
            Halted = true;
            StopReason = "Double bus fault (during stack frame construction)";
            ExceptionOccurred?.Invoke(
                $"DOUBLE BUS FAULT - CPU halted\n" +
                $"  Original fault: addr=${faultAddress:X8}, write={isWrite}, FC={functionCode}, SSW=${ssw:X4}\n" +
                $"  Frame push fault: addr=${ex2.FaultAddress:X8}, write={ex2.IsWrite}\n" +
                $"  CPU: PC=${PC:X8}, SR=${SR:X4}, A7=${A[7]:X8}, SSP=${SSP:X8}, USP=${USP:X8}, VBR=${VBR:X8}");
        }
        finally
        {
            _processingBusError = false;
        }
    }

    private void ProcessInterrupt(int level)
    {
        ushort oldSR = SR;
        SupervisorMode = true;
        InterruptMask = level;

        if ((oldSR & 0x2000) == 0)
        {
            USP = A[7];
            A[7] = SSP;
        }

        // Use PCC-provided vector if available, otherwise autovector
        int vector = _pendingVector >= 0 ? _pendingVector : 24 + level;
        ushort formatVector = (ushort)(0x0000 | ((vector * 4) & 0x0FFF));
        PushWord(formatVector);
        PushLong(PC);
        PushWord(oldSR);

        uint vectorAddr = VBR + (uint)(vector * 4);
        PC = ReadLong(vectorAddr);
    }

    public void RaiseTrap(int trapNum)
    {
        TrapHandled = false;
        TrapExecuted?.Invoke(trapNum);
        if (!TrapHandled)
            RaiseException(32 + trapNum);
    }

    public void PushWord(ushort value)
    {
        A[7] -= 2;
        WriteWord(A[7], value);
    }

    public void PushLong(uint value)
    {
        A[7] -= 4;
        WriteLong(A[7], value);
    }

    public ushort PopWord()
    {
        ushort val = ReadWord(A[7]);
        A[7] += 2;
        return val;
    }

    public uint PopLong()
    {
        uint val = ReadLong(A[7]);
        A[7] += 4;
        return val;
    }

    public bool EvaluateCondition(int condCode)
    {
        bool c = FlagC, v = FlagV, z = FlagZ, n = FlagN;
        return condCode switch
        {
            0x0 => true,              // T (True)
            0x1 => false,             // F (False)
            0x2 => !c && !z,          // HI
            0x3 => c || z,            // LS
            0x4 => !c,               // CC (HI)
            0x5 => c,                // CS (LO)
            0x6 => !z,              // NE
            0x7 => z,               // EQ
            0x8 => !v,              // VC
            0x9 => v,               // VS
            0xA => !n,              // PL
            0xB => n,               // MI
            0xC => n == v,           // GE
            0xD => n != v,           // LT
            0xE => !z && (n == v),   // GT
            0xF => z || (n != v),    // LE
            _ => false
        };
    }
}

public class BusErrorException : Exception
{
    public uint FaultAddress { get; }
    public bool IsWrite { get; }
    public byte FunctionCode { get; }
    public ushort SpecialStatusWord { get; }

    public BusErrorException(uint faultAddress, bool isWrite, byte functionCode, ushort ssw)
        : base($"Bus error at ${faultAddress:X8}")
    {
        FaultAddress = faultAddress;
        IsWrite = isWrite;
        FunctionCode = functionCode;
        SpecialStatusWord = ssw;
    }
}
