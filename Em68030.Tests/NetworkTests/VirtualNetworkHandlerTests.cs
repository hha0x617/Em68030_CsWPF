using Em68030.IO;
using Xunit;

namespace Em68030.Tests.NetworkTests;

/// <summary>
/// VirtualNetworkHandler のユニットテスト。
/// 内蔵エコーサーバ (ARP/ICMP/TCP:7/UDP:7) の動作を検証する。
/// </summary>
public class VirtualNetworkHandlerTests
{
    private readonly VirtualNetworkHandler _handler = new();
    private static readonly byte[] GuestMac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
    private static readonly byte[] GatewayMac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
    private static readonly byte[] GatewayIp = { 10, 0, 2, 2 };
    private static readonly byte[] GuestIp = { 10, 0, 2, 15 };

    public VirtualNetworkHandlerTests()
    {
        _handler.SetGuestMac(GuestMac);
    }

    // ====================================================================
    // ARP
    // ====================================================================

    [Fact]
    public void Arp_Request_GetsReply()
    {
        byte[] arpRequest = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        _handler.ProcessPacket(arpRequest, arpRequest.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // Should be ARP reply
        Assert.Equal((ushort)0x0806, NetworkUtils.ReadBE16(reply, 12));
        Assert.Equal((ushort)0x0002, NetworkUtils.ReadBE16(reply, 20)); // ARP Reply opcode
        // Sender MAC = gateway MAC
        Assert.Equal(GatewayMac, reply[22..28]);
        // Target MAC = guest MAC
        Assert.Equal(GuestMac, reply[32..38]);
    }

    [Fact]
    public void Arp_ProxyArp_RespondsToAnyTargetIp()
    {
        byte[] targetIp = { 192, 168, 1, 1 };
        byte[] arpRequest = BuildArpRequest(GuestMac, GuestIp, targetIp);
        _handler.ProcessPacket(arpRequest, arpRequest.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // Sender Protocol Address should be the target IP from the request
        Assert.Equal(targetIp, reply[28..32]);
    }

    [Fact]
    public void Arp_ShortPacket_Ignored()
    {
        byte[] tooShort = new byte[30]; // ARP needs at least 42
        WriteBE16(tooShort, 12, 0x0806);
        _handler.ProcessPacket(tooShort, tooShort.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void Arp_ReplyPacket_Ignored()
    {
        byte[] arpReply = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        WriteBE16(arpReply, 20, 0x0002); // Change opcode to Reply
        _handler.ProcessPacket(arpReply, arpReply.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    // ====================================================================
    // ICMP
    // ====================================================================

    [Fact]
    public void Icmp_EchoRequest_GetsEchoReply()
    {
        byte[] ping = BuildIcmpEchoRequest(GuestMac, GatewayMac, GuestIp, GatewayIp,
            id: 0x1234, seq: 0x0001, payload: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        // Need to set guest IP first via ARP
        SendArpToLearnGuestIp();

        _handler.ProcessPacket(ping, ping.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // ICMP type = 0 (Echo Reply)
        Assert.Equal(0, reply[34]);
        // ID and sequence preserved
        Assert.Equal((ushort)0x1234, NetworkUtils.ReadBE16(reply, 38));
        Assert.Equal((ushort)0x0001, NetworkUtils.ReadBE16(reply, 40));
        // Payload preserved
        Assert.Equal(0xAA, reply[42]);
        Assert.Equal(0xBB, reply[43]);
        Assert.Equal(0xCC, reply[44]);
        Assert.Equal(0xDD, reply[45]);
        // Source IP = gateway (was destination)
        Assert.Equal(GatewayIp, reply[26..30]);
        // Dest IP = guest (was source)
        Assert.Equal(GuestIp, reply[30..34]);
    }

    [Fact]
    public void Icmp_NonEchoRequest_Ignored()
    {
        // ICMP type 3 (Dest Unreachable) should be ignored
        byte[] pkt = BuildIcmpEchoRequest(GuestMac, GatewayMac, GuestIp, GatewayIp, 1, 1, Array.Empty<byte>());
        pkt[34] = 3; // Change type to Dest Unreachable
        // Recalc ICMP checksum
        pkt[36] = 0; pkt[37] = 0;
        ushort csum = NetworkUtils.ComputeChecksum(pkt, 34, pkt.Length - 34);
        WriteBE16(pkt, 36, csum);

        SendArpToLearnGuestIp();
        _handler.ProcessPacket(pkt, pkt.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    // ====================================================================
    // TCP — Port 7 Echo
    // ====================================================================

    [Fact]
    public void Tcp_SynToPort7_GetsSynAck()
    {
        SendArpToLearnGuestIp();

        byte[] syn = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1000, ack: 0, flags: 0x02 /* SYN */);

        _handler.ProcessPacket(syn, syn.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // Flags: SYN+ACK = 0x12
        Assert.Equal(0x12, reply[47]);
        // ACK number = seq+1
        Assert.Equal(1001u, NetworkUtils.ReadBE32(reply, 42));
    }

    [Fact]
    public void Tcp_DataToPort7_GetsEchoBack()
    {
        SendArpToLearnGuestIp();

        // SYN
        byte[] syn = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1000, ack: 0, flags: 0x02);
        _handler.ProcessPacket(syn, syn.Length);
        byte[] synAck = _handler.DequeuePacket();
        uint serverSeq = NetworkUtils.ReadBE32(synAck, 38);

        // ACK (handshake complete)
        byte[] ack = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1001, ack: serverSeq + 1, flags: 0x10);
        _handler.ProcessPacket(ack, ack.Length);

        // PSH+ACK with data "Hi"
        byte[] data = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1001, ack: serverSeq + 1, flags: 0x18,
            payload: new byte[] { 0x48, 0x69 }); // "Hi"
        _handler.ProcessPacket(data, data.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] echoReply = _handler.DequeuePacket();

        // Should echo "Hi" back
        int tcpDataOff = 34 + 20; // TCP header is 20 bytes
        Assert.Equal(0x48, echoReply[tcpDataOff]);     // 'H'
        Assert.Equal(0x69, echoReply[tcpDataOff + 1]); // 'i'
    }

    [Fact]
    public void Tcp_FinToPort7_GetsFinAck()
    {
        SendArpToLearnGuestIp();

        // SYN → SYN-ACK → ACK → FIN
        byte[] syn = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1000, ack: 0, flags: 0x02);
        _handler.ProcessPacket(syn, syn.Length);
        _handler.DequeuePacket(); // SYN-ACK

        byte[] ack = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1001, ack: 1001, flags: 0x10);
        _handler.ProcessPacket(ack, ack.Length);

        byte[] fin = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 7, seq: 1001, ack: 1001, flags: 0x01); // FIN
        _handler.ProcessPacket(fin, fin.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();
        // FIN+ACK = 0x11
        Assert.Equal(0x11, reply[47]);
    }

    [Fact]
    public void Tcp_SynToNonEchoPort_GetsRst()
    {
        SendArpToLearnGuestIp();

        byte[] syn = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 80, seq: 1000, ack: 0, flags: 0x02);
        _handler.ProcessPacket(syn, syn.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();
        // RST+ACK = 0x14
        Assert.Equal(0x14, reply[47]);
    }

    [Fact]
    public void Tcp_RstToNonEchoPort_NoReply()
    {
        SendArpToLearnGuestIp();

        byte[] rst = BuildTcpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 12345, dstPort: 80, seq: 1000, ack: 0, flags: 0x04); // RST
        _handler.ProcessPacket(rst, rst.Length);

        Assert.False(_handler.HasPendingPacket());
    }

    // ====================================================================
    // UDP — Port 7 Echo
    // ====================================================================

    [Fact]
    public void Udp_Port7_EchoesPayload()
    {
        SendArpToLearnGuestIp();

        byte[] payload = { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
        byte[] udp = BuildUdpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 5000, dstPort: 7, payload: payload);
        _handler.ProcessPacket(udp, udp.Length);

        Assert.True(_handler.HasPendingPacket());
        byte[] reply = _handler.DequeuePacket();

        // Ports swapped
        Assert.Equal((ushort)7, NetworkUtils.ReadBE16(reply, 34));
        Assert.Equal((ushort)5000, NetworkUtils.ReadBE16(reply, 36));
        // Payload echoed
        for (int i = 0; i < payload.Length; i++)
            Assert.Equal(payload[i], reply[42 + i]);
    }

    [Fact]
    public void Udp_NonEchoPort_Ignored()
    {
        SendArpToLearnGuestIp();

        byte[] udp = BuildUdpPacket(GuestMac, GatewayMac, GuestIp, GatewayIp,
            srcPort: 5000, dstPort: 53, payload: new byte[] { 0x01 });
        _handler.ProcessPacket(udp, udp.Length);

        Assert.False(_handler.HasPendingPacket());
    }

    // ====================================================================
    // Reset
    // ====================================================================

    [Fact]
    public void Reset_ClearsPendingPackets()
    {
        SendArpToLearnGuestIp();

        byte[] ping = BuildIcmpEchoRequest(GuestMac, GatewayMac, GuestIp, GatewayIp, 1, 1, Array.Empty<byte>());
        _handler.ProcessPacket(ping, ping.Length);
        Assert.True(_handler.HasPendingPacket());

        _handler.Reset();
        Assert.False(_handler.HasPendingPacket());
    }

    [Fact]
    public void ShortFrame_Ignored()
    {
        byte[] tiny = { 0x00, 0x01, 0x02 };
        _handler.ProcessPacket(tiny, tiny.Length);
        Assert.False(_handler.HasPendingPacket());
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private void SendArpToLearnGuestIp()
    {
        byte[] arp = BuildArpRequest(GuestMac, GuestIp, GatewayIp);
        _handler.ProcessPacket(arp, arp.Length);
        _handler.DequeuePacket(); // discard reply
    }

    private static byte[] BuildArpRequest(byte[] senderMac, byte[] senderIp, byte[] targetIp)
    {
        byte[] pkt = new byte[60];
        // Ethernet: broadcast dst
        for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
        Array.Copy(senderMac, 0, pkt, 6, 6);
        WriteBE16(pkt, 12, 0x0806);
        // ARP
        WriteBE16(pkt, 14, 0x0001); // Ethernet
        WriteBE16(pkt, 16, 0x0800); // IPv4
        pkt[18] = 6; pkt[19] = 4;
        WriteBE16(pkt, 20, 0x0001); // Request
        Array.Copy(senderMac, 0, pkt, 22, 6);
        Array.Copy(senderIp, 0, pkt, 28, 4);
        // Target MAC = zeros (unknown)
        Array.Copy(targetIp, 0, pkt, 38, 4);
        return pkt;
    }

    private static byte[] BuildIcmpEchoRequest(byte[] srcMac, byte[] dstMac,
        byte[] srcIp, byte[] dstIp, ushort id, ushort seq, byte[] payload)
    {
        int icmpLen = 8 + payload.Length;
        int ipLen = 20 + icmpLen;
        int frameLen = Math.Max(14 + ipLen, 60);
        byte[] pkt = new byte[frameLen];

        Array.Copy(dstMac, 0, pkt, 0, 6);
        Array.Copy(srcMac, 0, pkt, 6, 6);
        WriteBE16(pkt, 12, 0x0800);

        pkt[14] = 0x45;
        WriteBE16(pkt, 16, (ushort)ipLen);
        WriteBE16(pkt, 20, 0x4000);
        pkt[22] = 64;
        pkt[23] = 1; // ICMP
        Array.Copy(srcIp, 0, pkt, 26, 4);
        Array.Copy(dstIp, 0, pkt, 30, 4);
        ushort ipCsum = NetworkUtils.ComputeChecksum(pkt, 14, 20);
        WriteBE16(pkt, 24, ipCsum);

        pkt[34] = 8; // Echo Request
        WriteBE16(pkt, 38, id);
        WriteBE16(pkt, 40, seq);
        if (payload.Length > 0)
            Array.Copy(payload, 0, pkt, 42, payload.Length);
        ushort icmpCsum = NetworkUtils.ComputeChecksum(pkt, 34, icmpLen);
        WriteBE16(pkt, 36, icmpCsum);

        return pkt;
    }

    private static byte[] BuildTcpPacket(byte[] srcMac, byte[] dstMac,
        byte[] srcIp, byte[] dstIp, ushort srcPort, ushort dstPort,
        uint seq, uint ack, byte flags, byte[]? payload = null)
    {
        int payloadLen = payload?.Length ?? 0;
        int tcpLen = 20 + payloadLen;
        int ipLen = 20 + tcpLen;
        int frameLen = 14 + ipLen; // No padding — exact length for correct data parsing
        byte[] pkt = new byte[frameLen];

        Array.Copy(dstMac, 0, pkt, 0, 6);
        Array.Copy(srcMac, 0, pkt, 6, 6);
        WriteBE16(pkt, 12, 0x0800);

        pkt[14] = 0x45;
        WriteBE16(pkt, 16, (ushort)ipLen);
        WriteBE16(pkt, 20, 0x4000);
        pkt[22] = 64;
        pkt[23] = 6; // TCP
        Array.Copy(srcIp, 0, pkt, 26, 4);
        Array.Copy(dstIp, 0, pkt, 30, 4);
        ushort ipCsum = NetworkUtils.ComputeChecksum(pkt, 14, 20);
        WriteBE16(pkt, 24, ipCsum);

        WriteBE16(pkt, 34, srcPort);
        WriteBE16(pkt, 36, dstPort);
        WriteBE32(pkt, 38, seq);
        WriteBE32(pkt, 42, ack);
        pkt[46] = 0x50; // data offset = 5
        pkt[47] = flags;
        WriteBE16(pkt, 48, 65535); // window

        if (payload != null && payloadLen > 0)
            Array.Copy(payload, 0, pkt, 54, payloadLen);

        // TCP checksum
        ushort tcpCsum = NetworkUtils.ComputeTransportChecksum(srcIp, dstIp, 6, pkt, 34, tcpLen);
        WriteBE16(pkt, 50, tcpCsum);

        return pkt;
    }

    private static byte[] BuildUdpPacket(byte[] srcMac, byte[] dstMac,
        byte[] srcIp, byte[] dstIp, ushort srcPort, ushort dstPort, byte[] payload)
    {
        int udpLen = 8 + payload.Length;
        int ipLen = 20 + udpLen;
        int frameLen = Math.Max(14 + ipLen, 60);
        byte[] pkt = new byte[frameLen];

        Array.Copy(dstMac, 0, pkt, 0, 6);
        Array.Copy(srcMac, 0, pkt, 6, 6);
        WriteBE16(pkt, 12, 0x0800);

        pkt[14] = 0x45;
        WriteBE16(pkt, 16, (ushort)ipLen);
        WriteBE16(pkt, 20, 0x4000);
        pkt[22] = 64;
        pkt[23] = 17; // UDP
        Array.Copy(srcIp, 0, pkt, 26, 4);
        Array.Copy(dstIp, 0, pkt, 30, 4);
        ushort ipCsum = NetworkUtils.ComputeChecksum(pkt, 14, 20);
        WriteBE16(pkt, 24, ipCsum);

        WriteBE16(pkt, 34, srcPort);
        WriteBE16(pkt, 36, dstPort);
        WriteBE16(pkt, 38, (ushort)udpLen);
        if (payload.Length > 0)
            Array.Copy(payload, 0, pkt, 42, payload.Length);
        ushort udpCsum = NetworkUtils.ComputeTransportChecksum(srcIp, dstIp, 17, pkt, 34, udpLen);
        if (udpCsum == 0) udpCsum = 0xFFFF;
        WriteBE16(pkt, 40, udpCsum);

        return pkt;
    }

    private static void WriteBE16(byte[] buf, int off, ushort val)
    {
        buf[off] = (byte)(val >> 8);
        buf[off + 1] = (byte)(val & 0xFF);
    }

    private static void WriteBE32(byte[] buf, int off, uint val)
    {
        buf[off] = (byte)(val >> 24);
        buf[off + 1] = (byte)((val >> 16) & 0xFF);
        buf[off + 2] = (byte)((val >> 8) & 0xFF);
        buf[off + 3] = (byte)(val & 0xFF);
    }
}
