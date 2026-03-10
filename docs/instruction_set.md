# MC68030 Emulator (C# WPF) - Instruction Set Implementation Status

Date: 2026-03-07

## Legend

- [x] = Implemented
- [ ] = Not implemented
- JIT = JIT compilable (register-only or with data page cache bailout)
  - (blank) = Not yet attempted for JIT

---

## Implementation Strategy (C# WPF)

### Opcode Dispatch

65536-entry delegate table (`Action<ushort>[65536]`). Each entry is a delegate to an
InstructionDecoder method. Initialized in `InitOpcodeTable()`, with hot opcodes overridden
by specialized fast handlers.

### Fast Handlers

High-frequency opcodes bypass the group decoder and are dispatched directly:

| Handler | Opcodes | Description |
|---------|---------|-------------|
| FastMOVEQ | 0x7000-0x7FFE (all Group7) | Move quick immediate. Bit8 checked inside handler; raises exception 4 if set |
| FastMOVE_L_Dn_Dm | MOVE.L Dn,Dm | Register-to-register long move |
| FastBRA_B | 0x6001-0x60FE | Unconditional branch with 8-bit displacement |
| FastBcc_B | 0x6x01-0x6xFE (cond!=0) | Conditional branch with 8-bit displacement |
| FastRTS | 0x4E75 | Return from subroutine |
| FastADD_L | ADD.L Dn,Dm | Register-to-register long add |
| FastSUB_L | SUB.L Dn,Dm | Register-to-register long sub |
| FastCMP_L | CMP.L Dn,Dm | Register-to-register long compare |

Differences from C++: C# registers FastBRA_B as a dedicated handler (C++ handles it via
DecodeGroup6). FastMOVEQ is registered for all Group7 opcodes with a bit8 guard inside the
handler (C++ guards at table registration time).

### Register Snapshot

Deferred register snapshot is not used. Although effective in C++, it caused a speed
regression in C# due to JIT overhead and was reverted. Snapshots are taken before each
instruction execution.

### JIT Compiler

IL code generation via System.Reflection.Emit. Register-only instruction blocks are compiled
to .NET IL and executed as DynamicMethod instances.

- Files: `Core/Jit/CompiledBlock.cs`, `Core/Jit/JitCache.cs`, `Core/Jit/JitCompiler.cs`
- ExecuteNextFast() and ExecuteNextFastJit() are completely separate methods (same separation
  design as C++).
- Method selection uses a bool branch. Func<bool> delegate dispatch caused a 36 to 30 MHz
  regression and was rejected.
- JIT ON is currently slower than JIT OFF (31.0 vs 32.1 MIPS): NoInlining method call
  overhead for ExecuteNextJit + DynamicMethod delegate dispatch cost for block.Execute()
  outweigh the benefit given the rarity of compilable blocks.

### FPU Diagnostic Trace

FpuInstructionDecoder has a `FpuTrace()` method. When `FpuTraceEnabled` is set, FPU
instruction tracing is output to diagnostics (not available in C++ version).

### Performance (JIT OFF)

~32.14 MIPS / ~216.75 MHz-cycles (measured via Avg mode), ~6.7 cycles/instruction

> **Note**: These figures are approximate estimates, not cycle-accurate measurements.
> - **MHz** = total emulated cycles / wall-clock seconds / 1,000,000. Cycle counts come from
>   a 65,536-entry static lookup table (`s_cycleTable`) with EA cost adjustments, which
>   approximates MC68030 timing but does not model pipeline, cache, or bus wait states.
> - **MIPS** = total emulated instructions / wall-clock seconds / 1,000,000.
> - **Avg** values are cumulative from the start of a Run session. Instantaneous values
>   are sampled every ~500ms.
> - Results vary depending on workload, host CPU, and system load.

> **STOP instruction idle time exclusion**: Linux uses the M68K `STOP` instruction
> (0x4E72) to halt the CPU while waiting for interrupts (interrupt-driven idle).
> The emulator tracks wall-clock time spent in the STOP state and excludes it from
> the MHz/MIPS calculation denominator. This ensures that the displayed values reflect
> actual CPU execution speed rather than idle time. For example, at an idle Linux shell
> prompt where the CPU spends nearly all time in STOP, the MHz/MIPS values remain
> broadly consistent with active execution speed without showing obviously
> anomalous values. NetBSD, by contrast, tends to use busy-wait loops that
> continuously execute instructions, so STOP time exclusion has minimal effect.

### Cycle Table

`s_cycleTable[65536]` (byte): Static lookup table. EA cost functions compute
memory access overhead per addressing mode.

### Source Files

| File | Description |
|------|-------------|
| Core/InstructionDecoder.cs | Integer instruction decoder (~3200 lines) |
| Core/FpuInstructionDecoder.cs | FPU instruction decoder |
| Core/Jit/JitCompiler.cs | JIT basic-block compiler (IL generation) |
| Core/Jit/CompiledBlock.cs | Compiled block representation |
| Core/Jit/JitCache.cs | JIT block cache |
| Core/MC68030.cs | CPU core, opcode table, execution loop |
| Core/Mmu.cs | MMU / address translation |

---

## Integer Instructions

### Data Transfer

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | MOVE.B / .W / .L | Move data | JIT (.L (An)/d16(An)->Dm, (An)+->Dm, Dm->(An)/d16(An) with bailout) |
| [x] | MOVEA.W / .L | Move to address register | JIT (.L Dn->An, An->Am) |
| [x] | MOVEQ | Move quick (8-bit immediate) | JIT |
| [x] | MOVEM | Move multiple registers | |
| [x] | MOVEP.W / .L | Move peripheral data | |
| [x] | MOVES | Move with function code (SFC/DFC) | |
| [x] | MOVE from SR | Read status register | |
| [x] | MOVE to CCR | Write condition code register | |
| [x] | MOVE to SR | Write status register (supervisor) | |
| [x] | MOVE USP | Move user stack pointer (supervisor) | |
| [x] | MOVEC | Move control register (supervisor) | |
| [x] | EXG | Exchange registers (Dn<->Dm, An<->Am, Dn<->An) | JIT |

### Arithmetic

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | ADD.B / .W / .L | Add | JIT (.B/.W/.L Dn,Dm) |
| [x] | ADDA.W / .L | Add to address register | |
| [x] | ADDI | Add immediate | |
| [x] | ADDQ | Add quick (1-8) | JIT (.B/.W/.L Dn / An) |
| [x] | ADDX.B / .W / .L | Add with extend | |
| [x] | SUB.B / .W / .L | Subtract | JIT (.B/.W/.L Dn,Dm) |
| [x] | SUBA.W / .L | Subtract from address register | |
| [x] | SUBI | Subtract immediate | |
| [x] | SUBQ | Subtract quick (1-8) | JIT (.B/.W/.L Dn / An) |
| [x] | SUBX.B / .W / .L | Subtract with extend | |
| [x] | NEG.B / .W / .L | Negate | JIT (.B/.W/.L Dn) |
| [x] | NEGX.B / .W / .L | Negate with extend | |
| [x] | CLR.B / .W / .L | Clear | JIT (.B/.W/.L Dn) |
| [x] | CMP.B / .W / .L | Compare | JIT (.B/.W/.L Dn,Dm) |
| [x] | CMPA.W / .L | Compare with address register | |
| [x] | CMPI | Compare immediate | |
| [x] | CMPM.B / .W / .L | Compare memory (An)+,(Am)+ | |
| [x] | CMP2 / CHK2 | Compare/Check against bounds (68020+) | |
| [x] | MULU.W | Unsigned multiply 16x16->32 | JIT (Dn,Dm) |
| [x] | MULS.W | Signed multiply 16x16->32 | JIT (Dn,Dm) |
| [x] | MULU.L | Unsigned multiply 32x32->32/64 (68020+) | |
| [x] | MULS.L | Signed multiply 32x32->32/64 (68020+) | |
| [x] | DIVU.W | Unsigned divide 32/16 | |
| [x] | DIVS.W | Signed divide 32/16 | |
| [x] | DIVU.L | Unsigned divide 64/32 (68020+) | |
| [x] | DIVS.L | Signed divide 64/32 (68020+) | |

### Logic

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | AND.B / .W / .L | Logical AND | JIT (.B/.W/.L Dn,Dm) |
| [x] | ANDI | AND immediate | |
| [x] | ANDI to CCR | AND immediate to CCR | |
| [x] | ANDI to SR | AND immediate to SR (supervisor) | |
| [x] | OR.B / .W / .L | Logical OR | JIT (.B/.W/.L Dn,Dm) |
| [x] | ORI | OR immediate | |
| [x] | ORI to CCR | OR immediate to CCR | |
| [x] | ORI to SR | OR immediate to SR (supervisor) | |
| [x] | EOR.B / .W / .L | Exclusive OR | JIT (.B/.W/.L Dn,Dm) |
| [x] | EORI | XOR immediate | |
| [x] | EORI to CCR | XOR immediate to CCR | |
| [x] | EORI to SR | XOR immediate to SR (supervisor) | |
| [x] | NOT.B / .W / .L | Logical NOT | JIT (.B/.W/.L Dn) |
| [x] | TST.B / .W / .L | Test | JIT (.B/.W/.L Dn) |

### Shift & Rotate

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | ASL.B / .W / .L | Arithmetic shift left (reg/imm/mem) | JIT (.L #imm,Dn) |
| [x] | ASR.B / .W / .L | Arithmetic shift right (reg/imm/mem) | JIT (.L #imm,Dn) |
| [x] | LSL.B / .W / .L | Logical shift left (reg/imm/mem) | JIT (.L #imm,Dn) |
| [x] | LSR.B / .W / .L | Logical shift right (reg/imm/mem) | JIT (.L #imm,Dn) |
| [x] | ROL.B / .W / .L | Rotate left (reg/imm/mem) | |
| [x] | ROR.B / .W / .L | Rotate right (reg/imm/mem) | |
| [x] | ROXL.B / .W / .L | Rotate left with extend (reg/imm/mem) | |
| [x] | ROXR.B / .W / .L | Rotate right with extend (reg/imm/mem) | |

### Bit Manipulation

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | BTST | Bit test (register/immediate) | JIT (Dn,Dm) |
| [x] | BCHG | Bit change (register/immediate) | |
| [x] | BCLR | Bit clear (register/immediate) | |
| [x] | BSET | Bit set (register/immediate) | |

### Bit Field (68020+)

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | BFTST | Bit field test | |
| [x] | BFCHG | Bit field change | |
| [x] | BFCLR | Bit field clear | |
| [x] | BFSET | Bit field set | |
| [x] | BFEXTU | Bit field extract unsigned | |
| [x] | BFEXTS | Bit field extract signed | |
| [x] | BFFFO | Bit field find first one | |
| [x] | BFINS | Bit field insert | |

### BCD

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | ABCD | Add BCD | |
| [x] | SBCD | Subtract BCD | |
| [x] | NBCD | Negate BCD | |
| [x] | PACK | Pack BCD (68020+) | |
| [x] | UNPK | Unpack BCD (68020+) | |

### Program Control

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | BRA | Branch always (.B/.W/.L) | JIT (.B/.W) |
| [x] | Bcc | Branch on condition (.B/.W/.L), all 16 conditions | JIT (.B/.W) |
| [x] | BSR | Branch to subroutine (.B/.W/.L) | |
| [x] | DBcc | Decrement and branch, all 16 conditions | |
| [x] | Scc | Set byte on condition, all 16 conditions | |
| [x] | JMP | Jump | |
| [x] | JSR | Jump to subroutine | |
| [x] | RTS | Return from subroutine | JIT (bailout) |
| [x] | RTR | Return and restore CCR | |
| [x] | RTE | Return from exception (supervisor) | |
| [x] | RTD | Return with displacement (68010+) | |
| [x] | NOP | No operation | JIT |
| [x] | TRAPcc | Trap on condition (68020+) | |

### Register Manipulation

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | SWAP | Swap register halves | JIT |
| [x] | EXT.W | Sign-extend byte to word | JIT |
| [x] | EXT.L | Sign-extend word to long | JIT |
| [x] | EXTB.L | Sign-extend byte to long (68020+) | JIT |

### Stack & Address

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | PEA | Push effective address | |
| [x] | LEA | Load effective address | JIT ((An), d16(An), d8(An,Xn)) |
| [x] | LINK.W / .L | Link and allocate stack frame | |
| [x] | UNLK | Unlink stack frame | |

### Exception & System

| Status | Instruction | Description | JIT |
|--------|-------------|-------------|-----|
| [x] | TRAP | Software trap #0-#15 | |
| [x] | TRAPV | Trap on overflow | |
| [x] | CHK.W / .L | Check register against bounds | |
| [x] | ILLEGAL | Force illegal instruction exception | |
| [x] | RESET | Reset external devices (supervisor) | |
| [x] | STOP | Stop CPU (supervisor) | |
| [x] | TAS | Test and set (atomic) | |
| [x] | CAS | Compare and swap (68020+) | |
| [x] | CAS2 | Double compare and swap (68020+) | |

---

## FPU Instructions (MC68881/MC68882)

### Data Transfer

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FMOVE | Move FP register <-> memory/register |
| [x] | FMOVECR | Move constant ROM (pi, e, ln2, etc.) |
| [x] | FMOVEM | Move multiple FP registers |
| [x] | FMOVE to/from FPCR/FPSR/FPIAR | Move FPU control registers |

### Arithmetic

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FADD | Add |
| [x] | FSUB | Subtract |
| [x] | FMUL | Multiply |
| [x] | FDIV | Divide |
| [x] | FMOD | Modulo (IEEE remainder, dividend sign) |
| [x] | FREM | IEEE remainder |
| [x] | FABS | Absolute value |
| [x] | FNEG | Negate |
| [x] | FSQRT | Square root |
| [x] | FSCALE | Scale by power of 2 |
| [x] | FSGLDIV | Single-precision divide |
| [x] | FSGLMUL | Single-precision multiply |
| [x] | FINT | Round to integer |
| [x] | FINTRZ | Round to integer toward zero |
| [x] | FGETEXP | Get exponent |
| [x] | FGETMAN | Get mantissa |

### Trigonometric

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FSIN | Sine |
| [x] | FCOS | Cosine |
| [x] | FSINCOS | Sine and cosine (simultaneous) |
| [x] | FTAN | Tangent |
| [x] | FASIN | Arc sine |
| [x] | FACOS | Arc cosine |
| [x] | FATAN | Arc tangent |

### Hyperbolic

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FSINH | Hyperbolic sine |
| [x] | FCOSH | Hyperbolic cosine |
| [x] | FTANH | Hyperbolic tangent |
| [x] | FATANH | Hyperbolic arc tangent |

### Exponential & Logarithmic

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FETOX | e^x |
| [x] | FETOXM1 | e^x - 1 |
| [x] | FTWOTOX | 2^x |
| [x] | FTENTOX | 10^x |
| [x] | FLOGN | Natural logarithm |
| [x] | FLOGNP1 | ln(x+1) |
| [x] | FLOG10 | Base-10 logarithm |
| [x] | FLOG2 | Base-2 logarithm |

### Comparison & Branching

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FCMP | Compare |
| [x] | FTST | Test |
| [x] | FBcc | Branch on FPU condition (all 32 codes) |
| [x] | FScc | Set byte on FPU condition |
| [x] | FDBcc | Decrement and branch on FPU condition |
| [x] | FTRAPcc | Trap on FPU condition |

### FPU State

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | FSAVE | Save FPU state (supervisor) |
| [x] | FRESTORE | Restore FPU state (supervisor) |

---

## MMU Instructions (MC68030 On-chip)

| Status | Instruction | Description |
|--------|-------------|-------------|
| [x] | PMOVE | Move to/from MMU registers (TC, TT0, TT1, SRP, CRP, MMUSR) |
| [x] | PFLUSH | Flush TLB entries (by FC and mask) |
| [x] | PFLUSHA | Flush all TLB entries |
| [x] | PLOAD | Preload TLB entry (PLOADR/PLOADW) |
| [x] | PTEST | Test address translation |

---

## Not Implemented

| Instruction | Description | Notes |
|-------------|-------------|-------|
| Line-A (0xAxxx) | Line-A emulator trap | Raises exception 10 (by design) |
| CALLM / RTM | Module call/return (68020) | Removed in 68030; not needed |
| cpBcc / cpDBcc / cpScc / cpTRAPcc | Coprocessor branch/set (ID >= 2) | Raises exception 11 (Line-F) |
| LPSTOP | Low-power stop (68060) | 68060-only instruction |

---

## JIT Summary

JIT compiles basic blocks of instructions. Register-only blocks execute entirely in JIT.
Memory access blocks use a **data page cache bailout** mechanism: on cache hit, execution
continues in JIT; on cache miss, the block bails out to the interpreter at the current
instruction. Blocks that bail out too frequently (>64 times) are blacklisted and evicted.

~60 instruction patterns are supported:

| Category | Instructions |
|----------|-------------|
| Move (register) | MOVEQ, MOVE.L Dn->Dm, MOVE.L An->Dn, MOVEA.L Dn->An, MOVEA.L An->Am |
| Move (memory) | MOVE.L (An)->Dm, (An)+->Dm, Dm->(An), d16(An)->Dm, Dm->d16(An) (bailout) |
| Arithmetic | ADD/SUB/CMP .B/.W/.L (Dn,Dm), ADDQ/SUBQ .B/.W/.L (Dn, An), NEG .B/.W/.L Dn |
| Multiply | MULU.W, MULS.W (Dn,Dm) |
| Logic | AND/OR/EOR .B/.W/.L (Dn,Dm), NOT/CLR/TST .B/.W/.L Dn |
| Shift | ASL/ASR/LSL/LSR.L #imm,Dn |
| Bit | BTST Dn,Dm |
| Register | EXG (all 3 forms), SWAP Dn, EXT.W/EXT.L/EXTB.L Dn |
| Address | LEA (An)/d16(An)/d8(An,Xn),Ar |
| Branch | BRA .B/.W, Bcc .B/.W (all conditions) |
| Subroutine | RTS (bailout) |
| Other | NOP |

### C# JIT Implementation Details

- IL code generation via System.Reflection.Emit (DynamicMethod)
- Per-opcode cycle costs are summed at compile time and stored in CompiledBlock.TotalCycles
- Sampling at TickInterval (256), threshold = 16 hits to compile

#### Tuning Parameters

The following parameters are configurable via Settings → Performance → JIT:

- **Compile Threshold** (default: 16): The number of times a basic block must be sampled
  at the hot-block detection point before it is compiled. The emulator samples the current PC
  every TickInterval (256) instructions; when the same block address accumulates this many hits,
  compilation is triggered. A lower value compiles more aggressively (more blocks compiled sooner,
  but more compilation overhead). A higher value is more conservative (only the hottest loops
  are compiled). Since JIT currently has net negative performance impact (~3.5% MIPS slower),
  adjusting this value has limited practical effect.

- **Min Block Length** (default: 3): The minimum number of JIT-compilable instructions a basic
  block must contain to be worth caching. Blocks shorter than this threshold are immediately
  marked as uncompilable and will never be compiled, even if they are frequently executed.
  This avoids the overhead of compiling and dispatching very short blocks where the per-block
  setup cost (lookup, delegate call) would outweigh the benefit. Increasing this value reduces
  the number of compiled blocks; decreasing it allows more blocks to be compiled but may increase
  dispatch overhead for trivial blocks.
- ExecuteNextFast() and ExecuteNextFastJit() are completely separate methods (same separation
  design as C++)
- Method selection uses a bool branch. Func<bool> delegate dispatch caused a 36 to 30 MIPS
  regression (a constant-direction branch is essentially free thanks to branch prediction)
- Bailout mechanism: memory access instructions call `TryReadLongCached()` and communicate
  partial execution via `_jitExecutedCount`/`_jitExecutedCycles` side-channel fields.
  Blocks exceeding 64 bailouts are blacklisted.
- Tests: 327 total (including JIT-specific tests in Em68030.Tests/)

### Performance

| Mode | MHz (cycles) | MIPS | Notes |
|------|-------------|------|-------|
| JIT OFF | ~217 | ~32.1 | Baseline |
| JIT ON | ~190 | ~31.0 | -3.5% MIPS / -12.3% MHz overhead |

JIT ON is currently slower than JIT OFF (-3.5% MIPS, -12.3% MHz). NoInlining method call
overhead for ExecuteNextJit + DynamicMethod delegate dispatch cost for block.Execute()
outweigh the benefit given the rarity of compilable blocks. JIT is disabled by default.
