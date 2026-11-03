"""橋を起こした AI の道具の本体（いちばん近い先祖のプロセス）を探す部品（Windows だけ・状態を持たない）。"""

import os

# Claude Code の本体の名前（いちばん近い先祖のうち、最初に当たった物を本体とみなす）。
CLAUDE_EXES = ("claude.exe", "node.exe", "bun.exe")


def ancestor_process(exe_names=CLAUDE_EXES):
    """この台本の先祖のうち、名前が exe_names に入るいちばん近い物の (pid, 起きた時刻) を返す。
    見つからない時・Windows でない時は None。⭐ 係（サブエージェント）も同じ本体の中で動く＝親と同じ答え。
    ⚠ Windows の親の番号は、親が死ぬと別のプロセスに使い回される＝子より後に起きた「親」が出たら、そこで止める。"""
    if os.name != "nt":
        return None
    try:
        import ctypes
        import ctypes.wintypes as wt

        class _Entry(ctypes.Structure):
            _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                        ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                        ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                        ("szExeFile", ctypes.c_wchar * 260)]

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
        k32.OpenProcess.restype = wt.HANDLE
        snap = k32.CreateToolhelp32Snapshot(2, 0)  # TH32CS_SNAPPROCESS
        if not snap or snap == wt.HANDLE(-1).value:
            return None
        table = {}
        try:
            entry = _Entry()
            entry.dwSize = ctypes.sizeof(_Entry)
            more = k32.Process32FirstW(wt.HANDLE(snap), ctypes.byref(entry))
            while more:
                table[entry.th32ProcessID] = (entry.th32ParentProcessID, entry.szExeFile.lower())
                more = k32.Process32NextW(wt.HANDLE(snap), ctypes.byref(entry))
        finally:
            k32.CloseHandle(wt.HANDLE(snap))

        def started(pid):
            handle = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
            if not handle:
                return None
            try:
                times = [wt.FILETIME() for _ in range(4)]
                if not k32.GetProcessTimes(wt.HANDLE(handle), *[ctypes.byref(t) for t in times]):
                    return None
                return (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
            finally:
                k32.CloseHandle(wt.HANDLE(handle))

        pid = os.getpid()
        child_started = started(pid)
        for _ in range(24):
            if pid not in table:
                return None
            parent = table[pid][0]
            if parent not in table or parent == pid:
                return None
            parent_started = started(parent)
            if parent_started is None or (child_started is not None and parent_started > child_started):
                return None
            if table[parent][1] in exe_names:
                return parent, parent_started
            pid, child_started = parent, parent_started
    except (OSError, AttributeError, ValueError):
        return None
    return None
