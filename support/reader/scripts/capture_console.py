"""Single-console capture with passive Windows status until the user requests a read."""
import ctypes
from ctypes import wintypes
import json
import subprocess
import msvcrt
import time
from datetime import datetime
from pathlib import Path

import calibration_capture as c
import preflight as p


def status(snapshot):
    devices = {str(d.get('PNPDeviceID', '')).upper(): d for d in snapshot['devices']}
    if p.TARGET in devices:
        mode = 'D4X detected (1003)'
    elif p.XINPUT in devices:
        mode = 'XInput (100A) - waiting for D4X. If it stays in XInput, close any apps that may use XInput indirectly. If it still persists, restart your PC.'
    else:
        mode = 'Waiting for D4X controller'
    nexus = any(p.nexus_identity(proc, installed=True) for proc in snapshot['conflicts'])
    decision = p.evaluate(snapshot, with_nexus=True, installed=True)
    ready = decision == 'ELIGIBLE_CAPABILITY_ONLY'
    parts = [mode, 'Nexus open' if nexus else 'Waiting for Nexus']
    if not ready and nexus and p.TARGET in devices:
        parts.append(decision)
    if ready:
        parts.append('Ready')
    return ' | '.join(parts), ready


def nexus_in_front(pid):
    if not pid:
        return False
    user = ctypes.windll.user32
    user.GetForegroundWindow.restype = wintypes.HWND
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    actual = wintypes.DWORD()
    window = user.GetForegroundWindow()
    user.GetWindowThreadProcessId(window, ctypes.byref(actual))
    if actual.value == pid:
        return True
    kind = ctypes.create_unicode_buffer(80)
    user.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user.GetClassNameW(window, kind, len(kind))
    if kind.value != 'ApplicationFrameWindow':
        return False
    found = False
    callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    @callback
    def inspect(child, unused):
        nonlocal found
        owner = wintypes.DWORD()
        user.GetWindowThreadProcessId(child, ctypes.byref(owner))
        found = owner.value == pid
        return not found
    user.EnumChildWindows.argtypes = [wintypes.HWND, callback, wintypes.LPARAM]
    user.EnumChildWindows(window, inspect, 0)
    return found


def request_nexus(process):
    subprocess.Popen(['explorer.exe', r'shell:AppsFolder\GuangzhouChickenRunNetwor.16460D2A87234_0zmrycy25r4k8!App'])


def wait_for_ready(pid):
    deadline = time.monotonic() + 30
    requested = False
    while time.monotonic() < deadline:
        snap = p.snapshot()
        message, ready = status(snap)
        same = any(proc.get('ProcessId') == pid and p.nexus_identity(proc, installed=True)
                   for proc in snap['conflicts'])
        if ready and same:
            return
        if not requested:
            print(message + ' | Waiting for the same controller and Nexus session. If XInput persists, close apps that may use XInput indirectly or restart your PC.')
            request_nexus(None)
            requested = True
        time.sleep(0.5)
    raise ValueError('D4X did not return in the same Nexus session within 30 seconds.')


def capture_reader(pid, foreground=False):
    def read(out, side, receipt):
        nonlocal foreground
        wait_for_ready(pid)
        if not foreground:
            try:
                c.read_one(out, side, receipt, console=True)
                return
            except ValueError:
                terminal_path = out/'capture/terminal.json'
                if not terminal_path.is_file():
                    raise
                terminal = c.load_json(terminal_path)
                audit = terminal.get('native_audit')
                if (terminal.get('terminal') != 'BindingNotReady' or not isinstance(audit, list)
                        or any(row.get('api') == 'StockRead.FixedGipDispatch' for row in audit)):
                    raise
                # Retain the no-dispatch attempt alongside the separate native reader run.
                out.rename(out.with_name(out.name + '-background'))
                foreground = True
                print('Windows withheld background input. Starting the original reader automatically.')
                wait_for_ready(pid)
        c.read_one(out, side, receipt, console=False)
    return read


def run():
    print('G7 Pro calibration | D4X status monitor | F for original foreground reader | Q to quit')
    saved = p.ROOT.parent/'exodus-d4x-session-v2/status-viewer-v1/latest-record.js'
    try:
        receipt = json.loads(saved.read_text().split('=', 1)[1].strip().rstrip(';'))
        print('D4X viewer saved session:', receipt['receipt']['Child']['Report']['Interval']['Failure'])
    except (OSError, ValueError, KeyError, IndexError):
        print('D4X viewer saved session unavailable.')
    print('Checking current Windows device and Nexus status...')
    armed = True
    requested_nexus = set()
    previous = None
    baseline = None
    while True:
        try:
            snapshot = p.snapshot()
            message, ready = status(snapshot)
            devices = {d.get('PNPDeviceID', '').upper() for d in snapshot['devices']}
            process = next((v for v in snapshot['conflicts'] if p.nexus_identity(v, installed=True)), None)
            pid = process['ProcessId'] if process else None
            if armed and devices.intersection({p.XINPUT, p.TARGET}) and pid not in requested_nexus and not nexus_in_front(pid):
                requested_nexus.add(pid)
                request_nexus(process)
                print('Requested GameSir Nexus in the foreground.')
            if ready:
                message += (' | Waiting for the GameSir Nexus window to be active' if armed
                            else ' | Press C for another capture')
        except Exception as error:
            message, ready = f'Status check failed: {error}', False
        if message != previous:
            print(datetime.now().strftime('%H:%M:%S'), message)
            previous = message
        for _ in range(15):
            key = msvcrt.getwch().lower() if msvcrt.kbhit() else ''
            if key == 'q':
                return
            if key == 'c':
                armed = True
                requested_nexus.clear()
                break
            foreground_read = key == 'f' and ready
            if foreground_read or (ready and armed and nexus_in_front(pid)):
                armed = False
                try:
                    if foreground_read:
                        print("Original reader selected. If Windows leaves its reader window in the background, click it once.")
                    path = c.capture_pair(c.DEFAULT_OUTPUT, c.DEFAULT_RECEIPT,
                        'Console comparison' if baseline else 'Console baseline', baseline,
                        read_one=capture_reader(pid, foreground_read), progress=print)
                    for record in c.load_snapshot(path)['records']:
                        print(record['side'], record['fields'])
                    print('Saved:', path)
                    baseline = baseline or path
                except Exception as error:
                    print('Capture stopped:', error)
                    if not foreground_read and 'BindingNotReady' in str(error):
                        print('Windows did not resume the background reader. Focus this console and press F to use the original reader.')
                previous = None
                break
            time.sleep(0.1)


if __name__ == '__main__':
    run()

