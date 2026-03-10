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

namespace Em68030.IO;

/// <summary>
/// Shared network packet utility methods used by network handlers.
/// </summary>
public static class NetworkUtils
{
    public const ushort ETHERTYPE_ARP = 0x0806;
    public const ushort ETHERTYPE_IPV4 = 0x0800;
    public const byte IP_PROTO_ICMP = 1;
    public const byte IP_PROTO_TCP = 6;
    public const byte IP_PROTO_UDP = 17;
    public const int MIN_ETHERNET_FRAME = 60;

    public static ushort ComputeChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int i = 0;
        while (i < length - 1)
        {
            sum += (uint)((data[offset + i] << 8) | data[offset + i + 1]);
            i += 2;
        }
        if (i < length)
            sum += (uint)(data[offset + i] << 8);

        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }

    public static void WriteBE16(byte[] buf, int off, ushort val)
    {
        buf[off] = (byte)(val >> 8);
        buf[off + 1] = (byte)(val & 0xFF);
    }

    public static void WriteBE32(byte[] buf, int off, uint val)
    {
        buf[off] = (byte)(val >> 24);
        buf[off + 1] = (byte)((val >> 16) & 0xFF);
        buf[off + 2] = (byte)((val >> 8) & 0xFF);
        buf[off + 3] = (byte)(val & 0xFF);
    }

    public static ushort ReadBE16(byte[] buf, int off)
    {
        return (ushort)((buf[off] << 8) | buf[off + 1]);
    }

    public static uint ReadBE32(byte[] buf, int off)
    {
        return (uint)((buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3]);
    }

    /// <summary>
    /// Build an Ethernet + IPv4 header. Returns the packet buffer (caller fills protocol-specific data).
    /// </summary>
    public static byte[] BuildIpPacket(byte[] dstMac, byte[] srcMac, byte[] srcIp, byte[] dstIp,
        byte protocol, int payloadLen)
    {
        int ipTotalLen = 20 + payloadLen;
        int frameLen = 14 + ipTotalLen;
        if (frameLen < MIN_ETHERNET_FRAME) frameLen = MIN_ETHERNET_FRAME;

        byte[] pkt = new byte[frameLen];

        // Ethernet header
        Array.Copy(dstMac, 0, pkt, 0, 6);
        Array.Copy(srcMac, 0, pkt, 6, 6);
        WriteBE16(pkt, 12, ETHERTYPE_IPV4);

        // IP header
        pkt[14] = 0x45; // version=4, IHL=5
        WriteBE16(pkt, 16, (ushort)ipTotalLen);
        WriteBE16(pkt, 20, 0x4000); // Don't Fragment
        pkt[22] = 64; // TTL
        pkt[23] = protocol;
        Array.Copy(srcIp, 0, pkt, 26, 4);
        Array.Copy(dstIp, 0, pkt, 30, 4);

        // IP checksum
        ushort ipCsum = ComputeChecksum(pkt, 14, 20);
        WriteBE16(pkt, 24, ipCsum);

        return pkt;
    }

    /// <summary>
    /// Build an ARP reply packet.
    /// </summary>
    public static byte[] BuildArpReply(byte[] dstMac, byte[] srcMac, byte[] srcIp, byte[] dstIp)
    {
        byte[] reply = new byte[MIN_ETHERNET_FRAME];

        // Ethernet header
        Array.Copy(dstMac, 0, reply, 0, 6);
        Array.Copy(srcMac, 0, reply, 6, 6);
        WriteBE16(reply, 12, ETHERTYPE_ARP);

        // ARP header
        WriteBE16(reply, 14, 0x0001); // hardware type: Ethernet
        WriteBE16(reply, 16, 0x0800); // protocol type: IPv4
        reply[18] = 6;                // hardware size
        reply[19] = 4;                // protocol size
        WriteBE16(reply, 20, 0x0002); // opcode: Reply

        // Sender
        Array.Copy(srcMac, 0, reply, 22, 6);
        Array.Copy(srcIp, 0, reply, 28, 4);

        // Target
        Array.Copy(dstMac, 0, reply, 32, 6);
        Array.Copy(dstIp, 0, reply, 38, 4);

        return reply;
    }

    /// <summary>
    /// Compute TCP or UDP checksum with pseudo-header.
    /// </summary>
    public static ushort ComputeTransportChecksum(byte[] srcIp, byte[] dstIp, byte protocol,
        byte[] segment, int segOffset, int segLen)
    {
        byte[] pseudo = new byte[12 + segLen];
        Array.Copy(srcIp, 0, pseudo, 0, 4);
        Array.Copy(dstIp, 0, pseudo, 4, 4);
        pseudo[8] = 0;
        pseudo[9] = protocol;
        WriteBE16(pseudo, 10, (ushort)segLen);
        Array.Copy(segment, segOffset, pseudo, 12, segLen);
        return ComputeChecksum(pseudo, 0, pseudo.Length);
    }
}
