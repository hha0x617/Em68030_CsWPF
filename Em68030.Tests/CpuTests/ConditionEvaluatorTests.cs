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
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.CpuTests;

public class ConditionEvaluatorTests : IClassFixture<CpuTestFixture>
{
    private readonly CpuTestFixture _fixture;
    private MC68030 Cpu => _fixture.Cpu;
    private Memory Mem => _fixture.Memory;

    public ConditionEvaluatorTests(CpuTestFixture fixture) => _fixture = fixture;

    private bool Eval(string cond) => ConditionEvaluator.Evaluate(cond, Cpu, Mem);

    // ========================================================================
    // Empty / malformed input → always true (unconditional)
    // ========================================================================

    [Fact] public void EmptyCondition_ReturnsTrue() => Assert.True(Eval(""));
    [Fact] public void NullCondition_ReturnsTrue() => Assert.True(ConditionEvaluator.Evaluate(null!, Cpu, Mem));
    [Fact] public void WhitespaceOnly_ReturnsTrue() => Assert.True(Eval("   "));
    [Fact] public void UnknownRegister_ReturnsTrue() => Assert.True(Eval("XY==0"));

    [Fact]
    public void MalformedOperator_ReturnsTrue()
    {
        Cpu.D[0] = 100;
        Assert.True(Eval("D0~100"));
    }

    [Fact]
    public void MalformedMemoryDeref_MissingCloseBracket_ReturnsTrue()
        => Assert.True(Eval("[0x1000.w==0"));

    // ========================================================================
    // Register comparisons: ==, !=, <, >, <=, >=
    // ========================================================================

    [Fact]
    public void DataRegister_Equal_True()
    {
        Cpu.D[0] = 0x1234;
        Assert.True(Eval("D0==0x1234"));
    }

    [Fact]
    public void DataRegister_Equal_False()
    {
        Cpu.D[0] = 0x1235;
        Assert.False(Eval("D0==0x1234"));
    }

    [Fact]
    public void DataRegister_NotEqual_True()
    {
        Cpu.D[3] = 0xABCD;
        Assert.True(Eval("D3!=0x1234"));
    }

    [Fact]
    public void DataRegister_NotEqual_False()
    {
        Cpu.D[3] = 0x1234;
        Assert.False(Eval("D3!=0x1234"));
    }

    [Fact]
    public void AddressRegister_LessThan_True()
    {
        Cpu.A[7] = 0x0FFFF;
        Assert.True(Eval("A7<0x10000"));
    }

    [Fact]
    public void AddressRegister_LessThan_False()
    {
        Cpu.A[7] = 0x10000;
        Assert.False(Eval("A7<0x10000"));
    }

    [Fact]
    public void GreaterThan()
    {
        Cpu.D[1] = 100;
        Assert.True(Eval("D1>50"));
        Assert.False(Eval("D1>100"));
    }

    [Fact]
    public void LessThanOrEqual()
    {
        Cpu.D[2] = 100;
        Assert.True(Eval("D2<=100"));
        Assert.True(Eval("D2<=200"));
        Assert.False(Eval("D2<=99"));
    }

    [Fact]
    public void GreaterThanOrEqual()
    {
        Cpu.D[4] = 100;
        Assert.True(Eval("D4>=100"));
        Assert.True(Eval("D4>=50"));
        Assert.False(Eval("D4>=101"));
    }

    // ========================================================================
    // Special registers: PC, SR, SP
    // ========================================================================

    [Fact]
    public void PC_Register()
    {
        Cpu.PC = 0x00002000;
        Assert.True(Eval("PC==0x2000"));
        Assert.False(Eval("PC==0x3000"));
    }

    [Fact]
    public void SR_Register()
    {
        Cpu.SR = 0x2700;
        Assert.True(Eval("SR==0x2700"));
    }

    [Fact]
    public void SP_Register_IsA7()
    {
        Cpu.A[7] = 0x00800000;
        Assert.True(Eval("SP==0x800000"));
    }

    [Fact]
    public void CaseInsensitive_Register()
    {
        Cpu.SR = 0x2700;
        Cpu.PC = 0x1000;
        Cpu.A[7] = 0x00800000;
        Assert.True(Eval("sr==0x2700"));
        Assert.True(Eval("pc==0x1000"));
        Assert.True(Eval("sp==0x800000"));
    }

    // ========================================================================
    // Bitwise AND test
    // ========================================================================

    [Fact]
    public void BitwiseAnd_SupervisorBitSet()
    {
        Cpu.SR = 0x2700;
        Assert.True(Eval("SR&0x2000!=0"));
    }

    [Fact]
    public void BitwiseAnd_SupervisorBitClear()
    {
        Cpu.SR = 0x0000;
        Assert.False(Eval("SR&0x2000!=0"));
    }

    [Fact]
    public void BitwiseAnd_EqualZero()
    {
        Cpu.SR = 0x0700;
        Assert.True(Eval("SR&0x2000==0"));
    }

    [Fact]
    public void BitwiseAnd_BareResult()
    {
        Cpu.D[0] = 0x00000100;
        Assert.False(Eval("D0&0xFF")); // low byte is 0
        Cpu.D[0] = 0x00000001;
        Assert.True(Eval("D0&0xFF"));  // low byte is 1
    }

    // ========================================================================
    // Number format variants
    // ========================================================================

    [Fact]
    public void DecimalNumber()
    {
        Cpu.D[0] = 255;
        Assert.True(Eval("D0==255"));
    }

    [Fact]
    public void HexNumber_0x()
    {
        Cpu.D[0] = 0xFF;
        Assert.True(Eval("D0==0xFF"));
        Assert.True(Eval("D0==0xff"));
        Assert.True(Eval("D0==0XFF"));
    }

    [Fact]
    public void HexNumber_Dollar()
    {
        Cpu.D[0] = 0xABCD;
        Assert.True(Eval("D0==$ABCD"));
    }

    // ========================================================================
    // Register-to-register comparison
    // ========================================================================

    [Fact]
    public void RegisterVsRegister_Equal()
    {
        Cpu.D[0] = 42;
        Cpu.D[1] = 42;
        Assert.True(Eval("D0==D1"));
    }

    [Fact]
    public void RegisterVsRegister_NotEqual()
    {
        Cpu.D[0] = 42;
        Cpu.D[1] = 99;
        Assert.False(Eval("D0==D1"));
        Assert.True(Eval("D0!=D1"));
    }

    [Fact]
    public void RegisterVsRegister_LessThan()
    {
        Cpu.D[0] = 10;
        Cpu.A[0] = 20;
        Assert.True(Eval("D0<A0"));
    }

    // ========================================================================
    // Bare expression (no operator): true if non-zero
    // ========================================================================

    [Fact]
    public void BareRegister_NonZero()
    {
        Cpu.D[0] = 1;
        Assert.True(Eval("D0"));
    }

    [Fact]
    public void BareRegister_Zero()
    {
        Cpu.D[0] = 0;
        Assert.False(Eval("D0"));
    }

    [Fact] public void BareNumber_NonZero() => Assert.True(Eval("0x1234"));
    [Fact] public void BareNumber_Zero() => Assert.False(Eval("0"));

    // ========================================================================
    // Memory dereference
    // ========================================================================

    [Fact]
    public void MemoryDeref_Byte()
    {
        Mem.WriteByte(0x1000, 0x42);
        Assert.True(Eval("[0x1000].b==0x42"));
        Assert.False(Eval("[0x1000].b==0x43"));
    }

    [Fact]
    public void MemoryDeref_Word()
    {
        Mem.WriteWord(0x2000, 0xABCD);
        Assert.True(Eval("[0x2000].w==0xABCD"));
    }

    [Fact]
    public void MemoryDeref_Long()
    {
        Mem.WriteLong(0x3000, 0x12345678);
        Assert.True(Eval("[0x3000].l==0x12345678"));
    }

    [Fact]
    public void MemoryDeref_DefaultIsWord()
    {
        Mem.WriteWord(0x4000, 0xBEEF);
        Assert.True(Eval("[0x4000]==0xBEEF"));
    }

    [Fact]
    public void MemoryDeref_RegisterAddress()
    {
        Cpu.A[0] = 0x5000;
        Mem.WriteLong(0x5000, 0xDEADBEEF);
        Assert.True(Eval("[A0].l==0xDEADBEEF"));
    }

    [Fact]
    public void MemoryDeref_DollarAddress()
    {
        Mem.WriteByte(0x6000, 0xFF);
        Assert.True(Eval("[$6000].b==0xFF"));
    }

    // ========================================================================
    // Whitespace handling
    // ========================================================================

    [Fact]
    public void WhitespaceAroundOperator()
    {
        Cpu.D[0] = 100;
        Assert.True(Eval("D0 == 100"));
        Assert.True(Eval("  D0 == 100  "));
    }

    [Fact]
    public void WhitespaceAroundBitwiseAnd()
    {
        Cpu.SR = 0x2700;
        Assert.True(Eval("SR & 0x2000 != 0"));
    }

    // ========================================================================
    // All data/address registers
    // ========================================================================

    [Fact]
    public void AllDataRegisters()
    {
        for (int i = 0; i < 8; i++)
        {
            Cpu.D[i] = (uint)(100 + i);
            Assert.True(Eval($"D{i}=={100 + i}"), $"Failed for D{i}");
        }
    }

    [Fact]
    public void AllAddressRegisters()
    {
        Cpu.A[0] = 0x1000;
        Cpu.A[6] = 0x7000;
        Assert.True(Eval("A0==0x1000"));
        Assert.True(Eval("A6==0x7000"));
    }
}
