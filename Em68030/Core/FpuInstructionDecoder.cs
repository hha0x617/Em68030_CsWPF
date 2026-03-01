namespace Em68030.Core;

using System;

/// <summary>
/// Decodes and executes MC68881/MC68882 FPU coprocessor instructions.
/// Called from InstructionDecoder when a Line-F with cpID=1 is detected.
/// </summary>
public class FpuInstructionDecoder
{
    private readonly MC68030 _cpu;
    private readonly Fpu _fpu;

    public FpuInstructionDecoder(MC68030 cpu, Fpu fpu)
    {
        _cpu = cpu;
        _fpu = fpu;
    }

    /// <summary>Decode and execute an FPU instruction. opcode is the first word already fetched.</summary>
    public void Execute(ushort opcode)
    {
        _fpu.FPIAR = _cpu.PC - 2; // Save instruction address

        int type = (opcode >> 6) & 7;
        int eaMode = (opcode >> 3) & 7;
        int eaReg = opcode & 7;

        switch (type)
        {
            case 0: // General FPU instructions
                ExecuteGeneral(opcode, eaMode, eaReg);
                break;

            case 1: // FDBcc / FScc / FTRAPcc
                ExecuteFSccDBccTRAPcc(opcode, eaMode, eaReg);
                break;

            case 2: // FBcc.W
                ExecuteFBcc(opcode, false);
                break;

            case 3: // FBcc.L
                ExecuteFBcc(opcode, true);
                break;

            case 4: // FSAVE (supervisor only)
                if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
                ExecuteFSave(opcode, eaMode, eaReg);
                break;

            case 5: // FRESTORE (supervisor only)
                if (!_cpu.SupervisorMode) { _cpu.RaiseException(8); return; }
                ExecuteFRestore(opcode, eaMode, eaReg);
                break;

            default:
                _cpu.RaiseException(11); // Line-F
                break;
        }
    }

    private void ExecuteGeneral(ushort opcode, int eaMode, int eaReg)
    {
        ushort cmdWord = _cpu.FetchWord();
        int cmdType = (cmdWord >> 13) & 7;

        switch (cmdType)
        {
            case 0: // Register to register
            {
                int srcReg = (cmdWord >> 10) & 7;
                int dstReg = (cmdWord >> 7) & 7;
                int op = cmdWord & 0x7F;
                double src = _fpu.FP[srcReg];
                ExecuteArithmetic(op, src, dstReg);
                break;
            }

            case 2: // EA to register (with operation)
            {
                int srcFormat = (cmdWord >> 10) & 7;
                int dstReg = (cmdWord >> 7) & 7;
                int op = cmdWord & 0x7F;
                double src = ReadEAFloat(eaMode, eaReg, srcFormat);
                ExecuteArithmetic(op, src, dstReg);
                break;
            }

            case 3: // Register to EA (FMOVE)
            {
                int dstFormat = (cmdWord >> 10) & 7;
                int srcReg = (cmdWord >> 7) & 7;
                double val = _fpu.FP[srcReg];
                _fpu.SetConditionCodes(val);
                WriteEAFloat(eaMode, eaReg, dstFormat, val);
                break;
            }

            case 4: // EA to control register (FMOVE/FMOVEM to FPCR/FPSR/FPIAR)
            {
                int regSelect = (cmdWord >> 10) & 7;
                var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
                if (regSelect == 0) break; // No register selected

                // May move multiple control registers
                if ((regSelect & 4) != 0) // FPCR
                {
                    _fpu.FPCR = EffectiveAddress.ReadValue(_cpu, mode, reg, 4);
                    if ((regSelect & 3) != 0)
                    {
                        // Advance address for next register
                        mode = AdvanceEA(mode, ref reg, 4, eaMode, eaReg);
                    }
                }
                if ((regSelect & 2) != 0) // FPSR
                {
                    _fpu.FPSR = EffectiveAddress.ReadValue(_cpu, mode, reg, 4);
                    if ((regSelect & 1) != 0)
                    {
                        mode = AdvanceEA(mode, ref reg, 4, eaMode, eaReg);
                    }
                }
                if ((regSelect & 1) != 0) // FPIAR
                {
                    _fpu.FPIAR = EffectiveAddress.ReadValue(_cpu, mode, reg, 4);
                }
                break;
            }

            case 5: // Control register to EA (FMOVE/FMOVEM from FPCR/FPSR/FPIAR)
            {
                int regSelect = (cmdWord >> 10) & 7;
                var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
                if (regSelect == 0) break;

                if ((regSelect & 4) != 0) // FPCR
                {
                    EffectiveAddress.WriteValue(_cpu, mode, reg, 4, _fpu.FPCR);
                    if ((regSelect & 3) != 0)
                        mode = AdvanceEA(mode, ref reg, 4, eaMode, eaReg);
                }
                if ((regSelect & 2) != 0) // FPSR
                {
                    EffectiveAddress.WriteValue(_cpu, mode, reg, 4, _fpu.FPSR);
                    if ((regSelect & 1) != 0)
                        mode = AdvanceEA(mode, ref reg, 4, eaMode, eaReg);
                }
                if ((regSelect & 1) != 0) // FPIAR
                {
                    EffectiveAddress.WriteValue(_cpu, mode, reg, 4, _fpu.FPIAR);
                }
                break;
            }

            case 6: // FMOVEM memory to FP register list (RESTORE)
                    // Per MC68881/68882 UM: dr=0 (bit 13=0) = memory to register
            {
                int listMode = (cmdWord >> 11) & 3;
                var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);

                if (listMode == 0 || listMode == 2) // Static list
                {
                    int regList = cmdWord & 0xFF;
                    bool postincrement = (eaMode == 3);
                    uint addr;
                    if (postincrement)
                        addr = _cpu.A[eaReg];
                    else
                        addr = EffectiveAddress.ResolveAddress(_cpu, mode, reg, 12);

                    // listMode 0 = predecrement order (reversed): bit 0=FP7
                    // listMode 2 = postincrement order (natural): bit 0=FP0
                    for (int i = 0; i < 8; i++)
                    {
                        if ((regList & (1 << i)) != 0)
                        {
                            int fpReg = (listMode == 0) ? 7 - i : i;
                            _fpu.FP[fpReg] = Fpu.ReadFromMemory(_cpu, addr, 2);
                            addr += 12;
                        }
                    }
                    if (postincrement) _cpu.A[eaReg] = addr;
                }
                else // Dynamic list (listMode 1 or 3)
                {
                    int dynReg = (cmdWord >> 4) & 7;
                    int regList = (int)(_cpu.D[dynReg] & 0xFF);
                    bool postincrement = (eaMode == 3);
                    uint addr;
                    if (postincrement)
                        addr = _cpu.A[eaReg];
                    else
                        addr = EffectiveAddress.ResolveAddress(_cpu, mode, reg, 12);

                    bool reversed = (listMode == 1);
                    for (int i = 0; i < 8; i++)
                    {
                        if ((regList & (1 << i)) != 0)
                        {
                            int fpReg = reversed ? 7 - i : i;
                            _fpu.FP[fpReg] = Fpu.ReadFromMemory(_cpu, addr, 2);
                            addr += 12;
                        }
                    }
                    if (postincrement) _cpu.A[eaReg] = addr;
                }
                break;
            }

            case 7: // FMOVEM FP register list to memory (SAVE)
                    // Per MC68881/68882 UM: dr=1 (bit 13=1) = register to memory
            {
                int listMode = (cmdWord >> 11) & 3;
                var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);

                if (listMode == 0 || listMode == 2) // Static list
                {
                    int regList = cmdWord & 0xFF;
                    bool predecrement = (eaMode == 4);

                    if (predecrement)
                    {
                        // Predecrement: manually handle address without ResolveAddress
                        // to avoid double-decrement. Reversed register order: bit 0=FP7.
                        uint addr = _cpu.A[eaReg];
                        for (int i = 0; i < 8; i++)
                        {
                            if ((regList & (1 << i)) != 0)
                            {
                                addr -= 12;
                                Fpu.WriteToMemory(_cpu, addr, 2, _fpu.FP[7 - i]);
                            }
                        }
                        _cpu.A[eaReg] = addr;
                    }
                    else
                    {
                        uint addr = EffectiveAddress.ResolveAddress(_cpu, mode, reg, 12);
                        // listMode 0 = predecrement order (reversed): bit 0=FP7
                        // listMode 2 = postincrement order (natural): bit 0=FP0
                        for (int i = 0; i < 8; i++)
                        {
                            if ((regList & (1 << i)) != 0)
                            {
                                int fpReg = (listMode == 0) ? 7 - i : i;
                                Fpu.WriteToMemory(_cpu, addr, 2, _fpu.FP[fpReg]);
                                addr += 12;
                            }
                        }
                    }
                }
                else // Dynamic list (listMode 1 or 3)
                {
                    int dynReg = (cmdWord >> 4) & 7;
                    int regList = (int)(_cpu.D[dynReg] & 0xFF);
                    bool predecrement = (eaMode == 4);

                    if (predecrement)
                    {
                        uint addr = _cpu.A[eaReg];
                        bool reversed = (listMode == 1);
                        for (int i = 0; i < 8; i++)
                        {
                            if ((regList & (1 << i)) != 0)
                            {
                                addr -= 12;
                                int fpReg = reversed ? 7 - i : i;
                                Fpu.WriteToMemory(_cpu, addr, 2, _fpu.FP[fpReg]);
                            }
                        }
                        _cpu.A[eaReg] = addr;
                    }
                    else
                    {
                        uint addr = EffectiveAddress.ResolveAddress(_cpu, mode, reg, 12);
                        bool reversed = (listMode == 1);
                        for (int i = 0; i < 8; i++)
                        {
                            if ((regList & (1 << i)) != 0)
                            {
                                int fpReg = reversed ? 7 - i : i;
                                Fpu.WriteToMemory(_cpu, addr, 2, _fpu.FP[fpReg]);
                                addr += 12;
                            }
                        }
                    }
                }
                break;
            }

            default:
                _cpu.RaiseException(11);
                break;
        }
    }

    private void ExecuteArithmetic(int op, double src, int dstReg)
    {
        double dst = _fpu.FP[dstReg];
        double result;

        switch (op)
        {
            case 0x00: // FMOVE
                result = src;
                break;
            case 0x01: // FINT (round to integer)
                result = Math.Round(src, MidpointRounding.ToEven);
                break;
            case 0x02: // FSINH
                result = Math.Sinh(src);
                break;
            case 0x03: // FINTRZ (round to integer toward zero)
                result = Math.Truncate(src);
                break;
            case 0x04: // FSQRT
                result = Math.Sqrt(src);
                break;
            case 0x06: // FLOGNP1 (ln(x+1))
                result = Math.Log(src + 1.0);
                break;
            case 0x08: // FETOXM1 (e^x - 1)
                result = Math.Exp(src) - 1.0;
                break;
            case 0x09: // FTANH
                result = Math.Tanh(src);
                break;
            case 0x0A: // FATAN
                result = Math.Atan(src);
                break;
            case 0x0C: // FASIN
                result = Math.Asin(src);
                break;
            case 0x0D: // FATANH
                result = Math.Atanh(src);
                break;
            case 0x0E: // FSIN
                result = Math.Sin(src);
                break;
            case 0x0F: // FTAN
                result = Math.Tan(src);
                break;
            case 0x10: // FETOX (e^x)
                result = Math.Exp(src);
                break;
            case 0x11: // FTWOTOX (2^x)
                result = Math.Pow(2.0, src);
                break;
            case 0x12: // FTENTOX (10^x)
                result = Math.Pow(10.0, src);
                break;
            case 0x14: // FLOGN (ln)
                result = Math.Log(src);
                break;
            case 0x15: // FLOG10
                result = Math.Log10(src);
                break;
            case 0x16: // FLOG2
                result = Math.Log2(src);
                break;
            case 0x18: // FABS
                result = Math.Abs(src);
                break;
            case 0x19: // FCOSH
                result = Math.Cosh(src);
                break;
            case 0x1A: // FNEG
                result = -src;
                break;
            case 0x1C: // FACOS
                result = Math.Acos(src);
                break;
            case 0x1D: // FCOS
                result = Math.Cos(src);
                break;
            case 0x1E: // FGETEXP
            {
                if (src == 0.0 || double.IsNaN(src) || double.IsInfinity(src))
                    result = src;
                else
                    result = Math.ILogB(src);
                break;
            }
            case 0x1F: // FGETMAN
            {
                if (src == 0.0 || double.IsNaN(src) || double.IsInfinity(src))
                    result = src;
                else
                {
                    int exp = Math.ILogB(src);
                    result = src / Math.Pow(2.0, exp);
                }
                break;
            }
            case 0x20: // FDIV
                result = dst / src;
                break;
            case 0x21: // FMOD (IEEE remainder with quotient sign of dividend)
                result = Math.IEEERemainder(dst, src);
                // Adjust for FMOD semantics (same sign as dividend)
                if (src != 0)
                {
                    result = dst - Math.Truncate(dst / src) * src;
                }
                break;
            case 0x22: // FADD
                result = dst + src;
                break;
            case 0x23: // FMUL
                result = dst * src;
                break;
            case 0x24: // FSGLDIV (single precision divide)
                result = (float)(dst / src);
                break;
            case 0x25: // FREM (IEEE remainder)
                result = Math.IEEERemainder(dst, src);
                break;
            case 0x26: // FSCALE
                result = dst * Math.Pow(2.0, Math.Truncate(src));
                break;
            case 0x27: // FSGLMUL (single precision multiply)
                result = (float)(dst * src);
                break;
            case 0x28: // FSUB
                result = dst - src;
                break;
            case 0x38: // FCMP
                _fpu.SetConditionCodes(dst - src);
                return; // Don't write result

            case 0x3A: // FTST
                _fpu.SetConditionCodes(src);
                return; // Don't write result

            default:
                // Check for FSINCOS (0x30-0x37)
                if (op >= 0x30 && op <= 0x37)
                {
                    int cosReg = op & 7;
                    _fpu.FP[cosReg] = Math.Cos(src);
                    result = Math.Sin(src);
                    break;
                }
                // 68040 single/double precision variants
                if ((op & 0x40) != 0)
                {
                    int baseOp = op & 0x3F;
                    // Recurse with base operation, result will be rounded
                    ExecuteArithmetic(baseOp, src, dstReg);
                    return;
                }
                _cpu.RaiseException(11);
                return;
        }

        _fpu.FP[dstReg] = result;
        _fpu.SetConditionCodes(result);
    }

    private void ExecuteFBcc(ushort opcode, bool longDisp)
    {
        int condition = opcode & 0x3F;
        int disp;

        if (longDisp)
        {
            uint d = _cpu.FetchLong();
            disp = (int)d;
        }
        else
        {
            disp = (short)_cpu.FetchWord();
        }

        if (_fpu.EvaluateCondition(condition))
        {
            _cpu.PC = (uint)(_cpu.PC - (longDisp ? 4 : 2) + disp);
        }
    }

    private void ExecuteFSccDBccTRAPcc(ushort opcode, int eaMode, int eaReg)
    {
        ushort cmdWord = _cpu.FetchWord();
        int condition = cmdWord & 0x3F;

        if (eaMode == 1) // FDBcc
        {
            int dispWord = (short)_cpu.FetchWord();
            if (!_fpu.EvaluateCondition(condition))
            {
                int cnt = (short)(_cpu.D[eaReg] & 0xFFFF) - 1;
                _cpu.D[eaReg] = (_cpu.D[eaReg] & 0xFFFF0000) | (uint)(cnt & 0xFFFF);
                if (cnt != -1)
                {
                    _cpu.PC = (uint)(_cpu.PC - 2 + dispWord);
                }
            }
        }
        else if (eaMode == 7 && eaReg == 2) // FTRAPcc.W
        {
            _cpu.FetchWord(); // skip extension
            if (_fpu.EvaluateCondition(condition))
                _cpu.RaiseException(7); // TRAP
        }
        else if (eaMode == 7 && eaReg == 3) // FTRAPcc.L
        {
            _cpu.FetchLong(); // skip extension
            if (_fpu.EvaluateCondition(condition))
                _cpu.RaiseException(7);
        }
        else if (eaMode == 7 && eaReg == 4) // FTRAPcc (no operand)
        {
            if (_fpu.EvaluateCondition(condition))
                _cpu.RaiseException(7);
        }
        else // FScc
        {
            var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
            uint val = _fpu.EvaluateCondition(condition) ? 0xFFu : 0u;
            EffectiveAddress.WriteValue(_cpu, mode, reg, 1, val);
        }
    }

    private void ExecuteFSave(ushort opcode, int eaMode, int eaReg)
    {
        // Simplified: write a null frame (idle state)
        // Use CPU memory access (through MMU) not direct physical memory
        var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
        uint addr = EffectiveAddress.ResolveAddress(_cpu, mode, reg, 4);
        _cpu.WriteLong(addr, 0x00000000); // Null frame
    }

    private void ExecuteFRestore(ushort opcode, int eaMode, int eaReg)
    {
        // Simplified: read frame header and skip
        // Use CPU memory access (through MMU) not direct physical memory
        var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
        uint addr = EffectiveAddress.ResolveAddress(_cpu, mode, reg, 4);
        uint header = _cpu.ReadLong(addr);
        // Null frame = reset FPU
        if (header == 0)
        {
            _fpu.Reset();
        }
    }

    /// <summary>Read an FP value from the effective address in the given format.</summary>
    private double ReadEAFloat(int eaMode, int eaReg, int format)
    {
        if (eaMode <= 1) // Data/Address register direct
        {
            if (format == 0) // Long integer from Dn
                return (int)_cpu.D[eaReg];
            if (format == 4) // Word integer from Dn
                return (short)(_cpu.D[eaReg] & 0xFFFF);
            if (format == 6) // Byte integer from Dn
                return (sbyte)(_cpu.D[eaReg] & 0xFF);
            if (format == 1) // Single from Dn
                return BitConverter.Int32BitsToSingle((int)_cpu.D[eaReg]);
            // Other formats: treat as long
            return (int)_cpu.D[eaReg];
        }

        var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
        int size = Fpu.FormatSize(format);

        // For immediate mode, data follows the instruction
        if (mode == AddressingMode.Immediate)
        {
            uint addr = _cpu.PC;
            double val = Fpu.ReadFromMemory(_cpu, addr, format);
            _cpu.PC += (uint)((size + 1) & ~1); // Align to word
            return val;
        }

        uint ea = EffectiveAddress.ResolveAddress(_cpu, mode, reg, size);
        double result = Fpu.ReadFromMemory(_cpu, ea, format);
        // Post-increment and pre-decrement already handled in ResolveAddress

        return result;
    }

    /// <summary>Write an FP value to the effective address in the given format.</summary>
    private void WriteEAFloat(int eaMode, int eaReg, int format, double value)
    {
        if (eaMode == 0) // Data register direct
        {
            if (format == 0) // Long integer
                _cpu.D[eaReg] = (uint)(int)Math.Round(value);
            else if (format == 1) // Single
                _cpu.D[eaReg] = (uint)BitConverter.SingleToInt32Bits((float)value);
            else if (format == 4) // Word
            {
                short sv = (short)Math.Round(value);
                _cpu.D[eaReg] = (_cpu.D[eaReg] & 0xFFFF0000) | (uint)(ushort)sv;
            }
            else if (format == 6) // Byte
            {
                sbyte bv = (sbyte)Math.Round(value);
                _cpu.D[eaReg] = (_cpu.D[eaReg] & 0xFFFFFF00) | (uint)(byte)bv;
            }
            return;
        }

        var (mode, reg) = EffectiveAddress.Decode(eaMode, eaReg);
        int size = Fpu.FormatSize(format);
        uint ea = EffectiveAddress.ResolveAddress(_cpu, mode, reg, size);
        Fpu.WriteToMemory(_cpu, ea, format, value);
        // Post-increment and pre-decrement already handled in ResolveAddress
    }

    /// <summary>Helper for multi-register FMOVEM control register transfers.</summary>
    private AddressingMode AdvanceEA(AddressingMode mode, ref int reg, int size, int origEaMode, int origEaReg)
    {
        // For memory modes, we just re-decode with offset
        // This is a simplification; real hardware advances the address
        return mode;
    }
}
