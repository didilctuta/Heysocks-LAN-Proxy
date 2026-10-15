# Heysocks 局域网代理

Heysocks 局域网代理是 **Heysocks Windows 桌面 VPN 客户端的配套扩展工具**。它利用 Heysocks 提供的 VirtualNet 隧道，在局域网内共享 HTTP/HTTPS 代理，让其他设备的受支持应用通过运行 Heysocks 的 Windows 电脑访问网络。

本项目不是 Heysocks 官方客户端，也不替代 Heysocks。服务器端依赖已安装并连接节点的 Heysocks Windows 客户端及其 VirtualNet 虚拟网络接口。项目提供 Windows x64 服务端和客户端，以及 Ubuntu 24.04 LTS GNOME 客户端。

## 发布包

| 发布包 | 包含内容 | 适用场景 |
| --- | --- | --- |
| Heysocks局域网代理-v1.3-win-x64 | Windows x64 服务端和客户端 | 在运行 Heysocks 的 Windows 电脑上启动服务端；其他设备连接 |
| release-ubuntu24.04 | Ubuntu 24.04 LTS 客户端 .deb | Ubuntu 设备连接 Windows 服务端 |

Ubuntu 包**只有客户端**，不包含服务端。使用前必须在同一局域网准备一台已连接 Heysocks 的 Windows 服务器，并在 Ubuntu 客户端填写该服务器的局域网 IP 和服务端口（默认 8080）。

## 使用方式

1. 在已安装 Heysocks 并成功连接节点的 Windows 电脑上启动 Server/Heysocks局域网服务器.exe。程序会请求管理员权限。
2. 等待服务器显示代理可用，记下局域网 IPv4 地址。服务端默认监听 8080。
3. 在 Windows 客户端运行 Client/Heysocks局域网客户端.exe，或在 Ubuntu 24.04 安装 .deb；填写服务器 IP 和端口 8080，测试连接后开启代理。
4. 使用完毕后点击“关闭并恢复”，恢复开启代理前的系统代理设置。

Ubuntu 安装命令：

```bash
sudo apt install ./heysocks-lan-proxy-client_1.3.0_all.deb
```

Ubuntu 客户端以当前桌面用户启动，不要使用 sudo 启动。

## 工作方式与范围

- Windows 服务端运行在安装并已连接 Heysocks 的电脑上，提供局域网 HTTP/HTTPS 代理，并检查 Heysocks 运行状态、VirtualNet 接口和默认出口。
- Windows 和 Ubuntu 客户端配置各自桌面用户的 HTTP/HTTPS 系统代理，并可恢复原设置。
- 不接管 UDP，也无法强制忽略系统代理的软件、游戏或沙盒应用使用代理。
- 建议服务器网络设为“专用网络”，并为其保留固定 DHCP 地址。
- Heysocks 自身的“代理地址”保持为空，不要填写本工具的“服务器IP:8080”。
- Windows 程序尚未商业代码签名，SmartScreen 可能显示未知发布者提示。

## 源码

Windows 部分需要 .NET 8 SDK。Ubuntu 客户端源码与 Debian 打包说明位于 HeysocksLanProxy.Client.Ubuntu。完整发布说明见 GITHUB_RELEASE_NOTES.md。
