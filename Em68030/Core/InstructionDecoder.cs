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

public class InstructionDecoder
{
    private readonly MC68030 _cpu;
    private readonly FpuInstructionDecoder _fpuDecoder;
    private readonly Action<ushort>[] _dispatchTable;

    public InstructionDecoder(MC68030 cpu)
    {
        _cpu = cpu;
        _fpuDecoder = new FpuInstructionDecoder(cpu, cpu.Fpu);
        _dispatchTable = BuildDispatchTable();
    }

    private Action<ushort>[] BuildDispatchTable()
    {
        var table = new Action<ushort>[65536];

        // Fill with group-based dispatch as default
        for (int op = 0; op < 65536; op++)
        {
            int group = (op >> 12) & 0xF;
            table[op] = group switch
            {
                0x0 => DecodeGroup0,
                0x1 => op1 => DecodeMOVE(op1, 1),     // MOVE.B
                0x2 => op1 => DecodeMOVE(op1, 4),     // MOVE.L
                0x3 => op1 => DecodeMOVE(op1, 2),     // MOVE.W
                0x4 => DecodeGroup4,
                0x5 => DecodeGroup5,
                0x6 => DecodeGroup6,
                0x7 => DecodeMOVEQ,
                0x8 => DecodeGroup8,
                0x9 => DecodeGroup9,
                0xA => DecodeLineA,
                0xB => DecodeGroupB,
                0xC => DecodeGroupC,
                0xD => DecodeGroupD,
                0xE => DecodeGroupE,
                0xF => DecodeLineF,
                _ => DecodeGroup0 // unreachable
            };
        }

        // --- Specialized fast handlers ---

        // MOVEQ: 0x7000-0x7FFF where bit 8 == 0 (bit 8 set = illegal)
        for (int reg = 0; reg < 8; reg++)
        {
            for (int data = 0; data < 256; data++)
            {
                int op = 0x7000 | (reg << 9) | data;
                table[op] = FastMOVEQ;
            }
        }

        // MOVE.L Dn,Dm: group 2, src mode=0 (data reg), dst mode=0 (data reg)
        // Opcode: 0010_ddd_000_000_sss = 0x2000 | (dstReg<<9) | srcReg
        for (int dst = 0; dst < 8; dst++)
        {
            for (int src = 0; src < 8; src++)
            {
                int op = 0x2000 | (dst << 9) | src;
                table[op] = FastMOVE_L_Dn_Dm;
            }
        }

        // BRA.B: cond=0, 8-bit displacement != 0 and != -1 (0xFF)
        for (int disp = 1; disp < 255; disp++)
        {
            int op = 0x6000 | disp;
            table[op] = FastBRA_B;
        }

        // Bcc.B: group 6, 8-bit displacement != 0 and != -1 (0xFF)
        // Opcode: 0110_cccc_dddddddd
        for (int cond = 2; cond < 16; cond++) // skip BRA (0) and BSR (1)
        {
            for (int disp = 1; disp < 255; disp++) // skip 0 (word) and 0xFF (long)
            {
                int op = 0x6000 | (cond << 8) | disp;
                table[op] = FastBcc_B;
            }
        }

        // RTS: 0x4E75
        table[0x4E75] = FastRTS;

        // ADD.L Dn,Dm: group D, opMode=2 (size=L, EA->Dn), src mode=0 (Dn), not ADDX or ADDA
        // Opcode: 1101_ddd_010_000_sss
        for (int dst = 0; dst < 8; dst++)
        {
            for (int src = 0; src < 8; src++)
            {
                int op = 0xD080 | (dst << 9) | src;
                table[op] = FastADD_L_Dn_Dm;
            }
        }

        // SUB.L Dn,Dm (EA->Dn): group 9, opMode=2, src mode=0
        // Opcode: 1001_ddd_010_000_sss
        for (int dst = 0; dst < 8; dst++)
        {
            for (int src = 0; src < 8; src++)
            {
                int op = 0x9080 | (dst << 9) | src;
                table[op] = FastSUB_L_Dn_Dm;
            }
        }

        // CMP.L Dn,Dn: group B, opMode=2, src mode=0
        // Opcode: 1011_ddd_010_000_sss
        for (int dst = 0; dst < 8; dst++)
        {
            for (int src = 0; src < 8; src++)
            {
                int op = 0xB080 | (dst << 9) | src;
                table[op] = FastCMP_L_Dn_Dm;
            }
        }

        return table;
    }

    // --- Fast instruction handlers ---

    private void FastMOVEQ(ushort opcode)
    {
        if ((opcode & 0x0100) != 0) { _cpu.RaiseException(4); return; }
        int reg = (opcode >> 9) & 7;
        uint val = (uint)(int)(sbyte)(opcode & 0xFF);
        _cpu.D[reg] = val;
        // Inline NZ flags for longword (V=0, C=0)
        byte ccr = 0;
        if (val == 0) ccr = 0x04;
        else if ((val & 0x80000000) != 0) ccr = 0x08;
        _cpu.UpdateCCR(ccr, 0x0F);
    }

    private void FastMOVE_L_Dn_Dm(ushort opcode)
    {
        int src = opcode & 7;
        int dst = (opcode >> 9) & 7;
        uint val = _cpu.D[src];
        _cpu.D[dst] = val;
        byte ccr = 0;
        if (val == 0) ccr = 0x04;
        else if ((val & 0x80000000) != 0) ccr = 0x08;
        _cpu.UpdateCCR(ccr, 0x0F);
    }

    private void FastBRA_B(ushort opcode)
    {
        int disp8 = (sbyte)(opcode & 0xFF);
        _cpu.PC = (uint)(_cpu.PC + disp8);
    }

    private void FastBcc_B(ushort opcode)
    {
        int cond = (opcode >> 8) & 0xF;
        if (_cpu.EvaluateCondition(cond))
        {
            int disp8 = (sbyte)(opcode & 0xFF);
            // PC was advanced by FetchWord past the opcode; displacement is
            // relative to (opcode_address + 2), which IS the current PC.
            _cpu.PC = (uint)(_cpu.PC + disp8);
        }
    }

    private void FastRTS(ushort opcode)
    {

        _cpu.PC = _cpu.PopLong();
    }

    private void FastADD_L_Dn_Dm(ushort opcode)
    {
        int dst = (opcode >> 9) & 7;
        int src = opcode & 7;
        uint a = _cpu.D[dst];
        uint b = _cpu.D[src];
        var (result, ccr) = Alu.AddLong(a, b, _cpu.CCR);
        _cpu.D[dst] = result;
        _cpu.SetCCR(ccr);
    }

    private void FastSUB_L_Dn_Dm(ushort opcode)
    {
        int dst = (opcode >> 9) & 7;
        int src = opcode & 7;
        uint a = _cpu.D[dst];
        uint b = _cpu.D[src];
        var (result, ccr) = Alu.SubLong(a, b, _cpu.CCR);
        _cpu.D[dst] = result;
        _cpu.SetCCR(ccr);
    }

    private void FastCMP_L_Dn_Dm(ushort opcode)
    {
        int dst = (opcode >> 9) & 7;
        int src = opcode & 7;
        var (_, ccr) = Alu.SubLong(_cpu.D[dst], _cpu.D[src], _cpu.CCR);
        _cpu.UpdateCCR(ccr, 0x0F);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public ushort ExecuteNext()
    {
        ushort opcode = _cpu.FetchWord();
        _dispatchTable[opcode](opcode);
        return opcode;
    }

    // ====================================================================
    // Cycle table — approximate MC68030 cycle counts per opcode
    // ====================================================================

    private static readonly byte[] s_cycleTable = InitCycleTable();

    public static byte GetCycles(ushort opcode) => s_cycleTable[opcode];

    private static byte EaReadCost(int mode, int reg)
    {
        return mode switch
        {
            0 => 0, // Dn
            1 => 0, // An
            2 => 4, // (An)
            3 => 4, // (An)+
            4 => 4, // -(An)
            5 => 4, // d16(An)
            6 => 6, // d8(An,Xn)
            7 => reg switch
            {
                0 => 4, // abs.W
                1 => 8, // abs.L
                2 => 4, // d16(PC)
                3 => 6, // d8(PC,Xn)
                4 => 4, // #imm
                _ => 4,
            },
            _ => 4,
        };
    }

    private static byte EaWriteCost(int mode, int reg)
    {
        return mode switch
        {
            0 => 0, // Dn
            1 => 0, // An
            2 => 4, // (An)
            3 => 4, // (An)+
            4 => 4, // -(An)
            5 => 4, // d16(An)
            6 => 6, // d8(An,Xn)
            7 => reg switch
            {
                0 => 4, // abs.W
                1 => 8, // abs.L
                _ => 4,
            },
            _ => 4,
        };
    }

    private static byte[] InitCycleTable()
    {
        var table = new byte[65536];

        for (int op = 0; op < 65536; op++)
        {
            int group = (op >> 12) & 0xF;
            int srcMode = (op >> 3) & 7;
            int srcReg = op & 7;
            int cycles = 4; // default

            switch (group)
            {
                case 0x0: // ORI/ANDI/SUBI/ADDI/CMPI/EORI/Bit ops
                {
                    cycles = 4 + EaReadCost(srcMode, srcReg);
                    if (srcMode != 0 && srcMode != 1)
                        cycles += EaWriteCost(srcMode, srcReg);
                    break;
                }

                case 0x1: // MOVE.B
                case 0x2: // MOVE.L
                case 0x3: // MOVE.W
                {
                    int dmMode = (op >> 6) & 7;
                    int dmReg = (op >> 9) & 7;
                    cycles = 2 + EaReadCost(srcMode, srcReg) + EaWriteCost(dmMode, dmReg);
                    break;
                }

                case 0x4: // Misc group
                {
                    ushort opcode = (ushort)op;
                    if (opcode == 0x4E71) { cycles = 2; break; } // NOP
                    if (opcode == 0x4E75) { cycles = 10; break; } // RTS
                    if (opcode == 0x4E73) { cycles = 14; break; } // RTE
                    if (opcode == 0x4E70) { cycles = 255; break; } // RESET

                    if ((opcode & 0xFFF0) == 0x4E40) { cycles = 20; break; } // TRAP #n
                    if ((opcode & 0xFFF8) == 0x4E50) { cycles = 6; break; } // LINK.W
                    if ((opcode & 0xFFF8) == 0x4808) { cycles = 6; break; } // LINK.L
                    if ((opcode & 0xFFF8) == 0x4E58) { cycles = 6; break; } // UNLK
                    if ((opcode & 0xFFF8) == 0x4840) { cycles = 2; break; } // SWAP
                    if ((opcode & 0xFFF8) == 0x4880) { cycles = 2; break; } // EXT.W
                    if ((opcode & 0xFFF8) == 0x48C0) { cycles = 2; break; } // EXT.L
                    if ((opcode & 0xFFF8) == 0x49C0) { cycles = 2; break; } // EXTB.L

                    if ((opcode & 0xFFC0) == 0x4E80) { cycles = 8 + EaReadCost(srcMode, srcReg); break; } // JSR
                    if ((opcode & 0xFFC0) == 0x4EC0) { cycles = 4 + EaReadCost(srcMode, srcReg); break; } // JMP
                    if ((opcode & 0xF1C0) == 0x41C0) { cycles = 2 + EaReadCost(srcMode, srcReg); break; } // LEA
                    if ((opcode & 0xFFC0) == 0x4840 && srcMode != 0) { cycles = 6 + EaReadCost(srcMode, srcReg); break; } // PEA
                    if ((opcode & 0xFB80) == 0x4880) { cycles = 20; break; } // MOVEM
                    if ((opcode & 0xFFC0) == 0x4C00) { cycles = 44; break; } // MULU.L/MULS.L
                    if ((opcode & 0xFFC0) == 0x4C40) { cycles = 78; break; } // DIVU.L/DIVS.L
                    if ((opcode & 0xF040) == 0x4000 && ((opcode >> 7) & 3) >= 2) { cycles = 8; break; } // CHK

                    // CLR/NEG/NOT/NEGX/TST
                    int subOp = (opcode >> 8) & 0xF;
                    if (subOp == 0x2 || subOp == 0x4 || subOp == 0x6 || subOp == 0x0 || subOp == 0xA)
                    {
                        if (srcMode == 0) { cycles = 2; break; }
                        cycles = 2 + EaReadCost(srcMode, srcReg) + EaWriteCost(srcMode, srcReg);
                        break;
                    }

                    cycles = 4 + EaReadCost(srcMode, srcReg);
                    break;
                }

                case 0x5: // ADDQ/SUBQ/Scc/DBcc
                {
                    int sizeField = (op >> 6) & 3;
                    if (sizeField == 3)
                    {
                        if (srcMode == 1) { cycles = 6; break; } // DBcc
                        cycles = 2 + EaWriteCost(srcMode, srcReg); // Scc
                        break;
                    }
                    cycles = 2 + EaReadCost(srcMode, srcReg);
                    if (srcMode != 0 && srcMode != 1)
                        cycles += EaWriteCost(srcMode, srcReg);
                    break;
                }

                case 0x6: // Bcc/BRA/BSR
                {
                    int cond = (op >> 8) & 0xF;
                    cycles = (cond == 1) ? 8 : 6;
                    break;
                }

                case 0x7: cycles = 2; break; // MOVEQ

                case 0x8: // OR/DIVU/DIVS/SBCD
                {
                    int opMode = (op >> 6) & 7;
                    if (opMode == 3) { cycles = 38; break; } // DIVU.W
                    if (opMode == 7) { cycles = 38; break; } // DIVS.W
                    if (opMode == 4 && (srcMode == 0 || srcMode == 1)) { cycles = 4; break; } // SBCD
                    cycles = 2 + EaReadCost(srcMode, srcReg);
                    if (opMode >= 4 && opMode <= 6 && srcMode != 0)
                        cycles += EaWriteCost(srcMode, srcReg);
                    break;
                }

                case 0x9: // SUB/SUBA/SUBX
                {
                    int opMode = (op >> 6) & 7;
                    if ((opMode == 4 || opMode == 5 || opMode == 6) && (srcMode == 0 || srcMode == 1))
                    { cycles = 4; break; }
                    cycles = 2 + EaReadCost(srcMode, srcReg);
                    break;
                }

                case 0xA: cycles = 34; break; // Line-A

                case 0xB: // CMP/CMPA/EOR/CMPM
                {
                    int opMode = (op >> 6) & 7;
                    if ((opMode == 4 || opMode == 5 || opMode == 6) && srcMode == 1) { cycles = 12; break; } // CMPM
                    if (opMode == 4 || opMode == 5 || opMode == 6)
                    {
                        cycles = 2 + EaReadCost(srcMode, srcReg) + EaWriteCost(srcMode, srcReg); // EOR
                        break;
                    }
                    cycles = 2 + EaReadCost(srcMode, srcReg);
                    break;
                }

                case 0xC: // AND/MULU/MULS/EXG/ABCD
                {
                    int opMode = (op >> 6) & 7;
                    if (opMode == 3) { cycles = 28; break; } // MULU.W
                    if (opMode == 7) { cycles = 28; break; } // MULS.W
                    if (opMode == 4 && (srcMode == 0 || srcMode == 1)) { cycles = 4; break; } // ABCD
                    if (opMode == 5 && (srcMode == 0 || srcMode == 1)) { cycles = 4; break; } // EXG
                    if (opMode == 6 && srcMode == 1) { cycles = 4; break; } // EXG Dn↔An
                    cycles = 2 + EaReadCost(srcMode, srcReg);
                    if (opMode >= 4 && opMode <= 6 && srcMode != 0)
                        cycles += EaWriteCost(srcMode, srcReg);
                    break;
                }

                case 0xD: // ADD/ADDA/ADDX
                {
                    int opMode = (op >> 6) & 7;
                    if ((opMode == 4 || opMode == 5 || opMode == 6) && (srcMode == 0 || srcMode == 1))
                    { cycles = 4; break; }
                    cycles = 2 + EaReadCost(srcMode, srcReg);
                    break;
                }

                case 0xE: cycles = 4; break; // Shifts/Rotates
                case 0xF: cycles = 40; break; // FPU
            }

            table[op] = (byte)(cycles > 255 ? 255 : cycles);
        }

        return table;
    }

    // ====================================================================
    // Group 0: Bit operations, MOVEP, Immediate operations
    // ====================================================================
    private void DecodeGroup0(ushort opcode)
    {

        int reg = (opcode >> 9) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if ((opcode & 0x0100) != 0)
        {
            // Bit operations with register
            if (mode == 1)
            {
                // MOVEP
                DecodeMOVEP(opcode);
                return;
            }
            int bitNum = (int)(_cpu.D[reg] % 32);
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            int size = (eaMode == AddressingMode.DataRegDirect) ? 4 : 1;

            switch ((opcode >> 6) & 3)
            {
                case 0: // BTST
                    {
                        uint val = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
                        if (size == 1) bitNum %= 8;
                        _cpu.FlagZ = (val & (1u << bitNum)) == 0;
                    }
                    break;
                case 1: // BCHG
                    {
                        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                        if (size == 1) bitNum %= 8;
                        _cpu.FlagZ = (val & (1u << bitNum)) == 0;
                        val ^= (1u << bitNum);
                        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, val);
                    }
                    break;
                case 2: // BCLR
                    {
                        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                        if (size == 1) bitNum %= 8;
                        _cpu.FlagZ = (val & (1u << bitNum)) == 0;
                        val &= ~(1u << bitNum);
                        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, val);
                    }
                    break;
                case 3: // BSET
                    {
                        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                        if (size == 1) bitNum %= 8;
                        _cpu.FlagZ = (val & (1u << bitNum)) == 0;
                        val |= (1u << bitNum);
                        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, val);
                    }
                    break;
            }
        }
        else
        {
            // Immediate operations or static bit operations
            switch (reg)
            {
                case 0: // ORI or CMP2/CHK2.B
                    if (((opcode >> 6) & 3) == 3) DecodeCMP2_CHK2(opcode);
                    else DecodeORI(opcode);
                    break;
                case 1: // ANDI or CMP2/CHK2.W
                    if (((opcode >> 6) & 3) == 3) DecodeCMP2_CHK2(opcode);
                    else DecodeANDI(opcode);
                    break;
                case 2: // SUBI or CMP2/CHK2.L
                    if (((opcode >> 6) & 3) == 3) DecodeCMP2_CHK2(opcode);
                    else DecodeSUBI(opcode);
                    break;
                case 3: // ADDI
                    DecodeADDI(opcode);
                    break;
                case 4: // BTST/BCHG/BCLR/BSET #imm
                    DecodeStaticBit(opcode, (opcode >> 6) & 3);
                    break;
                case 5: // EORI or CAS.B
                    if (((opcode >> 6) & 3) == 3)
                        DecodeCAS(opcode);
                    else
                        DecodeEORI(opcode);
                    break;
                case 6: // CMPI or CAS.W/CAS2.W
                    if (((opcode >> 6) & 3) == 3)
                        DecodeCAS(opcode);
                    else
                        DecodeCMPI(opcode);
                    break;
                case 7: // MOVES or CAS.L/CAS2.L
                    if (((opcode >> 6) & 3) == 3)
                        DecodeCAS(opcode);
                    else
                        DecodeMOVES(opcode);
                    break;
            }
        }
    }

    private void DecodeMOVEP(ushort opcode)
    {
        int dataReg = (opcode >> 9) & 7;
        int addrReg = opcode & 7;
        short disp = (short)_cpu.FetchWord();
        uint addr = (uint)(_cpu.A[addrReg] + disp);
        int opMode = (opcode >> 6) & 7;

        switch (opMode)
        {
            case 4: // MOVEP.W (d,An),Dn - memory to register word
                {
                    byte hi = _cpu.ReadByte(addr);
                    byte lo = _cpu.ReadByte(addr + 2);
                    _cpu.D[dataReg] = (_cpu.D[dataReg] & 0xFFFF0000) | (uint)(hi << 8) | lo;
                }
                break;
            case 5: // MOVEP.L (d,An),Dn - memory to register long
                {
                    byte b3 = _cpu.ReadByte(addr);
                    byte b2 = _cpu.ReadByte(addr + 2);
                    byte b1 = _cpu.ReadByte(addr + 4);
                    byte b0 = _cpu.ReadByte(addr + 6);
                    _cpu.D[dataReg] = (uint)(b3 << 24) | (uint)(b2 << 16) | (uint)(b1 << 8) | b0;
                }
                break;
            case 6: // MOVEP.W Dn,(d,An) - register to memory word
                {
                    _cpu.WriteByte(addr, (byte)(_cpu.D[dataReg] >> 8));
                    _cpu.WriteByte(addr + 2, (byte)_cpu.D[dataReg]);
                }
                break;
            case 7: // MOVEP.L Dn,(d,An) - register to memory long
                {
                    _cpu.WriteByte(addr, (byte)(_cpu.D[dataReg] >> 24));
                    _cpu.WriteByte(addr + 2, (byte)(_cpu.D[dataReg] >> 16));
                    _cpu.WriteByte(addr + 4, (byte)(_cpu.D[dataReg] >> 8));
                    _cpu.WriteByte(addr + 6, (byte)_cpu.D[dataReg]);
                }
                break;
        }
    }

    private void DecodeORI(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        // ORI: destination must be data alterable — An (mode 1) is invalid
        if (mode == 1 || (mode == 7 && reg >= 2 && reg != 4))
        {
            _cpu.PC = _cpu._lastPC;
            _cpu.RaiseException(4);
            return;
        }
        int size = GetSize2(opcode);

        if (mode == 7 && reg == 4)
        {
            // ORI to CCR/SR
            ushort imm = _cpu.FetchWord();
            if (size == 1)
                _cpu.CCR |= (byte)(imm & 0xFF);
            else if (size == 2 && _cpu.SupervisorMode)
                _cpu.SetSR((ushort)(_cpu.SR | imm));
            return;
        }

        uint immVal = ReadImmediate(size);
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
        uint result = val | immVal;
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        SetLogicFlags(result, size);
    }

    private void DecodeANDI(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        // ANDI: destination must be data alterable — An (mode 1) is invalid
        if (mode == 1 || (mode == 7 && reg >= 2 && reg != 4))
        {
            _cpu.PC = _cpu._lastPC;
            _cpu.RaiseException(4);
            return;
        }
        int size = GetSize2(opcode);

        if (mode == 7 && reg == 4)
        {
            ushort imm = _cpu.FetchWord();
            if (size == 1)
                _cpu.CCR &= (byte)(imm & 0xFF);
            else if (size == 2 && _cpu.SupervisorMode)
                _cpu.SetSR((ushort)(_cpu.SR & imm));
            return;
        }

        uint immVal = ReadImmediate(size);
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
        uint result = val & immVal;
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        SetLogicFlags(result, size);
    }

    private void DecodeEORI(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        // EORI: destination must be data alterable — An (mode 1) is invalid
        if (mode == 1 || (mode == 7 && reg >= 2 && reg != 4))
        {
            _cpu.PC = _cpu._lastPC;
            _cpu.RaiseException(4);
            return;
        }
        int size = GetSize2(opcode);

        if (mode == 7 && reg == 4)
        {
            ushort imm = _cpu.FetchWord();
            if (size == 1)
                _cpu.CCR ^= (byte)(imm & 0xFF);
            else if (size == 2 && _cpu.SupervisorMode)
                _cpu.SetSR((ushort)(_cpu.SR ^ imm));
            return;
        }

        uint immVal = ReadImmediate(size);
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
        uint result = val ^ immVal;
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        SetLogicFlags(result, size);
    }

    private void DecodeSUBI(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        // SUBI: destination must be data alterable — An (mode 1) is invalid
        if (mode == 1 || (mode == 7 && reg >= 2))
        {
            _cpu.PC = _cpu._lastPC; // restore PC to point to the illegal opcode
            _cpu.RaiseException(4);
            return;
        }
        int size = GetSize2(opcode);
        uint immVal = ReadImmediate(size);
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);

        byte ccr;
        uint result;
        switch (size)
        {
            case 1:
                (var rb, ccr) = Alu.SubByte((byte)val, (byte)immVal, _cpu.CCR);
                result = rb;
                break;
            case 2:
                (var rw, ccr) = Alu.SubWord((ushort)val, (ushort)immVal, _cpu.CCR);
                result = rw;
                break;
            default:
                (result, ccr) = Alu.SubLong(val, immVal, _cpu.CCR);
                break;
        }
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        _cpu.SetCCR(ccr);
    }

    private void DecodeADDI(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        // ADDI: destination must be data alterable — An (mode 1) is invalid
        if (mode == 1 || (mode == 7 && reg >= 2))
        {
            _cpu.PC = _cpu._lastPC;
            _cpu.RaiseException(4);
            return;
        }
        int size = GetSize2(opcode);
        uint immVal = ReadImmediate(size);
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);

        byte ccr;
        uint result;
        switch (size)
        {
            case 1:
                (var rb, ccr) = Alu.AddByte((byte)val, (byte)immVal, _cpu.CCR);
                result = rb;
                break;
            case 2:
                (var rw, ccr) = Alu.AddWord((ushort)val, (ushort)immVal, _cpu.CCR);
                result = rw;
                break;
            default:
                (result, ccr) = Alu.AddLong(val, immVal, _cpu.CCR);
                break;
        }
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        _cpu.SetCCR(ccr);
    }

    private void DecodeCMPI(ushort opcode)
    {
        int size = GetSize2(opcode);
        uint immVal = ReadImmediate(size);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);

        byte ccr;
        switch (size)
        {
            case 1:
                (_, ccr) = Alu.SubByte((byte)val, (byte)immVal, _cpu.CCR);
                break;
            case 2:
                (_, ccr) = Alu.SubWord((ushort)val, (ushort)immVal, _cpu.CCR);
                break;
            default:
                (_, ccr) = Alu.SubLong(val, immVal, _cpu.CCR);
                break;
        }
        _cpu.UpdateCCR(ccr, 0x0F); // Don't update X
    }

    private void DecodeStaticBit(ushort opcode, int op)
    {
        int bitNum = _cpu.FetchWord() & 0xFF;
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        int size = (eaMode == AddressingMode.DataRegDirect) ? 4 : 1;
        if (size == 1) bitNum %= 8;
        else bitNum %= 32;

        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
        _cpu.FlagZ = (val & (1u << bitNum)) == 0;

        switch (op)
        {
            case 1: val ^= (1u << bitNum); EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, val); break;
            case 2: val &= ~(1u << bitNum); EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, val); break;
            case 3: val |= (1u << bitNum); EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, val); break;
        }
    }

    private void DecodeCMP2_CHK2(ushort opcode)
    {
        // CMP2/CHK2 - Compare/Check register against bounds
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort ext = _cpu.FetchWord();
        int size = GetSize2(opcode);
        int rn = (ext >> 12) & 0xF;
        bool isChk = (ext & 0x0800) != 0;

        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, size);
        uint lower, upper;
        if (size == 1)
        {
            lower = _cpu.ReadByte(addr);
            upper = _cpu.ReadByte(addr + 1);
        }
        else if (size == 2)
        {
            lower = _cpu.ReadWord(addr);
            upper = _cpu.ReadWord(addr + 2);
        }
        else
        {
            lower = _cpu.ReadLong(addr);
            upper = _cpu.ReadLong(addr + 4);
        }

        uint val = rn < 8 ? _cpu.D[rn] : _cpu.A[rn - 8];
        if (size == 1) val &= 0xFF;
        else if (size == 2) val &= 0xFFFF;

        _cpu.FlagZ = (val == lower || val == upper);
        _cpu.FlagC = (val < lower || val > upper);

        if (isChk && _cpu.FlagC)
            _cpu.RaiseException(6); // CHK exception
    }

    private void DecodeMOVES(ushort opcode)
    {
        if (!_cpu.SupervisorMode)
        {
            _cpu.RaiseException(8); // Privilege violation
            return;
        }
        int size = GetSize2(opcode);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort ext = _cpu.FetchWord();
        int rn = (ext >> 12) & 0xF;
        bool toMem = (ext & 0x0800) != 0;

        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);

        // MOVES uses DFC for writes to memory and SFC for reads from memory.
        // This allows supervisor code to access user address space (e.g. copyout).
        int overrideFC = toMem ? (int)(_cpu.DFC & 7) : (int)(_cpu.SFC & 7);
        // Invalidate data page cache before FC override (cache entry is for current FC)
        _cpu.InvalidateDataCache();
        try
        {
            if (toMem)
            {
                _cpu.FunctionCodeOverride = overrideFC;
                uint val = rn < 8 ? _cpu.D[rn] : _cpu.A[rn - 8];
                EffectiveAddress.WriteValue(_cpu, eaMode, eaR, size, val);
            }
            else
            {
                _cpu.FunctionCodeOverride = overrideFC;
                uint val = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
                if (rn < 8)
                {
                    switch (size)
                    {
                        case 1: _cpu.D[rn] = (_cpu.D[rn] & 0xFFFFFF00) | (val & 0xFF); break;
                        case 2: _cpu.D[rn] = (_cpu.D[rn] & 0xFFFF0000) | (val & 0xFFFF); break;
                        default: _cpu.D[rn] = val; break;
                    }
                }
                else
                {
                    // Word-size loads to address registers must be sign-extended (same as MOVEA.W)
                    _cpu.A[rn - 8] = (size == 2) ? (uint)(int)(short)(ushort)val : val;
                }
            }
        }
        catch (BusErrorException)
        {
            throw; // Re-throw so HandleBusError processes it normally
        }
        finally
        {
            _cpu.FunctionCodeOverride = -1;
        }
    }

    // ====================================================================
    // MOVE (Groups 1, 2, 3)
    // ====================================================================
    private void DecodeMOVE(ushort opcode, int size)
    {
        int srcMode = (opcode >> 3) & 7;
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;
        int dstMode = (opcode >> 6) & 7;

        var (srcEaMode, srcEaR) = EffectiveAddress.Decode(srcMode, srcReg);
        uint value = EffectiveAddress.ReadValue(_cpu, srcEaMode, srcEaR, size);

        // Check for MOVEA
        if (dstMode == 1)
        {
            // MOVEA - no flags affected
            _cpu.A[dstReg] = size == 2 ? (uint)(int)(short)(ushort)value : value;
            return;
        }

        var (dstEaMode, dstEaR) = EffectiveAddress.Decode(dstMode, dstReg);
        EffectiveAddress.WriteValue(_cpu, dstEaMode, dstEaR, size, value);
        SetLogicFlags(value, size);
    }

    // ====================================================================
    // Group 4: Miscellaneous
    // ====================================================================
    private void DecodeGroup4(ushort opcode)
    {

        int op = (opcode >> 8) & 0xF;

        // MOVE from SR
        if ((opcode & 0xFFC0) == 0x40C0)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            EffectiveAddress.WriteValue(_cpu, eaMode, eaR, 2, _cpu.SR);
            return;
        }

        // MOVE to CCR
        if ((opcode & 0xFFC0) == 0x44C0)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint val = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2);
            _cpu.CCR = (byte)(val & 0xFF);
            return;
        }

        // MOVE to SR
        if ((opcode & 0xFFC0) == 0x46C0)
        {
            if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint val = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2);
            _cpu.SetSR((ushort)val);
            return;
        }

        // NEGX
        if ((opcode & 0xFF00) == 0x4000 && ((opcode >> 6) & 3) != 3)
        {
            DecodeNEGX(opcode);
            return;
        }

        // CLR
        if ((opcode & 0xFF00) == 0x4200 && ((opcode >> 6) & 3) != 3)
        {
            DecodeCLR(opcode);
            return;
        }

        // NEG
        if ((opcode & 0xFF00) == 0x4400 && ((opcode >> 6) & 3) != 3)
        {
            DecodeNEG(opcode);
            return;
        }

        // NOT
        if ((opcode & 0xFF00) == 0x4600 && ((opcode >> 6) & 3) != 3)
        {
            DecodeNOT(opcode);
            return;
        }

        // EXT / EXTB
        if ((opcode & 0xFFF8) == 0x4880) { ExtWord((opcode) & 7); return; }
        if ((opcode & 0xFFF8) == 0x48C0) { ExtLong((opcode) & 7); return; }
        if ((opcode & 0xFFF8) == 0x49C0) { ExtByteLong((opcode) & 7); return; } // EXTB.L (68020+)

        // SWAP
        if ((opcode & 0xFFF8) == 0x4840)
        {
            int reg = opcode & 7;
            _cpu.D[reg] = (_cpu.D[reg] >> 16) | (_cpu.D[reg] << 16);
            SetLogicFlags(_cpu.D[reg], 4);
            return;
        }

        // PEA
        if ((opcode & 0xFFC0) == 0x4840)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
            _cpu.PushLong(addr);
            return;
        }

        // TST
        if ((opcode & 0xFF00) == 0x4A00 && ((opcode >> 6) & 3) != 3)
        {
            int size = GetSize2(opcode);
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint val = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
            SetLogicFlags(val, size);
            return;
        }

        // TAS
        if ((opcode & 0xFFC0) == 0x4AC0)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, 1);
            SetLogicFlags(val, 1);
            val |= 0x80;
            EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, 1, val);
            return;
        }

        // ILLEGAL
        if (opcode == 0x4AFC)
        {
            _cpu.RaiseException(4);
            return;
        }

        // MOVEM
        if ((opcode & 0xFB80) == 0x4880)
        {
            DecodeMOVEM(opcode);
            return;
        }

        // LEA
        if ((opcode & 0xF1C0) == 0x41C0)
        {
            int areg = (opcode >> 9) & 7;
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
            _cpu.A[areg] = addr;
            return;
        }

        // CHK
        if ((opcode & 0xF1C0) == 0x4180)
        {
            int dreg = (opcode >> 9) & 7;
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint bound = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2);
            short val = (short)(ushort)_cpu.D[dreg];
            if (val < 0 || val > (short)(ushort)bound)
            {
                _cpu.FlagN = val < 0;
                _cpu.RaiseException(6);
            }
            return;
        }

        // LINK / UNLK
        if ((opcode & 0xFFF8) == 0x4E50) { DecodeLINK(opcode, false); return; }
        if ((opcode & 0xFFF8) == 0x4808) { DecodeLINK(opcode, true); return; } // LINK.L (68020+)
        if ((opcode & 0xFFF8) == 0x4E58) { DecodeUNLK(opcode); return; }

        // MOVE USP
        if ((opcode & 0xFFF0) == 0x4E60)
        {
            if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
            int areg = opcode & 7;
            if ((opcode & 0x0008) != 0)
                _cpu.A[areg] = _cpu.USP; // MOVE USP,An: read from USP
            else
                _cpu.USP = _cpu.A[areg]; // MOVE An,USP: write to USP
            return;
        }

        // RESET — asserts RSTO to reset external devices; CPU state is NOT modified
        if (opcode == 0x4E70) { if (_cpu.SupervisorMode) { _cpu.DiagnosticOutput?.Invoke($"\n[EMU] RESET instruction at PC=${_cpu.PC - 2:X8}\n"); _cpu.OnResetInstruction?.Invoke(); } else _cpu.RaiseException(8); return; }

        // NOP
        if (opcode == 0x4E71) return;

        // STOP
        if (opcode == 0x4E72)
        {
            if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
            ushort imm = _cpu.FetchWord();
            _cpu.SetSR(imm);
            _cpu.Stopped = true;
            _cpu.StopReason = "STOP instruction";
            return;
        }

        // RTE
        if (opcode == 0x4E73)
        {
            if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
            ushort newSR = _cpu.PopWord();
            uint newPC = _cpu.PopLong();
            ushort frameWord = _cpu.PopWord();
            int format = (frameWord >> 12) & 0xF;
            // Skip extra frame data based on format
            switch (format)
            {
                case 0x0: break;                    // Format 0: 4-word frame
                case 0x1: break;                    // Format 1: throwaway (68010)
                case 0x2: _cpu.A[7] += 4; break;   // Format 2: 6-word + instruction address
                case 0x9: _cpu.A[7] += 12; break;  // Format 9: coprocessor mid-instruction
                case 0xA: _cpu.A[7] += 24; break;  // Format A: short bus fault (16 words total)
                case 0xB: _cpu.A[7] += 84; break;  // Format B: long bus fault
                default:
                    _cpu.RaiseException(14); // Format error
                    return;
            }
            // Restore SR with proper USP/SSP swap
            _cpu.SetSR(newSR);
            _cpu.PC = newPC;
            return;
        }

        // RTD (68010+)
        if (opcode == 0x4E74)
        {
            short disp = (short)_cpu.FetchWord();
            _cpu.PC = _cpu.PopLong();
            _cpu.A[7] += (uint)disp;
            return;
        }

        // RTS
        if (opcode == 0x4E75)
        {
            _cpu.PC = _cpu.PopLong();
            return;
        }

        // RTR
        if (opcode == 0x4E77)
        {
            _cpu.CCR = (byte)_cpu.PopWord();
            _cpu.PC = _cpu.PopLong();
            return;
        }

        // MOVEC (68010+)
        if ((opcode & 0xFFFE) == 0x4E7A)
        {
            DecodeMOVEC(opcode);
            return;
        }

        // TRAP
        if ((opcode & 0xFFF0) == 0x4E40)
        {
            int vector = opcode & 0xF;
            _cpu.RaiseTrap(vector);
            return;
        }

        // JSR
        if ((opcode & 0xFFC0) == 0x4E80)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
            _cpu.PushLong(_cpu.PC);
            _cpu.PC = addr;
            return;
        }

        // JMP
        if ((opcode & 0xFFC0) == 0x4EC0)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
            _cpu.PC = addr;
            return;
        }

        // NBCD
        if ((opcode & 0xFFC0) == 0x4800)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            byte val = (byte)EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, 1);
            var (result, ccr) = Alu.SubBcd(val, 0, _cpu.CCR);
            EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, 1, result);
            _cpu.SetCCR(ccr);
            return;
        }

        // MULS.L / MULU.L (68020+)
        if ((opcode & 0xFFC0) == 0x4C00)
        {
            DecodeLongMul(opcode);
            return;
        }

        // DIVS.L / DIVU.L (68020+)
        if ((opcode & 0xFFC0) == 0x4C40)
        {
            DecodeLongDiv(opcode);
            return;
        }

        // Unimplemented
        _cpu.RaiseException(4); // Illegal instruction
    }

    private void DecodeNEGX(ushort opcode)
    {
        int size = GetSize2(opcode);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);

        byte ccr;
        uint result;
        switch (size)
        {
            case 1:
                (var rb, ccr) = Alu.SubByte(0, (byte)val, _cpu.CCR, true);
                result = rb;
                break;
            case 2:
                (var rw, ccr) = Alu.SubWord(0, (ushort)val, _cpu.CCR, true);
                result = rw;
                break;
            default:
                (result, ccr) = Alu.SubLong(0, val, _cpu.CCR, true);
                break;
        }

        // NEGX preserves Z if result is zero
        if ((ccr & 0x04) != 0) ccr = (byte)((ccr & ~0x04) | (_cpu.CCR & 0x04));
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        _cpu.SetCCR(ccr);
    }

    private void DecodeCLR(ushort opcode)
    {
        int size = GetSize2(opcode);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        EffectiveAddress.WriteValue(_cpu, eaMode, eaR, size, 0);
        _cpu.CCR = (byte)((_cpu.CCR & 0x10) | 0x04); // Z=1, N=V=C=0, X unchanged
    }

    private void DecodeNEG(ushort opcode)
    {
        int size = GetSize2(opcode);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);

        byte ccr;
        uint result;
        switch (size)
        {
            case 1:
                (var rb, ccr) = Alu.SubByte(0, (byte)val, _cpu.CCR);
                result = rb;
                break;
            case 2:
                (var rw, ccr) = Alu.SubWord(0, (ushort)val, _cpu.CCR);
                result = rw;
                break;
            default:
                (result, ccr) = Alu.SubLong(0, val, _cpu.CCR);
                break;
        }
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        _cpu.SetCCR(ccr);
    }

    private void DecodeNOT(ushort opcode)
    {
        int size = GetSize2(opcode);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
        uint result = ~val;
        EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
        SetLogicFlags(result, size);
    }

    private void ExtWord(int reg)
    {
        short val = (short)(sbyte)(byte)_cpu.D[reg];
        _cpu.D[reg] = (_cpu.D[reg] & 0xFFFF0000) | (uint)(ushort)val;
        SetLogicFlags((uint)(ushort)val, 2);
    }

    private void ExtLong(int reg)
    {
        int val = (short)(ushort)_cpu.D[reg];
        _cpu.D[reg] = (uint)val;
        SetLogicFlags(_cpu.D[reg], 4);
    }

    private void ExtByteLong(int reg)
    {
        int val = (sbyte)(byte)_cpu.D[reg];
        _cpu.D[reg] = (uint)val;
        SetLogicFlags(_cpu.D[reg], 4);
    }

    private void DecodeMOVEM(ushort opcode)
    {
        bool isLong = (opcode & 0x0040) != 0;
        int size = isLong ? 4 : 2;
        bool toRegs = (opcode & 0x0400) != 0;
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort mask = _cpu.FetchWord();

        if (toRegs)
        {
            // Memory to registers
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr;
            if (eaMode == AddressingMode.AddrRegPostInc)
                addr = _cpu.A[eaR];
            else
                addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, size);

            for (int i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    if (i < 8)
                    {
                        _cpu.D[i] = isLong ? _cpu.ReadLong(addr) : (uint)(int)(short)_cpu.ReadWord(addr);
                    }
                    else
                    {
                        _cpu.A[i - 8] = isLong ? _cpu.ReadLong(addr) : (uint)(int)(short)_cpu.ReadWord(addr);
                    }
                    addr += (uint)size;
                }
            }
            if (eaMode == AddressingMode.AddrRegPostInc)
                _cpu.A[eaR] = addr;
        }
        else
        {
            // Registers to memory
            if (mode == 4) // -(An) predecrement
            {
                uint addr = _cpu.A[reg];
                for (int i = 0; i < 16; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        addr -= (uint)size;
                        // bit 0 = A7, bit 7 = A0, bit 8 = D7, bit 15 = D0
                        int realReg = 15 - i;
                        uint val;
                        if (realReg < 8)
                            val = _cpu.D[realReg];
                        else if (realReg - 8 == reg)
                            // MC68020/30/40: the base register value saved is
                            // the initial value minus the operand size
                            val = _cpu.A[reg] - (uint)size;
                        else
                            val = _cpu.A[realReg - 8];
                        if (isLong) _cpu.WriteLong(addr, val);
                        else _cpu.WriteWord(addr, (ushort)val);
                    }
                }
                _cpu.A[reg] = addr;
            }
            else
            {
                var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
                uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, size);

                for (int i = 0; i < 16; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        uint val = (i < 8) ? _cpu.D[i] : _cpu.A[i - 8];
                        if (isLong) _cpu.WriteLong(addr, val);
                        else _cpu.WriteWord(addr, (ushort)val);
                        addr += (uint)size;
                    }
                }
            }
        }
    }

    private void DecodeLINK(ushort opcode, bool longDisp)
    {
        int reg = opcode & 7;
        _cpu.PushLong(_cpu.A[reg]);
        _cpu.A[reg] = _cpu.A[7];
        if (longDisp)
        {
            int disp = (int)_cpu.FetchLong();
            _cpu.A[7] = (uint)(_cpu.A[7] + disp);
        }
        else
        {
            short disp = (short)_cpu.FetchWord();
            _cpu.A[7] = (uint)(_cpu.A[7] + disp);
        }
    }

    private void DecodeUNLK(ushort opcode)
    {
        int reg = opcode & 7;
        _cpu.A[7] = _cpu.A[reg];
        _cpu.A[reg] = _cpu.PopLong();
    }

    private void DecodeMOVEC(ushort opcode)
    {
        if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
        ushort ext = _cpu.FetchWord();
        int rn = (ext >> 12) & 0xF;
        int creg = ext & 0xFFF;
        // 0x4E7A = MOVEC Rc,Rn (read control reg), 0x4E7B = MOVEC Rn,Rc (write control reg)
        bool toCtrl = (opcode & 1) != 0;

        if (toCtrl)
        {
            uint val = rn < 8 ? _cpu.D[rn] : _cpu.A[rn - 8];
            switch (creg)
            {
                case 0x000: _cpu.SFC = val & 7; break;
                case 0x001: _cpu.DFC = val & 7; break;
                case 0x002: _cpu.CACR = val; break;
                case 0x800: _cpu.USP = val; break;
                case 0x801: _cpu.VBR = val; break;
                case 0x802: _cpu.CAAR = val; break;
                case 0x803: /* MSP */ break;
                case 0x804: /* ISP */ break;
            }
        }
        else
        {
            uint val = creg switch
            {
                0x000 => _cpu.SFC,
                0x001 => _cpu.DFC,
                0x002 => _cpu.CACR,
                0x800 => _cpu.USP,
                0x801 => _cpu.VBR,
                0x802 => _cpu.CAAR,
                _ => 0
            };
            if (rn < 8) _cpu.D[rn] = val;
            else _cpu.A[rn - 8] = val;
        }
    }

    // ====================================================================
    // Group 5: ADDQ/SUBQ/Scc/DBcc/TRAPcc
    // ====================================================================
    private void DecodeGroup5(ushort opcode)
    {

        int sizeField = (opcode >> 6) & 3;

        if (sizeField == 3)
        {
            // Scc / DBcc / TRAPcc
            int cond = (opcode >> 8) & 0xF;
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;

            if (mode == 1)
            {
                // DBcc
                bool cc = _cpu.EvaluateCondition(cond);
                short disp = (short)_cpu.FetchWord();
                if (!cc)
                {
                    short count = (short)(ushort)(_cpu.D[reg] & 0xFFFF);
                    count--;
                    _cpu.D[reg] = (_cpu.D[reg] & 0xFFFF0000) | (uint)(ushort)count;
                    if (count != -1)
                        _cpu.PC = (uint)(_cpu.PC - 2 + disp);
                }
                return;
            }

            if (mode == 7 && reg >= 2 && reg <= 4)
            {
                // TRAPcc
                bool cc = _cpu.EvaluateCondition(cond);
                if (reg == 2) _cpu.FetchWord(); // word operand
                else if (reg == 3) _cpu.FetchLong(); // long operand
                // reg == 4: no operand
                if (cc) _cpu.RaiseException(7); // TRAPV
                return;
            }

            // Scc
            {
                bool cc = _cpu.EvaluateCondition(cond);
                var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
                EffectiveAddress.WriteValue(_cpu, eaMode, eaR, 1, (uint)(cc ? 0xFF : 0x00));
            }
            return;
        }

        // ADDQ / SUBQ
        int data = (opcode >> 9) & 7;
        if (data == 0) data = 8;
        int size = sizeField switch { 0 => 1, 1 => 2, _ => 4 };
        int eaMode2 = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if ((opcode & 0x0100) == 0)
        {
            // ADDQ
            if (eaMode2 == 1)
            {
                // ADDQ to An - no flags affected
                _cpu.A[eaReg] += (uint)data;
                return;
            }
            var (eaMode, eaR) = EffectiveAddress.Decode(eaMode2, eaReg);
            uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
            byte ccr;
            uint result;
            switch (size)
            {
                case 1:
                    (var rb, ccr) = Alu.AddByte((byte)val, (byte)data, _cpu.CCR);
                    result = rb;
                    break;
                case 2:
                    (var rw, ccr) = Alu.AddWord((ushort)val, (ushort)data, _cpu.CCR);
                    result = rw;
                    break;
                default:
                    (result, ccr) = Alu.AddLong(val, (uint)data, _cpu.CCR);
                    break;
            }
            EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
            _cpu.SetCCR(ccr);
        }
        else
        {
            // SUBQ
            if (eaMode2 == 1)
            {
                _cpu.A[eaReg] -= (uint)data;
                return;
            }
            var (eaMode, eaR) = EffectiveAddress.Decode(eaMode2, eaReg);
            uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
            byte ccr;
            uint result;
            switch (size)
            {
                case 1:
                    (var rb, ccr) = Alu.SubByte((byte)val, (byte)data, _cpu.CCR);
                    result = rb;
                    break;
                case 2:
                    (var rw, ccr) = Alu.SubWord((ushort)val, (ushort)data, _cpu.CCR);
                    result = rw;
                    break;
                default:
                    (result, ccr) = Alu.SubLong(val, (uint)data, _cpu.CCR);
                    break;
            }
            EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
            _cpu.SetCCR(ccr);
        }
    }

    // ====================================================================
    // Group 6: Bcc/BSR/BRA
    // ====================================================================
    private void DecodeGroup6(ushort opcode)
    {

        int cond = (opcode >> 8) & 0xF;
        int disp8 = (sbyte)(opcode & 0xFF);
        uint savedPC = _cpu.PC;

        int displacement;
        if (disp8 == 0)
            displacement = (short)_cpu.FetchWord();
        else if (disp8 == -1)
            displacement = (int)_cpu.FetchLong();
        else
            displacement = disp8;

        uint targetPC = (uint)(savedPC + displacement);
        if (disp8 == 0) targetPC -= 2;
        if (disp8 == -1) targetPC -= 4;

        // Recalculate target: displacement is relative to the start of the displacement part
        // For byte displacement: relative to PC after opcode word
        // For word displacement: relative to extension word location
        targetPC = (uint)(savedPC + displacement);
        if (disp8 != 0 && disp8 != -1)
        {
            // 8-bit displacement, PC already advanced past opcode
            // savedPC points after opcode, displacement is relative to there
            // But actually relative to opcode+2 which is savedPC
            // No adjustment needed
        }

        switch (cond)
        {
            case 0: // BRA
                _cpu.PC = targetPC;
                break;
            case 1: // BSR
                _cpu.PushLong(_cpu.PC);
                _cpu.PC = targetPC;
                break;
            default: // Bcc
                if (_cpu.EvaluateCondition(cond))
                    _cpu.PC = targetPC;
                break;
        }
    }

    // ====================================================================
    // Group 7: MOVEQ
    // ====================================================================
    private void DecodeMOVEQ(ushort opcode)
    {

        if ((opcode & 0x0100) != 0) { _cpu.RaiseException(4); return; }
        int reg = (opcode >> 9) & 7;
        int data = (sbyte)(opcode & 0xFF);
        _cpu.D[reg] = (uint)data;
        SetLogicFlags(_cpu.D[reg], 4);
    }

    // ====================================================================
    // Group 8: OR/DIV/SBCD
    // ====================================================================
    private void DecodeGroup8(ushort opcode)
    {

        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        // DIVU.W
        if (opMode == 3)
        {
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2) & 0xFFFF;
            if (src == 0) { _cpu.RaiseException(5); return; } // Division by zero
            var (quot, rem, ccr, overflow) = Alu.DivUnsigned(_cpu.D[reg], (uint)(ushort)src);
            if (overflow) { _cpu.FlagV = true; return; }
            _cpu.D[reg] = (rem << 16) | (quot & 0xFFFF);
            _cpu.UpdateCCR(ccr, 0x0F);
            return;
        }

        // DIVS.W
        if (opMode == 7)
        {
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2);
            short divisor = (short)(ushort)src;
            if (divisor == 0) { _cpu.RaiseException(5); return; }
            var (quot, rem, ccr, overflow) = Alu.DivSigned((int)_cpu.D[reg], divisor);
            if (overflow) { _cpu.FlagV = true; return; }
            _cpu.D[reg] = (rem << 16) | (quot & 0xFFFF);
            _cpu.UpdateCCR(ccr, 0x0F);
            return;
        }

        // SBCD
        if (opMode == 4 && (mode == 0 || mode == 1))
        {
            byte src, dst;
            if (mode == 0)
            {
                src = (byte)_cpu.D[eaReg];
                dst = (byte)_cpu.D[reg];
                var (result, ccr) = Alu.SubBcd(src, dst, _cpu.CCR);
                _cpu.D[reg] = (_cpu.D[reg] & 0xFFFFFF00) | result;
                _cpu.SetCCR(ccr);
            }
            else
            {
                _cpu.A[eaReg]--;
                _cpu.A[reg]--;
                src = _cpu.ReadByte(_cpu.A[eaReg]);
                dst = _cpu.ReadByte(_cpu.A[reg]);
                var (result, ccr) = Alu.SubBcd(src, dst, _cpu.CCR);
                _cpu.WriteByte(_cpu.A[reg], result);
                _cpu.SetCCR(ccr);
            }
            return;
        }

        // PACK (68020+)
        if (opMode == 5 && (mode == 0 || mode == 1))
        {
            ushort adj = _cpu.FetchWord();
            uint src;
            if (mode == 0)
                src = _cpu.D[eaReg];
            else
            {
                _cpu.A[eaReg] -= 2;
                src = _cpu.ReadWord(_cpu.A[eaReg]);
            }
            uint result = (uint)(((src + adj) >> 4) & 0xF0) | (uint)((src + adj) & 0x0F);
            if (mode == 0)
                _cpu.D[reg] = (_cpu.D[reg] & 0xFFFFFF00) | (result & 0xFF);
            else
            {
                _cpu.A[reg]--;
                _cpu.WriteByte(_cpu.A[reg], (byte)result);
            }
            return;
        }

        // UNPK (68020+)
        if (opMode == 6 && (mode == 0 || mode == 1))
        {
            ushort adj = _cpu.FetchWord();
            uint src;
            if (mode == 0)
                src = _cpu.D[eaReg] & 0xFF;
            else
            {
                _cpu.A[eaReg]--;
                src = _cpu.ReadByte(_cpu.A[eaReg]);
            }
            uint result = (uint)(((src & 0xF0) << 4) | (src & 0x0F)) + adj;
            if (mode == 0)
                _cpu.D[reg] = (_cpu.D[reg] & 0xFFFF0000) | (result & 0xFFFF);
            else
            {
                _cpu.A[reg] -= 2;
                _cpu.WriteWord(_cpu.A[reg], (ushort)result);
            }
            return;
        }

        // OR
        {
            int size = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            bool toEa = (opMode & 4) != 0;

            if (toEa)
            {
                uint src = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                uint result = src | _cpu.D[reg];
                EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
                SetLogicFlags(result, size);
            }
            else
            {
                uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
                uint result = _cpu.D[reg] | src;
                switch (size)
                {
                    case 1: _cpu.D[reg] = (_cpu.D[reg] & 0xFFFFFF00) | (result & 0xFF); break;
                    case 2: _cpu.D[reg] = (_cpu.D[reg] & 0xFFFF0000) | (result & 0xFFFF); break;
                    default: _cpu.D[reg] = result; break;
                }
                SetLogicFlags(result, size);
            }
        }
    }

    // ====================================================================
    // Group 9: SUB/SUBA/SUBX
    // ====================================================================
    private void DecodeGroup9(ushort opcode)
    {

        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        // SUBA
        if (opMode == 3 || opMode == 7)
        {
            int size = opMode == 3 ? 2 : 4;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
            if (size == 2) src = (uint)(int)(short)(ushort)src;
            _cpu.A[reg] -= src;
            return;
        }

        // SUBX
        if ((opMode == 0 || opMode == 1 || opMode == 2) && (mode == 0 || mode == 1) && (opMode & 4) != 0)
        {
            // Actually SUBX is opMode 4,5,6 with mode 0 or 1
        }
        if ((opMode == 4 || opMode == 5 || opMode == 6) && (mode == 0 || mode == 1))
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            if (mode == 0)
            {
                uint a = ReadRegValue(_cpu.D[reg], size);
                uint b = ReadRegValue(_cpu.D[eaReg], size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.SubByte((byte)a, (byte)b, _cpu.CCR, true);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.SubWord((ushort)a, (ushort)b, _cpu.CCR, true);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.SubLong(a, b, _cpu.CCR, true);
                        break;
                }
                if ((ccr & 0x04) != 0) ccr = (byte)((ccr & ~0x04) | (_cpu.CCR & 0x04));
                WriteRegValue(ref _cpu.D[reg], result, size);
                _cpu.SetCCR(ccr);
            }
            else
            {
                // -(An) mode
                int s = size; if (eaReg == 7 && size == 1) s = 2;
                _cpu.A[eaReg] -= (uint)s;
                _cpu.A[reg] -= (uint)s;
                uint b = ReadMemValue(_cpu.A[eaReg], size);
                uint a = ReadMemValue(_cpu.A[reg], size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.SubByte((byte)a, (byte)b, _cpu.CCR, true);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.SubWord((ushort)a, (ushort)b, _cpu.CCR, true);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.SubLong(a, b, _cpu.CCR, true);
                        break;
                }
                if ((ccr & 0x04) != 0) ccr = (byte)((ccr & ~0x04) | (_cpu.CCR & 0x04));
                WriteMemValue(_cpu.A[reg], result, size);
                _cpu.SetCCR(ccr);
            }
            return;
        }

        // SUB
        {
            int size = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            bool toEa = (opMode & 4) != 0;

            if (toEa)
            {
                uint dst = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.SubByte((byte)dst, (byte)_cpu.D[reg], _cpu.CCR);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.SubWord((ushort)dst, (ushort)_cpu.D[reg], _cpu.CCR);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.SubLong(dst, _cpu.D[reg], _cpu.CCR);
                        break;
                }
                EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
                _cpu.SetCCR(ccr);
            }
            else
            {
                uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
                uint dst = ReadRegValue(_cpu.D[reg], size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.SubByte((byte)dst, (byte)src, _cpu.CCR);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.SubWord((ushort)dst, (ushort)src, _cpu.CCR);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.SubLong(dst, src, _cpu.CCR);
                        break;
                }
                WriteRegValue(ref _cpu.D[reg], result, size);
                _cpu.SetCCR(ccr);
            }
        }
    }

    // ====================================================================
    // Group B: CMP/EOR/CMPA/CMPM
    // ====================================================================
    private void DecodeGroupB(ushort opcode)
    {

        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        // CMPA
        if (opMode == 3 || opMode == 7)
        {
            int size = opMode == 3 ? 2 : 4;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
            if (size == 2) src = (uint)(int)(short)(ushort)src;
            var (_, ccr) = Alu.SubLong(_cpu.A[reg], src, _cpu.CCR);
            _cpu.UpdateCCR(ccr, 0x0F);
            return;
        }

        // CMPM
        if ((opMode == 4 || opMode == 5 || opMode == 6) && mode == 1)
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            int inc = size; if (eaReg == 7 && size == 1) inc = 2;
            uint src = ReadMemValue(_cpu.A[eaReg], size);
            _cpu.A[eaReg] += (uint)inc;
            inc = size; if (reg == 7 && size == 1) inc = 2;
            uint dst = ReadMemValue(_cpu.A[reg], size);
            _cpu.A[reg] += (uint)inc;

            byte ccr;
            switch (size)
            {
                case 1: (_, ccr) = Alu.SubByte((byte)dst, (byte)src, _cpu.CCR); break;
                case 2: (_, ccr) = Alu.SubWord((ushort)dst, (ushort)src, _cpu.CCR); break;
                default: (_, ccr) = Alu.SubLong(dst, src, _cpu.CCR); break;
            }
            _cpu.UpdateCCR(ccr, 0x0F);
            return;
        }

        // EOR
        if (opMode >= 4 && opMode <= 6)
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint dst = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
            uint result = dst ^ _cpu.D[reg];
            EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
            SetLogicFlags(result, size);
            return;
        }

        // CMP
        {
            int size = opMode switch { 0 => 1, 1 => 2, _ => 4 };
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
            uint dst = ReadRegValue(_cpu.D[reg], size);

            byte ccr;
            switch (size)
            {
                case 1: (_, ccr) = Alu.SubByte((byte)dst, (byte)src, _cpu.CCR); break;
                case 2: (_, ccr) = Alu.SubWord((ushort)dst, (ushort)src, _cpu.CCR); break;
                default: (_, ccr) = Alu.SubLong(dst, src, _cpu.CCR); break;
            }
            _cpu.UpdateCCR(ccr, 0x0F);
        }
    }

    // ====================================================================
    // Group C: AND/MUL/ABCD/EXG
    // ====================================================================
    private void DecodeGroupC(ushort opcode)
    {

        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        // MULU.W
        if (opMode == 3)
        {
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2) & 0xFFFF;
            var (result, ccr) = Alu.MulUnsigned(_cpu.D[reg] & 0xFFFF, src);
            _cpu.D[reg] = result;
            _cpu.UpdateCCR(ccr, 0x0F);
            return;
        }

        // MULS.W
        if (opMode == 7)
        {
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2);
            var (result, ccr) = Alu.MulSigned((short)(ushort)(_cpu.D[reg] & 0xFFFF), (short)(ushort)src);
            _cpu.D[reg] = result;
            _cpu.UpdateCCR(ccr, 0x0F);
            return;
        }

        // ABCD
        if (opMode == 4 && (mode == 0 || mode == 1))
        {
            byte src, dst;
            if (mode == 0)
            {
                src = (byte)_cpu.D[eaReg];
                dst = (byte)_cpu.D[reg];
                var (result, ccr) = Alu.AddBcd(src, dst, _cpu.CCR);
                _cpu.D[reg] = (_cpu.D[reg] & 0xFFFFFF00) | result;
                _cpu.SetCCR(ccr);
            }
            else
            {
                _cpu.A[eaReg]--;
                _cpu.A[reg]--;
                src = _cpu.ReadByte(_cpu.A[eaReg]);
                dst = _cpu.ReadByte(_cpu.A[reg]);
                var (result, ccr) = Alu.AddBcd(src, dst, _cpu.CCR);
                _cpu.WriteByte(_cpu.A[reg], result);
                _cpu.SetCCR(ccr);
            }
            return;
        }

        // EXG
        if (opMode == 5 && mode == 0)
        {
            // EXG Dn,Dn
            (_cpu.D[reg], _cpu.D[eaReg]) = (_cpu.D[eaReg], _cpu.D[reg]);
            return;
        }
        if (opMode == 5 && mode == 1)
        {
            // EXG An,An
            (_cpu.A[reg], _cpu.A[eaReg]) = (_cpu.A[eaReg], _cpu.A[reg]);
            return;
        }
        if (opMode == 6 && mode == 1)
        {
            // EXG Dn,An
            (_cpu.D[reg], _cpu.A[eaReg]) = (_cpu.A[eaReg], _cpu.D[reg]);
            return;
        }

        // AND
        {
            int size = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            bool toEa = (opMode & 4) != 0;

            if (toEa)
            {
                uint dst = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                uint result = dst & _cpu.D[reg];
                EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
                SetLogicFlags(result, size);
            }
            else
            {
                uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
                uint result = _cpu.D[reg] & src;
                WriteRegValue(ref _cpu.D[reg], result, size);
                SetLogicFlags(result, size);
            }
        }
    }

    // ====================================================================
    // Group D: ADD/ADDA/ADDX
    // ====================================================================
    private void DecodeGroupD(ushort opcode)
    {

        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        // ADDA
        if (opMode == 3 || opMode == 7)
        {
            int size = opMode == 3 ? 2 : 4;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
            if (size == 2) src = (uint)(int)(short)(ushort)src;
            _cpu.A[reg] += src;
            return;
        }

        // ADDX
        if ((opMode == 4 || opMode == 5 || opMode == 6) && (mode == 0 || mode == 1))
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            if (mode == 0)
            {
                uint a = ReadRegValue(_cpu.D[reg], size);
                uint b = ReadRegValue(_cpu.D[eaReg], size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.AddByte((byte)a, (byte)b, _cpu.CCR, true);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.AddWord((ushort)a, (ushort)b, _cpu.CCR, true);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.AddLong(a, b, _cpu.CCR, true);
                        break;
                }
                if ((ccr & 0x04) != 0) ccr = (byte)((ccr & ~0x04) | (_cpu.CCR & 0x04));
                WriteRegValue(ref _cpu.D[reg], result, size);
                _cpu.SetCCR(ccr);
            }
            else
            {
                int s = size; if (eaReg == 7 && size == 1) s = 2;
                _cpu.A[eaReg] -= (uint)s;
                _cpu.A[reg] -= (uint)s;
                uint b = ReadMemValue(_cpu.A[eaReg], size);
                uint a = ReadMemValue(_cpu.A[reg], size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.AddByte((byte)a, (byte)b, _cpu.CCR, true);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.AddWord((ushort)a, (ushort)b, _cpu.CCR, true);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.AddLong(a, b, _cpu.CCR, true);
                        break;
                }
                if ((ccr & 0x04) != 0) ccr = (byte)((ccr & ~0x04) | (_cpu.CCR & 0x04));
                WriteMemValue(_cpu.A[reg], result, size);
                _cpu.SetCCR(ccr);
            }
            return;
        }

        // ADD
        {
            int size = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, eaReg);
            bool toEa = (opMode & 4) != 0;

            if (toEa)
            {
                uint dst = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.AddByte((byte)dst, (byte)_cpu.D[reg], _cpu.CCR);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.AddWord((ushort)dst, (ushort)_cpu.D[reg], _cpu.CCR);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.AddLong(dst, _cpu.D[reg], _cpu.CCR);
                        break;
                }
                EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, size, result);
                _cpu.SetCCR(ccr);
            }
            else
            {
                uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, size);
                uint dst = ReadRegValue(_cpu.D[reg], size);
                byte ccr;
                uint result;
                switch (size)
                {
                    case 1:
                        (var rb, ccr) = Alu.AddByte((byte)dst, (byte)src, _cpu.CCR);
                        result = rb;
                        break;
                    case 2:
                        (var rw, ccr) = Alu.AddWord((ushort)dst, (ushort)src, _cpu.CCR);
                        result = rw;
                        break;
                    default:
                        (result, ccr) = Alu.AddLong(dst, src, _cpu.CCR);
                        break;
                }
                WriteRegValue(ref _cpu.D[reg], result, size);
                _cpu.SetCCR(ccr);
            }
        }
    }

    // ====================================================================
    // Group E: Shift/Rotate
    // ====================================================================
    private void DecodeGroupE(ushort opcode)
    {

        int sizeField = (opcode >> 6) & 3;

        if (sizeField == 3)
        {
            // Check bit 11 to distinguish memory shift vs bit field
            if ((opcode & 0x0800) != 0)
            {
                DecodeBitField(opcode);
                return;
            }

            // Memory shift/rotate (always word size, shift by 1)
            int type = (opcode >> 9) & 3;
            bool left = (opcode & 0x0100) != 0;
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint val = EffectiveAddress.ReadValueForModify(_cpu, eaMode, eaR, 2);

            byte ccr;
            uint result;
            switch (type)
            {
                case 0: // ASL/ASR
                    if (left)
                        (var rw, ccr) = Alu.ShiftLeft((ushort)val, 1, _cpu.CCR);
                    else
                        (var rw2, ccr) = Alu.ArithShiftRight((ushort)val, 1);
                    // Re-do to capture result properly
                    if (left)
                    {
                        var r = Alu.ShiftLeft((ushort)val, 1, _cpu.CCR);
                        result = r.result; ccr = r.ccr;
                    }
                    else
                    {
                        var r = Alu.ArithShiftRight((ushort)val, 1);
                        result = r.result; ccr = r.ccr;
                    }
                    break;
                case 1: // LSL/LSR
                    if (left)
                    {
                        var r = Alu.ShiftLeft((ushort)val, 1, _cpu.CCR);
                        result = r.result; ccr = r.ccr;
                    }
                    else
                    {
                        var r = Alu.LogicalShiftRight((ushort)val, 1);
                        result = r.result; ccr = r.ccr;
                    }
                    break;
                case 2: // ROXL/ROXR memory
                    if (left)
                    {
                        var r = Alu.RotateLeftX(val, 1, 2, _cpu.CCR);
                        result = r.result; ccr = r.ccr;
                    }
                    else
                    {
                        var r = Alu.RotateRightX(val, 1, 2, _cpu.CCR);
                        result = r.result; ccr = r.ccr;
                    }
                    break;
                default: // ROL/ROR
                    if (left)
                    {
                        var r = Alu.RotateLeft(val, 1, 2);
                        result = r.result; ccr = r.ccr;
                    }
                    else
                    {
                        var r = Alu.RotateRight(val, 1, 2);
                        result = r.result; ccr = r.ccr;
                    }
                    break;
            }
            EffectiveAddress.WriteValueFromModify(_cpu, eaMode, eaR, 2, result);
            _cpu.SetCCR(ccr);
            return;
        }

        // Register shift/rotate
        int count;
        int countReg = (opcode >> 9) & 7;
        bool ir = (opcode & 0x0020) != 0; // i/r bit: 0=immediate count, 1=register count
        if (ir)
            count = (int)(_cpu.D[countReg] % 64);
        else
        {
            count = countReg;
            if (count == 0) count = 8;
        }

        int dreg = opcode & 7;
        int size = sizeField switch { 0 => 1, 1 => 2, _ => 4 };
        bool isLeft = (opcode & 0x0100) != 0;
        int shiftType = (opcode >> 3) & 3;

        uint val2 = ReadRegValue(_cpu.D[dreg], size);
        uint result2;
        byte ccr2;

        switch (shiftType)
        {
            case 0: // ASL/ASR
                if (isLeft)
                {
                    switch (size)
                    {
                        case 1: var r1 = Alu.ShiftLeft((byte)val2, count, _cpu.CCR); result2 = r1.result; ccr2 = r1.ccr; break;
                        case 2: var r2 = Alu.ShiftLeft((ushort)val2, count, _cpu.CCR); result2 = r2.result; ccr2 = r2.ccr; break;
                        default: var r4 = Alu.ShiftLeft(val2, count, _cpu.CCR); result2 = r4.result; ccr2 = r4.ccr; break;
                    }
                }
                else
                {
                    switch (size)
                    {
                        case 1: var r1 = Alu.ArithShiftRight((byte)val2, count); result2 = r1.result; ccr2 = r1.ccr; break;
                        case 2: var r2 = Alu.ArithShiftRight((ushort)val2, count); result2 = r2.result; ccr2 = r2.ccr; break;
                        default: var r4 = Alu.ArithShiftRight(val2, count); result2 = r4.result; ccr2 = r4.ccr; break;
                    }
                }
                break;
            case 1: // LSL/LSR
                if (isLeft)
                {
                    switch (size)
                    {
                        case 1: var r1 = Alu.ShiftLeft((byte)val2, count, _cpu.CCR); result2 = r1.result; ccr2 = r1.ccr; break;
                        case 2: var r2 = Alu.ShiftLeft((ushort)val2, count, _cpu.CCR); result2 = r2.result; ccr2 = r2.ccr; break;
                        default: var r4 = Alu.ShiftLeft(val2, count, _cpu.CCR); result2 = r4.result; ccr2 = r4.ccr; break;
                    }
                }
                else
                {
                    switch (size)
                    {
                        case 1: var r1 = Alu.LogicalShiftRight((byte)val2, count); result2 = r1.result; ccr2 = r1.ccr; break;
                        case 2: var r2 = Alu.LogicalShiftRight((ushort)val2, count); result2 = r2.result; ccr2 = r2.ccr; break;
                        default: var r4 = Alu.LogicalShiftRight(val2, count); result2 = r4.result; ccr2 = r4.ccr; break;
                    }
                }
                break;
            case 2: // ROXL/ROXR
                if (isLeft)
                {
                    var r = Alu.RotateLeftX(val2, count, size, _cpu.CCR);
                    result2 = r.result; ccr2 = r.ccr;
                }
                else
                {
                    var r = Alu.RotateRightX(val2, count, size, _cpu.CCR);
                    result2 = r.result; ccr2 = r.ccr;
                }
                break;
            case 3: // ROL/ROR
                if (isLeft)
                {
                    var r = Alu.RotateLeft(val2, count, size);
                    result2 = r.result; ccr2 = r.ccr;
                }
                else
                {
                    var r = Alu.RotateRight(val2, count, size);
                    result2 = r.result; ccr2 = r.ccr;
                }
                break;
            default:
                result2 = val2; ccr2 = _cpu.CCR;
                break;
        }

        WriteRegValue(ref _cpu.D[dreg], result2, size);
        _cpu.SetCCR(ccr2);
    }

    // ====================================================================
    // Line-A / Line-F
    // ====================================================================
    private void DecodeLineA(ushort opcode)
    {

        _cpu.RaiseException(10); // Line-A emulator
    }

    private void DecodeLineF(ushort opcode)
    {

        int cpId = (opcode >> 9) & 7;

        if (cpId == 0) // MMU (coprocessor ID = 0)
        {
            DecodeMMUInstruction(opcode);
            return;
        }

        if (cpId == 1) // FPU (coprocessor ID = 1)
        {
            _fpuDecoder.Execute(opcode);
            return;
        }

        // Log the unhandled line-F instruction for diagnostics
        _cpu.LogException($"F-line: opcode=${opcode:X4} cpId={cpId} at PC=${_cpu.PC - 2:X8}");
        _cpu.RaiseException(11); // Line-F emulator / F-line
    }

    private void DecodeMMUInstruction(ushort opcode)
    {
        if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }

        ushort ext = _cpu.FetchWord();
        int mmuOp = (ext >> 13) & 7;

        switch (mmuOp)
        {
            case 0: // PMOVE to/from TT0, TT1
                DecodePMOVE_TT(opcode, ext);
                break;

            case 1: // PFLUSH / PFLUSHA / PLOAD
                DecodePFLUSH_PLOAD(opcode, ext);
                break;

            case 2: // PMOVE to/from TC, SRP, CRP
                DecodePMOVE_TC_SRP_CRP(opcode, ext);
                break;

            case 3: // PMOVE to/from MMUSR
                DecodePMOVE_MMUSR(opcode, ext);
                break;

            case 4: // PTEST
                DecodePTEST(opcode, ext);
                break;

            default:
                _cpu.RaiseException(11); // Illegal
                break;
        }
    }

    private byte ResolveFunctionCode(ushort ext)
    {
        // FC source encoding in MC68030 PMMU extension word (bits 4-0):
        //   bit 4 = 1: Immediate FC value (bits 2-0 = value)
        //   bit 4 = 0, bit 3 = 1: Data register Dn (bits 2-0 = register number)
        //   bit 4 = 0, bit 3 = 0: SFC (bit 0=0) or DFC (bit 0=1)
        if ((ext & 0x10) != 0)
            return (byte)(ext & 7);                      // Immediate FC value
        if ((ext & 0x08) != 0)
            return (byte)(_cpu.D[ext & 7] & 7);          // Data register Dn
        return (ext & 1) != 0
            ? (byte)(_cpu.DFC & 7)                        // DFC register
            : (byte)(_cpu.SFC & 7);                       // SFC register
    }

    private void DecodePMOVE_TT(ushort opcode, ushort ext)
    {
        // mmuOp=0: PMOVE to/from TT0, TT1
        int pmReg = (ext >> 10) & 7;
        bool toMem = (ext & 0x0200) != 0; // bit 9: 1=reg->EA (write to memory)
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);

        switch (pmReg)
        {
            case 2: // TT0
                if (toMem)
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    _cpu.WriteLong(addr, _cpu.Mmu.TT0);
                }
                else
                {
                    _cpu.Mmu.TT0 = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 4);
                    _cpu.Mmu.FlushAll();
                }
                break;
            case 3: // TT1
                if (toMem)
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    _cpu.WriteLong(addr, _cpu.Mmu.TT1);
                }
                else
                {
                    _cpu.Mmu.TT1 = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 4);
                    _cpu.Mmu.FlushAll();
                }
                break;
        }
    }

    private void DecodePFLUSH_PLOAD(ushort opcode, ushort ext)
    {
        // mmuOp=1: PFLUSH / PFLUSHA / PLOAD

        // PFLUSHA: special pattern ext=0x2400
        if (ext == 0x2400)
        {
            _cpu.Mmu.FlushAll();
            return;
        }

        // Distinguish PLOAD from PFLUSH: bit 12 = 0 for PLOAD, 1 for PFLUSH
        // (PFLUSHA already handled above; it also has bit 12=0 but bit 10=1)
        if ((ext & 0x1000) == 0)
        {
            // PLOAD
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
            bool isRead = (ext & 0x0200) != 0; // bit 9: 1=PLOADR, 0=PLOADW
            byte fc = ResolveFunctionCode(ext);
            _cpu.Mmu.PLoad(addr, _cpu.SupervisorMode, !isRead, fc);
            return;
        }

        // PFLUSH FC,#mask or PFLUSH FC,#mask,(ea)
        byte flushFC = ResolveFunctionCode(ext);
        byte mask = (byte)((ext >> 5) & 7); // bits 7-5: 3-bit FC mask
        bool hasEA = (ext & 0x0800) != 0;   // bit 11: 1=has EA

        if (hasEA)
        {
            // PFLUSH FC,#mask,(ea)
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
            _cpu.Mmu.FlushByFCAndAddress(flushFC, mask, addr);
        }
        else
        {
            // PFLUSH FC,#mask
            _cpu.Mmu.FlushByFC(flushFC, mask);
        }
    }

    private void DecodePMOVE_TC_SRP_CRP(ushort opcode, ushort ext)
    {
        // mmuOp=2: PMOVE to/from TC, SRP, CRP
        int pmReg = (ext >> 10) & 7;
        bool toMem = (ext & 0x0200) != 0;
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);

        switch (pmReg)
        {
            case 0: // TC (32-bit)
                if (toMem)
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    _cpu.WriteLong(addr, _cpu.Mmu.TC);
                }
                else
                {
                    _cpu.Mmu.TC = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 4);
                    _cpu.Mmu.FlushAll(); // TC change invalidates all translations
                }
                break;
            case 2: // SRP (64-bit)
                if (toMem)
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    _cpu.WriteLong(addr, (uint)(_cpu.Mmu.SRP >> 32));
                    _cpu.WriteLong(addr + 4, (uint)_cpu.Mmu.SRP);
                }
                else
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    ulong hi = _cpu.ReadLong(addr);
                    ulong lo = _cpu.ReadLong(addr + 4);
                    _cpu.Mmu.SRP = (hi << 32) | lo;
                    _cpu.Mmu.FlushAll();
                }
                break;
            case 3: // CRP (64-bit)
                if (toMem)
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    _cpu.WriteLong(addr, (uint)(_cpu.Mmu.CRP >> 32));
                    _cpu.WriteLong(addr + 4, (uint)_cpu.Mmu.CRP);
                }
                else
                {
                    uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);
                    ulong hi = _cpu.ReadLong(addr);
                    ulong lo = _cpu.ReadLong(addr + 4);
                    _cpu.Mmu.CRP = (hi << 32) | lo;
                    _cpu.Mmu.FlushAll();
                }
                break;
        }
    }

    private void DecodePMOVE_MMUSR(ushort opcode, ushort ext)
    {
        // mmuOp=3: PMOVE to/from MMUSR (16-bit)
        bool toMem = (ext & 0x0200) != 0;
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);

        if (toMem)
        {
            EffectiveAddress.WriteValue(_cpu, eaMode, eaR, 2, _cpu.Mmu.MMUSR);
        }
        else
        {
            _cpu.Mmu.MMUSR = (ushort)EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 2);
        }
    }

    private void DecodePTEST(ushort opcode, ushort ext)
    {
        // mmuOp=4: PTEST
        int level = (ext >> 10) & 7;       // bits 12-10: level (max levels to search)
        bool isRead = (ext & 0x0200) != 0; // bit 9: 1=read, 0=write
        bool hasAReg = (ext & 0x0100) != 0; // bit 8: 1=store result in A-reg
        int aReg = (ext >> 5) & 7;         // bits 7-5: A-register for result

        byte fc = ResolveFunctionCode(ext);

        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 4);

        _cpu.Mmu.PTest(addr, _cpu.SupervisorMode, !isRead, fc, level);

        // If A-register specified, store the last descriptor address
        if (hasAReg)
        {
            _cpu.A[aReg] = _cpu.Mmu.LastDescriptorAddress;
        }
    }

    // ====================================================================
    // CAS / CAS2 (68020+)
    // ====================================================================
    private void DecodeCAS(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        int ssBits = (opcode >> 9) & 3; // size: 01=B, 10=W, 11=L
        int size = ssBits switch { 1 => 1, 2 => 2, _ => 4 };

        // CAS2 check: mode=7, reg=4
        if (mode == 7 && reg == 4)
        {
            DecodeCAS2(opcode, size);
            return;
        }

        ushort ext = _cpu.FetchWord();
        int dc = ext & 7;           // compare register
        int du = (ext >> 6) & 7;    // update register

        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint addr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, size);
        uint memVal = ReadMemValue(addr, size);
        uint cmpVal = ReadRegValue(_cpu.D[dc], size);

        // Compare Dc with <ea>
        byte ccr;
        switch (size)
        {
            case 1: (_, ccr) = Alu.SubByte((byte)memVal, (byte)cmpVal, _cpu.CCR); break;
            case 2: (_, ccr) = Alu.SubWord((ushort)memVal, (ushort)cmpVal, _cpu.CCR); break;
            default: (_, ccr) = Alu.SubLong(memVal, cmpVal, _cpu.CCR); break;
        }
        _cpu.SetCCR(ccr);

        if (_cpu.FlagZ)
        {
            // Equal: write Du to <ea>
            WriteMemValue(addr, ReadRegValue(_cpu.D[du], size), size);
        }
        else
        {
            // Not equal: write <ea> to Dc
            WriteRegValue(ref _cpu.D[dc], memVal, size);
        }
    }

    private void DecodeCAS2(ushort opcode, int size)
    {
        ushort ext1 = _cpu.FetchWord();
        ushort ext2 = _cpu.FetchWord();

        int dc1 = ext1 & 7;
        int du1 = (ext1 >> 6) & 7;
        int rn1 = (ext1 >> 12) & 0xF;

        int dc2 = ext2 & 7;
        int du2 = (ext2 >> 6) & 7;
        int rn2 = (ext2 >> 12) & 0xF;

        uint addr1 = rn1 < 8 ? _cpu.D[rn1] : _cpu.A[rn1 - 8];
        uint addr2 = rn2 < 8 ? _cpu.D[rn2] : _cpu.A[rn2 - 8];

        uint mem1 = ReadMemValue(addr1, size);
        uint mem2 = ReadMemValue(addr2, size);

        // Compare Dc1 with (Rn1)
        byte ccr;
        switch (size)
        {
            case 2: (_, ccr) = Alu.SubWord((ushort)mem1, (ushort)ReadRegValue(_cpu.D[dc1], size), _cpu.CCR); break;
            default: (_, ccr) = Alu.SubLong(mem1, ReadRegValue(_cpu.D[dc1], size), _cpu.CCR); break;
        }
        _cpu.SetCCR(ccr);

        if (_cpu.FlagZ)
        {
            // First compare equal, compare Dc2 with (Rn2)
            switch (size)
            {
                case 2: (_, ccr) = Alu.SubWord((ushort)mem2, (ushort)ReadRegValue(_cpu.D[dc2], size), _cpu.CCR); break;
                default: (_, ccr) = Alu.SubLong(mem2, ReadRegValue(_cpu.D[dc2], size), _cpu.CCR); break;
            }
            _cpu.SetCCR(ccr);

            if (_cpu.FlagZ)
            {
                // Both equal: write Du1 and Du2
                WriteMemValue(addr1, ReadRegValue(_cpu.D[du1], size), size);
                WriteMemValue(addr2, ReadRegValue(_cpu.D[du2], size), size);
            }
            else
            {
                WriteRegValue(ref _cpu.D[dc1], mem1, size);
                WriteRegValue(ref _cpu.D[dc2], mem2, size);
            }
        }
        else
        {
            WriteRegValue(ref _cpu.D[dc1], mem1, size);
            WriteRegValue(ref _cpu.D[dc2], mem2, size);
        }
    }

    // ====================================================================
    // Long Multiply (68020+)
    // ====================================================================
    private void DecodeLongMul(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort ext = _cpu.FetchWord();

        int dl = (ext >> 12) & 7;   // destination low register
        int dh = ext & 7;           // destination high register
        bool signed_ = (ext & 0x0800) != 0;
        bool quad = (ext & 0x0400) != 0; // 64-bit result

        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 4);

        if (signed_)
        {
            var (lo, hi, ccr) = Alu.MulSignedLong((int)_cpu.D[dl], (int)src);
            _cpu.D[dl] = lo;
            if (quad)
            {
                _cpu.D[dh] = hi;
                // For 64-bit result: N=bit63, Z=(full 64-bit result == 0)
                ccr = 0;
                if ((hi & 0x80000000) != 0) ccr |= 0x08; // N from bit 63
                if (lo == 0 && hi == 0) ccr |= 0x04;     // Z from full 64-bit
            }
            else
            {
                if (hi != 0 && hi != 0xFFFFFFFF) ccr |= 0x02; // V for 32-bit overflow
                else if (hi == 0xFFFFFFFF && (lo & 0x80000000) == 0) ccr |= 0x02;
            }
            _cpu.UpdateCCR(ccr, 0x0F);
        }
        else
        {
            var (lo, hi, ccr) = Alu.MulUnsignedLong(_cpu.D[dl], src);
            _cpu.D[dl] = lo;
            if (quad)
            {
                _cpu.D[dh] = hi;
                // For 64-bit result: N=bit63, Z=(full 64-bit result == 0)
                ccr = 0;
                if ((hi & 0x80000000) != 0) ccr |= 0x08; // N from bit 63
                if (lo == 0 && hi == 0) ccr |= 0x04;     // Z from full 64-bit
            }
            else
            {
                if (hi != 0) ccr |= 0x02; // V for 32-bit overflow
            }
            _cpu.UpdateCCR(ccr, 0x0F);
        }
    }

    // ====================================================================
    // Long Division (68020+)
    // ====================================================================
    private void DecodeLongDiv(ushort opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort ext = _cpu.FetchWord();

        int dq = (ext >> 12) & 7;   // quotient register
        int dr = ext & 7;           // remainder register
        bool signed_ = (ext & 0x0800) != 0;
        bool quad = (ext & 0x0400) != 0; // 64-bit dividend

        var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
        uint src = EffectiveAddress.ReadValue(_cpu, eaMode, eaR, 4);

        if (src == 0) { _cpu.RaiseException(5); return; } // Division by zero

        if (signed_)
        {
            long dividend;
            if (quad)
                dividend = ((long)(int)_cpu.D[dr] << 32) | _cpu.D[dq];
            else
                dividend = (int)_cpu.D[dq];

            var (quot, rem, ccr, overflow) = Alu.DivSignedLong(dividend, (int)src);
            if (overflow) { _cpu.FlagV = true; return; }
            _cpu.D[dq] = quot;
            if (dr != dq) _cpu.D[dr] = rem;
            _cpu.UpdateCCR(ccr, 0x0F);
        }
        else
        {
            ulong dividend;
            if (quad)
                dividend = ((ulong)_cpu.D[dr] << 32) | _cpu.D[dq];
            else
                dividend = _cpu.D[dq];

            var (quot, rem, ccr, overflow) = Alu.DivUnsignedLong(dividend, src);
            if (overflow) { _cpu.FlagV = true; return; }
            _cpu.D[dq] = quot;
            if (dr != dq) _cpu.D[dr] = rem;
            _cpu.UpdateCCR(ccr, 0x0F);
        }
    }

    // ====================================================================
    // Bit Field Instructions (68020+)
    // ====================================================================
    private void DecodeBitField(ushort opcode)
    {
        int bfOp = (opcode >> 8) & 7;
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort ext = _cpu.FetchWord();

        int dnReg = (ext >> 12) & 7; // data register for BFEXTU/BFEXTS/BFFFO/BFINS
        bool doReg = (ext & 0x0800) != 0;
        int offset = doReg ? (int)(_cpu.D[(ext >> 6) & 7] & 0x1F) : ((ext >> 6) & 0x1F);
        bool dwReg = (ext & 0x0020) != 0;
        int width = dwReg ? (int)(_cpu.D[ext & 7] % 32) : (ext & 0x1F);
        if (width == 0) width = 32;

        // For register operand
        if (mode == 0)
        {
            uint data = _cpu.D[reg];
            uint field = ExtractBitFieldReg(data, offset, width);

            switch (bfOp)
            {
                case 0: // BFTST
                    SetBitFieldFlags(field, width);
                    break;
                case 1: // BFEXTU
                    SetBitFieldFlags(field, width);
                    _cpu.D[dnReg] = field;
                    break;
                case 2: // BFCHG
                    SetBitFieldFlags(field, width);
                    _cpu.D[reg] = InsertBitFieldReg(data, ~field, offset, width);
                    break;
                case 3: // BFEXTS
                    SetBitFieldFlags(field, width);
                    // Sign extend
                    if (width < 32 && (field & (1u << (width - 1))) != 0)
                        field |= 0xFFFFFFFF << width;
                    _cpu.D[dnReg] = field;
                    break;
                case 4: // BFCLR
                    SetBitFieldFlags(field, width);
                    _cpu.D[reg] = InsertBitFieldReg(data, 0, offset, width);
                    break;
                case 5: // BFFFO
                    SetBitFieldFlags(field, width);
                    {
                        int ffo = 0;
                        for (int i = width - 1; i >= 0; i--)
                        {
                            if ((field & (1u << i)) != 0) break;
                            ffo++;
                        }
                        _cpu.D[dnReg] = (uint)(offset + ffo);
                    }
                    break;
                case 6: // BFSET
                    SetBitFieldFlags(field, width);
                    {
                        uint ones = width == 32 ? 0xFFFFFFFF : (1u << width) - 1;
                        _cpu.D[reg] = InsertBitFieldReg(data, ones, offset, width);
                    }
                    break;
                case 7: // BFINS
                    {
                        uint ins = _cpu.D[dnReg];
                        if (width < 32) ins &= (1u << width) - 1;
                        _cpu.D[reg] = InsertBitFieldReg(data, ins, offset, width);
                        SetBitFieldFlags(ins, width);
                    }
                    break;
            }
        }
        else
        {
            // Memory operand
            var (eaMode, eaR) = EffectiveAddress.Decode(mode, reg);
            uint baseAddr = EffectiveAddress.ResolveAddress(_cpu, eaMode, eaR, 1);

            // Adjust for offset (can be > 7 for register offset)
            if (doReg) offset = (int)_cpu.D[(ext >> 6) & 7];
            int byteOff = offset >> 3;
            int bitOff = offset & 7;
            baseAddr = (uint)(baseAddr + byteOff);

            // Read enough bytes to cover the field
            int totalBits = bitOff + width;
            int bytesNeeded = (totalBits + 7) / 8;
            ulong data = 0;
            for (int i = 0; i < bytesNeeded && i < 5; i++)
                data = (data << 8) | _cpu.ReadByte(baseAddr + (uint)i);

            // Extract field
            int shift = (bytesNeeded * 8) - bitOff - width;
            uint mask = width == 32 ? 0xFFFFFFFF : (1u << width) - 1;
            uint field = (uint)((data >> shift) & mask);

            switch (bfOp)
            {
                case 0: // BFTST
                    SetBitFieldFlags(field, width);
                    break;
                case 1: // BFEXTU
                    SetBitFieldFlags(field, width);
                    _cpu.D[dnReg] = field;
                    break;
                case 2: // BFCHG
                    SetBitFieldFlags(field, width);
                    data ^= (ulong)(mask) << shift;
                    WriteBitFieldMem(baseAddr, data, bytesNeeded);
                    break;
                case 3: // BFEXTS
                    SetBitFieldFlags(field, width);
                    if (width < 32 && (field & (1u << (width - 1))) != 0)
                        field |= 0xFFFFFFFF << width;
                    _cpu.D[dnReg] = field;
                    break;
                case 4: // BFCLR
                    SetBitFieldFlags(field, width);
                    data &= ~((ulong)mask << shift);
                    WriteBitFieldMem(baseAddr, data, bytesNeeded);
                    break;
                case 5: // BFFFO
                    SetBitFieldFlags(field, width);
                    {
                        int ffo = 0;
                        for (int i = width - 1; i >= 0; i--)
                        {
                            if ((field & (1u << i)) != 0) break;
                            ffo++;
                        }
                        _cpu.D[dnReg] = (uint)(offset + ffo);
                    }
                    break;
                case 6: // BFSET
                    SetBitFieldFlags(field, width);
                    data |= (ulong)mask << shift;
                    WriteBitFieldMem(baseAddr, data, bytesNeeded);
                    break;
                case 7: // BFINS
                    {
                        uint ins = _cpu.D[dnReg];
                        if (width < 32) ins &= (1u << width) - 1;
                        data &= ~((ulong)mask << shift);
                        data |= (ulong)ins << shift;
                        WriteBitFieldMem(baseAddr, data, bytesNeeded);
                        SetBitFieldFlags(ins, width);
                    }
                    break;
            }
        }
    }

    private static uint ExtractBitFieldReg(uint data, int offset, int width)
    {
        // Bit field in register: bit 31 is MSB (offset 0)
        offset %= 32;
        uint mask = width == 32 ? 0xFFFFFFFF : (1u << width) - 1;
        int shift = 32 - offset - width;
        if (shift >= 0)
            return (data >> shift) & mask;
        else
        {
            // Wraps around
            uint hi = data << (-shift);
            uint lo = data >> (32 + shift);
            return (hi | lo) & mask;
        }
    }

    private static uint InsertBitFieldReg(uint data, uint field, int offset, int width)
    {
        offset %= 32;
        uint mask = width == 32 ? 0xFFFFFFFF : (1u << width) - 1;
        field &= mask;
        int shift = 32 - offset - width;
        if (shift >= 0)
        {
            data &= ~(mask << shift);
            data |= field << shift;
        }
        else
        {
            uint hiMask = mask >> (-shift);
            uint loMask = mask << (32 + shift);
            data &= ~hiMask;
            data |= field >> (-shift);
            data &= ~loMask;
            data |= field << (32 + shift);
        }
        return data;
    }

    private void WriteBitFieldMem(uint addr, ulong data, int bytes)
    {
        for (int i = 0; i < bytes && i < 5; i++)
        {
            byte b = (byte)(data >> ((bytes - 1 - i) * 8));
            _cpu.WriteByte(addr + (uint)i, b);
        }
    }

    private void SetBitFieldFlags(uint field, int width)
    {
        _cpu.FlagN = width > 0 && (field & (1u << (width - 1))) != 0;
        _cpu.FlagZ = field == 0;
        _cpu.FlagV = false;
        _cpu.FlagC = false;
    }

    // ====================================================================
    // Helpers
    // ====================================================================
    private int GetSize2(ushort opcode)
    {
        return ((opcode >> 6) & 3) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 2
        };
    }

    private uint ReadImmediate(int size)
    {
        return size switch
        {
            1 => (uint)(_cpu.FetchWord() & 0xFF),
            2 => _cpu.FetchWord(),
            _ => _cpu.FetchLong()
        };
    }

    private void SetLogicFlags(uint value, int size)
    {
        byte ccr = size switch
        {
            1 => Alu.SetNZFlags((byte)value),
            2 => Alu.SetNZFlags((ushort)value),
            _ => Alu.SetNZFlags(value)
        };
        _cpu.UpdateCCR(ccr, 0x0F); // Update N,Z,V,C (V=0, C=0 for logic ops)
    }

    private static uint ReadRegValue(uint reg, int size)
    {
        return size switch
        {
            1 => reg & 0xFF,
            2 => reg & 0xFFFF,
            _ => reg
        };
    }

    private static void WriteRegValue(ref uint reg, uint value, int size)
    {
        switch (size)
        {
            case 1: reg = (reg & 0xFFFFFF00) | (value & 0xFF); break;
            case 2: reg = (reg & 0xFFFF0000) | (value & 0xFFFF); break;
            default: reg = value; break;
        }
    }

    private uint ReadMemValue(uint addr, int size)
    {
        return size switch
        {
            1 => _cpu.ReadByte(addr),
            2 => _cpu.ReadWord(addr),
            _ => _cpu.ReadLong(addr)
        };
    }

    private void WriteMemValue(uint addr, uint value, int size)
    {
        switch (size)
        {
            case 1: _cpu.WriteByte(addr, (byte)value); break;
            case 2: _cpu.WriteWord(addr, (ushort)value); break;
            default: _cpu.WriteLong(addr, value); break;
        }
    }
}
