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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// Information about a TAP-Windows adapter found on the system.
/// </summary>
public record TapAdapterInfo(string Guid, string Name, string Description);

/// <summary>
/// Bridge network handler using a TAP-Windows adapter.
/// Sends and receives raw Ethernet frames via the TAP device, which the
/// user bridges to a physical NIC in Windows Network Connections.
/// </summary>
public class TapNetworkHandler : INetworkHandler
{
    // TAP-Windows IOCTL to set media status (link up/down)
    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, 6, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    private const uint TAP_WIN_IOCTL_SET_MEDIA_STATUS = (0x22u << 16) | (6u << 2);

    // Network adapter class GUID in the registry
    private const string ADAPTER_CLASS_KEY =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string NETWORK_CONNECTIONS_KEY =
        @"SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}";

    // TAP-Windows component IDs
    private const string TAP_COMPONENT_ID = "tap0901";
    private const string TAP_COMPONENT_ID_ALT = @"root\tap0901";

    #region P/Invoke

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr CreateFileA(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        ref uint lpInBuffer, uint nInBufferSize,
        ref uint lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        IntPtr hFile, ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventA(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_SYSTEM = 0x04;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint ERROR_IO_PENDING = 997;
    private const uint WAIT_OBJECT_0 = 0;

    #endregion

    private IntPtr _tapHandle = INVALID_HANDLE_VALUE;
    private volatile bool _disposed;
    private Thread? _readThread;

    private readonly ConcurrentQueue<byte[]> _rxQueue = new();

    private IntPtr _readEvent;
    private IntPtr _writeEvent;

    private byte[] _guestMac = new byte[6];

    /// <summary>Diagnostic output callback (wired by MainViewModel).</summary>
    public Action<string>? DiagnosticOutput;

    public TapNetworkHandler(string adapterGuid)
    {
        if (string.IsNullOrEmpty(adapterGuid)) return;

        // Open the TAP device
        string devicePath = @"\\.\\Global\\" + adapterGuid + ".tap";
        _tapHandle = CreateFileA(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            0,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_SYSTEM | FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (_tapHandle == INVALID_HANDLE_VALUE)
        {
            DiagnosticOutput?.Invoke($"[TAP] Failed to open device: {devicePath} (error {Marshal.GetLastWin32Error()})\n");
            return;
        }

        // Create events for overlapped I/O
        _readEvent = CreateEventA(IntPtr.Zero, true, false, IntPtr.Zero);
        _writeEvent = CreateEventA(IntPtr.Zero, true, false, IntPtr.Zero);

        // Set link up
        SetMediaStatus(true);

        // Start background read thread
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "TAP-Read" };
        _readThread.Start();
    }

    public bool IsConnected => _tapHandle != INVALID_HANDLE_VALUE;

    private void SetMediaStatus(bool up)
    {
        if (_tapHandle == INVALID_HANDLE_VALUE) return;

        uint status = up ? 1u : 0u;
        uint outStatus = 0;
        DeviceIoControl(_tapHandle, TAP_WIN_IOCTL_SET_MEDIA_STATUS,
            ref status, 4, ref outStatus, 4, out _, IntPtr.Zero);
    }

    private void ReadLoop()
    {
        var buffer = new byte[2048];

        while (!_disposed)
        {
            var ov = new NativeOverlapped { EventHandle = _readEvent };
            ResetEvent(_readEvent);

            bool result = ReadFile(_tapHandle, buffer, (uint)buffer.Length, out uint bytesRead, ref ov);

            if (!result)
            {
                uint err = (uint)Marshal.GetLastWin32Error();
                if (err == ERROR_IO_PENDING)
                {
                    uint waitResult = WaitForSingleObject(_readEvent, 500);
                    if (_disposed) break;
                    if (waitResult != WAIT_OBJECT_0) continue;

                    if (!GetOverlappedResult(_tapHandle, ref ov, out bytesRead, false))
                        continue;
                }
                else
                {
                    if (_disposed) break;
                    Thread.Sleep(10);
                    continue;
                }
            }

            if (bytesRead > 0 && bytesRead >= 14) // minimum Ethernet frame
            {
                var packet = new byte[bytesRead];
                Array.Copy(buffer, packet, (int)bytesRead);
                _rxQueue.Enqueue(packet);
            }
        }
    }

    public void ProcessPacket(byte[] frame, int length)
    {
        if (_tapHandle == INVALID_HANDLE_VALUE || length <= 0) return;

        var ov = new NativeOverlapped { EventHandle = _writeEvent };
        ResetEvent(_writeEvent);

        bool result = WriteFile(_tapHandle, frame, (uint)length, out _, ref ov);
        if (!result && (uint)Marshal.GetLastWin32Error() == ERROR_IO_PENDING)
        {
            WaitForSingleObject(_writeEvent, 1000);
            GetOverlappedResult(_tapHandle, ref ov, out _, false);
        }
    }

    public bool HasPendingPacket() => !_rxQueue.IsEmpty;

    public byte[] DequeuePacket()
    {
        _rxQueue.TryDequeue(out var packet);
        return packet!;
    }

    public void SetGuestMac(byte[] mac)
    {
        _guestMac = new byte[6];
        Array.Copy(mac, _guestMac, Math.Min(mac.Length, 6));
    }

    public void Reset()
    {
        while (_rxQueue.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal the read event to unblock the read thread
        if (_readEvent != IntPtr.Zero)
            SetEvent(_readEvent);

        _readThread?.Join(2000);

        if (_tapHandle != INVALID_HANDLE_VALUE)
        {
            SetMediaStatus(false);
            CloseHandle(_tapHandle);
            _tapHandle = INVALID_HANDLE_VALUE;
        }
        if (_readEvent != IntPtr.Zero) { CloseHandle(_readEvent); _readEvent = IntPtr.Zero; }
        if (_writeEvent != IntPtr.Zero) { CloseHandle(_writeEvent); _writeEvent = IntPtr.Zero; }
    }

    // ========================================================================
    // Static: Enumerate TAP-Windows adapters
    // ========================================================================

    /// <summary>
    /// Enumerate all TAP-Windows adapters on the system.
    /// Returns an empty list if no TAP adapters are installed.
    /// </summary>
    public static List<TapAdapterInfo> EnumerateAdapters()
    {
        var result = new List<TapAdapterInfo>();

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(ADAPTER_CLASS_KEY);
            if (classKey == null) return result;

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                try
                {
                    using var adapterKey = classKey.OpenSubKey(subKeyName);
                    if (adapterKey == null) continue;

                    // Read ComponentId
                    string? componentId = adapterKey.GetValue("ComponentId") as string;
                    if (componentId == null) continue;

                    if (componentId != TAP_COMPONENT_ID && componentId != TAP_COMPONENT_ID_ALT)
                        continue;

                    // Read NetCfgInstanceId (GUID)
                    string? guid = adapterKey.GetValue("NetCfgInstanceId") as string;
                    if (string.IsNullOrEmpty(guid)) continue;

                    // Read DriverDesc
                    string description = adapterKey.GetValue("DriverDesc") as string ?? "";

                    // Read friendly name from Network Connections
                    string name = "";
                    string connPath = NETWORK_CONNECTIONS_KEY + @"\" + guid + @"\Connection";
                    try
                    {
                        using var connKey = Registry.LocalMachine.OpenSubKey(connPath);
                        if (connKey != null)
                            name = connKey.GetValue("Name") as string ?? "";
                    }
                    catch
                    {
                        // Connection key may not exist
                    }

                    result.Add(new TapAdapterInfo(guid, name, description));
                }
                catch
                {
                    // Skip adapters that can't be read
                }
            }
        }
        catch
        {
            // Registry access may fail
        }

        return result;
    }
}
