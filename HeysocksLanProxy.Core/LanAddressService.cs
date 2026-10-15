using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HeysocksLanProxy.Core;

public static class LanAddressService
{
    public static IReadOnlyList<IPAddress> GetPrivateIpv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                network.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address))
            .Distinct()
            .ToArray();
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
