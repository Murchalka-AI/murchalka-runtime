using System.Net.Sockets;
using System.Text;

namespace Murchalka.Runtime.ModuleGateway.Security;

internal static class LinuxProcessIdentityVerifier
{
    private const int MaximumAncestryDepth = 64;

    internal static bool Matches(Socket socket, int sandboxProcessId, string claimedProcessIdentity)
    {
        if (!int.TryParse(
                claimedProcessIdentity,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var claimedProcessId) ||
            claimedProcessId <= 0 ||
            sandboxProcessId <= 0)
            return false;

        try
        {
            var peerProcessId = LinuxPeerProcessReader.ReadProcessId(socket);
            var status = File.ReadAllText($"/proc/{peerProcessId}/status", Encoding.UTF8);
            return ParseInnermostNamespaceProcessId(status) == claimedProcessId &&
                IsDescendantOrSame(peerProcessId, sandboxProcessId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SocketException)
        {
            return false;
        }
    }

    internal static int ParseInnermostNamespaceProcessId(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        var line = status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(value => value.StartsWith("NSpid:", StringComparison.Ordinal));
        if (line is null)
            throw new InvalidDataException("Linux process status does not contain NSpid.");
        var values = line["NSpid:".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0 ||
            !int.TryParse(values[^1], System.Globalization.CultureInfo.InvariantCulture, out var processId) ||
            processId <= 0)
            throw new InvalidDataException("Linux process status contains an invalid NSpid.");
        return processId;
    }

    private static bool IsDescendantOrSame(int processId, int expectedAncestor)
    {
        var current = processId;
        for (var depth = 0; depth < MaximumAncestryDepth && current > 0; depth++)
        {
            if (current == expectedAncestor) return true;
            current = ReadParentProcessId(current);
        }
        return false;
    }

    private static int ReadParentProcessId(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat", Encoding.UTF8);
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
            throw new InvalidDataException("Linux process stat is malformed.");
        var fields = stat[(commandEnd + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 ||
            !int.TryParse(fields[1], System.Globalization.CultureInfo.InvariantCulture, out var parentProcessId))
            throw new InvalidDataException("Linux process stat does not contain a valid parent process id.");
        return parentProcessId;
    }
}
