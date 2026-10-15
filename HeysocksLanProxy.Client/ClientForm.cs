using System.Net;
using HeysocksLanProxy.Core;

namespace HeysocksLanProxy.Client;

public sealed class ClientForm : Form
{
    private readonly ConfigStore<ClientConfig> _configStore = new("Client");
    private readonly TextBox _serverAddress = new();
    private readonly NumericUpDown _serverPort = new();
    private readonly Label _status = new();
    private readonly Button _enableButton = new();
    private readonly Button _disableButton = new();
    private readonly Button _testButton = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayEnableItem = new("开启代理");
    private readonly ToolStripMenuItem _trayDisableItem = new("关闭并恢复");
    private readonly Icon _applicationIcon;
    private ClientConfig _config;
    private bool _allowExit;
    private bool _trayHintShown;

    public ClientForm()
    {
        _config = _configStore.Load();
        _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Shield.Clone();
        InitializeUi();
        InitializeTray();
        LoadSavedSettings();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => DisposeTrayResources();
    }

    private void InitializeUi()
    {
        Text = "Heysocks 局域网客户端";
        Icon = _applicationIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Size = new Size(590, 460);
        BackColor = Color.FromArgb(247, 249, 252);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "Heysocks 局域网客户端",
            Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(28, 24)
        };
        var subtitle = new Label
        {
            Text = "保存服务器地址，一键开启或恢复 Windows 系统代理。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(31, 66)
        };

        var panel = new Panel
        {
            Location = new Point(28, 104),
            Size = new Size(518, 180),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(new Label { Text = "服务器 IP", AutoSize = true, Location = new Point(22, 25) });
        _serverAddress.Location = new Point(126, 21);
        _serverAddress.Size = new Size(346, 27);
        _serverAddress.PlaceholderText = "例如 192.168.124.150";
        panel.Controls.Add(_serverAddress);

        panel.Controls.Add(new Label { Text = "服务器端口", AutoSize = true, Location = new Point(22, 69) });
        _serverPort.Location = new Point(126, 65);
        _serverPort.Minimum = 1024;
        _serverPort.Maximum = 65535;
        _serverPort.Value = 8080;
        _serverPort.Size = new Size(120, 27);
        panel.Controls.Add(_serverPort);

        var saveHint = new Label
        {
            Text = "地址会永久保存在当前 Windows 用户中，下次打开自动载入。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(22, 112)
        };
        panel.Controls.Add(saveHint);

        var saveButton = new Button
        {
            Text = "保存地址",
            Location = new Point(360, 135),
            Size = new Size(112, 32),
            FlatStyle = FlatStyle.Flat
        };
        saveButton.Click += (_, _) => SaveAddress(showConfirmation: true);
        panel.Controls.Add(saveButton);

        _status.Location = new Point(30, 304);
        _status.Size = new Size(516, 28);
        _status.Font = new Font(Font, FontStyle.Bold);

        ConfigureActionButton(_testButton, "测试连接", new Point(28, 350), Color.FromArgb(100, 110, 125));
        ConfigureActionButton(_enableButton, "开启代理", new Point(207, 350), Color.FromArgb(34, 111, 229));
        ConfigureActionButton(_disableButton, "关闭并恢复", new Point(386, 350), Color.FromArgb(196, 54, 54));
        _testButton.Click += async (_, _) => await TestConnectionAsync(showSuccess: true);
        _enableButton.Click += async (_, _) => await EnableProxyAsync();
        _disableButton.Click += (_, _) => DisableProxy();

        Controls.AddRange(new Control[]
        {
            title, subtitle, panel, _status, _testButton, _enableButton, _disableButton
        });
    }

    private void InitializeTray()
    {
        var showItem = new ToolStripMenuItem("显示窗口");
        showItem.Click += (_, _) => ShowMainWindow();
        _trayEnableItem.Click += async (_, _) => await EnableProxyAsync();
        _trayDisableItem.Click += (_, _) => DisableProxy();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _allowExit = true;
            Close();
        };

        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(_trayEnableItem);
        _trayMenu.Items.Add(_trayDisableItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon.Icon = _applicationIcon;
        _trayIcon.Text = "Heysocks 局域网客户端";
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static void ConfigureActionButton(Button button, string text, Point location, Color color)
    {
        button.Text = text;
        button.Location = location;
        button.Size = new Size(160, 44);
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
    }

    private void LoadSavedSettings()
    {
        _serverAddress.Text = _config.ServerAddress;
        _serverPort.Value = Math.Clamp(_config.ServerPort, 1024, 65535);
        if (_config.ManagedProxyActive)
        {
            SetStatus("代理已由本客户端开启，可点击“关闭并恢复”", success: true);
        }
        else
        {
            SetStatus("代理当前未由本客户端管理", success: false);
        }

        UpdateTrayActions();
    }

    private bool SaveAddress(bool showConfirmation)
    {
        var text = _serverAddress.Text.Trim();
        if (!IPAddress.TryParse(text, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
        {
            MessageBox.Show(this, "请输入有效的服务器 IPv4 地址，例如 192.168.124.150。",
                "地址不正确", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _config.ServerAddress = text;
        _config.ServerPort = (int)_serverPort.Value;
        _configStore.Save(_config);
        if (showConfirmation)
        {
            SetStatus($"已保存 {_config.ServerAddress}:{_config.ServerPort}", success: true);
        }

        return true;
    }

    private async Task<bool> TestConnectionAsync(bool showSuccess)
    {
        if (!SaveAddress(showConfirmation: false))
        {
            return false;
        }

        SetBusy(true);
        SetStatus("正在通过服务器验证代理连接…", success: false);
        try
        {
            var address = IPAddress.Parse(_config.ServerAddress);
            var working = await HttpProxyProbe.IsWorkingAsync(address, _config.ServerPort, TimeSpan.FromSeconds(8));
            if (working)
            {
                if (showSuccess)
                {
                    SetStatus("连接成功，服务器代理可用", success: true);
                }

                return true;
            }

            SetStatus("连接失败：服务器未启动、Heysocks 安全出口未就绪或防火墙未放行", success: false);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task EnableProxyAsync()
    {
        if (!await TestConnectionAsync(showSuccess: false))
        {
            MessageBox.Show(this, "服务器代理尚不可用，未修改 Windows 代理设置。",
                "无法开启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (!_config.ManagedProxyActive)
            {
                _config.OriginalProxy = WindowsProxyManager.Capture();
            }

            WindowsProxyManager.Enable(_config.ServerAddress, _config.ServerPort);
            _config.ManagedProxyActive = true;
            _configStore.Save(_config);
            SetStatus($"代理已开启：{_config.ServerAddress}:{_config.ServerPort}", success: true);
            UpdateTrayActions();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "开启失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DisableProxy()
    {
        if (!_config.ManagedProxyActive || _config.OriginalProxy is null)
        {
            MessageBox.Show(this, "本客户端没有可恢复的原始代理设置。",
                "无需恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            WindowsProxyManager.Restore(_config.OriginalProxy);
            _config.ManagedProxyActive = false;
            _config.OriginalProxy = null;
            _configStore.Save(_config);
            SetStatus("已关闭本客户端设置的代理，并恢复原来的 Windows 配置", success: true);
            UpdateTrayActions();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        _testButton.Enabled = !busy;
        _enableButton.Enabled = !busy;
        _disableButton.Enabled = !busy;
        _trayEnableItem.Enabled = !busy && !_config.ManagedProxyActive;
        _trayDisableItem.Enabled = !busy && _config.ManagedProxyActive && _config.OriginalProxy is not null;
    }

    private void SetStatus(string text, bool success)
    {
        _status.Text = text;
        _status.ForeColor = success ? Color.FromArgb(20, 130, 75) : Color.FromArgb(170, 95, 0);
    }

    private void UpdateTrayActions()
    {
        _trayEnableItem.Enabled = !_config.ManagedProxyActive;
        _trayDisableItem.Enabled = _config.ManagedProxyActive && _config.OriginalProxy is not null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowExit || eventArgs.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        eventArgs.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(2500, "Heysocks 局域网客户端",
            "程序仍在通知区域运行，双击图标可恢复窗口。", ToolTipIcon.Info);
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        WindowActivationService.RestoreAndActivate(Handle);
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
