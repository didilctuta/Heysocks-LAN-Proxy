#!/usr/bin/env python3
"""Build a reproducible Debian package without requiring dpkg-deb."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import os
import tarfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent
WORKSPACE = ROOT.parent
DEFAULT_OUTPUT = WORKSPACE / "release-ubuntu24.04" / "heysocks-lan-proxy-client_1.3.0_all.deb"
MTIME = 1_735_689_600


def add_bytes(archive: tarfile.TarFile, name: str, data: bytes, mode: int) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = mode
    info.uid = info.gid = 0
    info.uname = info.gname = "root"
    info.mtime = MTIME
    archive.addfile(info, io.BytesIO(data))


def make_tar_gz(files: list[tuple[str, Path, int]]) -> bytes:
    raw = io.BytesIO()
    with gzip.GzipFile(fileobj=raw, mode="wb", mtime=MTIME, filename="") as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.GNU_FORMAT) as archive:
            directories = set()
            for destination, _source, _mode in files:
                parent = Path(destination).parent
                while str(parent) not in (".", ""):
                    directories.add(parent.as_posix())
                    parent = parent.parent
            for directory in sorted(directories, key=lambda item: (item.count("/"), item)):
                info = tarfile.TarInfo(f"./{directory}/")
                info.type = tarfile.DIRTYPE
                info.mode = 0o755
                info.uid = info.gid = 0
                info.uname = info.gname = "root"
                info.mtime = MTIME
                archive.addfile(info)
            for destination, source, mode in sorted(files):
                add_bytes(archive, f"./{destination}", source.read_bytes(), mode)
    return raw.getvalue()


def ar_member(name: str, data: bytes) -> bytes:
    encoded_name = (name + "/").encode("ascii")
    if len(encoded_name) > 16:
        raise ValueError(f"ar member name is too long: {name}")
    header = (
        encoded_name.ljust(16, b" ")
        + str(MTIME).encode("ascii").ljust(12, b" ")
        + b"0".ljust(6, b" ")
        + b"0".ljust(6, b" ")
        + b"100644".ljust(8, b" ")
        + str(len(data)).encode("ascii").ljust(10, b" ")
        + b"`\n"
    )
    return header + data + (b"\n" if len(data) % 2 else b"")


def build(output: Path) -> str:
    required = [
        ROOT / "heysocks_client.py",
        ROOT / "packaging" / "control",
        ROOT / "packaging" / "heysocks-lan-proxy-client",
        ROOT / "packaging" / "com.heysocks.LanProxyClient.desktop",
        ROOT / "packaging" / "copyright",
        ROOT / "README-ubuntu24.04.md",
        WORKSPACE / "assets" / "heysocks-lan-proxy.png",
    ]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing package inputs: " + ", ".join(missing))

    control_tar = make_tar_gz([("control", ROOT / "packaging" / "control", 0o644)])
    data_tar = make_tar_gz(
        [
            ("usr/bin/heysocks-lan-proxy-client", ROOT / "packaging" / "heysocks-lan-proxy-client", 0o755),
            ("usr/lib/heysocks-lan-proxy-client/heysocks_client.py", ROOT / "heysocks_client.py", 0o644),
            ("usr/share/applications/com.heysocks.LanProxyClient.desktop", ROOT / "packaging" / "com.heysocks.LanProxyClient.desktop", 0o644),
            ("usr/share/doc/heysocks-lan-proxy-client/README.md", ROOT / "README-ubuntu24.04.md", 0o644),
            ("usr/share/doc/heysocks-lan-proxy-client/copyright", ROOT / "packaging" / "copyright", 0o644),
            ("usr/share/pixmaps/heysocks-lan-proxy.png", WORKSPACE / "assets" / "heysocks-lan-proxy.png", 0o644),
        ]
    )
    package = (
        b"!<arch>\n"
        + ar_member("debian-binary", b"2.0\n")
        + ar_member("control.tar.gz", control_tar)
        + ar_member("data.tar.gz", data_tar)
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_bytes(package)
    os.replace(temporary, output)
    return hashlib.sha256(package).hexdigest()


def read_ar(path: Path) -> dict[str, bytes]:
    data = path.read_bytes()
    if not data.startswith(b"!<arch>\n"):
        raise ValueError("Not a Debian ar archive")
    offset = 8
    members = {}
    while offset < len(data):
        header = data[offset : offset + 60]
        if len(header) != 60 or header[58:60] != b"`\n":
            raise ValueError("Invalid ar member header")
        name = header[:16].decode("ascii").strip().rstrip("/")
        size = int(header[48:58].decode("ascii").strip())
        start = offset + 60
        members[name] = data[start : start + size]
        offset = start + size + (size % 2)
    return members


def verify(path: Path) -> None:
    members = read_ar(path)
    if list(members) != ["debian-binary", "control.tar.gz", "data.tar.gz"]:
        raise ValueError(f"Unexpected Debian members: {list(members)}")
    if members["debian-binary"] != b"2.0\n":
        raise ValueError("Unsupported debian-binary version")
    with tarfile.open(fileobj=io.BytesIO(members["control.tar.gz"]), mode="r:gz") as archive:
        control = archive.extractfile("./control").read().decode("utf-8")
        if "Package: heysocks-lan-proxy-client" not in control or "Architecture: all" not in control:
            raise ValueError("Invalid control metadata")
    expected = {
        "./usr/bin/heysocks-lan-proxy-client",
        "./usr/lib/heysocks-lan-proxy-client/heysocks_client.py",
        "./usr/share/applications/com.heysocks.LanProxyClient.desktop",
        "./usr/share/doc/heysocks-lan-proxy-client/README.md",
        "./usr/share/doc/heysocks-lan-proxy-client/copyright",
        "./usr/share/pixmaps/heysocks-lan-proxy.png",
    }
    with tarfile.open(fileobj=io.BytesIO(members["data.tar.gz"]), mode="r:gz") as archive:
        entries = {item.name: item for item in archive.getmembers()}
        missing = expected.difference(entries)
        if missing:
            raise ValueError(f"Missing payload entries: {sorted(missing)}")
        if entries["./usr/bin/heysocks-lan-proxy-client"].mode != 0o755:
            raise ValueError("Launcher is not executable")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", type=Path)
    options = parser.parse_args()
    if options.verify_only:
        verify(options.verify_only.resolve())
        print(f"Verified: {options.verify_only.resolve()}")
        return 0
    output = options.output.resolve()
    checksum = build(output)
    verify(output)
    print(f"Built and verified: {output}")
    print(f"SHA256: {checksum}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
