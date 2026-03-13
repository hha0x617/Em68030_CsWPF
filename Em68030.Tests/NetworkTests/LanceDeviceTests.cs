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

namespace Em68030.Tests.NetworkTests;

/// <summary>
/// LanceDevice (AM7990 LANCE) のユニットテスト。
/// CSR レジスタ、初期化ブロック解析、TX/RX リング処理を検証する。
/// </summary>
public class LanceDeviceTests
{
    private readonly Memory _memory;
    private readonly LanceDevice _lance;
    private bool _interruptActive;

    private const uint BaseAddr = 0xFFFE1800;
    private const uint InitBlockAddr = 0x00010000;
    private const uint TxRingAddr = 0x00011000;
    private const uint RxRingAddr = 0x00012000;

    public LanceDeviceTests()
    {
        _memory = new Memory();
        _memory.AddRegion(0x00000000, 4 * 1024 * 1024, RegionType.Ram);
        // Register LANCE in I/O space
        _memory.AddRegion(0xFFFE0000, 0x10000, RegionType.Ram);

        _lance = new LanceDevice();
        _lance.AttachMemory(_memory);
        _lance.InterruptOutput = active => _interruptActive = active;
        _memory.RegisterDevice(BaseAddr, 4, _lance);
    }

    // ====================================================================
    // CSR Register Access
    // ====================================================================

    [Fact]
    public void InitialState_CSR0HasStopBit()
    {
        // RAP defaults to 0, read CSR0
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0004); // STOP bit
    }

    [Fact]
    public void WriteRAP_SelectsCSR()
    {
        // Write RAP = 1
        _lance.WriteWord(BaseAddr + 2, 1);
        Assert.Equal((ushort)1, _lance.ReadWord(BaseAddr + 2));
    }

    [Fact]
    public void CSR1_WritableInStopState()
    {
        // In STOP state, CSR1 should be writable
        _lance.WriteWord(BaseAddr + 2, 1); // RAP = 1
        _lance.WriteWord(BaseAddr, 0x1234); // Write CSR1
        Assert.Equal((ushort)0x1234, _lance.ReadWord(BaseAddr));
    }

    [Fact]
    public void CSR1_NotWritableWhenRunning()
    {
        // Initialize and start the LANCE
        SetupAndStartLance();

        // Now try to write CSR1 — should be ignored
        _lance.WriteWord(BaseAddr + 2, 1); // RAP = 1
        ushort before = _lance.ReadWord(BaseAddr);
        _lance.WriteWord(BaseAddr, 0xFFFF); // Try to overwrite
        ushort after = _lance.ReadWord(BaseAddr);
        Assert.Equal(before, after);
    }

    [Fact]
    public void CSR0_W1C_ClearsStatusBits()
    {
        SetupAndStartLance();

        // RAP = 0
        _lance.WriteWord(BaseAddr + 2, 0);

        // Read CSR0 — IDON should be set after init
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0100); // IDON

        // W1C: write 1 to IDON to clear it
        _lance.WriteWord(BaseAddr, 0x0100);
        csr0 = _lance.ReadWord(BaseAddr);
        Assert.Equal(0, csr0 & 0x0100); // IDON cleared
    }

    [Fact]
    public void CSR0_Stop_ResetsChip()
    {
        SetupAndStartLance();
        _lance.WriteWord(BaseAddr + 2, 0);

        // Verify running
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0020); // RXON

        // Write STOP
        _lance.WriteWord(BaseAddr, 0x0004);
        csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0004); // STOP
        Assert.Equal(0, csr0 & 0x0020);    // RXON cleared
    }

    // ====================================================================
    // Initialization
    // ====================================================================

    [Fact]
    public void Init_SetsIDON()
    {
        WriteInitBlock();
        SetCSRAddress(InitBlockAddr);

        // Write INIT to CSR0
        _lance.WriteWord(BaseAddr + 2, 0);
        _lance.WriteWord(BaseAddr, 0x0001);

        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0100); // IDON
    }

    [Fact]
    public void Init_ThenStart_SetsRunning()
    {
        SetupAndStartLance();
        _lance.WriteWord(BaseAddr + 2, 0);
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0020); // RXON
        Assert.NotEqual(0, csr0 & 0x0010); // TXON
    }

    [Fact]
    public void Init_ParsesMacAddress_LittleEndianWordOrder()
    {
        // AM7990 init block stores MAC byte-swapped within 16-bit words.
        // MAC 08:00:3E:21:00:00 → words 0x0008, 0x213E, 0x0000
        SetupAndStartLance();

        var mac = _lance.GetMacAddress();
        Assert.Equal(0x08, mac[0]);
        Assert.Equal(0x00, mac[1]);
        Assert.Equal(0x3E, mac[2]);
        Assert.Equal(0x21, mac[3]);
        Assert.Equal(0x00, mac[4]);
        Assert.Equal(0x00, mac[5]);
    }

    // ====================================================================
    // Interrupt
    // ====================================================================

    [Fact]
    public void Interrupt_INEA_EnablesInterrupt()
    {
        SetupAndStartLance();
        _lance.WriteWord(BaseAddr + 2, 0);

        // IDON is set from init — but INEA is not set yet, so no interrupt
        Assert.False(_interruptActive);

        // Set INEA
        _lance.WriteWord(BaseAddr, 0x0040);
        // Now INTR should fire (IDON is set + INEA)
        Assert.True(_interruptActive);
    }

    [Fact]
    public void Interrupt_ClearStatus_ClearsInterrupt()
    {
        SetupAndStartLance();
        _lance.WriteWord(BaseAddr + 2, 0);

        // Enable interrupt
        _lance.WriteWord(BaseAddr, 0x0040);
        Assert.True(_interruptActive);

        // Clear IDON (W1C)
        _lance.WriteWord(BaseAddr, 0x0140); // IDON + INEA (preserve INEA)
        Assert.False(_interruptActive);
    }

    [Fact]
    public void INEA_NotClearedByWritingZero()
    {
        SetupAndStartLance();
        _lance.WriteWord(BaseAddr + 2, 0);

        // Set INEA
        _lance.WriteWord(BaseAddr, 0x0040);
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0040); // INEA set

        // Write CSR0 without INEA (e.g. TDMD only) — per AM7990 spec, INEA must NOT be cleared
        _lance.WriteWord(BaseAddr, 0x0008); // TDMD only
        csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0040); // INEA still set
    }

    // ====================================================================
    // TX Ring
    // ====================================================================

    [Fact]
    public void TxRing_ProcessesSinglePacket()
    {
        SetupAndStartLance();

        // Create a TX descriptor with OWN=1, STP=1, ENP=1
        uint bufAddr = 0x00020000;
        byte[] testPacket = new byte[64];
        for (int i = 0; i < 64; i++) testPacket[i] = (byte)(i & 0xFF);
        // Write packet data to buffer
        for (int i = 0; i < 64; i++)
            _memory.PokeByte(bufAddr + (uint)i, testPacket[i]);

        // TMD0: buffer addr low 16
        _memory.PokeWord(TxRingAddr, (ushort)(bufAddr & 0xFFFF));
        // TMD1: OWN=1, STP=1, ENP=1 in high byte; addr bits 23:16 in low byte
        byte flags = 0x80 | 0x02 | 0x01; // OWN | STP | ENP
        _memory.PokeWord(TxRingAddr + 2, (ushort)((flags << 8) | ((bufAddr >> 16) & 0xFF)));
        // TMD2: byte count = -64 (2's complement, upper nibble 0xF)
        _memory.PokeWord(TxRingAddr + 4, (ushort)((-64) & 0x0FFF | 0xF000));
        // TMD3: clear
        _memory.PokeWord(TxRingAddr + 6, 0);

        // Trigger TDMD
        _lance.WriteWord(BaseAddr + 2, 0);
        _lance.WriteWord(BaseAddr, 0x0008); // TDMD
        _lance.Tick();

        // After processing, OWN should be cleared
        ushort tmd1 = _memory.PeekWord(TxRingAddr + 2);
        Assert.Equal(0, (tmd1 >> 8) & 0x80); // OWN cleared

        // TINT should be set
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0200); // TINT
    }

    // ====================================================================
    // RX Ring
    // ====================================================================

    [Fact]
    public void RxRing_ReceivesPacketFromHandler()
    {
        SetupAndStartLance();

        // Prepare RX descriptor with OWN=1, buffer size = 256
        uint bufAddr = 0x00030000;
        _memory.PokeWord(RxRingAddr, (ushort)(bufAddr & 0xFFFF));
        byte flags = 0x80; // OWN=1
        _memory.PokeWord(RxRingAddr + 2, (ushort)((flags << 8) | ((bufAddr >> 16) & 0xFF)));
        _memory.PokeWord(RxRingAddr + 4, (ushort)((-256) & 0x0FFF | 0xF000));
        _memory.PokeWord(RxRingAddr + 6, 0);

        // Send ARP request to generate a pending reply packet
        byte[] guestMac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
        byte[] guestIp = { 10, 0, 2, 15 };
        byte[] gatewayIp = { 10, 0, 2, 2 };
        byte[] arpReq = new byte[60];
        for (int i = 0; i < 6; i++) arpReq[i] = 0xFF;
        Array.Copy(guestMac, 0, arpReq, 6, 6);
        arpReq[12] = 0x08; arpReq[13] = 0x06;
        arpReq[14] = 0x00; arpReq[15] = 0x01;
        arpReq[16] = 0x08; arpReq[17] = 0x00;
        arpReq[18] = 6; arpReq[19] = 4;
        arpReq[20] = 0x00; arpReq[21] = 0x01;
        Array.Copy(guestMac, 0, arpReq, 22, 6);
        Array.Copy(guestIp, 0, arpReq, 28, 4);
        Array.Copy(gatewayIp, 0, arpReq, 38, 4);

        // Write ARP packet to TX buffer and submit it
        uint txBuf = 0x00020000;
        for (int i = 0; i < 60; i++)
            _memory.PokeByte(txBuf + (uint)i, arpReq[i]);
        _memory.PokeWord(TxRingAddr, (ushort)(txBuf & 0xFFFF));
        _memory.PokeWord(TxRingAddr + 2, (ushort)(((0x80 | 0x02 | 0x01) << 8) | ((txBuf >> 16) & 0xFF)));
        _memory.PokeWord(TxRingAddr + 4, (ushort)((-60) & 0x0FFF | 0xF000));

        // Process TX (sends ARP to handler, handler generates reply)
        _lance.WriteWord(BaseAddr + 2, 0);
        _lance.WriteWord(BaseAddr, 0x0008);
        _lance.Tick();

        // Now tick again — RX should pick up the ARP reply
        _lance.Tick();

        // Verify RX descriptor: OWN cleared, STP+ENP set
        ushort rmd1 = _memory.PeekWord(RxRingAddr + 2);
        Assert.Equal(0, (rmd1 >> 8) & 0x80); // OWN cleared
        Assert.NotEqual(0, (rmd1 >> 8) & 0x03); // STP+ENP

        // RINT should be set
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0400); // RINT

        // Verify received data is ARP reply (ethertype at offset 12-13)
        byte etHigh = _memory.PeekByte(bufAddr + 12);
        byte etLow = _memory.PeekByte(bufAddr + 13);
        Assert.Equal(0x08, etHigh);
        Assert.Equal(0x06, etLow);

        // Verify ARP reply destination MAC matches guest MAC (08:00:3E:21:00:00)
        Assert.Equal(0x08, _memory.PeekByte(bufAddr + 0));
        Assert.Equal(0x00, _memory.PeekByte(bufAddr + 1));
        Assert.Equal(0x3E, _memory.PeekByte(bufAddr + 2));
        Assert.Equal(0x21, _memory.PeekByte(bufAddr + 3));
        Assert.Equal(0x00, _memory.PeekByte(bufAddr + 4));
        Assert.Equal(0x00, _memory.PeekByte(bufAddr + 5));
    }

    // ====================================================================
    // SetNetworkHandler
    // ====================================================================

    [Fact]
    public void SetNetworkHandler_SwapsHandler()
    {
        var handler = new VirtualNetworkHandler();
        _lance.SetNetworkHandler(handler);

        // Should still work — init and verify
        SetupAndStartLance();
        _lance.WriteWord(BaseAddr + 2, 0);
        ushort csr0 = _lance.ReadWord(BaseAddr);
        Assert.NotEqual(0, csr0 & 0x0020); // RXON
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private void WriteInitBlock()
    {
        // Mode
        _memory.PokeWord(InitBlockAddr, 0x0000);
        // PADR (MAC): 08:00:3E:21:00:00
        // AM7990 LANCE is little-endian: bytes are swapped within each 16-bit word.
        // 08:00 → word 0x0008, 3E:21 → word 0x213E, 00:00 → word 0x0000
        _memory.PokeWord(InitBlockAddr + 0x02, 0x0008);
        _memory.PokeWord(InitBlockAddr + 0x04, 0x213E);
        _memory.PokeWord(InitBlockAddr + 0x06, 0x0000);
        // LADRF (multicast filter, all zeros)
        for (uint i = 0x08; i < 0x10; i += 2)
            _memory.PokeWord(InitBlockAddr + i, 0x0000);
        // RDRA low + RLEN|RHI: 1 entry (log2=0), RX ring at RxRingAddr
        _memory.PokeWord(InitBlockAddr + 0x10, (ushort)(RxRingAddr & 0xFFFF));
        _memory.PokeWord(InitBlockAddr + 0x12, (ushort)((0 << 13) | ((RxRingAddr >> 16) & 0xFF))); // rlen=0 (1 entry)
        // TDRA low + TLEN|THI: 1 entry (log2=0), TX ring at TxRingAddr
        _memory.PokeWord(InitBlockAddr + 0x14, (ushort)(TxRingAddr & 0xFFFF));
        _memory.PokeWord(InitBlockAddr + 0x16, (ushort)((0 << 13) | ((TxRingAddr >> 16) & 0xFF)));
    }

    private void SetCSRAddress(uint addr)
    {
        // CSR1 = low 16 bits of init block address
        _lance.WriteWord(BaseAddr + 2, 1);
        _lance.WriteWord(BaseAddr, (ushort)(addr & 0xFFFF));
        // CSR2 = high 8 bits
        _lance.WriteWord(BaseAddr + 2, 2);
        _lance.WriteWord(BaseAddr, (ushort)((addr >> 16) & 0xFF));
    }

    private void SetupAndStartLance()
    {
        WriteInitBlock();
        SetCSRAddress(InitBlockAddr);
        _lance.WriteWord(BaseAddr + 2, 0);
        _lance.WriteWord(BaseAddr, 0x0001); // INIT
        _lance.WriteWord(BaseAddr, 0x0002); // STRT
    }
}
