#!/usr/bin/python3
"""Heysocks LAN proxy client for Ubuntu 24.04 LTS (GNOME)."""

from __future__ import annotations

import argparse
import ipaddress
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Callable, Sequence


APP_ID = "com.heysocks.LanProxyClient"
APP_NAME = "Heysocks 局域网客户端"
VERSION = "1.3.0"
DEFAULT_PORT = 8080
PROBE_HOST = "www.example.com"


@dataclass
class ClientConfig:
    server_address: str = ""
    server_port: int = DEFAULT_PORT
    managed_proxy_active: bool = False
    original_proxy: dict[str, str] | None = field(default=None)


class ConfigStore:
    def __init__(self, root: Path | None = None) -> None:
        if root is None:
            config_home = os.environ.get("XDG_CONFIG_HOME")
            root = Path(config_home) if config_home else Path.home() / ".config"
        self.directory = root / "heysocks-lan-proxy" / "client"
        self.path = self.directory / "settings.json"

    def load(self) -> ClientConfig:
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
            return ClientConfig(
                server_address=str(data.get("server_address", "")),
                server_port=int(data.get("server_port", DEFAULT_PORT)),
                managed_proxy_active=bool(data.get("managed_proxy_active", False)),
                original_proxy=data.get("original_proxy"),
            )
        except (OSError, ValueError, TypeError, json.JSONDecodeError):
            return ClientConfig()

    def save(self, config: ClientConfig) -> None:
        self.directory.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_suffix(".json.tmp")
        temporary.write_text(
            json.dumps(asdict(config), ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        os.replace(temporary, self.path)


class ProxySettingsError(RuntimeError):
    pass


class GnomeProxyManager:
    """Capture, set and restore the current user's GNOME proxy settings."""

    KEYS: tuple[tuple[str, str], ...] = (
        ("org.gnome.system.proxy", "mode"),
        ("org.gnome.system.proxy.http", "host"),
        ("org.gnome.system.proxy.http", "port"),
        ("org.gnome.system.proxy.https", "host"),
        ("org.gnome.system.proxy.https", "port"),
    )

    def __init__(
        self,
        runner: Callable[[Sequence[str]], str] | None = None,
        executable: str | None = None,
    ) -> None:
        self.executable = executable or shutil.which("gsettings") or "gsettings"
        self.runner = runner or self._run

    def _run(self, arguments: Sequence[str]) -> str:
        try:
            completed = subprocess.run(
                [self.executable, *arguments],
                check=True,
                capture_output=True,
                text=True,
                timeout=8,
            )
            return completed.stdout.strip()
        except FileNotFoundError as error:
            raise ProxySettingsError(
                "未找到 gsettings。请确认正在使用 Ubuntu 24.04 桌面版。"
            ) from error
        except subprocess.TimeoutExpired as error:
            raise ProxySettingsError("读取 GNOME 系统代理设置超时。") from error
        except subprocess.CalledProcessError as error:
            detail = (error.stderr or error.stdout or str(error)).strip()
            raise ProxySettingsError(f"无法修改 GNOME 系统代理：{detail}") from error

    @staticmethod
    def _snapshot_key(schema: str, key: str) -> str:
        return f"{schema}|{key}"

    @staticmethod
    def _quote(value: str) -> str:
        return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"

    def capture(self) -> dict[str, str]:
        snapshot: dict[str, str] = {}
        for schema, key in self.KEYS:
            snapshot[self._snapshot_key(schema, key)] = self.runner(
                ("get", schema, key)
            )
        return snapshot

    def enable(self, server_address: str, server_port: int) -> None:
        values = (
            ("org.gnome.system.proxy.http", "host", self._quote(server_address)),
            ("org.gnome.system.proxy.http", "port", str(server_port)),
            ("org.gnome.system.proxy.https", "host", self._quote(server_address)),
            ("org.gnome.system.proxy.https", "port", str(server_port)),
            ("org.gnome.system.proxy", "mode", "'manual'"),
        )
        for schema, key, value in values:
            self.runner(("set", schema, key, value))

    def restore(self, snapshot: dict[str, str]) -> None:
        missing = [
            self._snapshot_key(schema, key)
            for schema, key in self.KEYS
            if self._snapshot_key(schema, key) not in snapshot
        ]
        if missing:
            raise ProxySettingsError("原代理设置备份不完整，已停止恢复以避免覆盖系统配置。")

        # Restore detailed values first and mode last, avoiding partial state.
        mode_value = snapshot[self._snapshot_key("org.gnome.system.proxy", "mode")]
        for schema, key in self.KEYS:
            if schema == "org.gnome.system.proxy" and key == "mode":
                continue
            self.runner(("set", schema, key, snapshot[self._snapshot_key(schema, key)]))
        self.runner(("set", "org.gnome.system.proxy", "mode", mode_value))


def validate_endpoint(address_text: str, port: int) -> str:
    try:
        address = ipaddress.ip_address(address_text.strip())
    except ValueError as error:
        raise ValueError("请输入有效的服务器 IPv4 地址，例如 192.168.124.150。") from error
    if address.version != 4 or address.is_loopback or address.is_unspecified:
        raise ValueError("请输入局域网服务器的 IPv4 地址，不能使用本机或 0.0.0.0。")
    if not 1024 <= port <= 65535:
        raise ValueError("服务器端口必须在 1024 到 65535 之间。")
    return str(address)


def _is_successful_connect_response(response: bytes) -> bool:
    try:
        first_line = response.splitlines()[0].decode("ascii", errors="strict")
        parts = first_line.split()
        return (
            len(parts) >= 2
            and parts[0].upper().startswith("HTTP/")
            and 200 <= int(parts[1]) < 300
        )
    except (IndexError, ValueError, UnicodeDecodeError):
        return False


def probe_proxy(address: str, port: int, timeout: float = 8.0) -> bool:
    request = (
        f"CONNECT {PROBE_HOST}:443 HTTP/1.1\r\n"
        f"Host: {PROBE_HOST}:443\r\n"
        "Proxy-Connection: close\r\n\r\n"
    ).encode("ascii")
    try:
        with socket.create_connection((address, port), timeout=timeout) as connection:
            connection.settimeout(timeout)
            connection.sendall(request)
            response = connection.recv(512)
        return _is_successful_connect_response(response)
    except (OSError, TimeoutError):
        return False


def run_self_test() -> int:
    checks: list[tuple[str, bool]] = []
    checks.append(("valid IPv4 endpoint", validate_endpoint("192.168.1.10", 8080) == "192.168.1.10"))
    try:
        validate_endpoint("127.0.0.1", 8080)
        checks.append(("reject loopback", False))
    except ValueError:
        checks.append(("reject loopback", True))
    checks.append(("accept CONNECT 200", _is_successful_connect_response(b"HTTP/1.1 200 OK\r\n\r\n")))
    checks.append(("reject CONNECT 403", not _is_successful_connect_response(b"HTTP/1.1 403 Forbidden\r\n\r\n")))

    with tempfile.TemporaryDirectory() as temporary:
        store = ConfigStore(Path(temporary))
        expected = ClientConfig("192.168.1.20", 8080, True, {"sample": "'value'"})
        store.save(expected)
        checks.append(("configuration round-trip", store.load() == expected))

    commands: list[tuple[str, ...]] = []

    def fake_runner(arguments: Sequence[str]) -> str:
        command = tuple(arguments)
        commands.append(command)
        if command[0] == "get":
            return "'none'" if command[-1] == "mode" else "''"
        return ""

    manager = GnomeProxyManager(runner=fake_runner)
    snapshot = manager.capture()
    manager.enable("192.168.1.20", 8080)
    manager.restore(snapshot)
    checks.append(("capture all GNOME keys", len(snapshot) == len(manager.KEYS)))
    checks.append(("restore mode last", commands[-1] == ("set", "org.gnome.system.proxy", "mode", "'none'")))

    for name, passed in checks:
        print(f"[{'PASS' if passed else 'FAIL'}] {name}")
    passed_count = sum(passed for _, passed in checks)
    print(f"Self-test: {passed_count}/{len(checks)} passed")
    return 0 if passed_count == len(checks) else 1


def run_gui() -> int:
    try:
        import gi

        gi.require_version("Gtk", "4.0")
        from gi.repository import GLib, Gtk
    except (ImportError, ValueError) as error:
        print(
            "无法启动图形界面。请安装依赖：sudo apt install python3-gi gir1.2-gtk-4.0",
            file=sys.stderr,
        )
        print(str(error), file=sys.stderr)
        return 2

    class ClientApplication(Gtk.Application):
        def __init__(self) -> None:
            super().__init__(application_id=APP_ID)
            self.store = ConfigStore()
            self.config = self.store.load()
            self.proxy_manager = GnomeProxyManager()
            self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="proxy-client")
            self.window = None
            self.address_entry = None
            self.port_spin = None
            self.status_label = None
            self.buttons = []

        def do_activate(self) -> None:
            if self.window is not None:
                self.window.present()
                return

            self.window = Gtk.ApplicationWindow(application=self)
            self.window.set_title(APP_NAME)
            self.window.set_default_size(620, 440)
            self.window.set_resizable(False)

            provider = Gtk.CssProvider()
            provider.load_from_string(
                ".success { color: #14824b; font-weight: 700; }"
                ".warning { color: #aa5f00; font-weight: 700; }"
            )
            Gtk.StyleContext.add_provider_for_display(
                self.window.get_display(),
                provider,
                Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION,
            )

            header = Gtk.HeaderBar()
            header.set_title_widget(Gtk.Label(label=APP_NAME))
            self.window.set_titlebar(header)

            root = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16)
            root.set_margin_top(28)
            root.set_margin_bottom(28)
            root.set_margin_start(32)
            root.set_margin_end(32)

            title = Gtk.Label(label="连接到局域网 Heysocks 代理服务器")
            title.set_xalign(0)
            title.add_css_class("title-2")
            root.append(title)

            subtitle = Gtk.Label(
                label="保存服务器地址，一键开启或恢复 Ubuntu 系统代理。"
            )
            subtitle.set_xalign(0)
            subtitle.add_css_class("dim-label")
            root.append(subtitle)

            grid = Gtk.Grid(column_spacing=14, row_spacing=14)
            grid.set_margin_top(10)
            address_label = Gtk.Label(label="服务器 IP")
            address_label.set_xalign(0)
            grid.attach(address_label, 0, 0, 1, 1)
            self.address_entry = Gtk.Entry()
            self.address_entry.set_placeholder_text("例如 192.168.124.150")
            self.address_entry.set_hexpand(True)
            self.address_entry.set_text(self.config.server_address)
            grid.attach(self.address_entry, 1, 0, 2, 1)

            port_label = Gtk.Label(label="服务器端口")
            port_label.set_xalign(0)
            grid.attach(port_label, 0, 1, 1, 1)
            self.port_spin = Gtk.SpinButton.new_with_range(1024, 65535, 1)
            self.port_spin.set_value(max(1024, min(65535, self.config.server_port)))
            grid.attach(self.port_spin, 1, 1, 1, 1)

            save_button = Gtk.Button(label="保存地址")
            save_button.connect("clicked", self.on_save)
            grid.attach(save_button, 2, 1, 1, 1)
            root.append(grid)

            self.status_label = Gtk.Label()
            self.status_label.set_xalign(0)
            self.status_label.set_wrap(True)
            self.status_label.set_margin_top(12)
            root.append(self.status_label)

            actions = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=12)
            actions.set_homogeneous(True)
            test_button = Gtk.Button(label="测试连接")
            enable_button = Gtk.Button(label="开启代理")
            enable_button.add_css_class("suggested-action")
            disable_button = Gtk.Button(label="关闭并恢复")
            disable_button.add_css_class("destructive-action")
            test_button.connect("clicked", self.on_test)
            enable_button.connect("clicked", self.on_enable)
            disable_button.connect("clicked", self.on_disable)
            self.buttons = [save_button, test_button, enable_button, disable_button]
            for button in (test_button, enable_button, disable_button):
                actions.append(button)
            root.append(actions)

            note = Gtk.Label(
                label="说明：关闭窗口不会自动改变代理；需要恢复时请先点击“关闭并恢复”。"
            )
            note.set_xalign(0)
            note.set_wrap(True)
            note.add_css_class("dim-label")
            root.append(note)

            self.window.set_child(root)
            self.show_initial_status()
            self.window.present()

        def show_initial_status(self) -> None:
            if self.config.managed_proxy_active and self.config.original_proxy:
                self.set_status("代理已由本客户端开启，可点击“关闭并恢复”。", True)
            else:
                self.set_status("代理当前未由本客户端管理。", False)

        def set_status(self, text: str, success: bool) -> None:
            self.status_label.set_text(text)
            self.status_label.remove_css_class("success")
            self.status_label.remove_css_class("warning")
            self.status_label.add_css_class("success" if success else "warning")

        def set_busy(self, busy: bool) -> None:
            for button in self.buttons:
                button.set_sensitive(not busy)

        def save_address(self) -> tuple[str, int] | None:
            try:
                port = int(self.port_spin.get_value())
                address = validate_endpoint(self.address_entry.get_text(), port)
                self.config.server_address = address
                self.config.server_port = port
                self.store.save(self.config)
                return address, port
            except (ValueError, OSError) as error:
                self.set_status(str(error), False)
                return None

        def on_save(self, _button) -> None:
            endpoint = self.save_address()
            if endpoint:
                self.set_status(f"已保存 {endpoint[0]}:{endpoint[1]}", True)

        def submit(self, operation: Callable[[], tuple[bool, str]]) -> None:
            self.set_busy(True)
            future = self.executor.submit(operation)

            def completed(done_future) -> None:
                try:
                    result = done_future.result()
                except Exception as error:
                    result = (False, str(error))
                GLib.idle_add(self.finish_operation, result)

            future.add_done_callback(completed)

        def finish_operation(self, result: tuple[bool, str]) -> bool:
            self.set_busy(False)
            self.set_status(result[1], result[0])
            return GLib.SOURCE_REMOVE

        def on_test(self, _button) -> None:
            endpoint = self.save_address()
            if not endpoint:
                return
            self.set_status("正在通过服务器验证代理连接…", False)
            address, port = endpoint
            self.submit(
                lambda: (
                    (True, "连接成功，服务器代理可用")
                    if probe_proxy(address, port)
                    else (False, "连接失败：请检查服务器、Heysocks 安全出口和防火墙")
                )
            )

        def on_enable(self, _button) -> None:
            endpoint = self.save_address()
            if not endpoint:
                return
            address, port = endpoint
            self.set_status("正在验证连接并开启系统代理…", False)

            def enable_operation() -> tuple[bool, str]:
                if not probe_proxy(address, port):
                    return False, "连接失败，未修改 Ubuntu 系统代理。"
                captured_now = False
                if not self.config.managed_proxy_active:
                    self.config.original_proxy = self.proxy_manager.capture()
                    # Mark the transaction before changing GNOME. If power is
                    # lost now, the next launch still offers a safe restore.
                    self.config.managed_proxy_active = True
                    self.store.save(self.config)
                    captured_now = True
                elif not self.config.original_proxy:
                    raise ProxySettingsError(
                        "代理状态缺少原配置备份，已停止修改以避免无法恢复。"
                    )
                try:
                    self.proxy_manager.enable(address, port)
                except Exception as enable_error:
                    if captured_now and self.config.original_proxy:
                        try:
                            self.proxy_manager.restore(self.config.original_proxy)
                        except Exception as restore_error:
                            raise ProxySettingsError(
                                f"开启代理失败且自动恢复失败：{restore_error}。"
                                "原配置备份已保留，请重新打开客户端并点击“关闭并恢复”。"
                            ) from enable_error
                        self.config.managed_proxy_active = False
                        self.config.original_proxy = None
                        self.store.save(self.config)
                    raise enable_error
                self.config.managed_proxy_active = True
                self.store.save(self.config)
                return True, f"代理已开启：{address}:{port}"

            self.submit(enable_operation)

        def on_disable(self, _button) -> None:
            if not self.config.managed_proxy_active or not self.config.original_proxy:
                self.set_status("本客户端没有可恢复的原始代理设置。", False)
                return
            self.set_status("正在恢复原来的 Ubuntu 系统代理…", False)

            def disable_operation() -> tuple[bool, str]:
                self.proxy_manager.restore(self.config.original_proxy or {})
                self.config.managed_proxy_active = False
                self.config.original_proxy = None
                self.store.save(self.config)
                return True, "已关闭本客户端设置的代理，并恢复原来的 Ubuntu 配置。"

            self.submit(disable_operation)

        def do_shutdown(self) -> None:
            self.executor.shutdown(wait=False, cancel_futures=True)
            Gtk.Application.do_shutdown(self)

    return ClientApplication().run([])


def main(arguments: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument("--self-test", action="store_true", help="运行无图形自检")
    parser.add_argument("--version", action="store_true", help="显示版本")
    options = parser.parse_args(arguments)
    if options.version:
        print(VERSION)
        return 0
    if options.self_test:
        return run_self_test()
    return run_gui()


if __name__ == "__main__":
    raise SystemExit(main())
