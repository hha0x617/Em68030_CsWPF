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

/// <summary>
/// SSW (Special Status Word) エンコーディングテスト。
/// BuildSSW は private static なので、BusErrorException.SpecialStatusWord 経由で検証する。
/// WP ページへの write → BusErrorException → SSW チェック。
/// </summary>
public class SswEncodingTests
{
    private (MmuTestFixture fixture, BusErrorException exception) TriggerBusError(
        byte functionCode, bool isWrite)
    {
        var f = new MmuTestFixture();
        if (isWrite)
        {
            // Write to WP page → BusError with SSW
            f.SetupWriteProtectedPage(0x40000000, 0x04000000);
        }
        else
        {
            // Read from invalid page → BusError with SSW
            f.SetupInvalidPage(0x40000000);
        }
        f.FlushAtc();

        var ex = Assert.Throws<BusErrorException>(() =>
            f.Mmu.Translate(0x40000000, (functionCode & 4) != 0, isWrite, functionCode));

        return (f, ex);
    }

    [Fact]
    public void BuildSSW_ReadAccess_SetsRWBit()
    {
        var (_, ex) = TriggerBusError(functionCode: 5, isWrite: false);

        // RW bit (bit 6) should be 1 for read access
        Assert.NotEqual(0, ex.SpecialStatusWord & 0x0040);
    }

    [Fact]
    public void BuildSSW_WriteAccess_ClearsRWBit()
    {
        var (_, ex) = TriggerBusError(functionCode: 5, isWrite: true);

        // RW bit (bit 6) should be 0 for write access
        Assert.Equal(0, ex.SpecialStatusWord & 0x0040);
    }

    [Fact]
    public void BuildSSW_AlwaysSets_DFBit()
    {
        // DF bit should be set for both read and write faults
        var (_, exRead) = TriggerBusError(functionCode: 5, isWrite: false);
        var (_, exWrite) = TriggerBusError(functionCode: 5, isWrite: true);

        // DF bit (bit 8) should always be 1
        Assert.NotEqual(0, exRead.SpecialStatusWord & 0x0100);
        Assert.NotEqual(0, exWrite.SpecialStatusWord & 0x0100);
    }

    [Fact]
    public void BuildSSW_PreservesFunctionCode()
    {
        var (_, ex) = TriggerBusError(functionCode: 5, isWrite: false);

        // FC bits (bits 2-0) should contain the function code
        Assert.Equal(5, ex.SpecialStatusWord & 0x07);
    }

    [Fact]
    public void BuildSSW_SupervisorData_FC5()
    {
        var (_, ex) = TriggerBusError(functionCode: 5, isWrite: false);

        // FC=5, RW=1 (read), DF=1
        int fc = ex.SpecialStatusWord & 0x07;
        bool rw = (ex.SpecialStatusWord & 0x0040) != 0;
        bool df = (ex.SpecialStatusWord & 0x0100) != 0;

        Assert.Equal(5, fc);
        Assert.True(rw);
        Assert.True(df);
    }

    [Fact]
    public void BuildSSW_UserData_FC1()
    {
        var (_, ex) = TriggerBusError(functionCode: 1, isWrite: false);

        // FC=1, RW=1 (read), DF=1
        int fc = ex.SpecialStatusWord & 0x07;
        Assert.Equal(1, fc);
    }
}
