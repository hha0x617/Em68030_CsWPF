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

using Em68030.Core;
using Xunit;

namespace Em68030.Tests.AluTests;

/// <summary>
/// ALU 演算テスト。
/// 加減算・乗除算・シフト・ローテート・BCD 演算と CCR フラグを検証する。
/// </summary>
public class AluTests
{
    // CCR bit masks
    private const byte CCR_C = 0x01;
    private const byte CCR_V = 0x02;
    private const byte CCR_Z = 0x04;
    private const byte CCR_N = 0x08;
    private const byte CCR_X = 0x10;

    // ====================================================================
    // Add
    // ====================================================================

    [Fact]
    public void AddByte_Basic()
    {
        var (result, ccr) = Alu.AddByte(0x10, 0x20, 0);
        Assert.Equal((byte)0x30, result);
        Assert.Equal(0, ccr & CCR_N);
        Assert.Equal(0, ccr & CCR_Z);
        Assert.Equal(0, ccr & CCR_V);
        Assert.Equal(0, ccr & CCR_C);
    }

    [Fact]
    public void AddWord_Basic()
    {
        var (result, ccr) = Alu.AddWord(0x1000, 0x2000, 0);
        Assert.Equal((ushort)0x3000, result);
        Assert.Equal(0, ccr & CCR_N);
        Assert.Equal(0, ccr & CCR_Z);
    }

    [Fact]
    public void AddLong_Basic()
    {
        var (result, ccr) = Alu.AddLong(0x10000000, 0x20000000, 0);
        Assert.Equal(0x30000000u, result);
        Assert.Equal(0, ccr & CCR_N);
        Assert.Equal(0, ccr & CCR_Z);
    }

    [Fact]
    public void AddByte_ZeroResult_SetsZFlag()
    {
        var (result, ccr) = Alu.AddByte(0x00, 0x00, 0);
        Assert.Equal((byte)0x00, result);
        Assert.NotEqual(0, ccr & CCR_Z);
    }

    [Fact]
    public void AddByte_Overflow_SetsVFlag()
    {
        // 0x7F + 0x01 = 0x80 : positive + positive = negative : overflow
        var (result, ccr) = Alu.AddByte(0x7F, 0x01, 0);
        Assert.Equal((byte)0x80, result);
        Assert.NotEqual(0, ccr & CCR_V);
        Assert.NotEqual(0, ccr & CCR_N);
    }

    [Fact]
    public void AddByte_Carry_SetsCFlag()
    {
        // 0xFF + 0x01 = 0x00 with carry
        var (result, ccr) = Alu.AddByte(0xFF, 0x01, 0);
        Assert.Equal((byte)0x00, result);
        Assert.NotEqual(0, ccr & CCR_C);
        Assert.NotEqual(0, ccr & CCR_X);
        Assert.NotEqual(0, ccr & CCR_Z);
    }

    // ====================================================================
    // Sub
    // ====================================================================

    [Fact]
    public void SubByte_Basic()
    {
        var (result, ccr) = Alu.SubByte(0x30, 0x10, 0);
        Assert.Equal((byte)0x20, result);
        Assert.Equal(0, ccr & CCR_N);
        Assert.Equal(0, ccr & CCR_Z);
        Assert.Equal(0, ccr & CCR_C);
    }

    [Fact]
    public void SubWord_Basic()
    {
        var (result, ccr) = Alu.SubWord(0x3000, 0x1000, 0);
        Assert.Equal((ushort)0x2000, result);
    }

    [Fact]
    public void SubLong_Basic()
    {
        var (result, ccr) = Alu.SubLong(0x30000000, 0x10000000, 0);
        Assert.Equal(0x20000000u, result);
    }

    [Fact]
    public void SubByte_Borrow_SetsCFlag()
    {
        // 0x00 - 0x01 = 0xFF with borrow
        var (result, ccr) = Alu.SubByte(0x00, 0x01, 0);
        Assert.Equal((byte)0xFF, result);
        Assert.NotEqual(0, ccr & CCR_C);
        Assert.NotEqual(0, ccr & CCR_X);
        Assert.NotEqual(0, ccr & CCR_N);
    }

    [Fact]
    public void SubByte_Overflow_SetsVFlag()
    {
        // 0x80 - 0x01 = 0x7F : negative - positive = positive : overflow
        var (result, ccr) = Alu.SubByte(0x80, 0x01, 0);
        Assert.Equal((byte)0x7F, result);
        Assert.NotEqual(0, ccr & CCR_V);
    }

    // ====================================================================
    // Multiply
    // ====================================================================

    [Fact]
    public void MulSigned_Basic()
    {
        var (result, ccr) = Alu.MulSigned(10, 20);
        Assert.Equal(200u, result);
        Assert.Equal(0, ccr & CCR_N);
        Assert.Equal(0, ccr & CCR_Z);
    }

    [Fact]
    public void MulSigned_Negative()
    {
        var (result, ccr) = Alu.MulSigned(-3, 7);
        // -3 * 7 = -21 = 0xFFFFFFEB
        Assert.Equal(unchecked((uint)-21), result);
        Assert.NotEqual(0, ccr & CCR_N);
    }

    [Fact]
    public void MulUnsigned_Basic()
    {
        var (result, ccr) = Alu.MulUnsigned(100, 200);
        Assert.Equal(20000u, result);
    }

    // ====================================================================
    // Divide
    // ====================================================================

    [Fact]
    public void DivSigned_Basic()
    {
        var (quotient, remainder, ccr, overflow) = Alu.DivSigned(100, 7);
        Assert.Equal(14u, quotient);
        Assert.Equal(2u, remainder);
        Assert.False(overflow);
    }

    [Fact]
    public void DivUnsigned_Basic()
    {
        var (quotient, remainder, ccr, overflow) = Alu.DivUnsigned(100, 7);
        Assert.Equal(14u, quotient);
        Assert.Equal(2u, remainder);
        Assert.False(overflow);
    }

    [Fact]
    public void DivSigned_ByZero_SetsOverflow()
    {
        var (_, _, _, overflow) = Alu.DivSigned(100, 0);
        Assert.True(overflow);
    }

    [Fact]
    public void DivUnsigned_ByZero_SetsOverflow()
    {
        var (_, _, _, overflow) = Alu.DivUnsigned(100, 0);
        Assert.True(overflow);
    }

    // ====================================================================
    // Shift
    // ====================================================================

    [Fact]
    public void ShiftLeft_Byte()
    {
        var (result, ccr) = Alu.ShiftLeft((byte)0x01, 3, 0);
        Assert.Equal((byte)0x08, result);
    }

    [Fact]
    public void ArithShiftRight_PreservesSign()
    {
        // 0x80 >> 1 = 0xC0 (sign extended)
        var (result, ccr) = Alu.ArithShiftRight((byte)0x80, 1);
        Assert.Equal((byte)0xC0, result);
        Assert.NotEqual(0, ccr & CCR_N);
    }

    [Fact]
    public void LogicalShiftRight_ZeroFill()
    {
        // 0x80 >> 1 = 0x40 (zero fill)
        var (result, ccr) = Alu.LogicalShiftRight((byte)0x80, 1);
        Assert.Equal((byte)0x40, result);
        Assert.Equal(0, ccr & CCR_N);
    }

    // ====================================================================
    // Rotate
    // ====================================================================

    [Fact]
    public void RotateLeft_Word()
    {
        // Rotate 0x8001 left by 1 (size=2 bytes = word)
        var (result, ccr) = Alu.RotateLeft(0x8001, 1, 2);
        Assert.Equal(0x0003u, result);
        Assert.NotEqual(0, ccr & CCR_C); // Last bit rotated out was 1
    }

    [Fact]
    public void RotateRight_Byte()
    {
        // Rotate 0x01 right by 1 (size=1 byte)
        var (result, ccr) = Alu.RotateRight(0x01, 1, 1);
        Assert.Equal(0x80u, result);
        Assert.NotEqual(0, ccr & CCR_C); // Last bit rotated out was 1
    }

    // ====================================================================
    // BCD
    // ====================================================================

    [Fact]
    public void AddBcd_Basic()
    {
        // BCD: 0x15 + 0x27 = 0x42
        var (result, ccr) = Alu.AddBcd(0x27, 0x15, 0);
        Assert.Equal((byte)0x42, result);
    }

    [Fact]
    public void SubBcd_Basic()
    {
        // BCD: 0x42 - 0x15 = 0x27
        var (result, ccr) = Alu.SubBcd(0x15, 0x42, 0);
        Assert.Equal((byte)0x27, result);
    }

    // ====================================================================
    // 32-bit extended operations (68020+)
    // ====================================================================

    [Fact]
    public void MulSignedLong_Basic()
    {
        var (resultLo, resultHi, ccr) = Alu.MulSignedLong(0x10000, 0x10000);
        // 0x10000 * 0x10000 = 0x1_0000_0000 : resultHi=1, resultLo=0
        Assert.Equal(0x00000000u, resultLo);
        Assert.Equal(0x00000001u, resultHi);
    }

    [Fact]
    public void MulUnsignedLong_Basic()
    {
        var (resultLo, resultHi, ccr) = Alu.MulUnsignedLong(0xFFFFFFFF, 2);
        // 0xFFFFFFFF * 2 = 0x1_FFFFFFFE : resultHi=1, resultLo=0xFFFFFFFE
        Assert.Equal(0xFFFFFFFEu, resultLo);
        Assert.Equal(0x00000001u, resultHi);
    }

    [Fact]
    public void DivSignedLong_Basic()
    {
        var (quotient, remainder, ccr, overflow) = Alu.DivSignedLong(1000000L, 333);
        Assert.Equal(3003u, quotient);
        Assert.Equal(1u, remainder);
        Assert.False(overflow);
    }

    [Fact]
    public void DivSignedLong_ByZero_SetsOverflow()
    {
        var (_, _, _, overflow) = Alu.DivSignedLong(1000000L, 0);
        Assert.True(overflow);
    }

    [Fact]
    public void DivUnsignedLong_Basic()
    {
        var (quotient, remainder, ccr, overflow) = Alu.DivUnsignedLong(1000000UL, 333);
        Assert.Equal(3003u, quotient);
        Assert.Equal(1u, remainder);
        Assert.False(overflow);
    }
}
