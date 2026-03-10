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

public enum AddressingMode
{
    DataRegDirect,      // Dn
    AddrRegDirect,      // An
    AddrRegIndirect,    // (An)
    AddrRegPostInc,     // (An)+
    AddrRegPreDec,      // -(An)
    AddrRegDisp,        // (d16,An)
    AddrRegIndex,       // (d8,An,Xn)
    AbsShort,           // (xxx).W
    AbsLong,            // (xxx).L
    Immediate,          // #imm
    PcDisp,             // (d16,PC)
    PcIndex,            // (d8,PC,Xn)
}

public class EffectiveAddress
{
    public AddressingMode Mode { get; set; }
    public int Register { get; set; }
    public uint Address { get; set; }
    public uint Value { get; set; }
    public int Size { get; set; } // 1=byte, 2=word, 4=long
    public int ExtWords { get; set; } // extension words consumed

    public static (AddressingMode mode, int reg) Decode(int modeField, int regField)
    {
        return modeField switch
        {
            0 => (AddressingMode.DataRegDirect, regField),
            1 => (AddressingMode.AddrRegDirect, regField),
            2 => (AddressingMode.AddrRegIndirect, regField),
            3 => (AddressingMode.AddrRegPostInc, regField),
            4 => (AddressingMode.AddrRegPreDec, regField),
            5 => (AddressingMode.AddrRegDisp, regField),
            6 => (AddressingMode.AddrRegIndex, regField),
            7 => regField switch
            {
                0 => (AddressingMode.AbsShort, 0),
                1 => (AddressingMode.AbsLong, 0),
                2 => (AddressingMode.PcDisp, 0),
                3 => (AddressingMode.PcIndex, 0),
                4 => (AddressingMode.Immediate, 0),
                _ => throw new InvalidOperationException($"Invalid addressing mode 7/{regField}")
            },
            _ => throw new InvalidOperationException($"Invalid addressing mode {modeField}")
        };
    }

    public static uint ResolveAddress(MC68030 cpu, AddressingMode mode, int reg, int size)
    {
        switch (mode)
        {
            case AddressingMode.AddrRegIndirect:
                return cpu.A[reg];

            case AddressingMode.AddrRegPostInc:
                {
                    uint addr = cpu.A[reg];
                    int inc = size;
                    if (reg == 7 && size == 1) inc = 2; // SP always word-aligned
                    cpu.A[reg] += (uint)inc;
                    return addr;
                }

            case AddressingMode.AddrRegPreDec:
                {
                    int dec = size;
                    if (reg == 7 && size == 1) dec = 2;
                    cpu.A[reg] -= (uint)dec;
                    return cpu.A[reg];
                }

            case AddressingMode.AddrRegDisp:
                {
                    short disp = (short)cpu.FetchWord();
                    return (uint)(cpu.A[reg] + disp);
                }

            case AddressingMode.AddrRegIndex:
                return ResolveIndexed(cpu, cpu.A[reg]);

            case AddressingMode.AbsShort:
                {
                    short addr = (short)cpu.FetchWord();
                    return (uint)addr;
                }

            case AddressingMode.AbsLong:
                return cpu.FetchLong();

            case AddressingMode.PcDisp:
                {
                    uint pc = cpu.PC;
                    short disp = (short)cpu.FetchWord();
                    return (uint)(pc + disp);
                }

            case AddressingMode.PcIndex:
                {
                    uint pc = cpu.PC;
                    return ResolveIndexed(cpu, pc);
                }

            default:
                return 0;
        }
    }

    private static uint ResolveIndexed(MC68030 cpu, uint baseAddr)
    {
        ushort ext = cpu.FetchWord();
        bool isLong = (ext & 0x0800) != 0;
        int indexReg = (ext >> 12) & 0xF;
        bool isAddrReg = (ext & 0x8000) != 0;
        int scale = 1 << ((ext >> 9) & 3);

        int indexVal;
        if (isAddrReg)
            indexVal = isLong ? (int)cpu.A[indexReg & 7] : (short)(ushort)cpu.A[indexReg & 7];
        else
            indexVal = isLong ? (int)cpu.D[indexReg & 7] : (short)(ushort)cpu.D[indexReg & 7];

        indexVal *= scale;

        if ((ext & 0x0100) != 0)
        {
            // Full extension word format (68020+)
            int bdSize = (ext >> 4) & 3;
            bool bs = (ext & 0x0080) != 0; // base suppress
            bool is_ = (ext & 0x0040) != 0; // index suppress

            int baseDisp = 0;
            if (bdSize == 2) baseDisp = (short)cpu.FetchWord();
            else if (bdSize == 3) baseDisp = (int)cpu.FetchLong();

            uint bAddr = bs ? 0 : baseAddr;
            int idx = is_ ? 0 : indexVal;

            int iis = ext & 7;
            if (iis == 0)
            {
                // No memory indirect: base + displacement + index
                return (uint)(bAddr + baseDisp + idx);
            }
            else if ((iis & 0x04) == 0)
            {
                // Pre-indexed memory indirect (I/IS = 001, 010, 011)
                // EA = ([base + disp + index]) + outer_disp
                uint intermediate = cpu.ReadLong((uint)(bAddr + baseDisp + idx));
                int outerDisp = 0;
                if ((iis & 3) == 2) outerDisp = (short)cpu.FetchWord();
                else if ((iis & 3) == 3) outerDisp = (int)cpu.FetchLong();
                return (uint)(intermediate + outerDisp);
            }
            else
            {
                // Post-indexed memory indirect (I/IS = 101, 110, 111)
                // EA = ([base + disp]) + index + outer_disp
                uint intermediate = cpu.ReadLong((uint)(bAddr + baseDisp));
                int outerDisp = 0;
                if ((iis & 3) == 2) outerDisp = (short)cpu.FetchWord();
                else if ((iis & 3) == 3) outerDisp = (int)cpu.FetchLong();
                return (uint)(intermediate + idx + outerDisp);
            }
        }
        else
        {
            // Brief extension word
            sbyte disp = (sbyte)(ext & 0xFF);
            return (uint)(baseAddr + disp + indexVal);
        }
    }

    public static uint ReadValue(MC68030 cpu, AddressingMode mode, int reg, int size)
    {
        switch (mode)
        {
            case AddressingMode.DataRegDirect:
                return size switch
                {
                    1 => cpu.D[reg] & 0xFF,
                    2 => cpu.D[reg] & 0xFFFF,
                    _ => cpu.D[reg]
                };

            case AddressingMode.AddrRegDirect:
                return size switch
                {
                    2 => cpu.A[reg] & 0xFFFF,
                    _ => cpu.A[reg]
                };

            case AddressingMode.Immediate:
                return size switch
                {
                    1 => (uint)(cpu.FetchWord() & 0xFF),
                    2 => cpu.FetchWord(),
                    _ => cpu.FetchLong()
                };

            default:
                {
                    uint addr = ResolveAddress(cpu, mode, reg, size);
                    return size switch
                    {
                        1 => cpu.ReadByte(addr),
                        2 => cpu.ReadWord(addr),
                        _ => cpu.ReadLong(addr)
                    };
                }
        }
    }

    // --- Read-Modify-Write support ---
    // For instructions that read a value, modify it, and write it back to the same EA,
    // the address must be resolved only ONCE. ReadValueForModify caches the resolved
    // address, and WriteValueFromModify reuses it instead of re-resolving (which would
    // consume extension words from the next instruction).

    private static uint _rmwAddress;

    public static uint ReadValueForModify(MC68030 cpu, AddressingMode mode, int reg, int size)
    {
        switch (mode)
        {
            case AddressingMode.DataRegDirect:
                return size switch
                {
                    1 => cpu.D[reg] & 0xFF,
                    2 => cpu.D[reg] & 0xFFFF,
                    _ => cpu.D[reg]
                };

            case AddressingMode.AddrRegDirect:
                return size switch
                {
                    2 => cpu.A[reg] & 0xFFFF,
                    _ => cpu.A[reg]
                };

            default:
                _rmwAddress = ResolveAddress(cpu, mode, reg, size);
                return size switch
                {
                    1 => cpu.ReadByte(_rmwAddress),
                    2 => cpu.ReadWord(_rmwAddress),
                    _ => cpu.ReadLong(_rmwAddress)
                };
        }
    }

    public static void WriteValueFromModify(MC68030 cpu, AddressingMode mode, int reg, int size, uint value)
    {
        switch (mode)
        {
            case AddressingMode.DataRegDirect:
                switch (size)
                {
                    case 1: cpu.D[reg] = (cpu.D[reg] & 0xFFFFFF00) | (value & 0xFF); break;
                    case 2: cpu.D[reg] = (cpu.D[reg] & 0xFFFF0000) | (value & 0xFFFF); break;
                    default: cpu.D[reg] = value; break;
                }
                break;

            case AddressingMode.AddrRegDirect:
                cpu.A[reg] = size == 2 ? (uint)(int)(short)(ushort)value : value;
                break;

            default:
                switch (size)
                {
                    case 1: cpu.WriteByte(_rmwAddress, (byte)value); break;
                    case 2: cpu.WriteWord(_rmwAddress, (ushort)value); break;
                    default: cpu.WriteLong(_rmwAddress, value); break;
                }
                break;
        }
    }

    public static void WriteValue(MC68030 cpu, AddressingMode mode, int reg, int size, uint value)
    {
        switch (mode)
        {
            case AddressingMode.DataRegDirect:
                switch (size)
                {
                    case 1:
                        cpu.D[reg] = (cpu.D[reg] & 0xFFFFFF00) | (value & 0xFF);
                        break;
                    case 2:
                        cpu.D[reg] = (cpu.D[reg] & 0xFFFF0000) | (value & 0xFFFF);
                        break;
                    default:
                        cpu.D[reg] = value;
                        break;
                }
                break;

            case AddressingMode.AddrRegDirect:
                cpu.A[reg] = size == 2 ? (uint)(int)(short)(ushort)value : value;
                break;

            default:
                {
                    uint addr = ResolveAddress(cpu, mode, reg, size);
                    switch (size)
                    {
                        case 1: cpu.WriteByte(addr, (byte)value); break;
                        case 2: cpu.WriteWord(addr, (ushort)value); break;
                        default: cpu.WriteLong(addr, value); break;
                    }
                }
                break;
        }
    }
}
