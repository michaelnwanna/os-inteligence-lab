"""Signal collectors. Standard library only; one implementation per operating system.

Every signal is documented in SIGNALS. Nothing outside this list is ever collected.
"""

from __future__ import annotations

import os
import platform
import re
import subprocess
import sys
import time

SIGNALS: dict[str, str] = {
    "session_heartbeat": "One 'session' event per interval so failure-rate denominators exist. No content.",
    "cpu_load_pct": "System CPU load, 0-100 (load average / cores on Linux and macOS, CPU time deltas on Windows).",
    "memory_mb": "System memory in use, in MB (used = total minus available).",
}


def parse_meminfo(text: str) -> tuple[float, float]:
    """Linux /proc/meminfo -> (used_mb, total_mb)."""
    kb = {m.group(1): int(m.group(2)) for m in re.finditer(r"^(\w+):\s+(\d+)\s*kB", text, re.M)}
    total = kb["MemTotal"]
    available = kb.get("MemAvailable", kb.get("MemFree", 0))
    return (total - available) / 1024, total / 1024


def parse_vm_stat(text: str, mem_bytes: int) -> tuple[float, float]:
    """macOS `vm_stat` output plus `hw.memsize` -> (used_mb, total_mb)."""
    page = int(re.search(r"page size of (\d+) bytes", text).group(1))
    pages = {k: int(v) for k, v in re.findall(r"^Pages ([\w \-]+):\s+(\d+)\.", text, re.M)}
    used = pages.get("active", 0) + pages.get("wired down", 0) + pages.get("occupied by compressor", 0)
    return used * page / 1048576, mem_bytes / 1048576


def _windows_memory() -> tuple[float, float]:
    import ctypes

    class Status(ctypes.Structure):
        _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong), ("total", ctypes.c_ulonglong),
                    ("avail", ctypes.c_ulonglong), ("tpage", ctypes.c_ulonglong), ("apage", ctypes.c_ulonglong),
                    ("tvirt", ctypes.c_ulonglong), ("avirt", ctypes.c_ulonglong), ("ext", ctypes.c_ulonglong)]

    s = Status()
    s.length = ctypes.sizeof(Status)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(s))
    return (s.total - s.avail) / 1048576, s.total / 1048576


def memory_mb() -> tuple[float, float]:
    """(used_mb, total_mb) for the current machine."""
    if sys.platform.startswith("linux"):
        with open("/proc/meminfo") as fh:
            return parse_meminfo(fh.read())
    if sys.platform == "darwin":
        run = lambda *cmd: subprocess.run(cmd, capture_output=True, text=True, timeout=5, check=True).stdout  # noqa: E731
        return parse_vm_stat(run("vm_stat"), int(run("sysctl", "-n", "hw.memsize")))
    if sys.platform == "win32":
        return _windows_memory()
    raise RuntimeError(f"unsupported platform: {sys.platform}")


def _windows_times() -> tuple[int, int]:
    import ctypes
    from ctypes import wintypes

    idle, kernel, user = wintypes.FILETIME(), wintypes.FILETIME(), wintypes.FILETIME()
    ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user))
    ticks = lambda f: (f.dwHighDateTime << 32) | f.dwLowDateTime  # noqa: E731
    return ticks(idle), ticks(kernel) + ticks(user)


def cpu_load_pct(sample_seconds: float = 0.5) -> float:
    if sys.platform == "win32":
        i0, t0 = _windows_times()
        time.sleep(sample_seconds)
        i1, t1 = _windows_times()
        return max(0.0, min(100.0, 100.0 * (1 - (i1 - i0) / max(t1 - t0, 1))))
    return max(0.0, min(100.0, os.getloadavg()[0] / (os.cpu_count() or 1) * 100))


def system_info() -> dict:
    """Coarse, non-identifying machine description. No hostname, serial or user name."""
    machine = platform.machine().lower()
    arch = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine, machine)
    name = {"Darwin": "macOS", "Windows": "Windows", "Linux": "Linux"}.get(platform.system(), platform.system())
    version = platform.mac_ver()[0] if name == "macOS" else platform.release()
    _, total = memory_mb()
    return {"os": name, "os_version": version or "unknown", "architecture": arch,
            "device_model": f"generic-{arch}", "cpu": f"{arch}-{os.cpu_count() or 1}c",
            "memory_gb": float(round(total / 1024))}


def collect(signal: str) -> float | None:
    if signal == "cpu_load_pct":
        return cpu_load_pct()
    if signal == "memory_mb":
        return memory_mb()[0]
    return None
