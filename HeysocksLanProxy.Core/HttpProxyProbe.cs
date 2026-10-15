using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HeysocksLanProxy.Core;

public static class HttpProxyProbe
{
    private const string ProbeHost = "www.example.com";

    public static async Task<bool> IsWorkingAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        try
        {
            using var client = new TcpClient(address.AddressFamily);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                client.Client.DualMode = false;
            }

            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                $"CONNECT {ProbeHost}:443 HTTP/1.1\r\n" +
                $"Host: {ProbeHost}:443\r\n" +
                "Proxy-Connection: close\r\n\r\n");
            await stream.WriteAsync(request, token).ConfigureAwait(false);

            var buffer = new byte[512];
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            var firstLine = Encoding.ASCII.GetString(buffer, 0, count)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            if (!firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 &&
                   int.TryParse(parts[1], out var statusCode) &&
                   statusCode is >= 200 and < 300;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
