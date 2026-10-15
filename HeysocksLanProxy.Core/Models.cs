namespace HeysocksLanProxy.Core;

public sealed class ServerConfig
{
    public int ListenPort { get; set; } = 8080;
    public bool AutoStart { get; set; } = true;
}

public sealed class ClientConfig
{
    public string ServerAddress { get; set; } = string.Empty;
    public int ServerPort { get; set; } = 8080;
    public bool ManagedProxyActive { get; set; }
    public WindowsProxySnapshot? OriginalProxy { get; set; }
}

public sealed class WindowsProxySnapshot
{
    public bool ProxyEnableExists { get; set; }
    public int ProxyEnable { get; set; }
    public bool ProxyServerExists { get; set; }
    public string? ProxyServer { get; set; }
    public bool ProxyOverrideExists { get; set; }
    public string? ProxyOverride { get; set; }
    public bool AutoConfigUrlExists { get; set; }
    public string? AutoConfigUrl { get; set; }
}

public sealed record HeysocksTunnelStatus(
    bool ProcessFound,
    bool TunnelInterfaceFound,
    bool IsTunnelActive,
    string? InterfaceName,
    string Message,
    DateTimeOffset Timestamp);
