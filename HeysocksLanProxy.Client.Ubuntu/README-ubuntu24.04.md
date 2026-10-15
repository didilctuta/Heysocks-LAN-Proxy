# Heysocks 局域网客户端（Ubuntu 24.04 LTS）

这是 Windows 客户端的 Ubuntu GNOME 版本，仅需要安装在局域网客户端电脑上。服务器仍运行现有 Windows 服务端和 Heysocks。

## 安装

双击 `.deb` 后使用“应用中心”安装，或在终端执行：

```bash
sudo apt install ./heysocks-lan-proxy-client_1.3.0_all.deb
```

安装后从应用列表打开“Heysocks 局域网客户端”。程序按当前桌面用户运行，不要使用 `sudo` 启动。

## 使用

1. 在服务器电脑运行 Heysocks 并连接节点，再启动现有 Heysocks 局域网服务器。
2. 在 Ubuntu 客户端输入服务器局域网 IPv4 地址，端口保持 `8080`。
3. 点击“测试连接”；成功后点击“开启代理”。
4. 不再使用时点击“关闭并恢复”，程序会恢复开启前的 GNOME 系统代理设置。

关闭程序窗口不会自动修改当前代理。配置和原始代理备份位于：

```text
~/.config/heysocks-lan-proxy/client/settings.json
```

## 适用范围

- 面向 Ubuntu 24.04 LTS 桌面版的默认 GNOME 会话。
- 设置 GNOME 当前用户的 HTTP/HTTPS 系统代理，不处理 UDP，也不强制接管忽略系统代理的软件。
- Firefox、浏览器或沙盒应用是否遵循系统代理，取决于各应用自身设置。

## 诊断

```bash
heysocks-lan-proxy-client --version
heysocks-lan-proxy-client --self-test
gsettings get org.gnome.system.proxy mode
```

卸载程序不会主动更改当前代理；卸载前如需恢复，请先在客户端中点击“关闭并恢复”。
