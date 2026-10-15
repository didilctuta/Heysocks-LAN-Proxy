# GitHub 发布说明（准备稿）

## 项目定位

Heysocks 局域网代理是面向 **Heysocks Windows 桌面 VPN 客户端** 的配套扩展工具。它借助 Heysocks 的 `VirtualNet` 隧道，将已连接 Heysocks 的 Windows 电脑作为局域网 HTTP/HTTPS 代理服务器，让局域网内支持系统代理的设备共享该出口。

该项目不是 Heysocks 官方软件，也不替代 Heysocks 客户端。服务端必须运行在安装并已连接 Heysocks、且提供 `VirtualNet` 虚拟网络接口的 Windows 电脑上。

## 本次发布文件

### `Heysocks局域网代理-v1.3-win-x64`

Windows x64 完整包，同时包含服务端和客户端：

- `Server/Heysocks局域网服务器.exe`：部署在已连接 Heysocks 的 Windows 电脑上，默认提供 `8080` 端口 HTTP/HTTPS 代理。
- `Client/Heysocks局域网客户端.exe`：部署在局域网内需要使用代理的 Windows 电脑上，设置并恢复当前用户的系统代理。
- `使用说明.md`：Windows 使用说明。

### `release-ubuntu24.04`

Ubuntu 24.04 LTS GNOME **客户端包**，包含 `heysocks-lan-proxy-client_1.3.0_all.deb`。该包不含服务端，必须连接到运行上述 Windows 服务端的电脑，并在客户端填写其局域网 IP 与服务端口（默认 `8080`）。

Ubuntu 安装和操作方法见 `HeysocksLanProxy.Client.Ubuntu/README-ubuntu24.04.md`。

## 使用限制

- 仅提供 HTTP/HTTPS 系统代理，不处理 UDP 流量。
- 不遵循系统代理的软件不会自动经过代理；浏览器、游戏及沙盒应用的代理行为由应用自身决定。
- Ubuntu 客户端针对 Ubuntu 24.04 LTS 默认 GNOME 桌面环境。
- Windows 可执行文件尚未进行商业代码签名，首次运行时 SmartScreen 可能提示未知发布者。

## 建议的 GitHub Release 附件

将发布文件夹打包为独立附件上传：

1. `Heysocks局域网代理-v1.3-win-x64.zip`：内含 Windows 服务端、客户端及说明文件。
2. `heysocks-lan-proxy-client_1.3.0_all.deb`：Ubuntu 24.04 客户端安装包。
3. `Heysocks-LAN-Proxy-source-v1.3.0.zip`：Windows 与 Ubuntu 客户端源码及构建文件。

上传前应确认 Windows 压缩包来自 `Heysocks局域网代理-v1.3-win-x64` 目录，并确认 Ubuntu `.deb` 来自 `release-ubuntu24.04`。建议在 GitHub Release 正文中保留上述项目定位、包内容差异和 Ubuntu 需连接 Windows 服务端的说明。
