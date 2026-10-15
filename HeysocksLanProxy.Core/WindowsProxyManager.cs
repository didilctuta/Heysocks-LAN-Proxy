using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HeysocksLanProxy.Core;

public static class WindowsProxyManager
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    public static WindowsProxySnapshot Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false)
            ?? throw new InvalidOperationException("无法读取 Windows 代理设置。 ");

        return new WindowsProxySnapshot
        {
            ProxyEnableExists = key.GetValue("ProxyEnable") is not null,
            ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0),
            ProxyServerExists = key.GetValue("ProxyServer") is not null,
            ProxyServer = key.GetValue("ProxyServer") as string,
            ProxyOverrideExists = key.GetValue("ProxyOverride") is not null,
            ProxyOverride = key.GetValue("ProxyOverride") as string,
            AutoConfigUrlExists = key.GetValue("AutoConfigURL") is not null,
            AutoConfigUrl = key.GetValue("AutoConfigURL") as string
        };
    }

    public static void Enable(string serverAddress, int serverPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法修改 Windows 代理设置。 ");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{serverAddress}:{serverPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        NotifyWindows();
    }

    public static void Restore(WindowsProxySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法恢复 Windows 代理设置。 ");

        RestoreValue(key, "ProxyEnable", snapshot.ProxyEnableExists, snapshot.ProxyEnable, RegistryValueKind.DWord);
        RestoreValue(key, "ProxyServer", snapshot.ProxyServerExists, snapshot.ProxyServer, RegistryValueKind.String);
        RestoreValue(key, "ProxyOverride", snapshot.ProxyOverrideExists, snapshot.ProxyOverride, RegistryValueKind.String);
        RestoreValue(key, "AutoConfigURL", snapshot.AutoConfigUrlExists, snapshot.AutoConfigUrl, RegistryValueKind.String);
        NotifyWindows();
    }

    public static void DisableManualProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法关闭 Windows 代理设置。 ");

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyWindows();
    }

    public static bool IsUsingEndpoint(
        WindowsProxySnapshot snapshot,
        IEnumerable<string> serverAddresses,
        int serverPort)
    {
        if (snapshot.ProxyEnable == 0 || string.IsNullOrWhiteSpace(snapshot.ProxyServer))
        {
            return false;
        }

        var expectedAddresses = serverAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim().Trim('[', ']'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot.ProxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            var endpoint = separator >= 0 ? entry[(separator + 1)..] : entry;
            if (!Uri.TryCreate($"http://{endpoint.Trim()}", UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Port == serverPort && expectedAddresses.Contains(uri.Host.Trim('[', ']')))
            {
                return true;
            }
        }

        return false;
    }

    private static void RestoreValue(
        RegistryKey key,
        string name,
        bool existed,
        object? value,
        RegistryValueKind valueKind)
    {
        if (existed && value is not null)
        {
            key.SetValue(name, value, valueKind);
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(
        IntPtr internetHandle,
        int option,
        IntPtr buffer,
        int bufferLength);
}
