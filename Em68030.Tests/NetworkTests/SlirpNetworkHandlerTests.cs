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

using Em68030.IO;
using Xunit;

namespace Em68030.Tests.NetworkTests;

/// <summary>
/// SlirpNetworkHandler のユニットテスト。
/// ARP プロキシ応答など、ネットワーク接続不要な機能を検証する。
/// ICMP/UDP/TCP はホストネットワーク依存のため結合テスト向き。
/// </summary>
public class SlirpNetworkHandlerTests : IDisposable
{
    private readonly SlirpNetworkHandler _handler = new();
    private static readonly byte[] GuestMac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
    private static readonly byte[] GatewayMac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
    private static readonly byte[] GatewayIp = { 10, 0, 2, 2 };
    private static readonly byte[] GuestIp = { 10, 0, 2, 15 };

    public SlirpNetworkHandlerTests()
    {
        _handler.SetGuestMac(GuestMac);
    }

    public void Dispose()
    {
        _handler.Dispose();
    }

    // ====================================================================
    // ARP — Proxy ARP
    // ====================================================================

    [Fact]
    public void Arp_Request_GetsProxyReply()
    {
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        _handler.ProcessPacket(arp, arp.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // ARP Reply
        Assert.Equal((ushort)0x0806, NetworkUtils.ReadBE16(reply, 12));
        Assert.Equal((ushort)0x0002, NetworkUtils.ReadBE16(reply, 20));
        // Sender MAC = gateway MAC
        Assert.Equal(GatewayMac, reply[22..28]);
        // Sender IP = target IP from request (proxy ARP)
        Assert.Equal(GatewayIp, reply[28..32]);
    }

    [Fact]
    public void Arp_ProxyArp_RespondsToAnyIp()
    {
        byte[] remoteIp = { 8, 8, 8, 8 };
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, remoteIp);
        _handler.ProcessPacket(arp, arp.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // Sender IP should be the requested target (8.8.8.8)
        Assert.Equal(remoteIp, reply[28..32]);
        // Gateway MAC is used for all proxy ARP responses
        Assert.Equal(GatewayMac, reply[22..28]);
    }

    [Fact]
    public void Arp_LearnGuestIp_FromSpa()
    {
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        _handler.ProcessPacket(arp, arp.Length);
        byte[] reply = _handler.DequeuePacket();

        // Target IP in reply should be the learned guest IP
        Assert.Equal(GuestIp, reply[38..42]);
    }

    [Fact]
    public void Arp_NonRequest_Ignored()
    {
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        arp[20] = 0x00; arp[21] = 0x02; // Change to Reply
        _handler.ProcessPacket(arp, arp.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void Arp_DadProbe_Ignored()
    {
        // DAD probe: SPA = 0.0.0.0, TPA = guest IP
        byte[] zeroIp = { 0, 0, 0, 0 };
        byte[] arp = BuildArpRequest(GuestMac, zeroIp, GuestIp);
        _handler.ProcessPacket(arp, arp.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void Arp_ForOwnIp_Ignored()
    {
        // ARP request where TPA = guest's own IP should not be answered
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, GuestIp);
        _handler.ProcessPacket(arp, arp.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void Arp_TooShort_Ignored()
    {
        byte[] small = new byte[30];
        small[12] = 0x08; small[13] = 0x06;
        _handler.ProcessPacket(small, small.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    // ====================================================================
    // General
    // ====================================================================

    [Fact]
    public void ProcessPacket_ShortFrame_Ignored()
    {
        _handler.ProcessPacket(new byte[] { 0x00, 0x01 }, 2);
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void ProcessPacket_UnknownEtherType_Ignored()
    {
        byte[] pkt = new byte[60];
        pkt[12] = 0x88; pkt[13] = 0x00; // Unknown ethertype
        _handler.ProcessPacket(pkt, pkt.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void Reset_ClearsQueue()
    {
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        _handler.ProcessPacket(arp, arp.Length);
        Assert.True(_handler.HasPendingPacket());

        _handler.Reset();
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void SetGuestMac_UpdatesMacInReplies()
    {
        byte[] newMac = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        _handler.SetGuestMac(newMac);

        byte[] arp = BuildArpRequest(newMac, GuestIp, GatewayIp);
        _handler.ProcessPacket(arp, arp.Length);

        byte[] reply = _handler.DequeuePacket();
        // Ethernet dst = new guest MAC
        Assert.Equal(newMac, reply[..6]);
        // ARP target MAC = new guest MAC
        Assert.Equal(newMac, reply[32..38]);
    }

    [Fact]
    public void INetworkHandler_ImplementsInterface()
    {
        INetworkHandler handler = _handler;
        Assert.NotNull(handler);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static byte[] BuildArpRequest(byte[] senderMac, byte[] senderIp, byte[] targetIp)
    {
        byte[] pkt = new byte[60];
        for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
        Array.Copy(senderMac, 0, pkt, 6, 6);
        pkt[12] = 0x08; pkt[13] = 0x06;
        pkt[14] = 0x00; pkt[15] = 0x01;
        pkt[16] = 0x08; pkt[17] = 0x00;
        pkt[18] = 6; pkt[19] = 4;
        pkt[20] = 0x00; pkt[21] = 0x01;
        Array.Copy(senderMac, 0, pkt, 22, 6);
        Array.Copy(senderIp, 0, pkt, 28, 4);
        Array.Copy(targetIp, 0, pkt, 38, 4);
        return pkt;
    }
}
