using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HeysocksLanProxy.Core;

public sealed class HttpForwardProxyServer : IAsyncDisposable
{
    private const int MaximumHeaderSize = 64 * 1024;
    private readonly Func<bool> _trafficAllowed;
    private readonly bool _allowLocalTargets;
    private readonly Func<IPAddress, (bool Safe, string? InterfaceName)>? _routeValidator;
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;
    private long _clientSequence;

    public HttpForwardProxyServer(
        Func<bool> trafficAllowed,
        bool allowLocalTargets = false,
        Func<IPAddress, (bool Safe, string? InterfaceName)>? routeValidator = null)
    {
        _trafficAllowed = trafficAllowed;
        _allowLocalTargets = allowLocalTargets;
        _routeValidator = routeValidator;
    }

    public event EventHandler<string>? Log;

    public int? ListeningPort { get; private set; }

    public void Start(int port)
    {
        if (_listener is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start(128);
        ListeningPort = port;
        _acceptTask = AcceptLoopAsync(_cancellation.Token);
        WriteLog($"HTTP/HTTPS 代理已监听 0.0.0.0:{port}");
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        _cancellation!.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        try
        {
            if (_acceptTask is not null)
            {
                await _acceptTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            _listener = null;
            _acceptTask = null;
            ListeningPort = null;
            _cancellation.Dispose();
            _cancellation = null;
            WriteLog("HTTP/HTTPS 代理已停止");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public void DisconnectAllClients(string reason)
    {
        var disconnected = 0;
        foreach (var client in _clients.Values)
        {
            client.Dispose();
            disconnected++;
        }

        if (disconnected > 0)
        {
            WriteLog($"安全出口失效，已断开 {disconnected} 个代理连接：{reason}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var id = Interlocked.Increment(ref _clientSequence);
            _clients[id] = client;
            _ = HandleClientAsync(id, client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(long id, TcpClient sourceClient, CancellationToken cancellationToken)
    {
        var responseStarted = false;
        var clientEndpoint = sourceClient.Client.RemoteEndPoint?.ToString() ?? "未知客户端";
        string? requestKind = null;
        string? requestTarget = null;
        try
        {
            var sourceStream = sourceClient.GetStream();
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var request = await ReadInitialRequestAsync(sourceStream, headerTimeout.Token).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            if (!_trafficAllowed())
            {
                await WriteErrorAsync(sourceStream, 503, "Heysocks tunnel is not active", cancellationToken).ConfigureAwait(false);
                WriteLog("已拒绝连接：Heysocks VirtualNet 不是当前出口");
                return;
            }

            var requestText = Encoding.ASCII.GetString(request.Header);
            var lines = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLineParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLineParts.Length != 3)
            {
                await WriteErrorAsync(sourceStream, 400, "Invalid proxy request", cancellationToken).ConfigureAwait(false);
                return;
            }

            var method = requestLineParts[0];
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                var (host, port) = ParseAuthority(requestLineParts[1], 443);
                requestKind = "HTTPS CONNECT";
                requestTarget = $"{host}:{port}";
                WriteLog($"客户端 {clientEndpoint} -> {requestKind} {requestTarget}");
                EnsureTargetAllowed(host, port);
                using var targetClient = await ConnectTargetAsync(host, port, cancellationToken).ConfigureAwait(false);
                await using var targetStream = targetClient.GetStream();
                if (!_trafficAllowed())
                {
                    throw new ProxyRequestException(503, "Heysocks tunnel became unavailable");
                }
                var connected = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: HeysocksLanProxy/1.1\r\n\r\n");
                await sourceStream.WriteAsync(connected, cancellationToken).ConfigureAwait(false);
                responseStarted = true;
                if (request.Extra.Length > 0)
                {
                    await targetStream.WriteAsync(request.Extra, cancellationToken).ConfigureAwait(false);
                }

                await RelayBothWaysAsync(sourceStream, targetStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            var targetInfo = ParseHttpTarget(requestLineParts[1], lines);
            requestKind = "HTTP";
            requestTarget = $"{targetInfo.Host}:{targetInfo.Port}";
            WriteLog($"客户端 {clientEndpoint} -> {requestKind} {requestTarget}");
            EnsureTargetAllowed(targetInfo.Host, targetInfo.Port);
            using (var targetClient = await ConnectTargetAsync(targetInfo.Host, targetInfo.Port, cancellationToken).ConfigureAwait(false))
            await using (var targetStream = targetClient.GetStream())
            {
                if (!_trafficAllowed())
                {
                    throw new ProxyRequestException(503, "Heysocks tunnel became unavailable");
                }
                var rewrittenHeader = RewriteHttpHeader(method, targetInfo.PathAndQuery, requestLineParts[2], lines);
                await targetStream.WriteAsync(rewrittenHeader, cancellationToken).ConfigureAwait(false);
                if (request.Extra.Length > 0)
                {
                    await targetStream.WriteAsync(request.Extra, cancellationToken).ConfigureAwait(false);
                }

                responseStarted = true;
                await RelayBothWaysAsync(sourceStream, targetStream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            if (!responseStarted)
            {
                try
                {
                    await WriteErrorAsync(sourceClient.GetStream(), 408, "Proxy request timed out", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            WriteLog($"连接超时：客户端 {clientEndpoint}，{requestKind ?? "未知类型"} {requestTarget ?? "未知目标"}");
        }
        catch (ProxyRequestException exception)
        {
            if (!responseStarted)
            {
                try
                {
                    await WriteErrorAsync(sourceClient.GetStream(), exception.StatusCode, exception.Message, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            WriteLog($"已拒绝请求：客户端 {clientEndpoint}，{requestKind ?? "未知类型"} {requestTarget ?? "未知目标"}，原因：{exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            if (!responseStarted)
            {
                try
                {
                    await WriteErrorAsync(sourceClient.GetStream(), 502, "Unable to reach target through Heysocks", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            WriteLog($"连接失败：客户端 {clientEndpoint}，{requestKind ?? "未知类型"} {requestTarget ?? "未知目标"}，原因：{exception.Message}");
        }
        finally
        {
            sourceClient.Dispose();
            _clients.TryRemove(id, out _);
        }
    }

    private async Task<TcpClient> ConnectTargetAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
        {
            var route = _routeValidator?.Invoke(address) ?? (true, null);
            if (!route.Safe)
            {
                WriteLog($"阻止不安全出站路由：{host}:{port} ({address}) 将使用 {route.InterfaceName ?? "未知接口"}");
                lastError = new ProxyRequestException(503, $"Target route does not use Heysocks VirtualNet ({address})");
                continue;
            }

            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                var local = client.Client.LocalEndPoint as IPEndPoint;
                var localInterface = local is null ? null : FindInterfaceName(local.Address);
                WriteLog($"出站连接成功：{host}:{port} ({address})，本地端点 {local}，实际接口 {localInterface ?? route.InterfaceName ?? "未知"}");
                return client;
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
            {
                client.Dispose();
                lastError = exception;
                WriteLog($"出站连接失败：{host}:{port} ({address})，接口 {route.InterfaceName ?? "未知"}，原因：{exception.Message}");
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static string? FindInterfaceName(IPAddress localAddress) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
            network.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(localAddress)))?.Name;

    private void EnsureTargetAllowed(string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ProxyRequestException(400, "Invalid target port");
        }

        if (_allowLocalTargets)
        {
            return;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProxyRequestException(403, "Localhost targets are blocked");
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw new ProxyRequestException(403, "Localhost targets are blocked");
            }

            if (port == ListeningPort && LanAddressService.GetPrivateIpv4Addresses().Contains(address))
            {
                throw new ProxyRequestException(403, "Recursive proxy targets are blocked");
            }
        }
    }

    private static async Task RelayBothWaysAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        var upload = source.CopyToAsync(target, cancellationToken);
        var download = target.CopyToAsync(source, cancellationToken);
        await Task.WhenAny(upload, download).ConfigureAwait(false);
    }

    private static async Task<InitialRequest?> ReadInitialRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= MaximumHeaderSize)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, count);
            var data = buffer.GetBuffer();
            var headerEnd = FindHeaderEnd(data, (int)buffer.Length);
            if (headerEnd >= 0)
            {
                var headerLength = headerEnd + 4;
                var header = data[..headerLength].ToArray();
                var extra = data[headerLength..(int)buffer.Length].ToArray();
                return new InitialRequest(header, extra);
            }
        }

        throw new ProxyRequestException(431, "Proxy request headers are too large");
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var index = 0; index <= length - 4; index++)
        {
            if (data[index] == 13 && data[index + 1] == 10 && data[index + 2] == 13 && data[index + 3] == 10)
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        if (authority.StartsWith('['))
        {
            var closingBracket = authority.IndexOf(']');
            if (closingBracket < 0)
            {
                throw new ProxyRequestException(400, "Invalid IPv6 target");
            }

            var host = authority[1..closingBracket];
            var port = closingBracket + 1 < authority.Length && authority[closingBracket + 1] == ':'
                ? ParsePort(authority[(closingBracket + 2)..])
                : defaultPort;
            return (host, port);
        }

        var lastColon = authority.LastIndexOf(':');
        if (lastColon > 0 && authority.IndexOf(':') == lastColon)
        {
            return (authority[..lastColon], ParsePort(authority[(lastColon + 1)..]));
        }

        return (authority, defaultPort);
    }

    private static HttpTarget ParseHttpTarget(string requestTarget, string[] lines)
    {
        if (Uri.TryCreate(requestTarget, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            var absolutePort = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            return new HttpTarget(uri.Host, absolutePort, string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery);
        }

        var hostHeader = lines.FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        if (hostHeader is null)
        {
            throw new ProxyRequestException(400, "Missing Host header");
        }

        var authority = hostHeader[5..].Trim();
        var (host, port) = ParseAuthority(authority, 80);
        return new HttpTarget(host, port, requestTarget.StartsWith('/') ? requestTarget : "/" + requestTarget);
    }

    private static byte[] RewriteHttpHeader(string method, string pathAndQuery, string version, string[] lines)
    {
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(pathAndQuery).Append(' ').Append(version).Append("\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrEmpty(line) ||
                line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(line).Append("\r\n");
        }
        builder.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            throw new ProxyRequestException(400, "Invalid target port");
        }

        return port;
    }

    private static async Task WriteErrorAsync(Stream stream, int statusCode, string message, CancellationToken cancellationToken)
    {
        var reason = statusCode switch
        {
            400 => "Bad Request",
            403 => "Forbidden",
            408 => "Request Timeout",
            431 => "Request Header Fields Too Large",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            _ => "Proxy Error"
        };
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private void WriteLog(string message) => Log?.Invoke(this, $"{DateTime.Now:HH:mm:ss}  {message}");

    private sealed record InitialRequest(byte[] Header, byte[] Extra);
    private sealed record HttpTarget(string Host, int Port, string PathAndQuery);

    private sealed class ProxyRequestException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}
