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

using System;

/// <summary>
/// MC68881/MC68882 compatible Floating Point Unit.
/// Uses double (64-bit) internally as approximation of 80-bit extended precision.
/// </summary>
public class Fpu
{
    // FP data registers
    public double[] FP { get; } = new double[8];

    // FPU control registers
    public uint FPCR { get; set; }   // Floating-Point Control Register
    public uint FPSR { get; set; }   // Floating-Point Status Register
    public uint FPIAR { get; set; }  // Floating-Point Instruction Address Register

    // FPSR condition code bits (bits 27-24)
    public bool CondN { get => (FPSR & 0x08000000) != 0; set => FPSR = value ? FPSR | 0x08000000 : FPSR & ~0x08000000u; }
    public bool CondZ { get => (FPSR & 0x04000000) != 0; set => FPSR = value ? FPSR | 0x04000000 : FPSR & ~0x04000000u; }
    public bool CondI { get => (FPSR & 0x02000000) != 0; set => FPSR = value ? FPSR | 0x02000000 : FPSR & ~0x02000000u; }
    public bool CondNAN { get => (FPSR & 0x01000000) != 0; set => FPSR = value ? FPSR | 0x01000000 : FPSR & ~0x01000000u; }

    // FPCR rounding mode (bits 5-4)
    public RoundingMode RoundMode => (RoundingMode)((FPCR >> 4) & 3);

    // FPCR rounding precision (bits 7-6)
    public RoundingPrecision RoundPrec => (RoundingPrecision)((FPCR >> 6) & 3);

    public void Reset()
    {
        for (int i = 0; i < 8; i++) FP[i] = 0.0;
        FPCR = 0;
        FPSR = 0;
        FPIAR = 0;
    }

    /// <summary>Update FPSR condition codes from a result value.</summary>
    public void SetConditionCodes(double value)
    {
        // Clear condition code bits
        FPSR &= 0xF0FFFFFF;

        if (double.IsNaN(value))
        {
            CondNAN = true;
        }
        else if (double.IsPositiveInfinity(value))
        {
            CondI = true;
        }
        else if (double.IsNegativeInfinity(value))
        {
            CondN = true;
            CondI = true;
        }
        else if (value == 0.0)
        {
            CondZ = true;
            // Check for negative zero
            if (double.IsNegative(value))
                CondN = true;
        }
        else if (value < 0.0)
        {
            CondN = true;
        }
        // else: all clear (positive non-zero)
    }

    /// <summary>Evaluate FPU condition predicate.</summary>
    public bool EvaluateCondition(int condition)
    {
        bool n = CondN, z = CondZ, nan = CondNAN;

        return (condition & 0x1F) switch
        {
            0x00 => false,                          // F
            0x01 => z,                              // EQ
            0x02 => !(nan || z || n),               // OGT
            0x03 => z || !(nan || n),               // OGE
            0x04 => n && !(nan || z),               // OLT
            0x05 => z || (n && !nan),               // OLE
            0x06 => !(nan || z),                    // OGL
            0x07 => !nan,                           // OR
            0x08 => nan,                            // UN
            0x09 => nan || z,                       // UEQ
            0x0A => nan || !(n || z),               // UGT
            0x0B => nan || z || !n,                 // UGE
            0x0C => nan || (n && !z),               // ULT
            0x0D => nan || z || n,                  // ULE
            0x0E => !z,                             // NE
            0x0F => true,                           // T
            0x10 => false,                          // SF
            0x11 => z,                              // SEQ
            0x12 => !(nan || z || n),               // GT
            0x13 => z || !(nan || n),               // GE
            0x14 => n && !(nan || z),               // LT
            0x15 => z || (n && !nan),               // LE
            0x16 => !(nan || z),                    // GL
            0x17 => !nan,                           // GLE
            0x18 => nan,                            // NGLE
            0x19 => nan || z,                       // NGL
            0x1A => nan || !(n || z),               // NLE
            0x1B => nan || z || !n,                 // NLT
            0x1C => nan || (n && !z),               // NGE
            0x1D => nan || z || n,                  // NGT
            0x1E => !z,                             // SNE
            0x1F => true,                           // ST
            _ => false
        };
    }

    /// <summary>Read a floating-point value from memory in the specified format.</summary>
    public static double ReadFromMemory(MC68030 cpu, uint address, int format)
    {
        switch (format)
        {
            case 0: // Long Integer (32-bit)
                return (int)cpu.ReadLong(address);

            case 1: // Single Precision (32-bit IEEE)
            {
                uint bits = cpu.ReadLong(address);
                return BitConverter.Int32BitsToSingle((int)bits);
            }

            case 2: // Extended Precision (96-bit: 4-byte exponent/sign + 4-byte padding? Actually 12 bytes: sign+exp 32bit, zeros 16bit, mantissa 64bit)
            {
                // 68881 extended format: 12 bytes (96 bits)
                // Bytes 0-1: sign (bit 15) + exponent (bits 14-0)
                // Bytes 2-3: zero padding
                // Bytes 4-11: 64-bit mantissa (with explicit integer bit)
                uint w0 = cpu.ReadLong(address);
                // skip padding at address+2 (it's included in w0's lower 16 bits)
                uint mantHi = cpu.ReadLong(address + 4);
                uint mantLo = cpu.ReadLong(address + 8);

                int sign = (int)(w0 >> 31) & 1;
                int exponent = (int)((w0 >> 16) & 0x7FFF);
                ulong mantissa = ((ulong)mantHi << 32) | mantLo;

                if (exponent == 0 && mantissa == 0)
                    return sign == 1 ? -0.0 : 0.0;
                if (exponent == 0x7FFF)
                {
                    if (mantissa == 0) return sign == 1 ? double.NegativeInfinity : double.PositiveInfinity;
                    return double.NaN;
                }

                // Convert from 80-bit extended to double
                // Extended: bias = 16383, mantissa has explicit integer bit
                // Double: bias = 1023, mantissa has implicit integer bit
                double val = (double)mantissa / (1UL << 63) * Math.Pow(2, exponent - 16383);
                return sign == 1 ? -val : val;
            }

            case 3: // Packed Decimal (96-bit BCD)
            {
                // Simplified: read as 12 bytes, extract BCD
                uint w0 = cpu.ReadLong(address);
                uint w1 = cpu.ReadLong(address + 4);
                uint w2 = cpu.ReadLong(address + 8);
                int sign = (int)(w0 >> 31) & 1;
                int signExp = (int)(w0 >> 30) & 1;
                // Simplified BCD decode
                return sign == 1 ? -0.0 : 0.0; // Placeholder
            }

            case 4: // Word Integer (16-bit)
                return (short)cpu.ReadWord(address);

            case 5: // Double Precision (64-bit IEEE)
            {
                uint hi = cpu.ReadLong(address);
                uint lo = cpu.ReadLong(address + 4);
                long bits = ((long)hi << 32) | lo;
                return BitConverter.Int64BitsToDouble(bits);
            }

            case 6: // Byte Integer (8-bit)
                return (sbyte)cpu.ReadByte(address);

            default:
                return 0.0;
        }
    }

    /// <summary>Write a floating-point value to memory in the specified format.</summary>
    public static void WriteToMemory(MC68030 cpu, uint address, int format, double value)
    {
        switch (format)
        {
            case 0: // Long Integer
                cpu.WriteLong(address, (uint)(int)Math.Round(value));
                break;

            case 1: // Single Precision
            {
                int bits = BitConverter.SingleToInt32Bits((float)value);
                cpu.WriteLong(address, (uint)bits);
                break;
            }

            case 2: // Extended Precision (96-bit)
            {
                int sign = double.IsNegative(value) ? 1 : 0;
                if (sign == 1) value = -value;

                ushort exponent;
                ulong mantissa;

                if (double.IsNaN(value))
                {
                    exponent = 0x7FFF;
                    mantissa = 0xFFFFFFFFFFFFFFFF;
                }
                else if (double.IsInfinity(value))
                {
                    exponent = 0x7FFF;
                    mantissa = 0x8000000000000000;
                }
                else if (value == 0.0)
                {
                    exponent = 0;
                    mantissa = 0;
                }
                else
                {
                    // Convert double to 80-bit extended
                    long dbits = BitConverter.DoubleToInt64Bits(value);
                    int dexp = (int)((dbits >> 52) & 0x7FF);
                    long dmant = dbits & 0x000FFFFFFFFFFFFF;

                    exponent = (ushort)(dexp - 1023 + 16383);
                    mantissa = (0x8000000000000000UL) | ((ulong)dmant << 11);
                }

                uint w0 = ((uint)sign << 31) | ((uint)exponent << 16);
                cpu.WriteLong(address, w0);
                cpu.WriteLong(address + 4, (uint)(mantissa >> 32));
                cpu.WriteLong(address + 8, (uint)(mantissa & 0xFFFFFFFF));
                break;
            }

            case 4: // Word Integer
                cpu.WriteWord(address, (ushort)(short)Math.Round(value));
                break;

            case 5: // Double Precision
            {
                long bits = BitConverter.DoubleToInt64Bits(value);
                cpu.WriteLong(address, (uint)(bits >> 32));
                cpu.WriteLong(address + 4, (uint)(bits & 0xFFFFFFFF));
                break;
            }

            case 6: // Byte Integer
                cpu.WriteByte(address, (byte)(sbyte)Math.Round(value));
                break;
        }
    }

    /// <summary>Get the byte size of a data format.</summary>
    public static int FormatSize(int format)
    {
        return format switch
        {
            0 => 4,  // Long Integer
            1 => 4,  // Single
            2 => 12, // Extended
            3 => 12, // Packed Decimal
            4 => 2,  // Word Integer
            5 => 8,  // Double
            6 => 1,  // Byte Integer
            _ => 4
        };
    }

    /// <summary>Get the format name string.</summary>
    public static string FormatName(int format)
    {
        return format switch
        {
            0 => ".L",
            1 => ".S",
            2 => ".X",
            3 => ".P",
            4 => ".W",
            5 => ".D",
            6 => ".B",
            _ => ""
        };
    }
}

public enum RoundingMode
{
    ToNearest = 0,
    TowardZero = 1,
    TowardNegInf = 2,
    TowardPosInf = 3
}

public enum RoundingPrecision
{
    Extended = 0,
    Single = 1,
    Double = 2,
    Reserved = 3
}
