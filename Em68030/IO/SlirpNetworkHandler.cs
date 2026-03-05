namespace Em68030.IO;

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>
/// User-mode NAT network handler. Routes guest packets to the host network
/// without requiring administrator privileges or external drivers.
/// Supports ARP (proxy), ICMP (ping), UDP (including DNS), and TCP.
/// </summary>
public class SlirpNetworkHandler : INetworkHandler
{
    private readonly byte[] GatewayMac;
    private readonly byte[] GatewayIp;

    public SlirpNetworkHandler()
        : this(new byte[] { 10, 0, 2, 2 }, new byte[] { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 })
    {
    }

    public SlirpNetworkHandler(byte[] gatewayIp, byte[] gatewayMac)
    {
        GatewayIp = gatewayIp;
        GatewayMac = gatewayMac;
    }

    public static byte[] ParseIpAddress(string s)
    {
        var def = new byte[] { 10, 0, 2, 2 };
        var parts = s.Split('.');
        if (parts.Length != 4) return def;
        var result = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out result[i])) return def;
        }
        return result;
    }

    public static byte[] ParseMacAddress(string s)
    {
        var def = new byte[] { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
        var parts = s.Split(':');
        if (parts.Length != 6) return def;
        var result = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out result[i]))
                return def;
        }
        return result;
    }

    private byte[] _guestMac = new byte[6];
    private readonly byte[] _guestIp = new byte[4];
    private bool _guestIpKnown;
    private readonly ConcurrentQueue<byte[]> _rxQueue = new();
    private bool _disposed;

    // UDP session management
    private readonly Dictionary<ushort, UdpSession> _udpSessions = new();
    private readonly object _udpLock = new();

    // TCP connection management
    private readonly Dictionary<(ushort srcPort, ushort dstPort, uint dstIp), TcpSession> _tcpSessions = new();
    private readonly object _tcpLock = new();

    // Diagnostic output callback (wired by MainViewModel)
    public Action<string>? DiagnosticOutput;

    private class UdpSession
    {
        public UdpClient Client = null!;
        public DateTime LastActivity;
        public ushort GuestPort;
        public byte[] DestIp = new byte[4];
    }

    private class TcpSession
    {
        public TcpClient Client = null!;
        public NetworkStream? Stream;
        public TcpState State;
        public uint OurSeq;
        public uint TheirSeq;
        public ushort GuestSrcPort;
        public ushort GuestDstPort;
        public byte[] DestIp = new byte[4];
        public DateTime LastActivity;
        public CancellationTokenSource? Cts;
        public bool ReceiveLoopRunning;
    }

    private enum TcpState { Connecting, SynAckSent, Established, FinWait, Closed }

    public void SetGuestMac(byte[] mac)
    {
        _guestMac = new byte[6];
        Array.Copy(mac, _guestMac, 6);
    }

    public void ProcessPacket(byte[] frame, int length)
    {
        if (length < 14) return;

        ushort etherType = NetworkUtils.ReadBE16(frame, 12);
        DiagnosticOutput?.Invoke($"[NAT] TX len={length} etype=0x{etherType:X4}\n");
        switch (etherType)
        {
            case NetworkUtils.ETHERTYPE_ARP:
                HandleArp(frame, length);
                break;
            case NetworkUtils.ETHERTYPE_IPV4:
                HandleIpv4(frame, length);
                break;
        }
    }

    public bool HasPendingPacket() => !_rxQueue.IsEmpty;

    public byte[] DequeuePacket()
    {
        _rxQueue.TryDequeue(out var packet);
        DiagnosticOutput?.Invoke($"[NAT] RX dequeue len={packet?.Length ?? 0} remaining={_rxQueue.Count}\n");
        return packet!;
    }

    public void Reset()
    {
        while (_rxQueue.TryDequeue(out _)) { }
        CleanupUdpSessions(force: true);
        CleanupTcpSessions(force: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupUdpSessions(force: true);
        CleanupTcpSessions(force: true);
    }

    // =======================================================================
    // ARP — Proxy ARP: respond to any ARP request with our gateway MAC
    // =======================================================================

    private void HandleArp(byte[] frame, int length)
    {
        if (length < 42) return;

        ushort op = NetworkUtils.ReadBE16(frame, 20);
        if (op != 1) return; // Only handle ARP Request

        // Ignore DAD probes (SPA = 0.0.0.0)
        if (frame[28] == 0 && frame[29] == 0 && frame[30] == 0 && frame[31] == 0)
            return;

        // Learn guest IP from sender protocol address
        Array.Copy(frame, 28, _guestIp, 0, 4);
        _guestIpKnown = true;

        // Don't respond if TPA = guest IP (would cause DAD conflict)
        byte[] targetIp = new byte[4];
        Array.Copy(frame, 38, targetIp, 0, 4);
        if (targetIp.AsSpan().SequenceEqual(_guestIp))
            return;

        // Respond with gateway MAC for the requested target IP (proxy ARP)
        byte[] reply = NetworkUtils.BuildArpReply(_guestMac, GatewayMac, targetIp, _guestIp);
        _rxQueue.Enqueue(reply);
    }

    // =======================================================================
    // IPv4 dispatcher
    // =======================================================================

    private void HandleIpv4(byte[] frame, int length)
    {
        if (length < 34) return;

        int ipHeaderLen = (frame[14] & 0x0F) * 4;
        if (length < 14 + ipHeaderLen) return;

        // Learn guest IP from source IP
        if (!_guestIpKnown)
        {
            Array.Copy(frame, 26, _guestIp, 0, 4);
            _guestIpKnown = true;
        }

        byte protocol = frame[23];
        switch (protocol)
        {
            case NetworkUtils.IP_PROTO_ICMP:
                HandleIcmp(frame, length, ipHeaderLen);
                break;
            case NetworkUtils.IP_PROTO_UDP:
                HandleUdp(frame, length, ipHeaderLen);
                break;
            case NetworkUtils.IP_PROTO_TCP:
                HandleTcp(frame, length, ipHeaderLen);
                break;
        }
    }

    // =======================================================================
    // ICMP — Forward ping to host network via System.Net.NetworkInformation.Ping
    // =======================================================================

    private void HandleIcmp(byte[] frame, int length, int ipHeaderLen)
    {
        int icmpOffset = 14 + ipHeaderLen;
        if (length < icmpOffset + 8) return;

        byte icmpType = frame[icmpOffset];
        if (icmpType != 8) return; // Only Echo Request

        // Extract destination IP
        byte[] destIp = new byte[4];
        Array.Copy(frame, 30, destIp, 0, 4);
        var destAddr = new IPAddress(destIp);

        // Extract ICMP ID, sequence, and payload
        ushort icmpId = NetworkUtils.ReadBE16(frame, icmpOffset + 4);
        ushort icmpSeq = NetworkUtils.ReadBE16(frame, icmpOffset + 6);
        DiagnosticOutput?.Invoke($"[NAT] ICMP Echo Request to {destAddr} id={icmpId} seq={icmpSeq}\n");
        int payloadLen = length - icmpOffset - 8;
        byte[] payload = new byte[payloadLen > 0 ? payloadLen : 0];
        if (payloadLen > 0)
            Array.Copy(frame, icmpOffset + 8, payload, 0, payloadLen);

        // Gateway ping: respond locally without forwarding to host
        if (destIp[0] == GatewayIp[0] && destIp[1] == GatewayIp[1] &&
            destIp[2] == GatewayIp[2] && destIp[3] == GatewayIp[3])
        {
            BuildIcmpReply(destIp, icmpId, icmpSeq, payload);
            return;
        }

        // Send ping asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(destAddr, 5000, payload);

                if (reply.Status == IPStatus.Success)
                {
                    DiagnosticOutput?.Invoke($"[NAT] ICMP Echo Reply from {destAddr} id={icmpId} seq={icmpSeq} rtt={reply.RoundtripTime}ms\n");
                    BuildIcmpReply(destIp, icmpId, icmpSeq, reply.Buffer);
                }
                else
                {
                    DiagnosticOutput?.Invoke($"[NAT] ICMP ping failed: {reply.Status} for {destAddr}\n");
                    // Send ICMP Destination Unreachable for non-success
                    BuildIcmpUnreachable(destIp, frame, ipHeaderLen);
                }
            }
            catch (Exception ex)
            {
                DiagnosticOutput?.Invoke($"[NAT] ICMP ping exception: {ex.Message}\n");
                BuildIcmpUnreachable(destIp, frame, ipHeaderLen);
            }
        });
    }

    private void BuildIcmpReply(byte[] fromIp, ushort id, ushort seq, byte[] payload)
    {
        int icmpLen = 8 + payload.Length;
        byte[] pkt = NetworkUtils.BuildIpPacket(_guestMac, GatewayMac, fromIp, _guestIp,
            NetworkUtils.IP_PROTO_ICMP, icmpLen);

        int icmpOff = 34;
        pkt[icmpOff] = 0; // Echo Reply
        pkt[icmpOff + 1] = 0;
        NetworkUtils.WriteBE16(pkt, icmpOff + 4, id);
        NetworkUtils.WriteBE16(pkt, icmpOff + 6, seq);
        if (payload.Length > 0)
            Array.Copy(payload, 0, pkt, icmpOff + 8, payload.Length);

        // ICMP checksum
        ushort csum = NetworkUtils.ComputeChecksum(pkt, icmpOff, icmpLen);
        NetworkUtils.WriteBE16(pkt, icmpOff + 2, csum);

        _rxQueue.Enqueue(pkt);
    }

    private void BuildIcmpUnreachable(byte[] fromIp, byte[] originalFrame, int ipHeaderLen)
    {
        // ICMP Destination Unreachable: type=3, code=1 (host unreachable)
        // Payload: original IP header + 8 bytes of original data
        int origDataLen = Math.Min(8, originalFrame.Length - 14 - ipHeaderLen);
        int icmpPayloadLen = ipHeaderLen + origDataLen;
        int icmpLen = 8 + icmpPayloadLen;
        byte[] pkt = NetworkUtils.BuildIpPacket(_guestMac, GatewayMac, fromIp, _guestIp,
            NetworkUtils.IP_PROTO_ICMP, icmpLen);

        int icmpOff = 34;
        pkt[icmpOff] = 3;     // Destination Unreachable
        pkt[icmpOff + 1] = 1; // Host Unreachable
        // bytes 4-7: unused (zeros)
        Array.Copy(originalFrame, 14, pkt, icmpOff + 8, icmpPayloadLen);

        ushort csum = NetworkUtils.ComputeChecksum(pkt, icmpOff, icmpLen);
        NetworkUtils.WriteBE16(pkt, icmpOff + 2, csum);

        _rxQueue.Enqueue(pkt);
    }

    // =======================================================================
    // UDP — Forward via UdpClient
    // =======================================================================

    private void HandleUdp(byte[] frame, int length, int ipHeaderLen)
    {
        int udpOffset = 14 + ipHeaderLen;
        if (length < udpOffset + 8) return;

        ushort srcPort = NetworkUtils.ReadBE16(frame, udpOffset);
        ushort dstPort = NetworkUtils.ReadBE16(frame, udpOffset + 2);
        ushort udpLen = NetworkUtils.ReadBE16(frame, udpOffset + 4);

        int payloadLen = udpLen - 8;
        if (payloadLen < 0 || length < udpOffset + udpLen) return;

        byte[] destIp = new byte[4];
        Array.Copy(frame, 30, destIp, 0, 4);
        var destAddr = new IPAddress(destIp);

        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0)
            Array.Copy(frame, udpOffset + 8, payload, 0, payloadLen);

        // Get or create UDP session for this source port
        UdpSession session;
        lock (_udpLock)
        {
            if (!_udpSessions.TryGetValue(srcPort, out session!))
            {
                try
                {
                    var client = new UdpClient();
                    client.Client.ReceiveTimeout = 5000;
                    session = new UdpSession
                    {
                        Client = client,
                        LastActivity = DateTime.UtcNow,
                        GuestPort = srcPort,
                        DestIp = (byte[])destIp.Clone()
                    };
                    _udpSessions[srcPort] = session;
                }
                catch
                {
                    return;
                }
            }
            session.LastActivity = DateTime.UtcNow;
            session.DestIp = (byte[])destIp.Clone();
        }

        // Send and receive asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.SendAsync(payload, payload.Length, new IPEndPoint(destAddr, dstPort));

                // Wait for response with timeout
                using var cts = new CancellationTokenSource(5000);
                var result = await session.Client.ReceiveAsync(cts.Token);

                BuildUdpReply(session.DestIp, dstPort, srcPort, result.Buffer);
            }
            catch
            {
                // Timeout or error — no reply to guest
            }
            finally
            {
                CleanupUdpSessions(force: false);
            }
        });
    }

    private void BuildUdpReply(byte[] fromIp, ushort srcPort, ushort dstPort, byte[] payload)
    {
        int udpLen = 8 + payload.Length;
        byte[] pkt = NetworkUtils.BuildIpPacket(_guestMac, GatewayMac, fromIp, _guestIp,
            NetworkUtils.IP_PROTO_UDP, udpLen);

        int udpOff = 34;
        NetworkUtils.WriteBE16(pkt, udpOff, srcPort);
        NetworkUtils.WriteBE16(pkt, udpOff + 2, dstPort);
        NetworkUtils.WriteBE16(pkt, udpOff + 4, (ushort)udpLen);
        // checksum field = 0 initially
        if (payload.Length > 0)
            Array.Copy(payload, 0, pkt, udpOff + 8, payload.Length);

        // UDP checksum
        ushort csum = NetworkUtils.ComputeTransportChecksum(fromIp, _guestIp,
            NetworkUtils.IP_PROTO_UDP, pkt, udpOff, udpLen);
        if (csum == 0) csum = 0xFFFF;
        NetworkUtils.WriteBE16(pkt, udpOff + 6, csum);

        _rxQueue.Enqueue(pkt);
    }

    private void CleanupUdpSessions(bool force)
    {
        lock (_udpLock)
        {
            var expired = new List<ushort>();
            foreach (var kv in _udpSessions)
            {
                if (force || (DateTime.UtcNow - kv.Value.LastActivity).TotalSeconds > 30)
                    expired.Add(kv.Key);
            }
            foreach (var port in expired)
            {
                try { _udpSessions[port].Client.Dispose(); } catch { }
                _udpSessions.Remove(port);
            }
        }
    }

    // =======================================================================
    // TCP — Forward via TcpClient with state machine
    // =======================================================================

    private void HandleTcp(byte[] frame, int length, int ipHeaderLen)
    {
        int tcpOffset = 14 + ipHeaderLen;
        if (length < tcpOffset + 20) return;

        ushort srcPort = NetworkUtils.ReadBE16(frame, tcpOffset);
        ushort dstPort = NetworkUtils.ReadBE16(frame, tcpOffset + 2);
        uint seq = NetworkUtils.ReadBE32(frame, tcpOffset + 4);
        uint ack = NetworkUtils.ReadBE32(frame, tcpOffset + 8);
        byte tcpDataOffset = (byte)((frame[tcpOffset + 12] >> 4) * 4);
        byte flags = frame[tcpOffset + 13];

        const byte FIN = 0x01, SYN = 0x02, RST = 0x04, ACK = 0x10;

        byte[] destIp = new byte[4];
        Array.Copy(frame, 30, destIp, 0, 4);
        uint destIpU = NetworkUtils.ReadBE32(frame, 30);
        var key = (srcPort, dstPort, destIpU);

        // RST from guest: tear down session
        if ((flags & RST) != 0)
        {
            lock (_tcpLock)
            {
                if (_tcpSessions.TryGetValue(key, out var s))
                {
                    CloseTcpSession(s);
                    _tcpSessions.Remove(key);
                }
            }
            return;
        }

        // SYN: start new connection
        if ((flags & SYN) != 0 && (flags & ACK) == 0)
        {
            lock (_tcpLock)
            {
                // Clean up any existing session
                if (_tcpSessions.TryGetValue(key, out var old))
                {
                    CloseTcpSession(old);
                    _tcpSessions.Remove(key);
                }
            }

            var session = new TcpSession
            {
                State = TcpState.Connecting,
                OurSeq = (uint)(Environment.TickCount & 0x7FFFFFFF),
                TheirSeq = seq + 1,
                GuestSrcPort = srcPort,
                GuestDstPort = dstPort,
                DestIp = (byte[])destIp.Clone(),
                LastActivity = DateTime.UtcNow,
                Cts = new CancellationTokenSource()
            };

            lock (_tcpLock)
            {
                _tcpSessions[key] = session;
            }

            // Connect asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    session.Client = new TcpClient();
                    var destAddr = new IPAddress(destIp);
                    using var connectCts = new CancellationTokenSource(10000);
                    await session.Client.ConnectAsync(destAddr, dstPort, connectCts.Token);
                    session.Stream = session.Client.GetStream();
                    session.State = TcpState.SynAckSent;

                    // Send SYN+ACK to guest
                    byte[] synAck = BuildTcpPacket(session.DestIp, dstPort, srcPort,
                        session.OurSeq, session.TheirSeq, SYN | ACK, null, 0, 0);
                    _rxQueue.Enqueue(synAck);
                    session.OurSeq++; // SYN consumes 1 seq

                    // Start receive loop
                    StartTcpReceiveLoop(session, key);
                }
                catch
                {
                    // Connection failed — send RST
                    byte[] rst = BuildTcpPacket(destIp, dstPort, srcPort,
                        0, seq + 1, RST | ACK, null, 0, 0);
                    _rxQueue.Enqueue(rst);

                    lock (_tcpLock)
                    {
                        _tcpSessions.Remove(key);
                    }
                    CloseTcpSession(session);
                }
            });
            return;
        }

        // Subsequent packets on existing connection
        TcpSession? sess;
        lock (_tcpLock)
        {
            if (!_tcpSessions.TryGetValue(key, out sess))
                return;
        }

        sess.LastActivity = DateTime.UtcNow;

        // ACK of our SYN+ACK → Established
        if (sess.State == TcpState.SynAckSent && (flags & ACK) != 0)
        {
            sess.State = TcpState.Established;
        }

        // FIN from guest
        if ((flags & FIN) != 0)
        {
            sess.TheirSeq = seq + 1;
            byte[] finAck = BuildTcpPacket(sess.DestIp, dstPort, srcPort,
                sess.OurSeq, sess.TheirSeq, FIN | ACK, null, 0, 0);
            _rxQueue.Enqueue(finAck);
            sess.OurSeq++;

            lock (_tcpLock)
            {
                _tcpSessions.Remove(key);
            }
            CloseTcpSession(sess);
            return;
        }

        // Data from guest
        if ((flags & ACK) != 0 && sess.State == TcpState.Established)
        {
            int dataLen = length - tcpOffset - tcpDataOffset;
            if (dataLen > 0 && sess.Stream != null)
            {
                sess.TheirSeq = seq + (uint)dataLen;

                // Forward data to host
                byte[] data = new byte[dataLen];
                Array.Copy(frame, tcpOffset + tcpDataOffset, data, 0, dataLen);

                try
                {
                    sess.Stream.Write(data, 0, dataLen);
                }
                catch
                {
                    // Connection broken — send RST
                    byte[] rst = BuildTcpPacket(sess.DestIp, dstPort, srcPort,
                        sess.OurSeq, sess.TheirSeq, RST | ACK, null, 0, 0);
                    _rxQueue.Enqueue(rst);
                    lock (_tcpLock) { _tcpSessions.Remove(key); }
                    CloseTcpSession(sess);
                    return;
                }

                // ACK the data
                byte[] ackPkt = BuildTcpPacket(sess.DestIp, dstPort, srcPort,
                    sess.OurSeq, sess.TheirSeq, ACK, null, 0, 0);
                _rxQueue.Enqueue(ackPkt);
            }
        }
    }

    private void StartTcpReceiveLoop(TcpSession session, (ushort, ushort, uint) key)
    {
        if (session.ReceiveLoopRunning) return;
        session.ReceiveLoopRunning = true;

        _ = Task.Run(async () =>
        {
            byte[] buffer = new byte[1460]; // MSS
            try
            {
                while (session.Stream != null && session.State == TcpState.Established || session.State == TcpState.SynAckSent)
                {
                    if (session.Cts?.IsCancellationRequested == true) break;

                    int bytesRead;
                    try
                    {
                        bytesRead = await session.Stream!.ReadAsync(buffer, 0, buffer.Length,
                            session.Cts?.Token ?? CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // Host closed connection — send FIN to guest
                        const byte FIN = 0x01, ACK = 0x10;
                        byte[] fin = BuildTcpPacket(session.DestIp, session.GuestDstPort, session.GuestSrcPort,
                            session.OurSeq, session.TheirSeq, FIN | ACK, null, 0, 0);
                        _rxQueue.Enqueue(fin);
                        session.OurSeq++;
                        session.State = TcpState.FinWait;
                        break;
                    }

                    session.LastActivity = DateTime.UtcNow;

                    // Send data to guest as TCP segment(s)
                    const byte PSH = 0x08;
                    byte[] dataPkt = BuildTcpPacket(session.DestIp, session.GuestDstPort, session.GuestSrcPort,
                        session.OurSeq, session.TheirSeq, PSH | 0x10, buffer, 0, bytesRead);
                    session.OurSeq += (uint)bytesRead;
                    _rxQueue.Enqueue(dataPkt);
                }
            }
            finally
            {
                session.ReceiveLoopRunning = false;
            }
        });
    }

    private byte[] BuildTcpPacket(byte[] destIp, ushort srcPort, ushort dstPort,
        uint seq, uint ack, byte flags, byte[]? payload, int payloadOffset, int payloadLen)
    {
        int tcpHeaderLen = 20;
        int transportLen = tcpHeaderLen + payloadLen;
        byte[] pkt = NetworkUtils.BuildIpPacket(_guestMac, GatewayMac, destIp, _guestIp,
            NetworkUtils.IP_PROTO_TCP, transportLen);

        // Fix IP total length (BuildIpPacket may have padded)
        int ipTotalLen = 20 + transportLen;
        NetworkUtils.WriteBE16(pkt, 16, (ushort)ipTotalLen);
        // Recalculate IP checksum
        pkt[24] = 0; pkt[25] = 0;
        ushort ipCsum = NetworkUtils.ComputeChecksum(pkt, 14, 20);
        NetworkUtils.WriteBE16(pkt, 24, ipCsum);

        int tcpOff = 34;
        NetworkUtils.WriteBE16(pkt, tcpOff, srcPort);
        NetworkUtils.WriteBE16(pkt, tcpOff + 2, dstPort);
        NetworkUtils.WriteBE32(pkt, tcpOff + 4, seq);
        NetworkUtils.WriteBE32(pkt, tcpOff + 8, ack);
        pkt[tcpOff + 12] = 0x50; // data offset = 5 (20 bytes)
        pkt[tcpOff + 13] = flags;
        NetworkUtils.WriteBE16(pkt, tcpOff + 14, 65535); // window size

        if (payload != null && payloadLen > 0)
            Array.Copy(payload, payloadOffset, pkt, tcpOff + tcpHeaderLen, payloadLen);

        // TCP checksum
        ushort tcpCsum = NetworkUtils.ComputeTransportChecksum(destIp, _guestIp,
            NetworkUtils.IP_PROTO_TCP, pkt, tcpOff, transportLen);
        NetworkUtils.WriteBE16(pkt, tcpOff + 16, tcpCsum);

        return pkt;
    }

    private void CloseTcpSession(TcpSession session)
    {
        session.State = TcpState.Closed;
        try { session.Cts?.Cancel(); } catch { }
        try { session.Stream?.Dispose(); } catch { }
        try { session.Client?.Dispose(); } catch { }
        try { session.Cts?.Dispose(); } catch { }
    }

    private void CleanupTcpSessions(bool force)
    {
        lock (_tcpLock)
        {
            var expired = new List<(ushort, ushort, uint)>();
            foreach (var kv in _tcpSessions)
            {
                if (force || (DateTime.UtcNow - kv.Value.LastActivity).TotalSeconds > 120)
                    expired.Add(kv.Key);
            }
            foreach (var key in expired)
            {
                CloseTcpSession(_tcpSessions[key]);
                _tcpSessions.Remove(key);
            }
        }
    }
}
