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

public class Disassembler
{
    private readonly Memory _memory;
    private uint _pc;

    public Disassembler(Memory memory)
    {
        _memory = memory;
    }

    public class DisasmLine
    {
        public uint Address { get; set; }
        public string RawBytes { get; set; } = "";
        public string Mnemonic { get; set; } = "";
        public string Operands { get; set; } = "";
        public int Length { get; set; }

        public override string ToString()
        {
            string ops = string.IsNullOrEmpty(Operands) ? "" : $" {Operands}";
            return $"{Address:X8}: {RawBytes,-20} {Mnemonic,-8}{ops}";
        }
    }

    public DisasmLine DisassembleOne(uint address)
    {
        _pc = address;
        var line = new DisasmLine { Address = address };

        ushort opcode = ReadWord();
        int group = (opcode >> 12) & 0xF;

        switch (group)
        {
            case 0x0: DisasmGroup0(opcode, line); break;
            case 0x1: DisasmMOVE(opcode, line, 1); break;
            case 0x2: DisasmMOVE(opcode, line, 4); break;
            case 0x3: DisasmMOVE(opcode, line, 2); break;
            case 0x4: DisasmGroup4(opcode, line); break;
            case 0x5: DisasmGroup5(opcode, line); break;
            case 0x6: DisasmGroup6(opcode, line); break;
            case 0x7: DisasmMOVEQ(opcode, line); break;
            case 0x8: DisasmGroup8(opcode, line); break;
            case 0x9: DisasmGroup9(opcode, line); break;
            case 0xA: line.Mnemonic = "DC.W"; line.Operands = $"${opcode:X4}"; break;
            case 0xB: DisasmGroupB(opcode, line); break;
            case 0xC: DisasmGroupC(opcode, line); break;
            case 0xD: DisasmGroupD(opcode, line); break;
            case 0xE: DisasmGroupE(opcode, line); break;
            case 0xF: DisasmGroupF(opcode, line); break;
        }

        line.Length = (int)(_pc - address);

        // Build raw bytes
        var bytes = new List<string>();
        for (uint i = address; i < _pc; i += 2)
        {
            bytes.Add($"{_memory.PeekWord(i):X4}");
        }
        line.RawBytes = string.Join(" ", bytes);

        return line;
    }

    public List<DisasmLine> Disassemble(uint startAddress, int count)
    {
        var lines = new List<DisasmLine>();
        uint addr = startAddress;
        for (int i = 0; i < count; i++)
        {
            var line = DisassembleOne(addr);
            lines.Add(line);
            addr += (uint)line.Length;
            if (line.Length == 0) { addr += 2; break; } // Safety
        }
        return lines;
    }

    public List<DisasmLine> DisassembleRange(uint startAddress, uint endAddress, int maxLines = 5000)
    {
        var lines = new List<DisasmLine>();
        uint addr = startAddress;
        while (addr < endAddress && lines.Count < maxLines)
        {
            var line = DisassembleOne(addr);
            lines.Add(line);
            addr += (uint)line.Length;
            if (line.Length == 0) { addr += 2; break; } // Safety
        }
        return lines;
    }

    private ushort ReadWord()
    {
        ushort val = _memory.PeekWord(_pc);
        _pc += 2;
        return val;
    }

    private uint ReadLong()
    {
        uint val = _memory.PeekLong(_pc);
        _pc += 4;
        return val;
    }

    private static readonly string[] CondCodes = {
        "T", "F", "HI", "LS", "CC", "CS", "NE", "EQ",
        "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE"
    };

    private static string SizeSuffix(int size) => size switch { 1 => ".B", 2 => ".W", 4 => ".L", _ => "" };

    private string FormatEA(int mode, int reg, int size)
    {
        switch (mode)
        {
            case 0: return $"D{reg}";
            case 1: return $"A{reg}";
            case 2: return $"(A{reg})";
            case 3: return $"(A{reg})+";
            case 4: return $"-(A{reg})";
            case 5:
                {
                    short disp = (short)ReadWord();
                    return $"({disp},A{reg})";
                }
            case 6:
                return FormatIndexed($"A{reg}");
            case 7:
                switch (reg)
                {
                    case 0:
                        {
                            ushort addr = ReadWord();
                            return $"(${addr:X4}).W";
                        }
                    case 1:
                        {
                            uint addr = ReadLong();
                            return $"(${addr:X8}).L";
                        }
                    case 2:
                        {
                            uint pcBefore = _pc;
                            short disp = (short)ReadWord();
                            return $"(${(uint)(pcBefore + disp):X8},PC)";
                        }
                    case 3:
                        return FormatIndexed("PC");
                    case 4:
                        {
                            if (size == 1)
                            {
                                ushort val = ReadWord();
                                return $"#${val & 0xFF:X2}";
                            }
                            else if (size == 2)
                            {
                                ushort val = ReadWord();
                                return $"#${val:X4}";
                            }
                            else
                            {
                                uint val = ReadLong();
                                return $"#${val:X8}";
                            }
                        }
                    default:
                        return "???";
                }
            default:
                return "???";
        }
    }

    private string FormatIndexed(string baseReg)
    {
        ushort ext = ReadWord();
        bool isLong = (ext & 0x0800) != 0;
        int indexReg = (ext >> 12) & 0xF;
        bool isAddr = (ext & 0x8000) != 0;
        int scale = 1 << ((ext >> 9) & 3);
        string idxReg = isAddr ? $"A{indexReg & 7}" : $"D{indexReg & 7}";
        string idxSize = isLong ? ".L" : ".W";
        string scaleStr = scale > 1 ? $"*{scale}" : "";

        if ((ext & 0x0100) != 0)
        {
            // Full extension
            int bdSize = (ext >> 4) & 3;
            bool bs = (ext & 0x0080) != 0;
            bool is_ = (ext & 0x0040) != 0;
            string bd = "";
            if (bdSize == 2) bd = $"${(short)ReadWord():X}";
            else if (bdSize == 3) bd = $"${ReadLong():X8}";

            string bStr = bs ? "" : baseReg;
            string iStr = is_ ? "" : $"{idxReg}{idxSize}{scaleStr}";

            if ((ext & 0x0004) == 0)
                return $"({bd},{bStr},{iStr})";
            else
            {
                int iis = ext & 3;
                string od = "";
                if (iis == 2) od = $"${(short)ReadWord():X}";
                else if (iis == 3) od = $"${ReadLong():X8}";
                return $"([{bd},{bStr}],{iStr},{od})";
            }
        }
        else
        {
            sbyte disp = (sbyte)(ext & 0xFF);
            return $"({disp},{baseReg},{idxReg}{idxSize}{scaleStr})";
        }
    }

    private string FormatRegisterList(ushort mask, bool reverse)
    {
        var regs = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            int bit = reverse ? (15 - i) : i;
            if ((mask & (1 << i)) != 0)
            {
                string name = bit < 8 ? $"D{bit}" : $"A{bit - 8}";
                regs.Add(name);
            }
        }
        return string.Join("/", regs);
    }

    // ====================================================================
    // Group disassemblers
    // ====================================================================
    private void DisasmGroup0(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if ((opcode & 0x0100) != 0)
        {
            if (mode == 1)
            {
                // MOVEP
                short disp = (short)ReadWord();
                int opMode = (opcode >> 6) & 7;
                string suf = (opMode == 4 || opMode == 6) ? ".W" : ".L";
                if (opMode >= 6)
                    line.Mnemonic = $"MOVEP{suf}";
                else
                    line.Mnemonic = $"MOVEP{suf}";

                if (opMode >= 6)
                    line.Operands = $"D{reg},({disp},A{eaReg})";
                else
                    line.Operands = $"({disp},A{eaReg}),D{reg}";
                return;
            }
            string[] bitOps = { "BTST", "BCHG", "BCLR", "BSET" };
            int op = (opcode >> 6) & 3;
            line.Mnemonic = bitOps[op];
            string ea = FormatEA(mode, eaReg, mode == 0 ? 4 : 1);
            line.Operands = $"D{reg},{ea}";
            return;
        }

        switch (reg)
        {
            case 0:
                if (((opcode >> 6) & 3) == 3) DisasmCMP2_CHK2(opcode, line, 1);
                else DisasmImmediate(opcode, line, "ORI");
                break;
            case 1:
                if (((opcode >> 6) & 3) == 3) DisasmCMP2_CHK2(opcode, line, 2);
                else DisasmImmediate(opcode, line, "ANDI");
                break;
            case 2:
                if (((opcode >> 6) & 3) == 3) DisasmCMP2_CHK2(opcode, line, 4);
                else DisasmImmediate(opcode, line, "SUBI");
                break;
            case 3: DisasmImmediate(opcode, line, "ADDI"); break;
            case 4:
                {
                    string[] bitOps = { "BTST", "BCHG", "BCLR", "BSET" };
                    int bitOp = (opcode >> 6) & 3;
                    int bitNum = ReadWord() & 0xFF;
                    line.Mnemonic = bitOps[bitOp];
                    string ea = FormatEA(mode, eaReg, mode == 0 ? 4 : 1);
                    line.Operands = $"#${bitNum:X2},{ea}";
                }
                break;
            case 5:
                if (((opcode >> 6) & 3) == 3) DisasmCAS(opcode, line, 1);
                else DisasmImmediate(opcode, line, "EORI");
                break;
            case 6:
                if (((opcode >> 6) & 3) == 3) DisasmCAS(opcode, line, 2);
                else DisasmImmediate(opcode, line, "CMPI");
                break;
            case 7:
                if (((opcode >> 6) & 3) == 3) DisasmCAS(opcode, line, 4);
                else
                {
                    ushort ext = ReadWord();
                    int rn = (ext >> 12) & 0xF;
                    int size = GetSize2(opcode);
                    line.Mnemonic = $"MOVES{SizeSuffix(size)}";
                    string ea = FormatEA(mode, eaReg, size);
                    string rStr = rn < 8 ? $"D{rn}" : $"A{rn - 8}";
                    if ((ext & 0x0800) != 0)
                        line.Operands = $"{rStr},{ea}";
                    else
                        line.Operands = $"{ea},{rStr}";
                }
                break;
        }
    }

    private void DisasmImmediate(ushort opcode, DisasmLine line, string name)
    {
        int size = GetSize2(opcode);
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;

        if (mode == 7 && reg == 4)
        {
            ushort imm = ReadWord();
            if (size == 1) { line.Mnemonic = $"{name}"; line.Operands = $"#${imm & 0xFF:X2},CCR"; }
            else { line.Mnemonic = $"{name}"; line.Operands = $"#${imm:X4},SR"; }
            return;
        }

        string immStr;
        if (size == 1) { ushort v = ReadWord(); immStr = $"#${v & 0xFF:X2}"; }
        else if (size == 2) { ushort v = ReadWord(); immStr = $"#${v:X4}"; }
        else { uint v = ReadLong(); immStr = $"#${v:X8}"; }

        string ea = FormatEA(mode, reg, size);
        line.Mnemonic = $"{name}{SizeSuffix(size)}";
        line.Operands = $"{immStr},{ea}";
    }

    private void DisasmMOVE(ushort opcode, DisasmLine line, int size)
    {
        int srcMode = (opcode >> 3) & 7;
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;
        int dstMode = (opcode >> 6) & 7;

        string src = FormatEA(srcMode, srcReg, size);

        if (dstMode == 1)
        {
            line.Mnemonic = $"MOVEA{SizeSuffix(size)}";
            line.Operands = $"{src},A{dstReg}";
            return;
        }

        string dst = FormatEA(dstMode, dstReg, size);
        line.Mnemonic = $"MOVE{SizeSuffix(size)}";
        line.Operands = $"{src},{dst}";
    }

    private void DisasmGroup4(ushort opcode, DisasmLine line)
    {
        // Special instructions
        if ((opcode & 0xFFC0) == 0x40C0) { line.Mnemonic = "MOVE"; line.Operands = $"SR,{FormatEA((opcode >> 3) & 7, opcode & 7, 2)}"; return; }
        if ((opcode & 0xFFC0) == 0x44C0) { line.Mnemonic = "MOVE"; line.Operands = $"{FormatEA((opcode >> 3) & 7, opcode & 7, 2)},CCR"; return; }
        if ((opcode & 0xFFC0) == 0x46C0) { line.Mnemonic = "MOVE"; line.Operands = $"{FormatEA((opcode >> 3) & 7, opcode & 7, 2)},SR"; return; }

        if ((opcode & 0xFF00) == 0x4000 && ((opcode >> 6) & 3) != 3)
        {
            int size = GetSize2(opcode);
            line.Mnemonic = $"NEGX{SizeSuffix(size)}";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, size);
            return;
        }
        if ((opcode & 0xFF00) == 0x4200 && ((opcode >> 6) & 3) != 3)
        {
            int size = GetSize2(opcode);
            line.Mnemonic = $"CLR{SizeSuffix(size)}";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, size);
            return;
        }
        if ((opcode & 0xFF00) == 0x4400 && ((opcode >> 6) & 3) != 3)
        {
            int size = GetSize2(opcode);
            line.Mnemonic = $"NEG{SizeSuffix(size)}";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, size);
            return;
        }
        if ((opcode & 0xFF00) == 0x4600 && ((opcode >> 6) & 3) != 3)
        {
            int size = GetSize2(opcode);
            line.Mnemonic = $"NOT{SizeSuffix(size)}";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, size);
            return;
        }

        if ((opcode & 0xFFF8) == 0x4880) { line.Mnemonic = "EXT.W"; line.Operands = $"D{opcode & 7}"; return; }
        if ((opcode & 0xFFF8) == 0x48C0) { line.Mnemonic = "EXT.L"; line.Operands = $"D{opcode & 7}"; return; }
        if ((opcode & 0xFFF8) == 0x49C0) { line.Mnemonic = "EXTB.L"; line.Operands = $"D{opcode & 7}"; return; }

        if ((opcode & 0xFFF8) == 0x4840) { line.Mnemonic = "SWAP"; line.Operands = $"D{opcode & 7}"; return; }

        if ((opcode & 0xFFC0) == 0x4840)
        {
            line.Mnemonic = "PEA";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, 4);
            return;
        }

        if ((opcode & 0xFF00) == 0x4A00 && ((opcode >> 6) & 3) != 3)
        {
            int size = GetSize2(opcode);
            line.Mnemonic = $"TST{SizeSuffix(size)}";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, size);
            return;
        }
        if ((opcode & 0xFFC0) == 0x4AC0) { line.Mnemonic = "TAS"; line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, 1); return; }

        if (opcode == 0x4AFC) { line.Mnemonic = "ILLEGAL"; return; }

        // MOVEM
        if ((opcode & 0xFB80) == 0x4880)
        {
            bool isLong = (opcode & 0x0040) != 0;
            bool toRegs = (opcode & 0x0400) != 0;
            ushort mask = ReadWord();
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;
            bool reverse = (mode == 4);

            string regs = FormatRegisterList(mask, reverse);
            string ea = FormatEA(mode, reg, isLong ? 4 : 2);

            line.Mnemonic = isLong ? "MOVEM.L" : "MOVEM.W";
            if (toRegs)
                line.Operands = $"{ea},{regs}";
            else
                line.Operands = $"{regs},{ea}";
            return;
        }

        // LEA
        if ((opcode & 0xF1C0) == 0x41C0)
        {
            int areg = (opcode >> 9) & 7;
            line.Mnemonic = "LEA";
            line.Operands = $"{FormatEA((opcode >> 3) & 7, opcode & 7, 4)},A{areg}";
            return;
        }

        // CHK
        if ((opcode & 0xF1C0) == 0x4180)
        {
            int dreg = (opcode >> 9) & 7;
            line.Mnemonic = "CHK.W";
            line.Operands = $"{FormatEA((opcode >> 3) & 7, opcode & 7, 2)},D{dreg}";
            return;
        }

        // LINK/UNLK
        if ((opcode & 0xFFF8) == 0x4E50)
        {
            short disp = (short)ReadWord();
            line.Mnemonic = "LINK";
            line.Operands = $"A{opcode & 7},#${disp:X4}";
            return;
        }
        if ((opcode & 0xFFF8) == 0x4808)
        {
            int disp = (int)ReadLong();
            line.Mnemonic = "LINK.L";
            line.Operands = $"A{opcode & 7},#${disp:X8}";
            return;
        }
        if ((opcode & 0xFFF8) == 0x4E58) { line.Mnemonic = "UNLK"; line.Operands = $"A{opcode & 7}"; return; }

        // MOVE USP
        if ((opcode & 0xFFF0) == 0x4E60)
        {
            if ((opcode & 0x0008) != 0)
                { line.Mnemonic = "MOVE"; line.Operands = $"USP,A{opcode & 7}"; }
            else
                { line.Mnemonic = "MOVE"; line.Operands = $"A{opcode & 7},USP"; }
            return;
        }

        if (opcode == 0x4E70) { line.Mnemonic = "RESET"; return; }
        if (opcode == 0x4E71) { line.Mnemonic = "NOP"; return; }
        if (opcode == 0x4E72) { ushort imm = ReadWord(); line.Mnemonic = "STOP"; line.Operands = $"#${imm:X4}"; return; }
        if (opcode == 0x4E73) { line.Mnemonic = "RTE"; return; }
        if (opcode == 0x4E74) { short disp = (short)ReadWord(); line.Mnemonic = "RTD"; line.Operands = $"#${disp:X4}"; return; }
        if (opcode == 0x4E75) { line.Mnemonic = "RTS"; return; }
        if (opcode == 0x4E77) { line.Mnemonic = "RTR"; return; }

        // MOVEC
        if ((opcode & 0xFFFE) == 0x4E7A)
        {
            ushort ext = ReadWord();
            int rn = (ext >> 12) & 0xF;
            int creg = ext & 0xFFF;
            string rStr = rn < 8 ? $"D{rn}" : $"A{rn - 8}";
            string cStr = creg switch
            {
                0x000 => "SFC", 0x001 => "DFC", 0x002 => "CACR",
                0x800 => "USP", 0x801 => "VBR", 0x802 => "CAAR",
                0x803 => "MSP", 0x804 => "ISP",
                _ => $"CR${creg:X3}"
            };
            line.Mnemonic = "MOVEC";
            // 0x4E7A = MOVEC Rc,Rn (read control reg), 0x4E7B = MOVEC Rn,Rc (write control reg)
            if ((opcode & 1) == 0)
                line.Operands = $"{cStr},{rStr}";
            else
                line.Operands = $"{rStr},{cStr}";
            return;
        }

        // TRAP
        if ((opcode & 0xFFF0) == 0x4E40)
        {
            line.Mnemonic = "TRAP";
            line.Operands = $"#{opcode & 0xF}";
            return;
        }

        // JSR
        if ((opcode & 0xFFC0) == 0x4E80)
        {
            line.Mnemonic = "JSR";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, 4);
            return;
        }

        // JMP
        if ((opcode & 0xFFC0) == 0x4EC0)
        {
            line.Mnemonic = "JMP";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, 4);
            return;
        }

        // NBCD
        if ((opcode & 0xFFC0) == 0x4800)
        {
            line.Mnemonic = "NBCD";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, 1);
            return;
        }

        // MULS.L / MULU.L (68020+)
        if ((opcode & 0xFFC0) == 0x4C00)
        {
            ushort ext = ReadWord();
            int dl = (ext >> 12) & 7;
            int dh = ext & 7;
            bool signed_ = (ext & 0x0800) != 0;
            bool quad = (ext & 0x0400) != 0;
            line.Mnemonic = signed_ ? "MULS.L" : "MULU.L";
            string ea = FormatEA((opcode >> 3) & 7, opcode & 7, 4);
            if (quad)
                line.Operands = $"{ea},D{dh}:D{dl}";
            else
                line.Operands = $"{ea},D{dl}";
            return;
        }

        // DIVS.L / DIVU.L (68020+)
        if ((opcode & 0xFFC0) == 0x4C40)
        {
            ushort ext = ReadWord();
            int dq = (ext >> 12) & 7;
            int dr = ext & 7;
            bool signed_ = (ext & 0x0800) != 0;
            bool quad = (ext & 0x0400) != 0;
            line.Mnemonic = signed_ ? "DIVS.L" : "DIVU.L";
            string ea = FormatEA((opcode >> 3) & 7, opcode & 7, 4);
            if (quad || dr != dq)
                line.Operands = $"{ea},D{dr}:D{dq}";
            else
                line.Operands = $"{ea},D{dq}";
            return;
        }

        line.Mnemonic = "DC.W";
        line.Operands = $"${opcode:X4}";
    }

    private void DisasmGroup5(ushort opcode, DisasmLine line)
    {
        int sizeField = (opcode >> 6) & 3;
        int cond = (opcode >> 8) & 0xF;

        if (sizeField == 3)
        {
            int mode = (opcode >> 3) & 7;
            int reg = opcode & 7;

            if (mode == 1)
            {
                uint pcBefore = _pc;
                short disp = (short)ReadWord();
                line.Mnemonic = $"DB{CondCodes[cond]}";
                line.Operands = $"D{reg},${(uint)(pcBefore + disp):X8}";
                return;
            }

            if (mode == 7 && reg >= 2 && reg <= 4)
            {
                line.Mnemonic = $"TRAP{CondCodes[cond]}";
                if (reg == 2) { ushort w = ReadWord(); line.Operands = $"#${w:X4}"; }
                else if (reg == 3) { uint l = ReadLong(); line.Operands = $"#${l:X8}"; }
                return;
            }

            line.Mnemonic = $"S{CondCodes[cond]}";
            line.Operands = FormatEA(mode, reg, 1);
            return;
        }

        int data = (opcode >> 9) & 7;
        if (data == 0) data = 8;
        int size = sizeField switch { 0 => 1, 1 => 2, _ => 4 };
        bool isSub = (opcode & 0x0100) != 0;

        line.Mnemonic = $"{(isSub ? "SUBQ" : "ADDQ")}{SizeSuffix(size)}";
        int eaMode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;
        string ea = FormatEA(eaMode, eaReg, size);
        line.Operands = $"#{data},{ea}";
    }

    private void DisasmGroup6(ushort opcode, DisasmLine line)
    {
        int cond = (opcode >> 8) & 0xF;
        int disp8 = (sbyte)(opcode & 0xFF);
        uint savedPC = _pc;

        int displacement;
        string suffix;
        if (disp8 == 0) { displacement = (short)ReadWord(); suffix = ".W"; }
        else if (disp8 == -1) { displacement = (int)ReadLong(); suffix = ".L"; }
        else { displacement = disp8; suffix = ".S"; }

        uint target = (uint)(savedPC + displacement);

        string mnemonic = cond switch
        {
            0 => "BRA",
            1 => "BSR",
            _ => $"B{CondCodes[cond]}"
        };

        line.Mnemonic = $"{mnemonic}{suffix}";
        line.Operands = $"${target:X8}";
    }

    private void DisasmMOVEQ(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int data = (sbyte)(opcode & 0xFF);
        line.Mnemonic = "MOVEQ";
        line.Operands = $"#${(byte)(opcode & 0xFF):X2},D{reg}";
    }

    private void DisasmGroup8(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if (opMode == 3) { line.Mnemonic = "DIVU.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},D{reg}"; return; }
        if (opMode == 7) { line.Mnemonic = "DIVS.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},D{reg}"; return; }
        if (opMode == 4 && (mode == 0 || mode == 1))
        {
            line.Mnemonic = "SBCD";
            if (mode == 0) line.Operands = $"D{eaReg},D{reg}";
            else line.Operands = $"-(A{eaReg}),-(A{reg})";
            return;
        }
        if (opMode == 5 && (mode == 0 || mode == 1))
        {
            ushort adj = ReadWord();
            line.Mnemonic = "PACK";
            if (mode == 0) line.Operands = $"D{eaReg},D{reg},#${adj:X4}";
            else line.Operands = $"-(A{eaReg}),-(A{reg}),#${adj:X4}";
            return;
        }
        if (opMode == 6 && (mode == 0 || mode == 1))
        {
            ushort adj = ReadWord();
            line.Mnemonic = "UNPK";
            if (mode == 0) line.Operands = $"D{eaReg},D{reg},#${adj:X4}";
            else line.Operands = $"-(A{eaReg}),-(A{reg}),#${adj:X4}";
            return;
        }

        int size = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
        line.Mnemonic = $"OR{SizeSuffix(size)}";
        string ea = FormatEA(mode, eaReg, size);
        if ((opMode & 4) != 0) line.Operands = $"D{reg},{ea}";
        else line.Operands = $"{ea},D{reg}";
    }

    private void DisasmGroup9(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if (opMode == 3) { line.Mnemonic = "SUBA.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},A{reg}"; return; }
        if (opMode == 7) { line.Mnemonic = "SUBA.L"; line.Operands = $"{FormatEA(mode, eaReg, 4)},A{reg}"; return; }

        if ((opMode == 4 || opMode == 5 || opMode == 6) && (mode == 0 || mode == 1))
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            line.Mnemonic = $"SUBX{SizeSuffix(size)}";
            if (mode == 0) line.Operands = $"D{eaReg},D{reg}";
            else line.Operands = $"-(A{eaReg}),-(A{reg})";
            return;
        }

        int sz = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
        line.Mnemonic = $"SUB{SizeSuffix(sz)}";
        string ea = FormatEA(mode, eaReg, sz);
        if ((opMode & 4) != 0) line.Operands = $"D{reg},{ea}";
        else line.Operands = $"{ea},D{reg}";
    }

    private void DisasmGroupB(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if (opMode == 3) { line.Mnemonic = "CMPA.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},A{reg}"; return; }
        if (opMode == 7) { line.Mnemonic = "CMPA.L"; line.Operands = $"{FormatEA(mode, eaReg, 4)},A{reg}"; return; }

        if ((opMode == 4 || opMode == 5 || opMode == 6) && mode == 1)
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            line.Mnemonic = $"CMPM{SizeSuffix(size)}";
            line.Operands = $"(A{eaReg})+,(A{reg})+";
            return;
        }

        if (opMode >= 4)
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            line.Mnemonic = $"EOR{SizeSuffix(size)}";
            line.Operands = $"D{reg},{FormatEA(mode, eaReg, size)}";
            return;
        }

        int sz = opMode switch { 0 => 1, 1 => 2, _ => 4 };
        line.Mnemonic = $"CMP{SizeSuffix(sz)}";
        line.Operands = $"{FormatEA(mode, eaReg, sz)},D{reg}";
    }

    private void DisasmGroupC(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if (opMode == 3) { line.Mnemonic = "MULU.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},D{reg}"; return; }
        if (opMode == 7) { line.Mnemonic = "MULS.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},D{reg}"; return; }

        if (opMode == 4 && (mode == 0 || mode == 1))
        {
            line.Mnemonic = "ABCD";
            if (mode == 0) line.Operands = $"D{eaReg},D{reg}";
            else line.Operands = $"-(A{eaReg}),-(A{reg})";
            return;
        }

        if (opMode == 5 && mode == 0) { line.Mnemonic = "EXG"; line.Operands = $"D{reg},D{eaReg}"; return; }
        if (opMode == 5 && mode == 1) { line.Mnemonic = "EXG"; line.Operands = $"A{reg},A{eaReg}"; return; }
        if (opMode == 6 && mode == 1) { line.Mnemonic = "EXG"; line.Operands = $"D{reg},A{eaReg}"; return; }

        int size = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
        line.Mnemonic = $"AND{SizeSuffix(size)}";
        string ea = FormatEA(mode, eaReg, size);
        if ((opMode & 4) != 0) line.Operands = $"D{reg},{ea}";
        else line.Operands = $"{ea},D{reg}";
    }

    private void DisasmGroupD(ushort opcode, DisasmLine line)
    {
        int reg = (opcode >> 9) & 7;
        int opMode = (opcode >> 6) & 7;
        int mode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        if (opMode == 3) { line.Mnemonic = "ADDA.W"; line.Operands = $"{FormatEA(mode, eaReg, 2)},A{reg}"; return; }
        if (opMode == 7) { line.Mnemonic = "ADDA.L"; line.Operands = $"{FormatEA(mode, eaReg, 4)},A{reg}"; return; }

        if ((opMode == 4 || opMode == 5 || opMode == 6) && (mode == 0 || mode == 1))
        {
            int size = opMode switch { 4 => 1, 5 => 2, _ => 4 };
            line.Mnemonic = $"ADDX{SizeSuffix(size)}";
            if (mode == 0) line.Operands = $"D{eaReg},D{reg}";
            else line.Operands = $"-(A{eaReg}),-(A{reg})";
            return;
        }

        int sz = opMode switch { 0 => 1, 1 => 2, 2 => 4, 4 => 1, 5 => 2, 6 => 4, _ => 2 };
        line.Mnemonic = $"ADD{SizeSuffix(sz)}";
        string ea = FormatEA(mode, eaReg, sz);
        if ((opMode & 4) != 0) line.Operands = $"D{reg},{ea}";
        else line.Operands = $"{ea},D{reg}";
    }

    private void DisasmGroupE(ushort opcode, DisasmLine line)
    {
        int sizeField = (opcode >> 6) & 3;

        if (sizeField == 3)
        {
            if ((opcode & 0x0800) != 0)
            {
                // Bit field instructions
                DisasmBitField(opcode, line);
                return;
            }

            string[] ops = { "ASd", "LSd", "ROXd", "ROd" };
            int type = (opcode >> 9) & 3;
            bool left = (opcode & 0x0100) != 0;
            string dir = left ? "L" : "R";
            line.Mnemonic = ops[type].Replace("d", dir) + ".W";
            line.Operands = FormatEA((opcode >> 3) & 7, opcode & 7, 2);
            return;
        }

        int count = (opcode >> 9) & 7;
        bool ir = (opcode & 0x0020) != 0;
        int dreg = opcode & 7;
        int size = sizeField switch { 0 => 1, 1 => 2, _ => 4 };
        bool isLeft = (opcode & 0x0100) != 0;
        int shiftType = (opcode >> 3) & 3;

        string[] shiftOps = { "AS", "LS", "ROX", "RO" };
        string dir2 = isLeft ? "L" : "R";
        line.Mnemonic = $"{shiftOps[shiftType]}{dir2}{SizeSuffix(size)}";

        if (ir)
            line.Operands = $"D{count},D{dreg}";
        else
        {
            int cnt = count == 0 ? 8 : count;
            line.Operands = $"#{cnt},D{dreg}";
        }
    }

    private void DisasmGroupF(ushort opcode, DisasmLine line)
    {
        int cpId = (opcode >> 9) & 7;

        if (cpId == 0) // MMU
        {
            DisasmMMU(opcode, line);
            return;
        }

        if (cpId == 1) // FPU
        {
            DisasmFPU(opcode, line);
            return;
        }

        line.Mnemonic = "DC.W";
        line.Operands = $"${opcode:X4}";
    }

    private static readonly string[] MmuTTRegNames = { "", "", "TT0", "TT1" };
    private static readonly string[] MmuTCSRegNames = { "TC", "", "SRP", "CRP" };

    private void DisasmMMU(ushort opcode, DisasmLine line)
    {
        ushort ext = ReadWord();
        int mmuOp = (ext >> 13) & 7;
        string ea = FormatEA((opcode >> 3) & 7, opcode & 7, 4);
        bool toMem = (ext & 0x0200) != 0;

        switch (mmuOp)
        {
            case 0: // PMOVE TT0/TT1
            {
                int pmReg = (ext >> 10) & 7;
                string regName = (pmReg >= 0 && pmReg < MmuTTRegNames.Length && MmuTTRegNames[pmReg] != "")
                    ? MmuTTRegNames[pmReg] : $"TT?{pmReg}";
                line.Mnemonic = "PMOVE";
                line.Operands = toMem ? $"{regName},{ea}" : $"{ea},{regName}";
                return;
            }

            case 1: // PFLUSH / PFLUSHA / PLOAD
            {
                if (ext == 0x2400)
                {
                    line.Mnemonic = "PFLUSHA";
                    return;
                }
                // PLOAD: bits 12-11=00, bits 4-1=0000
                if ((ext & 0x1800) == 0 && (ext & 0x001E) == 0)
                {
                    bool isRead = (ext & 0x0200) != 0;
                    line.Mnemonic = isRead ? "PLOADR" : "PLOADW";
                    line.Operands = $"#FC,{ea}";
                    return;
                }
                // PFLUSH
                byte mask = (byte)((ext >> 5) & 0xF);
                bool hasEA = (ext & 0x0010) != 0;
                line.Mnemonic = "PFLUSH";
                line.Operands = hasEA ? $"#FC,#{mask},{ea}" : $"#FC,#{mask}";
                return;
            }

            case 2: // PMOVE TC/SRP/CRP
            {
                int pmReg = (ext >> 10) & 7;
                string regName = (pmReg >= 0 && pmReg < MmuTCSRegNames.Length && MmuTCSRegNames[pmReg] != "")
                    ? MmuTCSRegNames[pmReg] : $"???{pmReg}";
                int regSize = (pmReg == 0) ? 4 : 8; // TC=4bytes, SRP/CRP=8bytes
                line.Mnemonic = "PMOVE";
                if (regSize == 8 && !toMem)
                    ea = FormatEA((opcode >> 3) & 7, opcode & 7, 4); // 8-byte read
                line.Operands = toMem ? $"{regName},{ea}" : $"{ea},{regName}";
                return;
            }

            case 3: // PMOVE MMUSR
            {
                string ea16 = FormatEA((opcode >> 3) & 7, opcode & 7, 2);
                line.Mnemonic = "PMOVE";
                line.Operands = toMem ? $"MMUSR,{ea16}" : $"{ea16},MMUSR";
                return;
            }

            case 4: // PTEST
            {
                int level = (ext >> 10) & 7;
                bool isRead = (ext & 0x0200) != 0;
                bool hasAReg = (ext & 0x0100) != 0;
                int aReg = (ext >> 5) & 7;
                line.Mnemonic = isRead ? "PTESTR" : "PTESTW";
                line.Operands = hasAReg ? $"#FC,{ea},#{level},A{aReg}" : $"#FC,{ea},#{level}";
                return;
            }
        }

        line.Mnemonic = "DC.W";
        line.Operands = $"${opcode:X4}";
    }

    private static readonly string[] FpuCondNames =
    {
        "F","EQ","OGT","OGE","OLT","OLE","OGL","OR",
        "UN","UEQ","UGT","UGE","ULT","ULE","NE","T",
        "SF","SEQ","GT","GE","LT","LE","GL","GLE",
        "NGLE","NGL","NLE","NLT","NGE","NGT","SNE","ST"
    };

    private static readonly string[] FpuOpNames = new string[128];

    static Disassembler()
    {
        for (int i = 0; i < 128; i++) FpuOpNames[i] = "";
        FpuOpNames[0x00] = "FMOVE";
        FpuOpNames[0x01] = "FINT";
        FpuOpNames[0x02] = "FSINH";
        FpuOpNames[0x03] = "FINTRZ";
        FpuOpNames[0x04] = "FSQRT";
        FpuOpNames[0x06] = "FLOGNP1";
        FpuOpNames[0x08] = "FETOXM1";
        FpuOpNames[0x09] = "FTANH";
        FpuOpNames[0x0A] = "FATAN";
        FpuOpNames[0x0C] = "FASIN";
        FpuOpNames[0x0D] = "FATANH";
        FpuOpNames[0x0E] = "FSIN";
        FpuOpNames[0x0F] = "FTAN";
        FpuOpNames[0x10] = "FETOX";
        FpuOpNames[0x11] = "FTWOTOX";
        FpuOpNames[0x12] = "FTENTOX";
        FpuOpNames[0x14] = "FLOGN";
        FpuOpNames[0x15] = "FLOG10";
        FpuOpNames[0x16] = "FLOG2";
        FpuOpNames[0x18] = "FABS";
        FpuOpNames[0x19] = "FCOSH";
        FpuOpNames[0x1A] = "FNEG";
        FpuOpNames[0x1C] = "FACOS";
        FpuOpNames[0x1D] = "FCOS";
        FpuOpNames[0x1E] = "FGETEXP";
        FpuOpNames[0x1F] = "FGETMAN";
        FpuOpNames[0x20] = "FDIV";
        FpuOpNames[0x21] = "FMOD";
        FpuOpNames[0x22] = "FADD";
        FpuOpNames[0x23] = "FMUL";
        FpuOpNames[0x24] = "FSGLDIV";
        FpuOpNames[0x25] = "FREM";
        FpuOpNames[0x26] = "FSCALE";
        FpuOpNames[0x27] = "FSGLMUL";
        FpuOpNames[0x28] = "FSUB";
        FpuOpNames[0x38] = "FCMP";
        FpuOpNames[0x3A] = "FTST";
        for (int i = 0x30; i <= 0x37; i++) FpuOpNames[i] = "FSINCOS";
    }

    private void DisasmFPU(ushort opcode, DisasmLine line)
    {
        int type = (opcode >> 6) & 7;
        int eaMode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        switch (type)
        {
            case 0: // General FPU
                DisasmFPUGeneral(opcode, line, eaMode, eaReg);
                break;

            case 1: // FDBcc / FScc / FTRAPcc
            {
                ushort cmdWord = ReadWord();
                int cond = cmdWord & 0x3F;
                string condName = cond < FpuCondNames.Length ? FpuCondNames[cond] : $"#{cond}";

                if (eaMode == 1) // FDBcc
                {
                    short disp = (short)ReadWord();
                    uint target = (uint)(_pc - 2 + disp);
                    line.Mnemonic = $"FDB{condName}";
                    line.Operands = $"D{eaReg},${target:X8}";
                }
                else if (eaMode == 7 && eaReg == 2) // FTRAPcc.W
                {
                    ushort imm = ReadWord();
                    line.Mnemonic = $"FTRAP{condName}.W";
                    line.Operands = $"#${imm:X4}";
                }
                else if (eaMode == 7 && eaReg == 3) // FTRAPcc.L
                {
                    uint imm = ReadLong();
                    line.Mnemonic = $"FTRAP{condName}.L";
                    line.Operands = $"#${imm:X8}";
                }
                else if (eaMode == 7 && eaReg == 4) // FTRAPcc
                {
                    line.Mnemonic = $"FTRAP{condName}";
                }
                else // FScc
                {
                    line.Mnemonic = $"FS{condName}";
                    line.Operands = FormatEA(eaMode, eaReg, 1);
                }
                break;
            }

            case 2: // FBcc.W
            {
                int cond = opcode & 0x3F;
                short disp = (short)ReadWord();
                uint target = (uint)(_pc - 2 + disp);
                string condName = cond < FpuCondNames.Length ? FpuCondNames[cond] : $"#{cond}";
                line.Mnemonic = $"FB{condName}.W";
                line.Operands = $"${target:X8}";
                break;
            }

            case 3: // FBcc.L
            {
                int cond = opcode & 0x3F;
                int disp = (int)ReadLong();
                uint target = (uint)(_pc - 4 + disp);
                string condName = cond < FpuCondNames.Length ? FpuCondNames[cond] : $"#{cond}";
                line.Mnemonic = $"FB{condName}.L";
                line.Operands = $"${target:X8}";
                break;
            }

            case 4: // FSAVE
                line.Mnemonic = "FSAVE";
                line.Operands = FormatEA(eaMode, eaReg, 4);
                break;

            case 5: // FRESTORE
                line.Mnemonic = "FRESTORE";
                line.Operands = FormatEA(eaMode, eaReg, 4);
                break;

            default:
                line.Mnemonic = "DC.W";
                line.Operands = $"${opcode:X4}";
                break;
        }
    }

    private void DisasmFPUGeneral(ushort opcode, DisasmLine line, int eaMode, int eaReg)
    {
        ushort cmdWord = ReadWord();
        int cmdType = (cmdWord >> 13) & 7;

        switch (cmdType)
        {
            case 0: // Register to register
            {
                int srcReg = (cmdWord >> 10) & 7;
                int dstReg = (cmdWord >> 7) & 7;
                int op = cmdWord & 0x7F;
                string opName = GetFpuOpName(op);
                if (op == 0x3A) // FTST
                    line.Operands = $"FP{srcReg}";
                else if (op >= 0x30 && op <= 0x37)
                    line.Operands = $"FP{srcReg},FP{op & 7}:FP{dstReg}";
                else if (op == 0x00 && srcReg == dstReg)
                    line.Operands = $"FP{dstReg}";
                else
                    line.Operands = $"FP{srcReg},FP{dstReg}";
                line.Mnemonic = opName;
                break;
            }

            case 2: // EA to register
            {
                int srcFormat = (cmdWord >> 10) & 7;
                int dstReg = (cmdWord >> 7) & 7;
                int op = cmdWord & 0x7F;
                string opName = GetFpuOpName(op);
                string fmtSuffix = Fpu.FormatName(srcFormat);
                string ea = FormatEAForFpu(eaMode, eaReg, srcFormat);
                if (op == 0x3A) // FTST
                    line.Operands = ea;
                else if (op >= 0x30 && op <= 0x37)
                    line.Operands = $"{ea},FP{op & 7}:FP{dstReg}";
                else
                    line.Operands = $"{ea},FP{dstReg}";
                line.Mnemonic = opName + fmtSuffix;
                break;
            }

            case 3: // Register to EA (FMOVE)
            {
                int dstFormat = (cmdWord >> 10) & 7;
                int srcReg = (cmdWord >> 7) & 7;
                string fmtSuffix = Fpu.FormatName(dstFormat);
                string ea = FormatEAForFpu(eaMode, eaReg, dstFormat);
                line.Mnemonic = "FMOVE" + fmtSuffix;
                line.Operands = $"FP{srcReg},{ea}";
                break;
            }

            case 4: // EA to control register
            case 5: // Control register to EA
            {
                int regSelect = (cmdWord >> 10) & 7;
                string regList = FormatFpuControlRegs(regSelect);
                string ea = FormatEA(eaMode, eaReg, 4);
                if (cmdType == 4)
                {
                    line.Mnemonic = regSelect != 0 && (regSelect & (regSelect - 1)) != 0 ? "FMOVEM.L" : "FMOVE.L";
                    line.Operands = $"{ea},{regList}";
                }
                else
                {
                    line.Mnemonic = regSelect != 0 && (regSelect & (regSelect - 1)) != 0 ? "FMOVEM.L" : "FMOVE.L";
                    line.Operands = $"{regList},{ea}";
                }
                break;
            }

            case 6: // FMOVEM register list to EA
            {
                int regList = cmdWord & 0xFF;
                string ea = FormatEA(eaMode, eaReg, 12);
                bool predec = (eaMode == 4);
                line.Mnemonic = "FMOVEM.X";
                line.Operands = $"{FormatFpuRegList(regList, predec)},{ea}";
                break;
            }

            case 7: // FMOVEM EA to register list
            {
                int regList = cmdWord & 0xFF;
                string ea = FormatEA(eaMode, eaReg, 12);
                line.Mnemonic = "FMOVEM.X";
                line.Operands = $"{ea},{FormatFpuRegList(regList, false)}";
                break;
            }

            default:
                line.Mnemonic = "DC.W";
                line.Operands = $"${opcode:X4},${cmdWord:X4}";
                break;
        }
    }

    private string GetFpuOpName(int op)
    {
        if (op < FpuOpNames.Length && !string.IsNullOrEmpty(FpuOpNames[op]))
            return FpuOpNames[op];
        return $"FPU_OP${op:X2}";
    }

    private string FormatEAForFpu(int eaMode, int eaReg, int format)
    {
        int size = Fpu.FormatSize(format);
        if (eaMode == 7 && eaReg == 4) // Immediate
        {
            // Read immediate data based on format size
            switch (format)
            {
                case 0: // Long
                    return $"#${ReadLong():X8}";
                case 1: // Single
                {
                    uint bits = ReadLong();
                    float f = System.BitConverter.Int32BitsToSingle((int)bits);
                    return $"#${bits:X8} ({f:G})";
                }
                case 2: // Extended (12 bytes)
                {
                    uint w0 = ReadLong();
                    uint w1 = ReadLong();
                    uint w2 = ReadLong();
                    return $"#${w0:X8}{w1:X8}{w2:X8}";
                }
                case 3: // Packed (12 bytes)
                {
                    uint w0 = ReadLong();
                    uint w1 = ReadLong();
                    uint w2 = ReadLong();
                    return $"#${w0:X8}{w1:X8}{w2:X8}";
                }
                case 4: // Word
                    return $"#${ReadWord():X4}";
                case 5: // Double (8 bytes)
                {
                    uint hi = ReadLong();
                    uint lo = ReadLong();
                    long bits = ((long)hi << 32) | lo;
                    double d = System.BitConverter.Int64BitsToDouble(bits);
                    return $"#${hi:X8}{lo:X8} ({d:G})";
                }
                case 6: // Byte
                    return $"#${ReadWord() & 0xFF:X2}";
                default:
                    return FormatEA(eaMode, eaReg, size);
            }
        }
        return FormatEA(eaMode, eaReg, size);
    }

    private static string FormatFpuControlRegs(int select)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((select & 4) != 0) parts.Add("FPCR");
        if ((select & 2) != 0) parts.Add("FPSR");
        if ((select & 1) != 0) parts.Add("FPIAR");
        return parts.Count > 0 ? string.Join("/", parts) : "???";
    }

    private static string FormatFpuRegList(int mask, bool reverse)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (reverse)
        {
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) != 0) parts.Add($"FP{i}");
        }
        else
        {
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << (7 - i))) != 0) parts.Add($"FP{i}");
        }
        return parts.Count > 0 ? string.Join("/", parts) : "???";
    }

    private void DisasmCMP2_CHK2(ushort opcode, DisasmLine line, int size)
    {
        ushort ext = ReadWord();
        int rn = (ext >> 12) & 0xF;
        bool isChk = (ext & 0x0800) != 0;
        string rStr = rn < 8 ? $"D{rn}" : $"A{rn - 8}";
        line.Mnemonic = isChk ? $"CHK2{SizeSuffix(size)}" : $"CMP2{SizeSuffix(size)}";
        line.Operands = $"{FormatEA((opcode >> 3) & 7, opcode & 7, size)},{rStr}";
    }

    private void DisasmCAS(ushort opcode, DisasmLine line, int size)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;

        // CAS2 check
        if (mode == 7 && reg == 4)
        {
            ushort ext1 = ReadWord();
            ushort ext2 = ReadWord();
            int dc1 = ext1 & 7, du1 = (ext1 >> 6) & 7;
            int rn1 = (ext1 >> 12) & 0xF;
            int dc2 = ext2 & 7, du2 = (ext2 >> 6) & 7;
            int rn2 = (ext2 >> 12) & 0xF;
            string r1 = rn1 < 8 ? $"D{rn1}" : $"A{rn1 - 8}";
            string r2 = rn2 < 8 ? $"D{rn2}" : $"A{rn2 - 8}";
            line.Mnemonic = $"CAS2{SizeSuffix(size)}";
            line.Operands = $"D{dc1}:D{dc2},D{du1}:D{du2},({r1}):({r2})";
            return;
        }

        ushort ext = ReadWord();
        int dc = ext & 7;
        int du = (ext >> 6) & 7;
        string ea = FormatEA(mode, reg, size);
        line.Mnemonic = $"CAS{SizeSuffix(size)}";
        line.Operands = $"D{dc},D{du},{ea}";
    }

    private static readonly string[] BfOpNames = { "BFTST", "BFEXTU", "BFCHG", "BFEXTS", "BFCLR", "BFFFO", "BFSET", "BFINS" };

    private void DisasmBitField(ushort opcode, DisasmLine line)
    {
        int bfOp = (opcode >> 8) & 7;
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        ushort ext = ReadWord();

        int dnReg = (ext >> 12) & 7;
        bool doReg = (ext & 0x0800) != 0;
        bool dwReg = (ext & 0x0020) != 0;
        string offsetStr = doReg ? $"D{(ext >> 6) & 7}" : $"{(ext >> 6) & 0x1F}";
        int widthVal = ext & 0x1F;
        string widthStr = dwReg ? $"D{ext & 7}" : (widthVal == 0 ? "32" : $"{widthVal}");

        string ea = mode == 0 ? $"D{reg}" : FormatEA(mode, reg, 1);
        string bfSpec = $"{{{offsetStr}:{widthStr}}}";

        line.Mnemonic = BfOpNames[bfOp];
        switch (bfOp)
        {
            case 0: // BFTST
            case 2: // BFCHG
            case 4: // BFCLR
            case 6: // BFSET
                line.Operands = $"{ea}{bfSpec}";
                break;
            case 1: // BFEXTU
            case 3: // BFEXTS
            case 5: // BFFFO
                line.Operands = $"{ea}{bfSpec},D{dnReg}";
                break;
            case 7: // BFINS
                line.Operands = $"D{dnReg},{ea}{bfSpec}";
                break;
        }
    }

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
}
