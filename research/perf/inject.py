"""Loads a DLL into a running game of the perf instance (never the installed game).

    python research/perf/inject.py <dll> [--wait-exe <full path of the exe to wait for>] [--pid N]

The classic way: the DLL's path is written into the game's memory and a thread is started
there on LoadLibraryW. That is a write into the game process, so by this project's rules it
is for throw-away test games only. With --wait-exe it waits for the process to appear and
loads the DLL as soon as kernel32 is there (a fraction of a second after the start).
"""
from __future__ import annotations

import ctypes
import os
import sys
import time
from ctypes import wintypes
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from stack_sampler import find_process, kernel  # noqa: E402

kernel.VirtualAllocEx.restype = ctypes.c_void_p
kernel.VirtualAllocEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]
kernel.WriteProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p]
kernel.CreateRemoteThread.restype = wintypes.HANDLE
kernel.CreateRemoteThread.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p,
                                      wintypes.DWORD, ctypes.c_void_p]
kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.GetExitCodeThread.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
kernel.GetModuleHandleW.restype = ctypes.c_void_p
kernel.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel.GetProcAddress.restype = ctypes.c_void_p
kernel.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_char_p]


def inject(pid: int, dll: str, attempts: int = 200) -> bool:
    process = kernel.OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, False, pid)  # thread, vm operation/read/write, query
    if not process:
        raise OSError(f"cannot open process {pid}: error {ctypes.get_last_error()}")
    path = (dll + "\0").encode("utf-16-le")
    # kernel32 is mapped at the same address in every process of a boot session.
    load_library = kernel.GetProcAddress(kernel.GetModuleHandleW("kernel32.dll"), b"LoadLibraryW")
    for _ in range(attempts):
        remote = kernel.VirtualAllocEx(process, None, len(path), 0x3000, 0x04)
        if remote and kernel.WriteProcessMemory(process, remote, path, len(path), None):
            thread = kernel.CreateRemoteThread(process, None, 0, load_library, remote, 0, None)
            if thread:
                kernel.WaitForSingleObject(thread, 15000)
                code = wintypes.DWORD()
                kernel.GetExitCodeThread(thread, ctypes.byref(code))
                kernel.CloseHandle(thread)
                if code.value:      # the low half of the module handle: not zero means loaded
                    kernel.CloseHandle(process)
                    return True
        time.sleep(0.02)            # too early in the process's life: try again
    kernel.CloseHandle(process)
    return False


def main() -> int:
    arguments = sys.argv[1:]
    dll = str(Path(arguments[0]).resolve())
    if "--pid" in arguments:
        pid = int(arguments[arguments.index("--pid") + 1])
    else:
        exe = arguments[arguments.index("--wait-exe") + 1]
        if os.path.normcase(r"\steamapps\common\Baldurs Gate 3\bin") in os.path.normcase(exe):
            sys.exit("not the installed game")
        pid = find_process(exe)
    started = time.time()
    ok = inject(pid, dll)
    print(f"{'loaded' if ok else 'NOT loaded'} {dll} into {pid} ({time.time() - started:.2f} s)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
