import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


SOURCE = Path(__file__).resolve().parents[1] / "heysocks_client.py"
SPEC = importlib.util.spec_from_file_location("heysocks_client", SOURCE)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class EndpointTests(unittest.TestCase):
    def test_valid_lan_ipv4(self):
        self.assertEqual(MODULE.validate_endpoint("192.168.10.5", 8080), "192.168.10.5")

    def test_rejects_loopback(self):
        with self.assertRaises(ValueError):
            MODULE.validate_endpoint("127.0.0.1", 8080)

    def test_rejects_ipv6(self):
        with self.assertRaises(ValueError):
            MODULE.validate_endpoint("2001:db8::1", 8080)

    def test_rejects_invalid_port(self):
        with self.assertRaises(ValueError):
            MODULE.validate_endpoint("192.168.10.5", 80)


class ProbeResponseTests(unittest.TestCase):
    def test_accepts_2xx(self):
        self.assertTrue(MODULE._is_successful_connect_response(b"HTTP/1.1 200 OK\r\n\r\n"))

    def test_rejects_non_2xx_and_malformed(self):
        self.assertFalse(MODULE._is_successful_connect_response(b"HTTP/1.1 407 Proxy Auth Required\r\n"))
        self.assertFalse(MODULE._is_successful_connect_response(b"nonsense"))


class ConfigStoreTests(unittest.TestCase):
    def test_round_trip(self):
        with tempfile.TemporaryDirectory() as directory:
            store = MODULE.ConfigStore(Path(directory))
            config = MODULE.ClientConfig("192.168.1.2", 8080, True, {"a": "'b'"})
            store.save(config)
            self.assertEqual(store.load(), config)


class GnomeProxyManagerTests(unittest.TestCase):
    def test_enable_and_restore(self):
        commands = []

        def runner(arguments):
            command = tuple(arguments)
            commands.append(command)
            if command[0] == "get":
                return "'none'" if command[-1] == "mode" else "''"
            return ""

        manager = MODULE.GnomeProxyManager(runner=runner)
        snapshot = manager.capture()
        manager.enable("192.168.1.2", 8080)
        manager.restore(snapshot)

        self.assertEqual(len(snapshot), len(manager.KEYS))
        self.assertIn(
            ("set", "org.gnome.system.proxy.http", "host", "'192.168.1.2'"),
            commands,
        )
        self.assertEqual(
            commands[-1],
            ("set", "org.gnome.system.proxy", "mode", "'none'"),
        )

    def test_refuses_incomplete_snapshot(self):
        manager = MODULE.GnomeProxyManager(runner=lambda _arguments: "")
        with self.assertRaises(MODULE.ProxySettingsError):
            manager.restore({})


if __name__ == "__main__":
    unittest.main()
