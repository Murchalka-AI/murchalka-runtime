using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Murchalka.Runtime.ModuleGateway.Security;

internal static class LinuxPeerProcessReader
{
    private const int SocketLevel = 1;
    private const int PeerCredentialsOption = 17;
    private const int CredentialsSize = 12;

    internal static int ReadProcessId(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        Span<byte> credentials = stackalloc byte[CredentialsSize];
        socket.GetRawSocketOption(SocketLevel, PeerCredentialsOption, credentials);
        return MemoryMarshal.Read<int>(credentials);
    }
}
