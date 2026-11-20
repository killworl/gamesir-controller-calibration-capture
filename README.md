# GameSir Controller Calibration Capture

**Made by u/peoplehurt6666 - Reddit** · [Reddit profile](https://www.reddit.com/user/peoplehurt6666/)

A Windows console tool for reading the stored calibration values of the wired GameSir G7 SE and G7 Pro. Each successful capture displays both sticks and saves a local JSON snapshot.

## Controller support

The launcher checks the Windows USB identity first. Both tested devices report vendor ID `3537`. A ready D4X interface selects the matching reader automatically.

| Model and tested firmware | D4X USB PID | USB revision | Windows provider version (major, minor, build, revision) | Tested capture setup |
|---|---|---|---|---|
| G7 SE 6.64 | `1071` | `REV_0664` | 1, 2, 1636, 2 | D4X with Nexus running |
| G7 Pro 2.31 | `1003` | `REV_0231` | 1, 2, 561, 2 | D4X with Nexus running |

Connect one controller at a time. Both models use the same Nexus-first workflow. After Enter, the launcher opens or reuses GameSir Nexus and waits for the model-specific D4X interface. Leave Nexus open for both stick reads. Each model uses its own reader and firmware checks. The launcher keeps the selected USB identity for both reads. Other models and firmware versions are untested. A different image reporting identical identifiers can pass the checks; the tool does not verify a firmware image hash.

## What the capture sends

The reader asks Windows for the controller identity and firmware version. Once the identity checks pass, it uses the Windows Gaming Input GIP provider to read one stored calibration record per stick, left first. The normal successful capture sends two fixed read requests.

| Stick | Request prefix (hex) | Record address | Expected reply prefix (hex) |
|---|---|---|---|
| Left | `14 17 00 1E` | `0x1700` | `15 17 00 1E` |
| Right | `14 17 20 1E` | `0x1720` | `15 17 20 1E` |

Each request uses GIP command class `0`, message ID `0x0F`, and a 60-byte body. The four bytes shown are followed by 56 zero bytes. The first byte (`14`) selects the read operation, the middle two identify the record, and `1E` requests 30 bytes. The reader expects reply message ID `0x10` and checks the matching prefix, record and padding. These are application-level bytes passed to Windows; Windows handles the underlying USB framing.

The capture reads stored values. It sends no calibration-start, write, firmware-update, polling-rate or force-D4X command. Nexus runs separately and can generate its own traffic. To audit a run, see `left/capture/events.jsonl` and `right/capture/events.jsonl` for observed messages and each sides `capture/terminal.json` for the recorded dispatch body and result. The sending code is in `src/Interop/FixedStockRead.cs` in the reader source.

## What happens when you run it

1. Clone this repository or use **Code → Download ZIP** and extract it into a writable folder. Double-click **Capture.cmd**.
2. Setup checks Python 3.10+ and the x64 .NET 10 Runtime. You can approve a missing dependency install or exit.
3. Press **Enter** when ready. Nexus opens if needed for either controller. A wired rate of **250 Hz** is preferred for consistency. The tool does not measure the rate.
4. Connect one supported controller. The console checks for D4X and displays its model-specific status. The SE uses PID **1071** and the Pro uses **1003**. If a Pro stays in XInput (**100A**), wait, close apps that might use XInput indirectly, or restart your PC.
5. The reader requests the left record, then the right. Its small window asks Windows for focus automatically. Click it only if it asks, then leave it active until the read finishes.
6. The console shows **Lower**, **Center**, and **Upper** for each axis in calibration counts. **E0-E7** are the eight directional entries stored for each stick. A successful pair produces **captures/capture-/snapshot.json**. An incomplete attempt can leave diagnostic files in its own folder.
7. Press **S** to save an extra JSON copy beside the launcher, or **Enter** to skip. At the next prompt, **R** starts another capture and **Enter** closes the console.

## What the files do

| File or group | Purpose |
|---|---|
| Capture.cmd | Starts the release and sets the save location. |
| Read me.txt / README.md | Quick-start instructions and this guide. |
| LaunchCapture.cmd / Start-Capture.ps1 | Start setup, check prerequisites, offer missing installs, and open Nexus. |
| release_console.py | Handles controller selection, prompts, colored axis tables, and saving beside the launcher. |
| capture_console.py / preflight.py | Check Nexus, the controller interface and readiness, and coordinate the native reader. |
| calibration_capture.py / run_read_once.py | Request each stick record, validate the replies, and save the pair. |
| **ProCalibrationReadHost.dll** | The compiled **G7 Pro Calibration Reader**, which talks to the controller through Windows. Keep it beside its two companion files. |
| **ReceiveHost.dll** / release_se.py | The verified SE reader and its console adapter. The adapter checks device identity, reader hashes and the returned records. |
| Each reader’s .deps.json / .runtimeconfig.json | Tell .NET how to load the reader and which runtime it needs. |
| receipt.json | Lists packaged file checksums for the integrity check. |
| reference-checks/windows-g7-pro-access.json | Saved result from the original Windows controller-access test. Current controller readiness is checked on each run. |
| global.json / NuGet.Config / origin-sources.json | Record the build configuration and source origins. |

Keep the support folder together. To share a reading, use the saved snapshot; it includes USB device identity and capture metadata.

Run **Capture.cmd** from the repository root.

## Source and license

The native readers live in [src/pro](src/pro) and [src/se](src/se). Build either project with the .NET 10 SDK using `dotnet build src/pro/ReceiveHost.csproj -c Release` or the corresponding SE path. The launcher source is in [support/reader/scripts](support/reader/scripts). Published reader binaries are hash-checked. Local builds require matching integrity records before deployment.

Copyright © 2026 killworl (u/peoplehurt6666). Distributed under the [MIT License](LICENSE).
