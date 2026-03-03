namespace Em68030.IO;

/// <summary>
/// Interface for network handler backends used by the LANCE ethernet controller.
/// Implementations process outgoing packets and produce incoming packets for the guest.
/// </summary>
public interface INetworkHandler : IDisposable
{
    void ProcessPacket(byte[] frame, int length);
    bool HasPendingPacket();
    byte[] DequeuePacket();
    void SetGuestMac(byte[] mac);
    void Reset();
}
