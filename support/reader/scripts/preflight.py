"""One fixed Pro D4X capability-only run, five seconds, no sender or retries."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
FLAGS = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
TARGET = r"USB\VID_3537&PID_1003\UNSELECTED"
XINPUT = r"USB\VID_3537&PID_100A\UNSELECTED"
NEXUS_PATH = str(ROOT.parent/"nexus-2.5.4-research-loose-v1/package/HJC.GameSir.Nexus2_0.exe")
NEXUS_SHA256 = "118395699F973D77DC45A1225D837293FD73742DDB36F643854D3E0A01569B41"

def nexus_identity(process, *, installed=False):
    """Permit one locally pinned research app, not a general Nexus exemption."""
    if not isinstance(process, dict):
        return None
    pid = process.get("ProcessId")
    path = process.get("ExecutablePath")
    created = process.get("CreatedUtc")
    package_dir = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "WindowsApps"
    candidate = Path(path) if isinstance(path, str) else Path()
    store_app = (installed and candidate.parent.parent == package_dir
                 and re.fullmatch(r"GuangzhouChickenRunNetwor\.16460D2A87234_[0-9]+(?:\.[0-9]+){3}_[a-z0-9]+__0zmrycy25r4k8", candidate.parent.name, re.I)
                 and candidate.name.lower() == "hjc.gamesir.nexus2_0.exe"
                 and re.fullmatch(r"[a-f0-9]{64}", str(process.get("Sha256")), re.I))
    if (type(pid) is not int or pid <= 0 or process.get("Name") != "HJC.GameSir.Nexus2_0.exe"
            or not isinstance(path, str) or (not store_app and (os.path.normcase(path) != os.path.normcase(NEXUS_PATH)
            or process.get("Sha256") != NEXUS_SHA256)) or not isinstance(created, str)):
        return None
    try:
        stamp = datetime.fromisoformat(created.replace("Z", "+00:00"))
        if stamp.tzinfo is None:
            return None
    except ValueError:
        return None
    return (pid, created, os.path.normcase(path), process["Sha256"])

def evaluate(snapshot, *, with_nexus=False, installed=False):
    if not isinstance(snapshot, dict) or not isinstance(snapshot.get("devices"), list) or not isinstance(snapshot.get("conflicts"), list):
        return "BLOCKED_MALFORMED_PREFLIGHT"
    if with_nexus:
        if len(snapshot["conflicts"]) != 1:
            return "BLOCKED_CONFLICTING_PROCESS"
        nexus = nexus_identity(snapshot["conflicts"][0], installed=installed)
        if nexus is None:
            return "BLOCKED_NEXUS_IDENTITY"
        # Foreground is retained as evidence, not substituted for actual D4X presence.
        # User reported non-1kHz D4X persists; live snapshots confirmed background D4X.
    elif snapshot["conflicts"]:
        return "BLOCKED_CONFLICTING_PROCESS"
    if any(not isinstance(d, dict) or not isinstance(d.get("PNPDeviceID"), str) for d in snapshot["devices"]):
        return "BLOCKED_MALFORMED_PREFLIGHT"
    roots = [d for d in snapshot["devices"] if re.fullmatch(r"USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[^\\]+", d["PNPDeviceID"], re.I)]
    if len(roots) != 1:
        return "BLOCKED_IDENTITY_COUNT"
    d = roots[0]
    if d["PNPDeviceID"].upper() == XINPUT:
        return "BLOCKED_XINPUT_MODE"
    if d["PNPDeviceID"].upper() != TARGET:
        return "BLOCKED_IDENTITY_MISMATCH"
    # Other visible Xbox/XInput providers may lack a parent in the filtered list.
    if any(x.get("PNPClass") in ("XboxComposite", "XnaComposite") and "VID_3537&PID_1003" not in x["PNPDeviceID"].upper() for x in snapshot["devices"]):
        return "BLOCKED_OTHER_CONTROLLER"
    if d.get("Present") is not True or d.get("Status") != "OK" or type(d.get("ConfigManagerErrorCode")) is not int or d["ConfigManagerErrorCode"] != 0:
        return "BLOCKED_HEALTH"
    ids = d.get("HardwareID")
    if not isinstance(ids, list) or r"USB\VID_3537&PID_1003&REV_0231" not in ids:
        return "BLOCKED_REVISION"
    return "ELIGIBLE_CAPABILITY_ONLY"

def save(path, data):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(data, stream, indent=2)

def snapshot():
    command = r"""
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProCapabilityFocus {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
}
'@
$devices=@(Get-CimInstance Win32_PnPEntity -Filter 'Present=TRUE' | Where-Object {
 $_.PNPDeviceID -match 'VID_3537' -or $_.PNPClass -in @('XboxComposite','XnaComposite')
} | Select-Object Name,PNPDeviceID,PNPClass,Service,Present,Status,ConfigManagerErrorCode,HardwareID)
$conflicts=@(Get-CimInstance Win32_Process | Where-Object {
 $_.Name -match 'GameSir|Nexus|GSN|D4X|ReceiveHost|ProCapabilityHost|JL_Upgrade|FirmwareUpdater' -or
 $_.ExecutablePath -match 'nexus|d4x' -or
 ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'ReceiveHost|ProCapabilityHost')
} | ForEach-Object {
 $hash=$null
 if ($_.Name -eq 'HJC.GameSir.Nexus2_0.exe' -and $_.ExecutablePath) {
  $hasher=[System.Security.Cryptography.SHA256]::Create()
  $stream=[System.IO.File]::OpenRead($_.ExecutablePath)
  try { $hash=([System.BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','') }
  finally { $stream.Dispose(); $hasher.Dispose() }
 }
 [pscustomobject]@{ProcessId=$_.ProcessId;Name=$_.Name;ExecutablePath=$_.ExecutablePath;
   Sha256=$hash;CreatedUtc=$_.CreationDate.ToUniversalTime().ToString('o')}
})
[uint32]$focusProcess=0
[void][ProCapabilityFocus]::GetWindowThreadProcessId([ProCapabilityFocus]::GetForegroundWindow(),[ref]$focusProcess)
@{devices=$devices;conflicts=$conflicts;foreground_pid=$focusProcess} | ConvertTo-Json -Depth 5 -Compress
"""
    result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command], capture_output=True,
                            text=True, check=True, timeout=20, creationflags=FLAGS)
    return json.loads(result.stdout.lstrip("\ufeff"))

def verify_receipt(path):
    receipt = json.loads(path.read_text(encoding="utf-8-sig"))
    if receipt.get("status") != "PASS" or not receipt.get("sources") or not receipt.get("builds"):
        raise ValueError("missing passing offline source/build receipt")
    listed = set()
    for item in receipt["sources"] + receipt["builds"]:
        target = (ROOT/item["path"]).resolve()
        if not target.is_relative_to(ROOT) or target in listed or hashlib.sha256(target.read_bytes()).hexdigest().upper() != item["sha256"]:
            raise ValueError("source/build mismatch or duplicate path: " + item["path"])
        listed.add(target)
    required = {p.resolve() for folder in ("src","tests","scripts","fixtures") for p in (ROOT/folder).rglob("*")
                if p.is_file() and not {"bin","obj","__pycache__"}.intersection(p.relative_to(ROOT).parts)}
    required.update((ROOT/p).resolve() for p in ("global.json","NuGet.Config","origin-sources.json"))
    required.update(p.resolve() for p in (ROOT/"src/bin/Release/net10.0").glob("*") if p.is_file())
    if not required.issubset(listed):
        raise ValueError("receipt omits source/build inputs")
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()
