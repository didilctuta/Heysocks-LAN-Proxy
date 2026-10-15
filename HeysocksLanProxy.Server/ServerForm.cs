using HeysocksLanProxy.Core;

namespace HeysocksLanProxy.Server;

public sealed class ServerForm : Form
{
    private readonly ConfigStore<ServerConfig> _configStore = new("Server");
    private readonly ConfigStore<ClientConfig> _clientConfigStore = new("Client");
    private readonly HeysocksTunnelMonitor _tunnelMonitor = new();
    private readonly HttpForwardProxyServer _proxyServer;
    private readonly Label _serviceStatus = new();
    private readonly Label _processValue = new();
    private readonly Label _tunnelValue = new();
    private readonly Label _lanIpValue = new();
    private readonly NumericUpDown _listenPort = new();
    private readonly Button _toggleButton = new();
    private readonly Button _restoreButton = new();
    private readonly TextBox _logBox = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayToggleItem = new();
    private readonly Icon _applicationIcon;
    private ServerConfig _config;
    private bool _running;
    private bool _allowExit;
    private bool _exitCompleted;
    private bool _trayHintShown;

    public ServerForm()
    {
        _config = _configStore.Load();
        _proxyServer = new HttpForwardProxyServer(
            () => _tunnelMonitor.IsTunnelActive,
            routeValidator: address =>
            {
                var safe = _tunnelMonitor.IsSafeRoute(address, out var interfaceName);
                return (safe, interfaceName);
            });
        _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Shield.Clone();
        InitializeUi();
        InitializeTray();
        _tunnelMonitor.StatusChanged += TunnelMonitorOnStatusChanged;
        _proxyServer.Log += (_, message) => AppendLog(message);
        Shown += async (_, _) =>
        {
            RefreshLanAddresses();
            if (_config.AutoStart)
            {
                await StartServiceAsync();
            }
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        };
        FormClosing += OnFormClosingAsync;
        FormClosed += (_, _) => DisposeTrayResources();
    }

    private void InitializeUi()
    {
        Text = "Heysocks 局域网服务器";
        Icon = _applicationIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 570);
        Size = new Size(720, 610);
        BackColor = Color.FromArgb(247, 249, 252);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "Heysocks 局域网服务器",
            Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(28, 22)
        };
        var subtitle = new Label
        {
            Text = "通过 Heysocks VirtualNet 安全出口，为局域网提供固定 HTTP/HTTPS 代理。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(31, 64)
        };

        var statusPanel = CreatePanel(new Point(28, 98), new Size(648, 180));
        statusPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _serviceStatus.Text = "尚未启动";
        _serviceStatus.Font = new Font(Font, FontStyle.Bold);
        _serviceStatus.ForeColor = Color.FromArgb(170, 95, 0);
        _serviceStatus.AutoSize = true;
        _serviceStatus.Location = new Point(22, 18);
        statusPanel.Controls.Add(_serviceStatus);

        AddRow(statusPanel, "Heysocks 进程", _processValue, 56);
        AddRow(statusPanel, "Heysocks 安全出口", _tunnelValue, 88);
        AddRow(statusPanel, "本机局域网 IP", _lanIpValue, 120);
        var hint = new Label
        {
            Text = "客户端填写上面的局域网 IP，端口填写固定监听端口。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(22, 151)
        };
        statusPanel.Controls.Add(hint);

        var settingsPanel = CreatePanel(new Point(28, 294), new Size(648, 92));
        settingsPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var portLabel = new Label { Text = "固定监听端口", AutoSize = true, Location = new Point(22, 20) };
        settingsPanel.Controls.Add(portLabel);
        _listenPort.Minimum = 1024;
        _listenPort.Maximum = 65535;
        _listenPort.Value = Math.Clamp(_config.ListenPort, 1024, 65535);
        _listenPort.Location = new Point(145, 17);
        _listenPort.Width = 110;
        settingsPanel.Controls.Add(_listenPort);
        var securityHint = new Label
        {
            Text = "防火墙仅允许专用网络中的本地子网访问",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(22, 54)
        };
        settingsPanel.Controls.Add(securityHint);
        _toggleButton.Text = "启动共享";
        _toggleButton.Size = new Size(126, 40);
        _toggleButton.Location = new Point(354, 24);
        _toggleButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _toggleButton.BackColor = Color.FromArgb(34, 111, 229);
        _toggleButton.ForeColor = Color.White;
        _toggleButton.FlatStyle = FlatStyle.Flat;
        _toggleButton.FlatAppearance.BorderSize = 0;
        _toggleButton.Click += async (_, _) =>
        {
            if (_running)
            {
                await StopServiceAsync();
            }
            else
            {
                await StartServiceAsync();
            }
        };
        settingsPanel.Controls.Add(_toggleButton);

        _restoreButton.Text = "关闭并恢复";
        _restoreButton.Size = new Size(126, 40);
        _restoreButton.Location = new Point(493, 24);
        _restoreButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _restoreButton.BackColor = Color.FromArgb(196, 54, 54);
        _restoreButton.ForeColor = Color.White;
        _restoreButton.FlatStyle = FlatStyle.Flat;
        _restoreButton.FlatAppearance.BorderSize = 0;
        _restoreButton.Click += async (_, _) => await StopAndRestoreProxyAsync(showConfirmation: true);
        settingsPanel.Controls.Add(_restoreButton);

        var logLabel = new Label
        {
            Text = "运行记录",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(29, 403)
        };
        _logBox.Location = new Point(28, 430);
        _logBox.Size = new Size(648, 126);
        _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BackColor = Color.White;

        Controls.AddRange(new Control[] { title, subtitle, statusPanel, settingsPanel, logLabel, _logBox });
    }

    private void InitializeTray()
    {
        var showItem = new ToolStripMenuItem("显示窗口");
        showItem.Click += (_, _) => ShowMainWindow();

        _trayToggleItem.Text = "启动共享";
        _trayToggleItem.Click += async (_, _) =>
        {
            if (_running)
            {
                await StopServiceAsync();
            }
            else
            {
                await StartServiceAsync();
            }
        };

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _allowExit = true;
            Close();
        };

        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(_trayToggleItem);
        var restoreItem = new ToolStripMenuItem("关闭共享并恢复代理");
        restoreItem.Click += async (_, _) => await StopAndRestoreProxyAsync(showConfirmation: true);
        _trayMenu.Items.Add(restoreItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon.Icon = _applicationIcon;
        _trayIcon.Text = "Heysocks 局域网服务器";
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static Panel CreatePanel(Point location, Size size) => new()
    {
        Location = location,
        Size = size,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static void AddRow(Control panel, string name, Label value, int y)
    {
        var nameLabel = new Label { Text = name, ForeColor = Color.DimGray, AutoSize = true, Location = new Point(22, y) };
        value.Text = "—";
        value.AutoSize = true;
        value.Location = new Point(166, y);
        panel.Controls.Add(nameLabel);
        panel.Controls.Add(value);
    }

    private async Task StartServiceAsync()
    {
        _toggleButton.Enabled = false;
        _trayToggleItem.Enabled = false;
        _restoreButton.Enabled = false;
        try
        {
            var port = (int)_listenPort.Value;
            _config.ListenPort = port;
            _config.AutoStart = true;
            _configStore.Save(_config);
            AppendLog($"正在配置 TCP {port} 防火墙规则…");
            await FirewallService.EnsurePrivateLanRuleAsync(port);
            var initialTunnelStatus = _tunnelMonitor.ScanNow();
            _proxyServer.Start(port);
            _tunnelMonitor.Start();
            _running = true;
            _listenPort.Enabled = false;
            _toggleButton.Text = "停止共享";
            _trayToggleItem.Text = "停止共享";
            _toggleButton.BackColor = Color.FromArgb(196, 54, 54);
            UpdateServiceStatus(initialTunnelStatus);
        }
        catch (Exception exception)
        {
            AppendLog($"启动失败：{exception.Message}");
            MessageBox.Show(this, exception.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _toggleButton.Enabled = true;
            _trayToggleItem.Enabled = true;
            _restoreButton.Enabled = true;
        }
    }

    private async Task StopServiceAsync()
    {
        _toggleButton.Enabled = false;
        _trayToggleItem.Enabled = false;
        _restoreButton.Enabled = false;
        try
        {
            await _tunnelMonitor.StopAsync();
            await _proxyServer.StopAsync();
            _running = false;
            _listenPort.Enabled = true;
            _toggleButton.Text = "启动共享";
            _trayToggleItem.Text = "启动共享";
            _toggleButton.BackColor = Color.FromArgb(34, 111, 229);
            _serviceStatus.Text = "共享已停止";
            _serviceStatus.ForeColor = Color.FromArgb(170, 95, 0);
            _processValue.Text = "—";
            _tunnelValue.Text = "—";
        }
        finally
        {
            _toggleButton.Enabled = true;
            _trayToggleItem.Enabled = true;
            _restoreButton.Enabled = true;
        }
    }

    private async Task StopAndRestoreProxyAsync(bool showConfirmation)
    {
        await StopServiceAsync();
        _config.AutoStart = false;
        _configStore.Save(_config);

        string resultMessage;
        try
        {
            resultMessage = RestoreWindowsProxy();
            AppendLog(resultMessage);
            _serviceStatus.Text = resultMessage;
            _serviceStatus.ForeColor = Color.FromArgb(20, 130, 75);
        }
        catch (Exception exception)
        {
            resultMessage = $"Windows 代理恢复失败：{exception.Message}";
            AppendLog(resultMessage);
            _serviceStatus.Text = "共享已停止，但 Windows 代理恢复失败";
            _serviceStatus.ForeColor = Color.FromArgb(196, 54, 54);
            if (showConfirmation)
            {
                MessageBox.Show(this, resultMessage, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        if (showConfirmation)
        {
            MessageBox.Show(this, resultMessage, "关闭并恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private string RestoreWindowsProxy()
    {
        var clientConfig = _clientConfigStore.Load();
        if (clientConfig.ManagedProxyActive && clientConfig.OriginalProxy is not null)
        {
            WindowsProxyManager.Restore(clientConfig.OriginalProxy);
            clientConfig.ManagedProxyActive = false;
            clientConfig.OriginalProxy = null;
            _clientConfigStore.Save(clientConfig);
            return "已停止共享，并恢复客户端保存的原始 Windows 代理设置";
        }

        var currentProxy = WindowsProxyManager.Capture();
        var localAddresses = LanAddressService.GetPrivateIpv4Addresses()
            .Select(address => address.ToString())
            .Append("127.0.0.1")
            .Append("localhost")
            .ToArray();
        var pointsToCurrentServer = WindowsProxyManager.IsUsingEndpoint(
            currentProxy, localAddresses, (int)_listenPort.Value);
        var pointsToSavedServer = clientConfig.ManagedProxyActive &&
                                  WindowsProxyManager.IsUsingEndpoint(
                                      currentProxy,
                                      new[] { clientConfig.ServerAddress },
                                      clientConfig.ServerPort);

        if (pointsToCurrentServer || pointsToSavedServer)
        {
            WindowsProxyManager.DisableManualProxy();
            clientConfig.ManagedProxyActive = false;
            clientConfig.OriginalProxy = null;
            _clientConfigStore.Save(clientConfig);
            return "已停止共享；未找到原始备份，已关闭指向本机的 Windows 代理";
        }

        return currentProxy.ProxyEnable == 0
            ? "已停止共享；Windows 系统代理原本就是关闭状态"
            : "已停止共享；当前 Windows 代理不是本服务器地址，已保留原设置";
    }

    private void TunnelMonitorOnStatusChanged(object? sender, HeysocksTunnelStatus status)
    {
        RunOnUiThread(() =>
        {
            _processValue.Text = status.ProcessFound ? "heysocks_runtime.exe（已运行）" : "未发现";
            _tunnelValue.Text = status.IsTunnelActive
                ? $"{status.InterfaceName}（当前出口）"
                : status.TunnelInterfaceFound
                    ? $"{status.InterfaceName}（不是当前出口）"
                    : "未连接";
            if (_running && !status.IsTunnelActive)
            {
                _proxyServer.DisconnectAllClients(status.Message);
            }
            UpdateServiceStatus(status);
            AppendLog(status.Message);
        });
    }

    private void UpdateServiceStatus(HeysocksTunnelStatus status)
    {
        if (!_running)
        {
            return;
        }

        var port = (int)_listenPort.Value;
        if (status.IsTunnelActive)
        {
            _serviceStatus.Text = $"代理可用：0.0.0.0:{port}（流量经过 Heysocks）";
            _serviceStatus.ForeColor = Color.FromArgb(20, 130, 75);
        }
        else
        {
            _serviceStatus.Text = $"共享监听中：0.0.0.0:{port}（等待 Heysocks 安全出口）";
            _serviceStatus.ForeColor = Color.FromArgb(170, 95, 0);
        }
    }

    private void RefreshLanAddresses()
    {
        var addresses = LanAddressService.GetPrivateIpv4Addresses();
        _lanIpValue.Text = addresses.Count == 0 ? "未检测到" : string.Join("  /  ", addresses);
    }

    private void AppendLog(string message)
    {
        RunOnUiThread(() =>
        {
            _logBox.AppendText(message + Environment.NewLine);
            if (_logBox.Lines.Length > 300)
            {
                _logBox.Lines = _logBox.Lines.TakeLast(200).ToArray();
            }
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private async void OnFormClosingAsync(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_allowExit && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            HideToTray();
            return;
        }

        if (_exitCompleted)
        {
            return;
        }

        eventArgs.Cancel = true;
        _allowExit = true;
        await StopAndRestoreProxyAsync(showConfirmation: false);
        _exitCompleted = true;
        _trayIcon.Visible = false;
        Close();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        WindowState = FormWindowState.Normal;
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(2500, "Heysocks 局域网服务器",
            "程序仍在通知区域运行，双击图标可恢复窗口。", ToolTipIcon.Info);
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void DisposeTrayResources()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _applicationIcon.Dispose();
    }
}
