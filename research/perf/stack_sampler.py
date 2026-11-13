"""A sampling profiler for the running game that needs no administrator rights and no symbols.

    python research/research/perf/stack_sampler.py --pid 1234 --out stacks.json [--interval 0.05] [--seconds 60]
    python research/research/perf/stack_sampler.py --wait-exe "<...>\\bin\\bg3_dx11.exe" --out stacks.json

Every `interval` seconds it looks at each thread of the process; a thread that used processor
time since the last look is suspended just long enough to copy its registers and the top of its stack, and resumed;
the call stacks are worked out in this process after sampling has stopped, from those copies
and the unwind tables of the modules (mapped here from their files; a module unloaded by then
is lost). Memory: about 20 KB per stack while sampling. Nothing is written into the game. Every tenth look all threads are walked, busy or not, so that
what the main threads wait on is seen too. Stops on Ctrl+Break, after --seconds, or when the
process ends. The result is one JSON file: modules (base, size, path), threads (id, name) and
samples (time, thread, processor seconds since the previous look, return addresses, and - for
a thread caught inside a file system call - the path it asked the system about).
`perf/stack_report.py` turns it into names and tables.

Cost: a sampled thread stands still for well under a millisecond per look; still, use the
result to see proportions, not to time a start.
"""
from __future__ import annotations

import argparse
import bisect
import ctypes
import json
import os
import signal
import sys
import time
from ctypes import wintypes
from pathlib import Path

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
dbghelp = ctypes.WinDLL("dbghelp", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

HANDLE = wintypes.HANDLE
kernel.OpenProcess.restype = HANDLE
kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.OpenThread.restype = HANDLE
kernel.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.CloseHandle.argtypes = [HANDLE]
kernel.GetCurrentProcess.restype = HANDLE
kernel.SetPriorityClass.argtypes = [HANDLE, wintypes.DWORD]
kernel.SuspendThread.argtypes = [HANDLE]
kernel.SuspendThread.restype = wintypes.DWORD
kernel.ResumeThread.argtypes = [HANDLE]
kernel.GetThreadContext.argtypes = [HANDLE, ctypes.c_void_p]
kernel.GetThreadTimes.argtypes = [HANDLE] + [ctypes.c_void_p] * 4
kernel.CreateToolhelp32Snapshot.restype = HANDLE
kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
kernel.GetExitCodeProcess.argtypes = [HANDLE, ctypes.POINTER(wintypes.DWORD)]
kernel.GetThreadDescription.argtypes = [HANDLE, ctypes.POINTER(ctypes.c_wchar_p)]
kernel.GetThreadDescription.restype = ctypes.c_long
kernel.QueryFullProcessImageNameW.argtypes = [HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
kernel.GetProcessTimes.argtypes = [HANDLE] + [ctypes.c_void_p] * 4
dbghelp.SymInitializeW.argtypes = [HANDLE, wintypes.LPCWSTR, wintypes.BOOL]
dbghelp.SymSetOptions.argtypes = [wintypes.DWORD]
dbghelp.SymRefreshModuleList.argtypes = [HANDLE]
dbghelp.SymCleanup.argtypes = [HANDLE]
dbghelp.StackWalk64.argtypes = [wintypes.DWORD, HANDLE, HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
dbghelp.StackWalk64.restype = wintypes.BOOL
dbghelp.SymFunctionTableAccess64.restype = ctypes.c_void_p
dbghelp.SymFunctionTableAccess64.argtypes = [HANDLE, ctypes.c_ulonglong]
dbghelp.SymGetModuleBase64.restype = ctypes.c_ulonglong
dbghelp.SymGetModuleBase64.argtypes = [HANDLE, ctypes.c_ulonglong]
psapi.EnumProcessModulesEx.argtypes = [HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]
psapi.GetModuleFileNameExW.argtypes = [HANDLE, HANDLE, wintypes.LPWSTR, wintypes.DWORD]


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p), ("SizeOfImage", wintypes.DWORD), ("EntryPoint", ctypes.c_void_p)]


psapi.GetModuleInformation.argtypes = [HANDLE, HANDLE, ctypes.POINTER(MODULEINFO), wintypes.DWORD]


class THREADENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ThreadID", wintypes.DWORD),
                ("th32OwnerProcessID", wintypes.DWORD), ("tpBasePri", wintypes.LONG), ("tpDeltaPri", wintypes.LONG),
                ("dwFlags", wintypes.DWORD)]


CONTEXT_SIZE = 1232
CONTEXT_FULL = 0x100000 | 0x1 | 0x2 | 0x8   # AMD64: control, integer, floating point
OFFSET_FLAGS, OFFSET_RSP, OFFSET_RBP, OFFSET_RIP = 0x30, 0x98, 0xA0, 0xF8
IMAGE_FILE_MACHINE_AMD64 = 0x8664
MAX_FRAMES = 64
STACK_COPY = 32 * 1024


def aligned_buffer(size: int, alignment: int = 16):
    raw = ctypes.create_string_buffer(size + alignment)
    address = ctypes.addressof(raw)
    shift = (-address) % alignment
    return raw, address + shift


def thread_ids(pid: int) -> list[int]:
    snapshot = kernel.CreateToolhelp32Snapshot(0x4, 0)  # TH32CS_SNAPTHREAD: every thread of the system
    entry = THREADENTRY32()
    entry.dwSize = ctypes.sizeof(entry)
    found = []
    more = kernel.Thread32First(snapshot, ctypes.byref(entry))
    while more:
        if entry.th32OwnerProcessID == pid:
            found.append(entry.th32ThreadID)
        more = kernel.Thread32Next(snapshot, ctypes.byref(entry))
    kernel.CloseHandle(snapshot)
    return found


def find_process(exe: str, timeout: float = 600) -> int:
    wanted = os.path.normcase(exe)
    ids = (wintypes.DWORD * 16384)()
    used = wintypes.DWORD()
    name = ctypes.create_unicode_buffer(1024)
    deadline = time.time() + timeout
    while time.time() < deadline:
        psapi.EnumProcesses(ids, ctypes.sizeof(ids), ctypes.byref(used))
        for pid in ids[:used.value // 4]:
            handle = kernel.OpenProcess(0x1000, False, pid)
            if not handle:
                continue
            size = wintypes.DWORD(len(name))
            ok = kernel.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size))
            kernel.CloseHandle(handle)
            if ok and os.path.normcase(name.value) == wanted:
                return pid
        time.sleep(0.02)
    raise SystemExit(f"no process {exe} appeared in {timeout:.0f} s")


def modules_of(process) -> list[dict]:
    handles = (HANDLE * 2048)()
    needed = wintypes.DWORD()
    if not psapi.EnumProcessModulesEx(process, handles, ctypes.sizeof(handles), ctypes.byref(needed), 3):
        return []
    result = []
    name = ctypes.create_unicode_buffer(1024)
    for module in handles[:needed.value // ctypes.sizeof(HANDLE)]:
        info = MODULEINFO()
        if psapi.GetModuleInformation(process, module, ctypes.byref(info), ctypes.sizeof(info)) \
                and psapi.GetModuleFileNameExW(process, module, name, len(name)):
            result.append({"base": info.lpBaseOfDll or 0, "size": info.SizeOfImage, "path": name.value})
    return result


def thread_cpu(handle) -> float:
    times = (ctypes.c_ulonglong * 4)()
    if not kernel.GetThreadTimes(handle, ctypes.byref(times, 0), ctypes.byref(times, 8),
                                 ctypes.byref(times, 16), ctypes.byref(times, 24)):
        return -1.0
    return (times[2] + times[3]) / 1e7


kernel.ReadProcessMemory.argtypes = [HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
kernel.LoadLibraryExW.restype = ctypes.c_void_p
kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, HANDLE, wintypes.DWORD]
ntdll = ctypes.WinDLL("ntdll")
ntdll.RtlVirtualUnwind.restype = ctypes.c_void_p
ntdll.RtlVirtualUnwind.argtypes = [wintypes.DWORD, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_void_p,
                                   ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]


class Module:
    """A module of the game, mapped here as an image so that its unwind tables can be read."""

    def __init__(self, base: int, size: int, path: str) -> None:
        self.base, self.size, self.path = base, size, path
        self.local = None      # address of the local mapping; False when it could not be mapped
        self.begins: list[int] = []
        self.ends: list[int] = []
        self.table = 0

    def prepare(self) -> bool:
        if self.local is None:
            self.local = False
            handle = kernel.LoadLibraryExW(self.path, None, 0x20 | 0x2)  # image resource + data file: no code runs
            if handle:
                local = handle & ~3
                try:
                    pe = ctypes.c_uint32.from_address(local + 0x3C).value
                    directory = local + pe + 24 + 112 + 3 * 8  # optional header (PE32+), data directory 3: exceptions
                    rva = ctypes.c_uint32.from_address(directory).value
                    length = ctypes.c_uint32.from_address(directory + 4).value
                    if rva and length:
                        raw = (ctypes.c_uint32 * (length // 4)).from_address(local + rva)
                        self.begins = list(raw[0::3])
                        self.ends = list(raw[1::3])
                        self.table = local + rva
                    self.local = local
                except OSError:
                    self.local = False
        return bool(self.local)

    def entry(self, rva: int) -> int:
        """Local address of the RUNTIME_FUNCTION covering this rva, or 0 (a leaf function)."""
        index = bisect.bisect_right(self.begins, rva) - 1
        if index >= 0 and rva < self.ends[index]:
            return self.table + index * 12
        return 0


class Unwinder:
    def __init__(self, process) -> None:
        self.process = process
        self.modules: list[Module] = []
        self.bases: list[int] = []
        self.context_raw, self.context = aligned_buffer(CONTEXT_SIZE)
        self.stack = ctypes.create_string_buffer(STACK_COPY)
        self.stack_address = ctypes.addressof(self.stack)
        self.scratch = (ctypes.c_uint64 * 4)()
        # File system calls whose path argument is worth keeping: name -> (start of the stub in
        # ntdll, which is at the same address in every process; register holding OBJECT_ATTRIBUTES*).
        kernel.GetModuleHandleW.restype = ctypes.c_void_p
        kernel.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
        kernel.GetProcAddress.restype = ctypes.c_void_p
        kernel.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        ntdll_base = kernel.GetModuleHandleW("ntdll.dll")
        self.path_calls = []
        for name, register in (("NtQueryAttributesFile", 10), ("NtQueryFullAttributesFile", 10),
                               ("NtOpenFile", 8), ("NtCreateFile", 8)):
            start = kernel.GetProcAddress(ntdll_base, name.encode())
            if start:
                self.path_calls.append((start, start + 0x20, 0x78 + 8 * register))
        self.small = ctypes.create_string_buffer(1024)

    def read(self, address: int, size: int) -> bytes | None:
        got = ctypes.c_size_t()
        if address and kernel.ReadProcessMemory(self.process, address, self.small, min(size, 1024), ctypes.byref(got)):
            return self.small.raw[:got.value]
        return None

    def path_argument(self) -> str | None:
        """For a thread stopped inside one of the file system calls: the path it asked about."""
        rip = ctypes.c_uint64.from_address(self.context + OFFSET_RIP).value
        for start, end, _ in self.path_calls:
            if start <= rip < end:
                # The caller built an OBJECT_ATTRIBUTES on its stack: Length 0x30, RootDirectory,
                # ObjectName -> UNICODE_STRING { Length, MaximumLength, Buffer }. Look for it in
                # the first kilobyte above the stack pointer (volatile registers are not kept
                # across a system call, so the argument itself cannot be read back).
                top = self.stack.raw[:1024]
                for at in range(0, len(top) - 24, 8):
                    if top[at:at + 8] == b"\x30\0\0\0\0\0\0\0":
                        name = self.read(int.from_bytes(top[at + 16:at + 24], "little"), 16)
                        if name and len(name) == 16:
                            length = int.from_bytes(name[0:2], "little")
                            if 8 <= length <= 1000 and length <= int.from_bytes(name[2:4], "little"):
                                text = self.read(int.from_bytes(name[8:16], "little"), length)
                                if text and text[:8] == "\\??\\".encode("utf-16-le"):
                                    return text.decode("utf-16-le", "replace")
        return None

    def refresh(self) -> None:
        known = {m.base for m in self.modules}
        for found in modules_of(self.process):
            if found["base"] not in known:
                self.modules.append(Module(found["base"], found["size"], found["path"]))
        self.modules.sort(key=lambda m: m.base)
        self.bases = [m.base for m in self.modules]

    def module_at(self, address: int) -> Module | None:
        index = bisect.bisect_right(self.bases, address) - 1
        if index >= 0:
            module = self.modules[index]
            if address < module.base + module.size:
                return module
        return None

    def capture(self, thread) -> tuple[bytes, int, bytes] | None:
        """Suspends the thread, copies its registers and the top of its stack, resumes it.
        Returns (registers, rsp, the copied stack)."""
        if kernel.SuspendThread(thread) == 0xFFFFFFFF:
            return None
        try:
            ctypes.c_uint32.from_address(self.context + OFFSET_FLAGS).value = CONTEXT_FULL
            if not kernel.GetThreadContext(thread, self.context):
                return None
            rsp = ctypes.c_uint64.from_address(self.context + OFFSET_RSP).value
            got = ctypes.c_size_t()
            for size in (STACK_COPY, 16384, 4096, 4096 - (rsp & 0xFFF)):
                if size > 0 and kernel.ReadProcessMemory(self.process, rsp, self.stack, size, ctypes.byref(got)):
                    break
            else:
                got.value = 0
            path = self.path_argument()
        finally:
            kernel.ResumeThread(thread)
        # Kept as bytes; the stack is worked out after sampling has stopped (see main).
        return ctypes.string_at(self.context, CONTEXT_SIZE), rsp, self.stack.raw[:got.value], path

    def walk_saved(self, saved_context: bytes, rsp: int, stack: bytes, path: str | None = None) -> list[int]:
        ctypes.memmove(self.context, saved_context, CONTEXT_SIZE)
        ctypes.memmove(self.stack_address, stack, len(stack))
        return self.walk(ctypes.c_uint64.from_address(self.context + OFFSET_RIP).value, rsp, len(stack))

    def walk(self, rip: int, rsp: int, copied: int) -> list[int]:
        context = self.context
        delta = self.stack_address - rsp
        low, high = self.stack_address, self.stack_address + copied
        ctypes.c_uint64.from_address(context + OFFSET_RSP).value = low
        addresses = [rip]
        try:
            for _ in range(MAX_FRAMES):
                local_rsp = ctypes.c_uint64.from_address(context + OFFSET_RSP).value
                if not (low <= local_rsp <= high - 8):
                    break
                module = self.module_at(rip)
                entry = module.entry(rip - module.base) if module and module.prepare() else 0
                if not entry:
                    if len(addresses) > 1 and not module:
                        break  # code outside every module, not at the top: nothing to go by
                    rip = ctypes.c_uint64.from_address(local_rsp).value  # a leaf: the return address is on top
                    ctypes.c_uint64.from_address(context + OFFSET_RSP).value = local_rsp + 8
                else:
                    info = module.local + ctypes.c_uint32.from_address(entry + 8).value
                    register = ctypes.c_uint8.from_address(info + 3).value & 0xF
                    if register:  # the function addresses its frame through a register: point it at the copy
                        slot = context + 0x78 + 8 * register
                        value = ctypes.c_uint64.from_address(slot).value
                        if rsp <= value < rsp + copied:
                            ctypes.c_uint64.from_address(slot).value = value + delta
                    ntdll.RtlVirtualUnwind(0, module.local, rip - module.base + module.local, entry, context,
                                           ctypes.byref(self.scratch, 0), ctypes.byref(self.scratch, 8), None)
                    rip = ctypes.c_uint64.from_address(context + OFFSET_RIP).value
                if rip < 0x10000:
                    break
                addresses.append(rip)
        except OSError:
            pass
        return addresses


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pid", type=int)
    parser.add_argument("--wait-exe", help="full path of the executable to wait for")
    parser.add_argument("--out", required=True)
    parser.add_argument("--interval", type=float, default=0.05)
    parser.add_argument("--seconds", type=float, default=900)
    parser.add_argument("--all-every", type=int, default=10, help="walk idle threads too on every Nth look")
    arguments = parser.parse_args()

    pid = arguments.pid or find_process(arguments.wait_exe)
    process = kernel.OpenProcess(0x0400 | 0x0010, False, pid)  # QUERY_INFORMATION | VM_READ
    if not process:
        raise SystemExit(f"cannot open process {pid}: error {ctypes.get_last_error()}")
    created = (ctypes.c_ulonglong * 4)()
    kernel.GetProcessTimes(process, ctypes.byref(created, 0), ctypes.byref(created, 8),
                           ctypes.byref(created, 16), ctypes.byref(created, 24))
    created_at = (created[0] - 116444736000000000) / 1e7

    kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x80)  # high: on a busy machine the sampler must not starve
    stopping = []
    signal.signal(signal.SIGBREAK, lambda *_: stopping.append(1))
    signal.signal(signal.SIGINT, lambda *_: stopping.append(1))

    unwinder = Unwinder(process)
    threads: dict[int, dict] = {}
    names: dict[int, str] = {}
    samples: list[list] = []
    spent = 0.0
    look = 0
    next_refresh = 0.0
    deadline = time.time() + arguments.seconds
    code = wintypes.DWORD()

    while not stopping and time.time() < deadline:
        tick = time.time()
        kernel.GetExitCodeProcess(process, ctypes.byref(code))
        if code.value != 259:
            break
        if tick >= next_refresh:
            unwinder.refresh()
            for tid in thread_ids(pid):
                if tid not in threads:
                    handle = kernel.OpenThread(0x0002 | 0x0008 | 0x0040, False, tid)  # SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
                    if handle:
                        threads[tid] = {"handle": handle, "cpu": thread_cpu(handle)}
            for tid, thread in threads.items():
                text = ctypes.c_wchar_p()
                if kernel.GetThreadDescription(thread["handle"], ctypes.byref(text)) >= 0 and text.value:
                    names[tid] = text.value
            next_refresh = tick + 1.0
        look += 1
        everyone = look % arguments.all_every == 0
        for tid, thread in list(threads.items()):
            handle = thread["handle"]
            cpu = thread_cpu(handle)
            if cpu < 0:
                kernel.CloseHandle(handle)
                threads.pop(tid)
                continue
            used = min(cpu - thread["cpu"], (tick - thread.get("seen", tick - arguments.interval)) * 1.1)
            thread["cpu"] = cpu
            thread["seen"] = tick
            if used <= 0 and not everyone:
                continue
            captured = unwinder.capture(handle)
            if not captured:
                continue
            samples.append([round(tick - created_at, 4), tid, round(used, 4), captured])
        spent += time.time() - tick
        rest = arguments.interval - (time.time() - tick)
        if rest > 0:
            time.sleep(rest)

    sampled_for = time.time() - (deadline - arguments.seconds)
    unwinder.refresh()
    for sample in samples:      # now, with the game no longer disturbed, turn the copies into call stacks
        path = sample[3][3]
        sample[3] = unwinder.walk_saved(*sample[3])
        if path:
            sample.append(path)
    result = {"pid": pid, "sampledSeconds": round(sampled_for, 1), "created": created_at, "interval": arguments.interval, "allEvery": arguments.all_every,
              "looks": look, "walks": len(samples), "samplerBusySeconds": round(spent, 2),
              "modules": [{"base": m.base, "size": m.size, "path": m.path} for m in unwinder.modules],
              "threadNames": names, "samples": samples}
    Path(arguments.out).write_text(json.dumps(result), encoding="utf-8")
    print(f"{look} looks, {len(samples)} stacks, sampler busy {spent:.1f} s -> {arguments.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
