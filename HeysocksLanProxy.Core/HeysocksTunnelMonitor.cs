using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HeysocksLanProxy.Core;

public sealed class HeysocksTunnelMonitor : IAsyncDisposable
{
    private readonly TimeSpan _scanInterval;
    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;
    private string? _lastStatusKey;
    private volatile bool _isTunnelActive;

    public HeysocksTunnelMonitor(TimeSpan? scanInterval = null)
    {
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(1);
    }

    public event EventHandler<HeysocksTunnelStatus>? StatusChanged;

    public bool IsTunnelActive => _isTunnelActive;

    public bool IsSafeRoute(IPAddress destination, out string? interfaceName)
    {
        interfaceName = null;
        if (!OperatingSystem.IsWindows() || destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var interfaceIndex = GetBestIpv4InterfaceIndex(destination);
        if (interfaceIndex is null)
        {
            return false;
        }

        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
            candidate.OperationalStatus == OperationalStatus.Up &&
            TryGetIpv4Index(candidate) == interfaceIndex);
        interfaceName = network?.Name;
        return _isTunnelActive && network is not null && IsHeysocksTunnel(network);
    }

    public HeysocksTunnelStatus ScanNow()
    {
        var processes = Process.GetProcessesByName("heysocks_runtime");
        var processFound = processes.Length > 0;
        foreach (var process in processes)
        {
            process.Dispose();
        }

        var tunnelInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                IsHeysocksTunnel(network))
            .Select(network => new
            {
                network.Name,
                Index = TryGetIpv4Index(network)
            })
            .Where(network => network.Index is not null)
            .ToArray();

        var bestInterfaceIndex = GetBestIpv4InterfaceIndex(IPAddress.Parse("1.1.1.1"));
        var activeTunnel = tunnelInterfaces.FirstOrDefault(network => network.Index == bestInterfaceIndex);
        var tunnelActive = processFound && activeTunnel is not null;
        _isTunnelActive = tunnelActive;

        string message;
        if (!processFound)
        {
            message = "未发现 heysocks_runtime.exe，已阻止代理流量";
        }
        else if (tunnelInterfaces.Length == 0)
        {
            message = "Heysocks 已运行，但 VirtualNet 尚未连接，已阻止代理流量";
        }
        else if (activeTunnel is null)
        {
            message = "VirtualNet 不是当前网络出口，已阻止代理流量";
        }
        else
        {
            message = $"Heysocks 隧道正常，当前出口：{activeTunnel.Name}";
        }

        return new HeysocksTunnelStatus(
            processFound,
            tunnelInterfaces.Length > 0,
            tunnelActive,
            activeTunnel?.Name ?? tunnelInterfaces.FirstOrDefault()?.Name,
            message,
            DateTimeOffset.Now);
    }

    public void Start()
    {
        if (_monitorTask is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _monitorTask = MonitorAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_monitorTask is null)
        {
            return;
        }

        _cancellation!.Cancel();
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _monitorTask = null;
            _cancellation.Dispose();
            _cancellation = null;
            _isTunnelActive = false;
            _lastStatusKey = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HeysocksTunnelStatus status;
            try
            {
                status = ScanNow();
            }
            catch (Exception exception)
            {
                _isTunnelActive = false;
                status = new HeysocksTunnelStatus(
                    false, false, false, null,
                    $"隧道检测失败：{exception.Message}",
                    DateTimeOffset.Now);
            }

            var statusKey = $"{status.ProcessFound}|{status.TunnelInterfaceFound}|{status.IsTunnelActive}|{status.InterfaceName}|{status.Message}";
            if (!string.Equals(statusKey, _lastStatusKey, StringComparison.Ordinal))
            {
                _lastStatusKey = statusKey;
                StatusChanged?.Invoke(this, status);
            }

            await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int? TryGetIpv4Index(NetworkInterface network)
    {
        try
        {
            return network.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsHeysocksTunnel(NetworkInterface network) =>
        network.Name.Contains("VirtualNet", StringComparison.OrdinalIgnoreCase) ||
        network.Description.Contains("VirtualNet", StringComparison.OrdinalIgnoreCase) ||
        network.Name.Contains("Heysocks", StringComparison.OrdinalIgnoreCase) ||
        network.Description.Contains("Heysocks", StringComparison.OrdinalIgnoreCase);

    private static int? GetBestIpv4InterfaceIndex(IPAddress destinationAddress)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var destinationBytes = destinationAddress.GetAddressBytes();
        var destination = BitConverter.ToUInt32(destinationBytes, 0);
        return GetBestInterface(destination, out var index) == 0 ? (int)index : null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);
}
