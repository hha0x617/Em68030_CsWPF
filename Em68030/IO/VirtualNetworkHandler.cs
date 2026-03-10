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

using System.Collections.Generic;

/// <summary>
/// Virtual network backend for LANCE emulation.
/// Implements a gateway at 10.0.2.2 with proxy ARP, ICMP echo, TCP echo (port 7), and UDP echo (port 7).
/// No external network connection required.
/// </summary>
public class VirtualNetworkHandler : INetworkHandler
{
    private static readonly byte[] GatewayMac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
    private static readonly byte[] GatewayIp = { 10, 0, 2, 2 };

    private const ushort ETHERTYPE_ARP = 0x0806;
    private const ushort ETHERTYPE_IPV4 = 0x0800;
    private const byte IP_PROTO_ICMP = 1;
    private const byte IP_PROTO_TCP = 6;
    private const byte IP_PROTO_UDP = 17;
    private const int MIN_ETHERNET_FRAME = 60;

    private byte[] _guestMac = new byte[6];
    private readonly byte[] _guestIp = new byte[4];
    private bool _guestIpKnown;
    private readonly Queue<byte[]> _rxQueue = new();
    private readonly Dictionary<ushort, TcpConnectionState> _tcpConnections = new();

    private enum TcpState { SynReceived, Established }

    private struct TcpConnectionState
    {
        public TcpState State;
        public uint OurSeq;
        public uint TheirSeq;
    }

    public void SetGuestMac(byte[] mac)
    {
        _guestMac = new byte[6];
        System.Array.Copy(mac, _guestMac, 6);
    }

    public void ProcessPacket(byte[] frame, int length)
    {
        if (length < 14) return;

        ushort etherType = ReadBE16(frame, 12);
        switch (etherType)
        {
            case ETHERTYPE_ARP:
                HandleArp(frame, length);
                break;
            case ETHERTYPE_IPV4:
                HandleIpv4(frame, length);
                break;
        }
    }

    public bool HasPendingPacket() => _rxQueue.Count > 0;

    public byte[] DequeuePacket() => _rxQueue.Dequeue();

    public void Reset()
    {
        _rxQueue.Clear();
        _tcpConnections.Clear();
    }

    public void Dispose()
    {
    }

    // --- ARP ---

    private void HandleArp(byte[] frame, int length)
    {
        if (length < 42) return;

        ushort op = ReadBE16(frame, 20);
        if (op != 1) return; // Only handle ARP Request

        // Ignore DAD probes (SPA = 0.0.0.0) — responding would cause
        // "DAD duplicate address" errors during interface configuration.
        if (frame[28] == 0 && frame[29] == 0 && frame[30] == 0 && frame[31] == 0)
            return;

        // Learn guest IP from sender protocol address (SPA at offset 28)
        System.Array.Copy(frame, 28, _guestIp, 0, 4);
        _guestIpKnown = true;

        // Don't respond if TPA = guest IP (gratuitous ARP / DAD announcement)
        if (frame[38] == _guestIp[0] && frame[39] == _guestIp[1] &&
            frame[40] == _guestIp[2] && frame[41] == _guestIp[3])
            return;

        // Build ARP Reply
        byte[] reply = new byte[MIN_ETHERNET_FRAME];

        // Ethernet header: dst = guest MAC, src = gateway MAC
        System.Array.Copy(_guestMac, 0, reply, 0, 6);
        System.Array.Copy(GatewayMac, 0, reply, 6, 6);
        WriteBE16(reply, 12, ETHERTYPE_ARP);

        // ARP header
        WriteBE16(reply, 14, 0x0001); // hardware type: Ethernet
        WriteBE16(reply, 16, 0x0800); // protocol type: IPv4
        reply[18] = 6;                // hardware size
        reply[19] = 4;                // protocol size
        WriteBE16(reply, 20, 0x0002); // opcode: Reply

        // Sender: gateway
        System.Array.Copy(GatewayMac, 0, reply, 22, 6);
        // Use target protocol address from request as our SPA (proxy ARP)
        System.Array.Copy(frame, 38, reply, 28, 4);

        // Target: guest
        System.Array.Copy(_guestMac, 0, reply, 32, 6);
        System.Array.Copy(_guestIp, 0, reply, 38, 4);

        _rxQueue.Enqueue(reply);
    }

    // --- IPv4 ---

    private void HandleIpv4(byte[] frame, int length)
    {
        if (length < 34) return;

        int ipHeaderLen = (frame[14] & 0x0F) * 4;
        if (length < 14 + ipHeaderLen) return;

        byte protocol = frame[23];
        switch (protocol)
        {
            case IP_PROTO_ICMP:
                HandleIcmp(frame, length, ipHeaderLen);
                break;
            case IP_PROTO_TCP:
                HandleTcp(frame, length, ipHeaderLen);
                break;
            case IP_PROTO_UDP:
                HandleUdp(frame, length, ipHeaderLen);
                break;
        }
    }

    // --- ICMP ---

    private void HandleIcmp(byte[] frame, int length, int ipHeaderLen)
    {
        int icmpOffset = 14 + ipHeaderLen;
        if (length < icmpOffset + 8) return;

        byte icmpType = frame[icmpOffset];
        if (icmpType != 8) return; // Only Echo Request

        // Copy frame as reply
        byte[] reply = new byte[length];
        System.Array.Copy(frame, reply, length);

        // Swap MAC addresses
        System.Array.Copy(frame, 6, reply, 0, 6);   // src -> dst
        System.Array.Copy(GatewayMac, 0, reply, 6, 6); // gateway -> src

        // Swap IP addresses
        System.Array.Copy(frame, 26, reply, 30, 4); // src IP -> dst IP
        System.Array.Copy(frame, 30, reply, 26, 4); // dst IP -> src IP

        // Set TTL = 64
        reply[22] = 64;

        // Recalculate IP header checksum
        reply[24] = 0;
        reply[25] = 0;
        ushort ipCsum = ComputeChecksum(reply, 14, ipHeaderLen);
        WriteBE16(reply, 24, ipCsum);

        // ICMP: set type = 0 (Echo Reply)
        reply[icmpOffset] = 0;

        // Recalculate ICMP checksum
        reply[icmpOffset + 2] = 0;
        reply[icmpOffset + 3] = 0;
        int icmpLen = length - icmpOffset;
        ushort icmpCsum = ComputeChecksum(reply, icmpOffset, icmpLen);
        WriteBE16(reply, icmpOffset + 2, icmpCsum);

        _rxQueue.Enqueue(reply);
    }

    // --- TCP ---

    private void HandleTcp(byte[] frame, int length, int ipHeaderLen)
    {
        int tcpOffset = 14 + ipHeaderLen;
        if (length < tcpOffset + 20) return;

        ushort srcPort = ReadBE16(frame, tcpOffset);
        ushort dstPort = ReadBE16(frame, tcpOffset + 2);
        uint seq = ReadBE32(frame, tcpOffset + 4);
        byte tcpDataOffset = (byte)((frame[tcpOffset + 12] >> 4) * 4);
        byte flags = frame[tcpOffset + 13];

        const byte FIN = 0x01, SYN = 0x02, RST = 0x04, PSH = 0x08, ACK = 0x10;

        // Non-echo port: send RST
        if (dstPort != 7)
        {
            if ((flags & RST) != 0) return; // Don't RST a RST

            int dataLen = length - tcpOffset - tcpDataOffset;
            uint ackSeq = seq + (uint)dataLen;
            if ((flags & SYN) != 0) ackSeq = seq + 1;
            if ((flags & FIN) != 0) ackSeq++;

            byte[] rst = BuildTcpPacket(dstPort, srcPort, 0, ackSeq, RST | ACK, null, 0, 0);
            _rxQueue.Enqueue(rst);
            return;
        }

        // Port 7 echo server
        if ((flags & RST) != 0)
        {
            _tcpConnections.Remove(srcPort);
            return;
        }

        if ((flags & SYN) != 0)
        {
            // SYN -> SYN-ACK
            var state = new TcpConnectionState
            {
                State = TcpState.SynReceived,
                OurSeq = 1000,
                TheirSeq = seq + 1
            };
            _tcpConnections[srcPort] = state;

            byte[] synAck = BuildTcpPacket(7, srcPort, state.OurSeq, state.TheirSeq, SYN | ACK, null, 0, 0);
            _rxQueue.Enqueue(synAck);
            return;
        }

        if (!_tcpConnections.TryGetValue(srcPort, out var conn))
            return;

        if ((flags & FIN) != 0)
        {
            // FIN -> FIN-ACK
            uint finAckSeq = conn.OurSeq + 1;
            byte[] finAck = BuildTcpPacket(7, srcPort, finAckSeq, seq + 1, FIN | ACK, null, 0, 0);
            _rxQueue.Enqueue(finAck);
            _tcpConnections.Remove(srcPort);
            return;
        }

        if ((flags & ACK) != 0)
        {
            if (conn.State == TcpState.SynReceived)
            {
                // Handshake complete
                conn.State = TcpState.Established;
                conn.OurSeq = 1001; // SYN consumed one sequence number
                _tcpConnections[srcPort] = conn;
            }

            // Check for data
            int dataLen = length - tcpOffset - tcpDataOffset;
            if (dataLen > 0)
            {
                // ACK the data
                conn.TheirSeq = seq + (uint)dataLen;
                conn.OurSeq = conn.OurSeq; // unchanged until we send data

                // Echo data back
                int dataStart = tcpOffset + tcpDataOffset;
                byte[] echoAck = BuildTcpPacket(7, srcPort, conn.OurSeq, conn.TheirSeq, PSH | ACK,
                    frame, dataStart, dataLen);
                conn.OurSeq += (uint)dataLen;
                _tcpConnections[srcPort] = conn;
                _rxQueue.Enqueue(echoAck);
            }
        }
    }

    private byte[] BuildTcpPacket(ushort srcPort, ushort dstPort, uint seq, uint ack, byte flags,
        byte[]? payload, int payloadOffset, int payloadLen)
    {
        int tcpHeaderLen = 20;
        int ipTotalLen = 20 + tcpHeaderLen + payloadLen;
        int frameLen = 14 + ipTotalLen;
        if (frameLen < MIN_ETHERNET_FRAME) frameLen = MIN_ETHERNET_FRAME;

        byte[] pkt = new byte[frameLen];

        // Ethernet header
        System.Array.Copy(_guestMac, 0, pkt, 0, 6);
        System.Array.Copy(GatewayMac, 0, pkt, 6, 6);
        WriteBE16(pkt, 12, ETHERTYPE_IPV4);

        // IP header
        pkt[14] = 0x45; // version=4, IHL=5
        WriteBE16(pkt, 16, (ushort)ipTotalLen);
        WriteBE16(pkt, 20, 0x4000); // Don't Fragment
        pkt[22] = 64; // TTL
        pkt[23] = IP_PROTO_TCP;
        System.Array.Copy(GatewayIp, 0, pkt, 26, 4);
        System.Array.Copy(_guestIp, 0, pkt, 30, 4);
        // IP checksum
        ushort ipCsum = ComputeChecksum(pkt, 14, 20);
        WriteBE16(pkt, 24, ipCsum);

        // TCP header
        int tcpOff = 34;
        WriteBE16(pkt, tcpOff, srcPort);
        WriteBE16(pkt, tcpOff + 2, dstPort);
        WriteBE32(pkt, tcpOff + 4, seq);
        WriteBE32(pkt, tcpOff + 8, ack);
        pkt[tcpOff + 12] = 0x50; // data offset = 5 (20 bytes)
        pkt[tcpOff + 13] = flags;
        WriteBE16(pkt, tcpOff + 14, 65535); // window size

        // Payload
        if (payload != null && payloadLen > 0)
            System.Array.Copy(payload, payloadOffset, pkt, tcpOff + tcpHeaderLen, payloadLen);

        // TCP checksum (with pseudo-header)
        int tcpSegLen = tcpHeaderLen + payloadLen;
        byte[] pseudo = new byte[12 + tcpSegLen];
        System.Array.Copy(GatewayIp, 0, pseudo, 0, 4);
        System.Array.Copy(_guestIp, 0, pseudo, 4, 4);
        pseudo[8] = 0;
        pseudo[9] = IP_PROTO_TCP;
        WriteBE16(pseudo, 10, (ushort)tcpSegLen);
        System.Array.Copy(pkt, tcpOff, pseudo, 12, tcpSegLen);
        ushort tcpCsum = ComputeChecksum(pseudo, 0, pseudo.Length);
        WriteBE16(pkt, tcpOff + 16, tcpCsum);

        return pkt;
    }

    // --- UDP ---

    private void HandleUdp(byte[] frame, int length, int ipHeaderLen)
    {
        int udpOffset = 14 + ipHeaderLen;
        if (length < udpOffset + 8) return;

        ushort srcPort = ReadBE16(frame, udpOffset);
        ushort dstPort = ReadBE16(frame, udpOffset + 2);
        ushort udpLen = ReadBE16(frame, udpOffset + 4);

        if (dstPort != 7) return; // Only echo port

        int payloadLen = udpLen - 8;
        if (payloadLen < 0 || length < udpOffset + udpLen) return;

        int ipTotalLen = 20 + 8 + payloadLen;
        int frameLen = 14 + ipTotalLen;
        if (frameLen < MIN_ETHERNET_FRAME) frameLen = MIN_ETHERNET_FRAME;

        byte[] reply = new byte[frameLen];

        // Ethernet header
        System.Array.Copy(_guestMac, 0, reply, 0, 6);
        System.Array.Copy(GatewayMac, 0, reply, 6, 6);
        WriteBE16(reply, 12, ETHERTYPE_IPV4);

        // IP header
        reply[14] = 0x45;
        WriteBE16(reply, 16, (ushort)ipTotalLen);
        WriteBE16(reply, 20, 0x4000); // DF
        reply[22] = 64; // TTL
        reply[23] = IP_PROTO_UDP;
        System.Array.Copy(GatewayIp, 0, reply, 26, 4);
        System.Array.Copy(_guestIp, 0, reply, 30, 4);
        ushort ipCsum = ComputeChecksum(reply, 14, 20);
        WriteBE16(reply, 24, ipCsum);

        // UDP header
        int rUdpOff = 34;
        WriteBE16(reply, rUdpOff, dstPort);      // swap: dst -> src
        WriteBE16(reply, rUdpOff + 2, srcPort);  // swap: src -> dst
        WriteBE16(reply, rUdpOff + 4, udpLen);

        // Payload
        if (payloadLen > 0)
            System.Array.Copy(frame, udpOffset + 8, reply, rUdpOff + 8, payloadLen);

        // UDP checksum (with pseudo-header)
        byte[] pseudo = new byte[12 + udpLen];
        System.Array.Copy(GatewayIp, 0, pseudo, 0, 4);
        System.Array.Copy(_guestIp, 0, pseudo, 4, 4);
        pseudo[8] = 0;
        pseudo[9] = IP_PROTO_UDP;
        WriteBE16(pseudo, 10, udpLen);
        System.Array.Copy(reply, rUdpOff, pseudo, 12, udpLen);
        ushort udpCsum = ComputeChecksum(pseudo, 0, pseudo.Length);
        if (udpCsum == 0) udpCsum = 0xFFFF; // UDP: 0 means no checksum
        WriteBE16(reply, rUdpOff + 6, udpCsum);

        _rxQueue.Enqueue(reply);
    }

    // --- Checksum ---

    private static ushort ComputeChecksum(byte[] data, int offset, int length)
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

    // --- Big-endian helpers ---

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

    private static ushort ReadBE16(byte[] buf, int off)
    {
        return (ushort)((buf[off] << 8) | buf[off + 1]);
    }

    private static uint ReadBE32(byte[] buf, int off)
    {
        return (uint)((buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3]);
    }
}
