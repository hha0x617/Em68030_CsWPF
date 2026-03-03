using Em68030.IO;
using Xunit;

namespace Em68030.Tests.NetworkTests;

public class NetworkUtilsTests
{
    // ====================================================================
    // Big-endian helpers
    // ====================================================================

    [Fact]
    public void WriteBE16_ReadsBackCorrectly()
    {
        byte[] buf = new byte[4];
        NetworkUtils.WriteBE16(buf, 1, 0xABCD);
        Assert.Equal(0xAB, buf[1]);
        Assert.Equal(0xCD, buf[2]);
    }

    [Fact]
    public void ReadBE16_ReturnsCorrectValue()
    {
        byte[] buf = { 0x00, 0x12, 0x34, 0x00 };
        ushort val = NetworkUtils.ReadBE16(buf, 1);
        Assert.Equal((ushort)0x1234, val);
    }

    [Fact]
    public void WriteBE32_ReadsBackCorrectly()
    {
        byte[] buf = new byte[6];
        NetworkUtils.WriteBE32(buf, 1, 0xDEADBEEF);
        Assert.Equal(0xDE, buf[1]);
        Assert.Equal(0xAD, buf[2]);
        Assert.Equal(0xBE, buf[3]);
        Assert.Equal(0xEF, buf[4]);
    }

    [Fact]
    public void ReadBE32_ReturnsCorrectValue()
    {
        byte[] buf = { 0x00, 0xCA, 0xFE, 0xBA, 0xBE };
        uint val = NetworkUtils.ReadBE32(buf, 1);
        Assert.Equal(0xCAFEBABEu, val);
    }

    // ====================================================================
    // Checksum
    // ====================================================================

    [Fact]
    public void ComputeChecksum_ZeroData_ReturnsFFFF()
    {
        byte[] data = new byte[20];
        ushort csum = NetworkUtils.ComputeChecksum(data, 0, 20);
        Assert.Equal((ushort)0xFFFF, csum);
    }

    [Fact]
    public void ComputeChecksum_ValidIpHeader_ReturnsZeroOnVerify()
    {
        // Construct a minimal IP header with a correct checksum,
        // then verify that re-computing checksum over the whole header yields 0.
        byte[] hdr = new byte[20];
        hdr[0] = 0x45; // version=4, IHL=5
        NetworkUtils.WriteBE16(hdr, 2, 40); // total length
        hdr[8] = 64; // TTL
        hdr[9] = 6; // TCP
        // src IP = 10.0.2.15
        hdr[12] = 10; hdr[13] = 0; hdr[14] = 2; hdr[15] = 15;
        // dst IP = 8.8.8.8
        hdr[16] = 8; hdr[17] = 8; hdr[18] = 8; hdr[19] = 8;
        // checksum at offset 10-11 is zero before computation
        ushort csum = NetworkUtils.ComputeChecksum(hdr, 0, 20);
        NetworkUtils.WriteBE16(hdr, 10, csum);

        // Verify: checksum of header including checksum field should be 0
        ushort verify = NetworkUtils.ComputeChecksum(hdr, 0, 20);
        Assert.Equal((ushort)0, verify);
    }

    [Fact]
    public void ComputeChecksum_OddLength_HandlesCorrectly()
    {
        // Odd-length data: should pad last byte
        byte[] data = { 0x01, 0x02, 0x03 };
        ushort csum = NetworkUtils.ComputeChecksum(data, 0, 3);
        // Manual: sum = 0x0102 + 0x0300 = 0x0402, ~0x0402 = 0xFBFD
        Assert.Equal((ushort)0xFBFD, csum);
    }

    // ====================================================================
    // BuildArpReply
    // ====================================================================

    [Fact]
    public void BuildArpReply_HasCorrectFormat()
    {
        byte[] dstMac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
        byte[] srcMac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
        byte[] srcIp = { 10, 0, 2, 2 };
        byte[] dstIp = { 10, 0, 2, 15 };

        byte[] reply = NetworkUtils.BuildArpReply(dstMac, srcMac, srcIp, dstIp);

        Assert.True(reply.Length >= 60); // minimum Ethernet frame
        // Ethernet header
        Assert.Equal(dstMac, reply[..6]);
        Assert.Equal(srcMac, reply[6..12]);
        Assert.Equal((ushort)0x0806, NetworkUtils.ReadBE16(reply, 12)); // ARP ethertype
        // ARP header
        Assert.Equal((ushort)0x0001, NetworkUtils.ReadBE16(reply, 14)); // Ethernet
        Assert.Equal((ushort)0x0800, NetworkUtils.ReadBE16(reply, 16)); // IPv4
        Assert.Equal(6, reply[18]); // hw size
        Assert.Equal(4, reply[19]); // proto size
        Assert.Equal((ushort)0x0002, NetworkUtils.ReadBE16(reply, 20)); // Reply
        // Sender = src
        Assert.Equal(srcMac, reply[22..28]);
        Assert.Equal(srcIp, reply[28..32]);
        // Target = dst
        Assert.Equal(dstMac, reply[32..38]);
        Assert.Equal(dstIp, reply[38..42]);
    }

    // ====================================================================
    // BuildIpPacket
    // ====================================================================

    [Fact]
    public void BuildIpPacket_HasCorrectHeaders()
    {
        byte[] dstMac = { 0x08, 0x00, 0x3E, 0x21, 0x00, 0x00 };
        byte[] srcMac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
        byte[] srcIp = { 10, 0, 2, 2 };
        byte[] dstIp = { 10, 0, 2, 15 };

        byte[] pkt = NetworkUtils.BuildIpPacket(dstMac, srcMac, srcIp, dstIp, NetworkUtils.IP_PROTO_TCP, 20);

        // Ethernet
        Assert.Equal(dstMac, pkt[..6]);
        Assert.Equal(srcMac, pkt[6..12]);
        Assert.Equal((ushort)0x0800, NetworkUtils.ReadBE16(pkt, 12));
        // IP header
        Assert.Equal(0x45, pkt[14]); // version + IHL
        Assert.Equal((ushort)(20 + 20), NetworkUtils.ReadBE16(pkt, 16)); // total length
        Assert.Equal(64, pkt[22]); // TTL
        Assert.Equal(NetworkUtils.IP_PROTO_TCP, pkt[23]);
        // Verify IP checksum
        ushort verify = NetworkUtils.ComputeChecksum(pkt, 14, 20);
        Assert.Equal((ushort)0, verify);
        // IPs
        Assert.Equal(srcIp, pkt[26..30]);
        Assert.Equal(dstIp, pkt[30..34]);
    }

    [Fact]
    public void BuildIpPacket_PadsToMinimumFrame()
    {
        byte[] mac = new byte[6];
        byte[] ip = new byte[4];
        byte[] pkt = NetworkUtils.BuildIpPacket(mac, mac, ip, ip, 0, 0);
        Assert.True(pkt.Length >= 60);
    }

    // ====================================================================
    // ComputeTransportChecksum
    // ====================================================================

    [Fact]
    public void ComputeTransportChecksum_VerifiesCorrectly()
    {
        byte[] srcIp = { 10, 0, 2, 2 };
        byte[] dstIp = { 10, 0, 2, 15 };
        // Simple UDP segment: src=1234, dst=53, len=8, csum=0, no payload
        byte[] udp = new byte[8];
        NetworkUtils.WriteBE16(udp, 0, 1234);
        NetworkUtils.WriteBE16(udp, 2, 53);
        NetworkUtils.WriteBE16(udp, 4, 8);
        // compute checksum
        ushort csum = NetworkUtils.ComputeTransportChecksum(srcIp, dstIp, NetworkUtils.IP_PROTO_UDP, udp, 0, 8);
        NetworkUtils.WriteBE16(udp, 6, csum);
        // verify: should be 0
        ushort verify = NetworkUtils.ComputeTransportChecksum(srcIp, dstIp, NetworkUtils.IP_PROTO_UDP, udp, 0, 8);
        Assert.Equal((ushort)0, verify);
    }
}
