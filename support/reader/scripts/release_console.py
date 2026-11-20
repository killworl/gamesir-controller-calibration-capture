"""Interactive wired G7 Pro calibration capture for Windows."""
import ctypes
from ctypes import wintypes
from datetime import datetime
import os
import re
import sys
import tempfile
import time
from pathlib import Path

import calibration_capture as capture
import capture_console as console
import preflight
import subprocess
import release_se

COLOR = False
DEVICE = re.compile(r'USB\\VID_3537&PID_(1003|100A|1071)\\([^\\]+)', re.I)


def emit(text, color=37):
    print(f'\033[{color}m{text}\033[0m' if COLOR else text, flush=True)


def enable_color():
    kernel = ctypes.windll.kernel32
    kernel.GetStdHandle.restype = wintypes.HANDLE
    kernel.GetConsoleMode.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel.SetConsoleMode.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    handle = kernel.GetStdHandle(-11)
    mode = wintypes.DWORD()
    return sys.stdout.isatty() and kernel.GetConsoleMode(handle, ctypes.byref(mode)) and kernel.SetConsoleMode(handle, mode.value | 4)


def discover(snapshot):
    matching = []
    for device in snapshot['devices']:
        match = DEVICE.fullmatch(device.get('PNPDeviceID', ''))
        if match:
            matching.append(match)
    if len(matching) != 1:
        return None, ('Connect one wired G7 SE or G7 Pro.' if not matching else 'Multiple matching controllers. Connect only the one to capture.')
    match = matching[0]
    if match[1] == '100A':
        return None, 'XInput (100A). Wait for D4X. If it stays in XInput, close any apps that may use XInput indirectly. If it still persists, restart your PC.'
    serial = match[2].upper()
    return serial, 'SE D4X (1071)' if match[1]=='1071' else 'D4X (1003)'


def show_axes(snapshot):
    for record in sorted(snapshot['records'], key=lambda r: r['side']):
        fields = record['fields']
        emit('\n' + record['side'].upper() + ' STICK  |  calibration counts', 96 if record['side'] == 'left' else 95)
        emit(f'{"Axis":<10}{"Lower":>10}{"Center":>10}{"Upper":>10}')
        for i in (1, 2):
            emit(f"Axis {i:<5}{fields[f'axis_{i}_lower']:>10,}{fields[f'axis_{i}_reference']:>10,}{fields[f'axis_{i}_upper']:>10,}", 92)
        emit('Directional entries  ' + '  '.join(f"E{i}={fields[f'direction_{i}']}" for i in range(8)), 90)


def export_snapshot(snapshot):
    directory = Path(os.environ.get('G7PRO_LAUNCH_DIR', preflight.ROOT))
    destination = directory / f'G7-Pro-{snapshot.parent.name}.json'
    with tempfile.TemporaryDirectory(dir=directory) as scratch:
        staged = Path(scratch) / 'snapshot.json'
        staged.write_bytes(snapshot.read_bytes())
        # Windows rename publishes the complete file and rejects an existing name.
        staged.rename(destination)
    return destination


def main():
    global COLOR
    COLOR = enable_color()
    emit('250 Hz wired is preferred for consistency. You can continue at your current polling rate.', 96)
    input('Press Enter when ready (Ctrl+C to quit): ')
    nexus_requested = False
    waiting_since = time.monotonic()
    warned = False
    previous = None
    selected = None
    while True:
        snapshot = preflight.snapshot()
        serial, message = discover(snapshot)
        se = message == 'SE D4X (1071)'
        key = ('SE' if se else 'Pro', serial)
        if not nexus_requested:
            nexus_requested = True
            if not any(preflight.nexus_identity(v,installed=True) for v in snapshot['conflicts']):
                setup = Path(__file__).with_name('Start-Capture.ps1')
                if subprocess.call(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',str(setup),'-NexusOnly']):
                    return
        if serial and (selected is None or selected == key):
            if se:
                device_id = 'USB\\VID_3537&PID_1071\\' + serial
                message, ready = release_se.status(snapshot,device_id)
            else:
                preflight.TARGET = 'USB\\VID_3537&PID_1003\\' + serial
                preflight.XINPUT = 'USB\\VID_3537&PID_100A\\' + serial
                message, ready = console.status(snapshot)
            if ready:
                warned = False
                selected = key
                emit(message if se else 'D4X ready | G7 Pro 2.31 | USB PID 1003', 92)
                emit('Reading both sticks. Keep the reader active if Windows requests focus.', 96)
                try:
                    output = Path(os.environ.get('G7PRO_CAPTURE_OUTPUT', capture.DEFAULT_OUTPUT))
                    if se:
                        path = release_se.capture_pair(output,device_id,lambda msg:emit(msg,96))
                        show_axes(capture.load_json(path))
                    else:
                        proc = next(v for v in snapshot['conflicts'] if preflight.nexus_identity(v,installed=True))
                        path = capture.capture_pair(output, capture.DEFAULT_RECEIPT,
                            '250 Hz preferred; actual rate unmeasured', read_one=console.capture_reader(proc['ProcessId'], True),
                            progress=lambda msg: emit(msg, 96))
                        show_axes(capture.load_snapshot(path))
                    emit(f'\nSnapshot saved to {path}', 92)
                    if input('Press S to also save the JSON beside the launcher, or Enter to skip: ').strip().lower() == 's':
                        try:
                            exported = export_snapshot(path)
                            emit(f'Saved beside the launcher: {exported}', 92)
                        except OSError as exc:
                            emit(f'Extra copy could not be saved: {exc}. Your capture is at {path}.', 93)
                except (OSError, ValueError, RuntimeError) as exc:
                    emit(f'Capture stopped: {exc}', 93)
                    emit('Check the SE is still connected in D4X (1071) and close competing controller tools.' if se else 'For consistency, try 250 Hz wired in Nexus. If D4X drops, wait for D4X, close apps that may use XInput indirectly, or restart your PC before retrying.', 93)
                if input('\nPress R to capture again, or Enter to close: ').strip().lower() != 'r':
                    return
                waiting_since = time.monotonic()
                previous = None
                continue
        elif serial:
            message = 'Reconnect the controller selected for this session.'
        if previous != message:
            emit(datetime.now().strftime('%H:%M:%S') + '  ' + message, 93)
            previous = message
        if not warned and time.monotonic() - waiting_since >= 30:
            emit("SE capture still waiting. See the device status above." if se else "Capture still waiting. Keep Nexus open and wait for D4X. If XInput persists, close apps that may use XInput indirectly or restart your PC. 250 Hz wired is preferred for consistency.", 93)
            warned = True
        time.sleep(1)


if __name__ == '__main__':
    try:
        main()
    except (KeyboardInterrupt, EOFError):
        print('\nClosed.')
