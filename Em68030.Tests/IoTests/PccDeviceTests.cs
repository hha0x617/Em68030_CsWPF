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
using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

// ============================================================================
// PCC (Peripheral Channel Controller) Tests
// ============================================================================

public class PccDeviceTests
{
    private readonly Memory _memory = new();
    private readonly MC68030 _cpu;
    private readonly PccDevice _pcc;

    private const uint Base = 0xFFFE1000;

    // Register offsets
    private const uint Timer1Preload = Base + 0x10;
    private const uint Timer1Count   = Base + 0x12;
    private const uint Timer2Preload = Base + 0x14;
    private const uint Timer2Count   = Base + 0x16;
    private const uint Timer1Icr     = Base + 0x18;
    private const uint Timer1Control = Base + 0x19;
    private const uint Timer2Icr     = Base + 0x1A;
    private const uint Timer2Control = Base + 0x1B;
    private const uint SccIcr        = Base + 0x26;
    private const uint LanceIcr      = Base + 0x28;
    private const uint ScsiIcr       = Base + 0x2A;
    private const uint ScsiIcrAlias  = Base + 0x30;
    private const uint Soft1Icr      = Base + 0x2C;
    private const uint VectorBaseReg = Base + 0x2D;
    private const uint Soft2Icr      = Base + 0x2E;
    private const uint DmaIcr        = Base + 0x20;
    private const uint DmaControl    = Base + 0x21;
    private const uint AbortIcr      = Base + 0x24;

    public PccDeviceTests()
    {
        _cpu = new MC68030(_memory);
        _pcc = new PccDevice(_cpu);
    }

    // ========================================================================
    // ICR Write-1-to-Clear (W1C) and bit layout
    // ========================================================================

    [Fact]
    public void IcrDefault_NoInterrupt()
    {
        Assert.Equal(0x00, _pcc.ReadByte(Timer1Icr));
        Assert.Equal(0x00, _pcc.ReadByte(SccIcr));
        Assert.Equal(0x00, _pcc.ReadByte(ScsiIcr));
        Assert.Equal(0x00, _pcc.ReadByte(LanceIcr));
    }

    [Fact]
    public void WriteIcr_SetsIenAndLevel()
    {
        _pcc.WriteByte(Timer1Icr, 0x0D); // IEN=1, level=5
        Assert.Equal(0x0D, _pcc.ReadByte(Timer1Icr) & 0x0F);
    }

    [Fact]
    public void WriteIcr_W1C_ClearsInt()
    {
        _pcc.SetDeviceInterrupt("scc", true);
        Assert.Equal(0x80, _pcc.ReadByte(SccIcr) & 0x80);

        _pcc.SetDeviceInterrupt("scc", false);
        _pcc.WriteByte(SccIcr, 0x80); // W1C
        Assert.Equal(0x00, _pcc.ReadByte(SccIcr) & 0x80);
    }

    [Fact]
    public void WriteIcr_PreservesIntIfNotW1C()
    {
        // Use a timer ICR (non-device) to test INT preservation
        // Simulate INT by using SetDmaDone which sets DMA ICR INT
        _pcc.SetDmaDone();
        Assert.Equal(0x80, _pcc.ReadByte(DmaIcr) & 0x80);

        // Write IEN/level WITHOUT bit 7 → INT preserved
        _pcc.WriteByte(DmaIcr, 0x0D); // no bit 7
        Assert.Equal(0x80, _pcc.ReadByte(DmaIcr) & 0x80);
    }

    // ========================================================================
    // Level-sensitive device ICR (SCC, SCSI, LANCE)
    // ========================================================================

    [Fact]
    public void DeviceIcr_Relatch_WhileActive()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        Assert.Equal(0x80, _pcc.ReadByte(ScsiIcr) & 0x80);

        // Try W1C while device still active — INT re-latches
        _pcc.WriteByte(ScsiIcr, 0x8D);
        Assert.Equal(0x80, _pcc.ReadByte(ScsiIcr) & 0x80);
    }

    [Fact]
    public void DeviceIcr_ClearsAfterDeassert()
    {
        _pcc.SetDeviceInterrupt("lance", true);
        Assert.Equal(0x80, _pcc.ReadByte(LanceIcr) & 0x80);

        _pcc.SetDeviceInterrupt("lance", false);
        _pcc.WriteByte(LanceIcr, 0x80);
        Assert.Equal(0x00, _pcc.ReadByte(LanceIcr) & 0x80);
    }

    [Fact]
    public void SetDeviceInterrupt_Deassert_ClearsInt()
    {
        _pcc.SetDeviceInterrupt("scc", true);
        Assert.Equal(0x80, _pcc.ReadByte(SccIcr) & 0x80);

        _pcc.SetDeviceInterrupt("scc", false);
        Assert.Equal(0x00, _pcc.ReadByte(SccIcr) & 0x80);
    }

    // ========================================================================
    // SCSI ICR alias at offset 0x30 (Linux uses this)
    // ========================================================================

    [Fact]
    public void ScsiIcrAlias_ReadsFromSameRegister()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0D);
        Assert.Equal(_pcc.ReadByte(ScsiIcr), _pcc.ReadByte(ScsiIcrAlias));
    }

    [Fact]
    public void ScsiIcrAlias_WritesAffectSameRegister()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcrAlias, 0x0D);
        Assert.Equal(0x0D, _pcc.ReadByte(ScsiIcr) & 0x0F);
    }

    // ========================================================================
    // Software Interrupt ICR
    // ========================================================================

    [Fact]
    public void SoftIcr_WriteBit7_SetsInt()
    {
        _pcc.WriteByte(Soft1Icr, 0x8D); // INT=1, IEN=1, level=5
        Assert.Equal(0x80, _pcc.ReadByte(Soft1Icr) & 0x80);
    }

    [Fact]
    public void SoftIcr_WriteBit7Zero_ClearsInt()
    {
        _pcc.WriteByte(Soft1Icr, 0x8D);
        _pcc.WriteByte(Soft1Icr, 0x0D);
        Assert.Equal(0x00, _pcc.ReadByte(Soft1Icr) & 0x80);
    }

    // ========================================================================
    // UpdateIPL — Priority Resolution
    // ========================================================================

    [Fact]
    public void NoInterrupt_IplZero()
    {
        Assert.Equal(0, _cpu._pendingIPL);
    }

    [Fact]
    public void SingleDevice_SetsIpl()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A); // IEN=1, level=2
        Assert.Equal(2, _cpu._pendingIPL);
    }

    [Fact]
    public void HigherLevel_Wins()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A); // level=2

        _pcc.SetDeviceInterrupt("scc", true);
        _pcc.WriteByte(SccIcr, 0x0C); // level=4

        Assert.Equal(4, _cpu._pendingIPL);
    }

    [Fact]
    public void DisabledIen_DoesNotContribute()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x02); // level=2, NO IEN
        Assert.Equal(0, _cpu._pendingIPL);
    }

    [Fact]
    public void LevelZero_TreatedAsLevel1()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x08); // IEN=1, level=0
        Assert.Equal(1, _cpu._pendingIPL);
    }

    [Fact]
    public void ClearInterrupt_ResetsIpl()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A);
        Assert.Equal(2, _cpu._pendingIPL);

        _pcc.SetDeviceInterrupt("scsi", false);
        _pcc.WriteByte(ScsiIcr, 0x8A); // W1C
        Assert.Equal(0, _cpu._pendingIPL);
    }

    // ========================================================================
    // Vector Calculation
    // ========================================================================

    [Fact]
    public void DefaultVectorBase_Is0x40()
    {
        Assert.Equal(0x40, _pcc.ReadByte(VectorBaseReg));
    }

    [Fact]
    public void ScsiInterrupt_VectorIs0x45()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A);
        Assert.Equal(0x40 + 5, _cpu._pendingVector);
    }

    [Fact]
    public void SccInterrupt_VectorIs0x43()
    {
        _pcc.SetDeviceInterrupt("scc", true);
        _pcc.WriteByte(SccIcr, 0x0C);
        Assert.Equal(0x40 + 3, _cpu._pendingVector);
    }

    [Fact]
    public void LanceInterrupt_VectorIs0x44()
    {
        _pcc.SetDeviceInterrupt("lance", true);
        _pcc.WriteByte(LanceIcr, 0x0B);
        Assert.Equal(0x40 + 4, _cpu._pendingVector);
    }

    [Fact]
    public void CustomVectorBase()
    {
        _pcc.WriteByte(VectorBaseReg, 0x50);
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A);
        Assert.Equal(0x50 + 5, _cpu._pendingVector);
    }

    [Fact]
    public void SoftInterrupt_VectorIs0x4A()
    {
        _pcc.WriteByte(Soft1Icr, 0x8B); // INT=1, IEN=1, level=3
        Assert.Equal(0x40 + 10, _cpu._pendingVector);
    }

    // ========================================================================
    // Timer Control
    // ========================================================================

    [Fact]
    public void TimerPreload_ReadWrite()
    {
        _pcc.WriteWord(Timer1Preload, 0xF9C0);
        Assert.Equal(0xF9C0, _pcc.ReadWord(Timer1Preload));
    }

    [Fact]
    public void TimerCount_ReadWrite()
    {
        _pcc.WriteWord(Timer1Count, 0x1234);
        Assert.Equal(0x1234, _pcc.ReadWord(Timer1Count));
    }

    [Fact]
    public void TimerControl_CciClear_ResetsCountToPreload()
    {
        _pcc.WriteWord(Timer1Preload, 0xF9C0);
        _pcc.WriteWord(Timer1Count, 0x1234);
        _pcc.WriteByte(Timer1Control, 0x00); // PCC_TIMERCLEAR: CCI=0
        Assert.Equal(0xF9C0, _pcc.ReadWord(Timer1Count));
    }

    [Fact]
    public void TimerControl_CciSet_PreservesCount()
    {
        _pcc.WriteWord(Timer1Preload, 0xF9C0);
        _pcc.WriteWord(Timer1Count, 0x1234);
        _pcc.WriteByte(Timer1Control, 0x01); // PCC_TIMERENABLE: CCI=1
        Assert.Equal(0x1234, _pcc.ReadWord(Timer1Count));
    }

    [Fact]
    public void TimerControl_ReadIncludesOverflowCount()
    {
        _pcc.WriteByte(Timer1Control, 0x07);
        byte ctrl = _pcc.ReadByte(Timer1Control);
        Assert.Equal(0x07, ctrl & 0x07);
        Assert.Equal(0, ctrl >> 4); // overflow count = 0
    }

    // ========================================================================
    // DMA Registers
    // ========================================================================

    [Fact]
    public void DmaDataAddress_ReadWrite()
    {
        _pcc.SetDmaDataAddress(0x00100000);
        Assert.Equal(0x00100000u, _pcc.GetDmaDataAddress());
    }

    [Fact]
    public void DmaDataAddress_ViaRegisterAccess()
    {
        _pcc.WriteByte(Base + 0x04, 0x00);
        _pcc.WriteByte(Base + 0x05, 0x20);
        _pcc.WriteByte(Base + 0x06, 0x00);
        _pcc.WriteByte(Base + 0x07, 0x00);
        Assert.Equal(0x00200000u, _pcc.GetDmaDataAddress());
    }

    [Fact]
    public void DmaByteCount_ReadWrite()
    {
        _pcc.WriteByte(Base + 0x08, 0x00);
        _pcc.WriteByte(Base + 0x09, 0x00);
        _pcc.WriteByte(Base + 0x0A, 0x02);
        _pcc.WriteByte(Base + 0x0B, 0x00);
        Assert.Equal(0x0200u, _pcc.GetDmaByteCount());
    }

    [Fact]
    public void SetDmaDone_SetsDoneAndInt()
    {
        _pcc.WriteByte(DmaIcr, 0x0A); // IEN=1, level=2
        _pcc.SetDmaDone();

        Assert.Equal(0x80, _pcc.ReadByte(DmaControl) & 0x80); // DONE
        Assert.Equal(0x80, _pcc.ReadByte(DmaIcr) & 0x80);     // INT
        Assert.Equal(2, _cpu._pendingIPL);
    }

    // ========================================================================
    // HardwareReset
    // ========================================================================

    [Fact]
    public void HardwareReset_ClearsAllState()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A);
        _pcc.WriteByte(Soft1Icr, 0x8D);
        _pcc.WriteWord(Timer1Preload, 0xF9C0);
        _pcc.WriteByte(Timer1Control, 0x07);

        Assert.NotEqual(0, _cpu._pendingIPL);

        _pcc.HardwareReset();

        Assert.Equal(0x00, _pcc.ReadByte(ScsiIcr));
        Assert.Equal(0x00, _pcc.ReadByte(SccIcr));
        Assert.Equal(0x00, _pcc.ReadByte(LanceIcr));
        Assert.Equal(0x00, _pcc.ReadByte(Soft1Icr));
        Assert.Equal(0x00, _pcc.ReadByte(Timer1Icr));
        Assert.Equal(0x00, _pcc.ReadByte(Timer1Control) & 0x07);
        Assert.Equal(0, _cpu._pendingIPL);
    }

    // ========================================================================
    // Multiple devices — priority and coexistence
    // ========================================================================

    [Fact]
    public void MultipleDevices_HighestWins()
    {
        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0A); // level=2

        _pcc.SetDeviceInterrupt("lance", true);
        _pcc.WriteByte(LanceIcr, 0x0B); // level=3

        _pcc.SetDeviceInterrupt("scc", true);
        _pcc.WriteByte(SccIcr, 0x0C); // level=4

        Assert.Equal(4, _cpu._pendingIPL);
        Assert.Equal(0x40 + 3, _cpu._pendingVector); // SCC = PCCV_ZS = 3
    }

    [Fact]
    public void ClearHighestDevice_NextHighestTakesOver()
    {
        _pcc.SetDeviceInterrupt("scc", true);
        _pcc.WriteByte(SccIcr, 0x0C); // level=4

        _pcc.SetDeviceInterrupt("lance", true);
        _pcc.WriteByte(LanceIcr, 0x0B); // level=3

        Assert.Equal(4, _cpu._pendingIPL);

        _pcc.SetDeviceInterrupt("scc", false);
        _pcc.WriteByte(SccIcr, 0x8C); // W1C

        Assert.Equal(3, _cpu._pendingIPL);
        Assert.Equal(0x40 + 4, _cpu._pendingVector); // LANCE = PCCV_LE = 4
    }

    [Fact]
    public void SameLevel_EarlierDeviceWins()
    {
        _pcc.SetDeviceInterrupt("scc", true);
        _pcc.WriteByte(SccIcr, 0x0B); // level=3

        _pcc.SetDeviceInterrupt("scsi", true);
        _pcc.WriteByte(ScsiIcr, 0x0B); // level=3

        Assert.Equal(3, _cpu._pendingIPL);
        // SCC (PCCV=3) is checked before SCSI (PCCV=5), wins at same level
        Assert.Equal(0x40 + 3, _cpu._pendingVector);
    }

    // ========================================================================
    // Watchdog Timer
    // ========================================================================

    [Fact]
    public void Watchdog_ArmValue_TriggersCallback()
    {
        bool callbackInvoked = false;
        _pcc.OnWatchdogReset = () => { callbackInvoked = true; };

        // Writing 0xA5 to watchdog register arms it and triggers immediate reset
        _pcc.WriteByte(Base + 0x1D, 0xA5);
        Assert.True(callbackInvoked);
    }

    [Fact]
    public void Watchdog_ClearValue_DoesNotTriggerCallback()
    {
        bool callbackInvoked = false;
        _pcc.OnWatchdogReset = () => { callbackInvoked = true; };

        // Writing 0x0A (clear) should NOT trigger watchdog — treated as normal ICR write
        _pcc.WriteByte(Base + 0x1D, 0x0A);
        Assert.False(callbackInvoked);
    }

    [Fact]
    public void Watchdog_OtherValues_DoNotTriggerCallback()
    {
        int callCount = 0;
        _pcc.OnWatchdogReset = () => { callCount++; };

        // Various non-0xA5 values should not trigger watchdog
        _pcc.WriteByte(Base + 0x1D, 0x00);
        _pcc.WriteByte(Base + 0x1D, 0x0D); // IEN=1, level=5
        _pcc.WriteByte(Base + 0x1D, 0x80); // W1C
        _pcc.WriteByte(Base + 0x1D, 0xFF);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Watchdog_NoCallback_DoesNotCrash()
    {
        // No callback set — writing 0xA5 should not crash
        _pcc.OnWatchdogReset = null;
        _pcc.WriteByte(Base + 0x1D, 0xA5);
        // Just verify no crash
    }

    [Fact]
    public void Watchdog_LinuxRebootSequence()
    {
        // Simulates Linux mvme147_reset(): clear then arm
        bool callbackInvoked = false;
        _pcc.OnWatchdogReset = () => { callbackInvoked = true; };

        _pcc.WriteByte(Base + 0x1D, 0x0A); // Clear timer
        Assert.False(callbackInvoked);

        _pcc.WriteByte(Base + 0x1D, 0xA5); // Arm watchdog
        Assert.True(callbackInvoked);
    }
}
