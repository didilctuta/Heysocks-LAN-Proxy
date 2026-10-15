using System.Diagnostics;

namespace HeysocksLanProxy.Core;

public static class FirewallService
{
    private const string RuleName = "Heysocks LAN Proxy";

    public static async Task EnsurePrivateLanRuleAsync(int port, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunNetshAsync(
            $"advfirewall firewall delete rule name=\"{RuleName}\"",
            cancellationToken,
            allowFailure: true).ConfigureAwait(false);

        await RunNetshAsync(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private remoteip=LocalSubnet",
            cancellationToken,
            allowFailure: false).ConfigureAwait(false);
    }

    private static async Task RunNetshAsync(string arguments, CancellationToken cancellationToken, bool allowFailure)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"防火墙配置失败：{error}{output}".Trim());
        }
    }
}
