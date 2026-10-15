using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HeysocksLanProxy.Client;
using HeysocksLanProxy.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("HTTP 代理探测接受 200 CONNECT", TestSuccessfulProxyProbeAsync),
    ("HTTP 代理探测拒绝内部 API 端口", TestRejectedProxyProbeAsync),
    ("内置 HTTPS 代理能够建立 CONNECT 隧道", TestBuiltInConnectProxyAsync),
    ("Heysocks 断开时内置代理拒绝流量", TestBuiltInProxyGuardAsync),
    ("目标路由绕过 VirtualNet 时内置代理拒绝流量", TestUnsafeTargetRouteGuardAsync),
    ("安全出口失效时断开现有代理连接", TestDisconnectActiveProxyClientsAsync),
    ("客户端从托盘恢复为可见窗口", TestClientTrayRestoreAsync),
    ("Windows 代理能够识别本机共享入口", TestProxyEndpointDetectionAsync),
    ("客户端配置能够持久保存", TestConfigStoreAsync)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"[FAIL] {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"完成：{tests.Count - failures}/{tests.Count} 项通过");
if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("\n当前 Heysocks VirtualNet 现场检测：");
    await using var monitor = new HeysocksTunnelMonitor();
    var status = monitor.ScanNow();
    Console.WriteLine($"  {status.Message}");
    if (status.IsTunnelActive)
    {
        var liveProxyPort = ReservePort();
        await using var liveProxy = new HttpForwardProxyServer(
            () => monitor.IsTunnelActive,
            routeValidator: address =>
            {
                var safe = monitor.IsSafeRoute(address, out var interfaceName);
                return (safe, interfaceName);
            });
        liveProxy.Start(liveProxyPort);
        var liveResult = await HttpProxyProbe.IsWorkingAsync(IPAddress.Loopback, liveProxyPort, TimeSpan.FromSeconds(12));
        Console.WriteLine(liveResult
            ? "  [PASS] 内置代理已通过 Heysocks 出口访问外网"
            : "  [FAIL] 内置代理无法通过 Heysocks 出口访问外网");
        if (!liveResult)
        {
            failures++;
        }
        else
        {
            using var directClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var proxyClient = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{liveProxyPort}"),
                UseProxy = true
            }) { Timeout = TimeSpan.FromSeconds(15) };
            var directBefore = (await directClient.GetStringAsync("https://ifconfig.me/ip")).Trim();
            var proxyExits = new List<string>();
            for (var index = 0; index < 20; index++)
            {
                proxyExits.Add((await proxyClient.GetStringAsync("https://ifconfig.me/ip")).Trim());
            }
            var directAfter = (await directClient.GetStringAsync("https://ifconfig.me/ip")).Trim();
            var plainHttpExit = (await proxyClient.GetStringAsync("http://ifconfig.me/ip")).Trim();
            var cloudflareTrace = await proxyClient.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var distinctProxyExits = proxyExits.Distinct(StringComparer.Ordinal).ToArray();
            Console.WriteLine($"  Windows 直接出口（前/后）：{directBefore} / {directAfter}");
            Console.WriteLine($"  代理连续 20 次出口：{string.Join(", ", distinctProxyExits)}");
            Console.WriteLine($"  普通 HTTP 代理出口：{plainHttpExit}");
            Console.WriteLine(cloudflareTrace.Contains("ip=", StringComparison.Ordinal)
                ? "  [PASS] Cloudflare HTTPS CONNECT trace 返回有效内容"
                : "  [FAIL] Cloudflare HTTPS CONNECT trace 缺少出口 IP");
            var exitsMatch = distinctProxyExits.All(exit => exit == directBefore || exit == directAfter);
            Console.WriteLine(exitsMatch
                ? "  [PASS] 20 次代理出口稳定，且与同时间 Windows 出口一致"
                : "  [WARN] 公网出口不一致；需结合 VPN 动态出口或按进程策略继续判断");
        }
    }
    else
    {
        Console.WriteLine("  [SKIP] Heysocks 安全出口未就绪");
    }
}

return failures == 0 ? 0 : 1;

static async Task TestSuccessfulProxyProbeAsync()
{
    await using var server = new FakeHttpServer("HTTP/1.1 200 Connection Established\r\n\r\n");
    var result = await HttpProxyProbe.IsWorkingAsync(IPAddress.Loopback, server.Port, TimeSpan.FromSeconds(2));
    Assert(result, "200 CONNECT 响应未被识别");
}

static async Task TestRejectedProxyProbeAsync()
{
    await using var server = new FakeHttpServer("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n");
    var result = await HttpProxyProbe.IsWorkingAsync(IPAddress.Loopback, server.Port, TimeSpan.FromSeconds(2));
    Assert(!result, "405 API 端口被误判为代理");
}

static async Task TestBuiltInConnectProxyAsync()
{
    await using var echoServer = new FakeEchoServer();
    var proxyPort = ReservePort();
    await using var proxy = new HttpForwardProxyServer(() => true, allowLocalTargets: true);
    proxy.Start(proxyPort);

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxyPort);
    var stream = client.GetStream();
    var request = Encoding.ASCII.GetBytes(
        $"CONNECT 127.0.0.1:{echoServer.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echoServer.Port}\r\n\r\n");
    await stream.WriteAsync(request);
    var response = await ReadHeaderAsync(stream);
    Assert(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "CONNECT 未返回 200");

    var payload = Encoding.ASCII.GetBytes("proxy-echo-test");
    await stream.WriteAsync(payload);
    var echoed = new byte[payload.Length];
    await stream.ReadExactlyAsync(echoed);
    Assert(echoed.SequenceEqual(payload), "CONNECT 隧道没有正确转发数据");
}

static async Task TestBuiltInProxyGuardAsync()
{
    var proxyPort = ReservePort();
    await using var proxy = new HttpForwardProxyServer(() => false, allowLocalTargets: true);
    proxy.Start(proxyPort);

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxyPort);
    var stream = client.GetStream();
    var request = Encoding.ASCII.GetBytes("CONNECT www.example.com:443 HTTP/1.1\r\nHost: www.example.com:443\r\n\r\n");
    await stream.WriteAsync(request);
    var response = await ReadHeaderAsync(stream);
    Assert(response.StartsWith("HTTP/1.1 503", StringComparison.Ordinal), "断线保护没有返回 503");
}

static async Task TestUnsafeTargetRouteGuardAsync()
{
    await using var echoServer = new FakeEchoServer();
    var proxyPort = ReservePort();
    await using var proxy = new HttpForwardProxyServer(
        () => true,
        allowLocalTargets: true,
        routeValidator: _ => (false, "物理网卡测试接口"));
    proxy.Start(proxyPort);

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxyPort);
    var stream = client.GetStream();
    var request = Encoding.ASCII.GetBytes(
        $"CONNECT 127.0.0.1:{echoServer.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echoServer.Port}\r\n\r\n");
    await stream.WriteAsync(request);
    var response = await ReadHeaderAsync(stream);
    Assert(response.StartsWith("HTTP/1.1 503", StringComparison.Ordinal), "不安全目标路由没有返回 503");
}

static async Task TestDisconnectActiveProxyClientsAsync()
{
    await using var echoServer = new FakeEchoServer();
    var proxyPort = ReservePort();
    await using var proxy = new HttpForwardProxyServer(() => true, allowLocalTargets: true);
    proxy.Start(proxyPort);

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxyPort);
    var stream = client.GetStream();
    var request = Encoding.ASCII.GetBytes(
        $"CONNECT 127.0.0.1:{echoServer.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echoServer.Port}\r\n\r\n");
    await stream.WriteAsync(request);
    var response = await ReadHeaderAsync(stream);
    Assert(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "测试隧道未建立");

    proxy.DisconnectAllClients("test");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        var buffer = new byte[1];
        var count = await stream.ReadAsync(buffer, timeout.Token);
        Assert(count == 0, "现有连接仍然可读");
    }
    catch (IOException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

static Task TestConfigStoreAsync()
{
    var folderName = Path.Combine("SmokeTests", Guid.NewGuid().ToString("N"));
    var store = new ConfigStore<ClientConfig>(folderName);
    try
    {
        store.Save(new ClientConfig { ServerAddress = "192.168.124.150", ServerPort = 8080 });
        var loaded = store.Load();
        Assert(loaded.ServerAddress == "192.168.124.150" && loaded.ServerPort == 8080, "保存后的配置不一致");
    }
    finally
    {
        if (Directory.Exists(store.DirectoryPath))
        {
            Directory.Delete(store.DirectoryPath, recursive: true);
        }
    }

    return Task.CompletedTask;
}

static Task TestProxyEndpointDetectionAsync()
{
    var directProxy = new WindowsProxySnapshot
    {
        ProxyEnable = 1,
        ProxyServer = "192.168.124.150:8080"
    };
    Assert(WindowsProxyManager.IsUsingEndpoint(
        directProxy, new[] { "127.0.0.1", "192.168.124.150" }, 8080),
        "未识别直接代理地址");

    var protocolProxy = new WindowsProxySnapshot
    {
        ProxyEnable = 1,
        ProxyServer = "http=192.168.124.150:8080;https=192.168.124.150:8080"
    };
    Assert(WindowsProxyManager.IsUsingEndpoint(
        protocolProxy, new[] { "192.168.124.150" }, 8080),
        "未识别按协议设置的代理地址");

    var unrelatedProxy = new WindowsProxySnapshot
    {
        ProxyEnable = 1,
        ProxyServer = "192.168.124.151:8080"
    };
    Assert(!WindowsProxyManager.IsUsingEndpoint(
        unrelatedProxy, new[] { "192.168.124.150" }, 8080),
        "误判了其他电脑的代理地址");

    var disabledProxy = new WindowsProxySnapshot
    {
        ProxyEnable = 0,
        ProxyServer = "192.168.124.150:8080"
    };
    Assert(!WindowsProxyManager.IsUsingEndpoint(
        disabledProxy, new[] { "192.168.124.150" }, 8080),
        "误判了已关闭的代理地址");

    return Task.CompletedTask;
}

static Task TestClientTrayRestoreAsync()
{
    Exception? failure = null;
    var testThread = new Thread(() =>
    {
        ClientForm? form = null;
        try
        {
            form = new ClientForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000)
            };
            typeof(ClientForm).GetField("_trayHintShown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(form, true);

            form.Show();
            Application.DoEvents();
            form.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Assert(!form.Visible, "客户端最小化后没有隐藏到托盘");

            typeof(ClientForm).GetMethod("ShowMainWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, null);
            Application.DoEvents();

            Assert(form.Visible, "点击显示窗口后窗体仍为隐藏状态");
            Assert(form.WindowState == FormWindowState.Normal, "点击显示窗口后窗体仍处于最小化状态");
            Assert(TestNativeMethods.IsWindowVisible(form.Handle), "点击显示窗口后原生窗口仍不可见");
            Assert(!TestNativeMethods.IsIconic(form.Handle), "点击显示窗口后原生窗口仍处于最小化状态");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            failure = exception.InnerException;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (form is not null)
            {
                typeof(ClientForm).GetField("_allowExit", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(form, true);
                form.Close();
                Application.DoEvents();
                form.Dispose();
            }
        }
    });

    testThread.SetApartmentState(ApartmentState.STA);
    testThread.Start();
    if (!testThread.Join(TimeSpan.FromSeconds(10)))
    {
        throw new TimeoutException("客户端托盘恢复测试超时");
    }

    if (failure is not null)
    {
        throw failure;
    }

    return Task.CompletedTask;
}

static int ReservePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static async Task<string> ReadHeaderAsync(Stream stream)
{
    using var buffer = new MemoryStream();
    var singleByte = new byte[1];
    while (buffer.Length < 16 * 1024)
    {
        var count = await stream.ReadAsync(singleByte);
        if (count == 0)
        {
            break;
        }
        buffer.WriteByte(singleByte[0]);
        if (buffer.Length >= 4)
        {
            var data = buffer.GetBuffer();
            var length = (int)buffer.Length;
            if (data[length - 4] == 13 && data[length - 3] == 10 && data[length - 2] == 13 && data[length - 1] == 10)
            {
                break;
            }
        }
    }
    return Encoding.ASCII.GetString(buffer.ToArray());
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _response;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;

    public FakeHttpServer(string response)
    {
        _response = response;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serverTask = ServeAsync(_cancellation.Token);
    }

    public int Port { get; }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        _cancellation.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, cancellationToken);
            var response = Encoding.ASCII.GetBytes(_response);
            await stream.WriteAsync(response, cancellationToken);
        }
    }
}

internal static class TestNativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr windowHandle);
}

internal sealed class FakeEchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;

    public FakeEchoServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serverTask = ServeAsync(_cancellation.Token);
    }

    public int Port { get; }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        _cancellation.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            _ = EchoAsync(client, cancellationToken);
        }
    }

    private static async Task EchoAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
        }
    }
}
