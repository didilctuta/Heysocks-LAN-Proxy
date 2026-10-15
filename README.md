# Heysocks 局域网代理

Heysocks 局域网代理是 **Heysocks Windows 桌面 VPN 客户端的配套扩展工具**。它利用 Heysocks 提供的 `VirtualNet` 隧道，在局域网内共享 HTTP/HTTPS 代理，让其他设备的受支持应用通过运行 Heysocks 的 Windows 电脑访问网络。

本项目不是 Heysocks 官方客户端，也不替代 Heysocks。服务器端依赖已安装并连接节点的 Heysocks Windows 客户端及其 `VirtualNet` 虚拟网络接口。项目当前提供 Windows x64 服务端和客户端，以及 Ubuntu 24.04 LTS GNOME 客户端。

## 发布包

[下载 v1.3.0 发布包](https://github.com/didilctuta/Heysocks-LAN-Proxy/releases/tag/v1.3.0)

| 发布包 | 包含内容 | 适用场景 |
| --- | --- | --- |
| [Windows x64 完整包](https://github.com/didilctuta/Heysocks-LAN-Proxy/releases/download/v1.3.0/Heysocks-LAN-Proxy-v1.3.0-win-x64.zip) | Windows x64 服务端和客户端 | 在安装并运行 Heysocks 的 Windows 电脑上启动服务端；其他 Windows 设备使用客户端连接 |
| [Ubuntu 24.04 客户端](https://github.com/didilctuta/Heysocks-LAN-Proxy/releases/download/v1.3.0/heysocks-lan-proxy-client_1.3.0_all.deb) | Ubuntu 24.04 LTS 客户端 `.deb` | Ubuntu 设备作为局域网客户端，连接运行 Windows 服务端的电脑 |

Ubuntu 包**只有客户端**，不包含也不提供代理服务器。使用前必须在同一局域网中准备一台已连接 Heysocks 的 Windows 服务器，并在 Ubuntu 客户端中填写该服务器的局域网 IP 和服务端口（默认 `8080`）。

## Windows 使用

1. 在已安装 Heysocks 并成功连接节点的 Windows 电脑上启动 `Server/Heysocks局域网服务器.exe`。程序会请求管理员权限。
2. 等待服务器界面显示代理可用，记下显示的局域网 IPv4 地址。服务器默认监听 `8080`，并自动设置仅允许专用网络本地子网访问的防火墙规则。
3. 在局域网内 Windows 客户端启动 `Client/Heysocks局域网客户端.exe`，填写服务器 IP，保持端口 `8080`，测试连接后开启代理。
4. 使用完毕后点击“关闭并恢复”，恢复开启代理前的 Windows 系统代理设置。

## Ubuntu 24.04 使用

安装下载的 `heysocks-lan-proxy-client_1.3.0_all.deb`：

```bash
sudo apt install ./heysocks-lan-proxy-client_1.3.0_all.deb
```

从应用列表以当前桌面用户身份启动客户端（不要使用 `sudo` 启动），填写 Windows 服务端的局域网 IPv4 地址及端口 `8080`，测试连接后开启代理。不再使用时点击“关闭并恢复”。更多细节见 [Ubuntu 客户端说明](HeysocksLanProxy.Client.Ubuntu/README-ubuntu24.04.md)。

## 工作方式与范围

- Windows 服务端运行在安装并已连接 Heysocks 的电脑上，提供局域网 HTTP/HTTPS 代理，并检查 Heysocks 运行状态、`VirtualNet` 接口和默认出口，避免 Heysocks 断开时继续建立新代理连接。
- Windows 和 Ubuntu 客户端配置各自桌面用户的 HTTP/HTTPS 系统代理，并可恢复开启前保存的代理设置。
- 这是系统 HTTP/HTTPS 代理扩展，不会接管 UDP，也无法强制不遵循系统代理的软件、游戏或沙盒应用使用代理。应用是否遵循系统代理取决于其自身行为和设置。
- 建议将服务器电脑的网络配置文件设为“专用网络”，并在路由器中为其保留固定 DHCP 地址。
- Heysocks 自身设置中的“代理地址”应保持为空，不要填写本工具的 `服务器IP:8080`。
- Windows 程序尚未进行商业代码签名，SmartScreen 可能显示未知发布者提示。

## 源码构建

Windows 部分需要 .NET 8 SDK：

```powershell
dotnet build HeysocksLanProxy.slnx -c Release
dotnet publish HeysocksLanProxy.Server\HeysocksLanProxy.Server.csproj -c Release -r win-x64 --self-contained true
dotnet publish HeysocksLanProxy.Client\HeysocksLanProxy.Client.csproj -c Release -r win-x64 --self-contained true
```

Ubuntu 客户端源码与 Debian 打包说明位于 `HeysocksLanProxy.Client.Ubuntu`。
