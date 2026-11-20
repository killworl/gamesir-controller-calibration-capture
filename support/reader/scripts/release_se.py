"""Release adapter for the verified G7 SE PID 1071 foreground record reader."""
import json
import os
import re
import subprocess
import uuid
from datetime import datetime, timezone
from pathlib import Path

import calibration_capture as capture
import preflight as p

NATIVE = p.ROOT.parent/'g7se-nexus-read-host-v1/src/bin/Release/net10.0'
HASHES = {
    'ReceiveHost.dll': 'A610E22D9ED52AF8A1F478BD38863B6F0E45A604305C42FD4E5FC274BFD4E231',
    'ReceiveHost.deps.json': 'F71FEAC87C14108131E38CB59ED0CF18D3368351B6BD73F457BDE04551C5D698',
    'ReceiveHost.runtimeconfig.json': '2CA90A18BAC3B48286C20ACD3C1AE4F83FA38DE5ACF4B2F8CDD277BF90DC9BD7',
}


def status(snapshot, device_id):
    roots = [d for d in snapshot['devices'] if re.fullmatch(r'USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[^\\]+', d.get('PNPDeviceID',''), re.I)]
    if len(roots) != 1 or roots[0]['PNPDeviceID'].upper() != device_id:
        return 'Connect one controller and keep the same SE connected.', False
    d = roots[0]
    if r'USB\VID_3537&PID_1071&REV_0664' not in d.get('HardwareID',[]):
        return 'SE detected. This firmware is untested; expected REV_0664.', False
    if not d.get('Present') or d.get('Status') != 'OK' or d.get('ConfigManagerErrorCode') != 0:
        return 'Waiting for the SE device to become ready.', False
    if not snapshot['conflicts']:
        return 'SE D4X detected (1071). Waiting for GameSir Nexus.', False
    if len(snapshot['conflicts']) != 1 or not p.nexus_identity(snapshot['conflicts'][0], installed=True):
        return 'Waiting for one recognized Nexus session. Close competing controller tools.', False
    return 'SE D4X detected (1071) | firmware 6.64 | ready', True


def record(folder, side):
    native = folder/'capture'
    cap = capture.load_json(native/'terminal.json')
    if cap['terminal'] != 'ReadReplyObservedUnqualified' or cap['source'] != 'windows_callback' or cap['rejected_events']:
        raise ValueError('SE reader stopped: ' + cap['terminal'])
    entries = capture.load_json(native/'manifest.json')['files']
    if len(entries)!=2 or {entry['path'] for entry in entries}!={'terminal.json','events.jsonl'}:
        raise ValueError('Incomplete SE capture manifest')
    for entry in entries:
        if entry['path'] not in ('terminal.json','events.jsonl') or capture.sha((native/entry['path']).read_bytes()) != entry['sha256']:
            raise ValueError('SE capture evidence mismatch')
    events = [json.loads(line) for line in (native/'events.jsonl').read_text().splitlines()]
    offset = 0 if side == 'left' else 32
    prefix = bytes([0x15,0x17,offset,30])
    sent = [a for a in cap['native_audit'] if a['api']=='StockRead.FixedGipDispatch']
    if len(sent)!=1 or sent[0]['h_result']<0 or bytes.fromhex(sent[0]['arguments'])!=bytes([0x14,0x17,offset,30])+bytes(56):
        raise ValueError('Unexpected SE read dispatch')
    begins = [int(e['host_ordinal']) for e in events if e['kind']=='stock_read_begin' and json.loads(e['detail_json'])['side']==side]
    msgs = [e for e in events if e['kind']=='message' and e['message_class']==0 and e['message_id']==16 and e['payload_hex'].startswith(prefix.hex().upper())]
    if len(events)!=cap['event_count'] or len(msgs)!=1 or len(begins)!=1 or int(msgs[0]['host_ordinal'])<=begins[0] or msgs[0]['session_id']!=cap['session_id']:
        raise ValueError('Missing or ambiguous SE reply')
    body=bytes.fromhex(msgs[0]['payload_hex'])
    if len(body)!=60 or any(body[34:]) or capture.sha(body)!=msgs[0]['payload_sha256']:
        raise ValueError('Invalid SE reply')
    return {**capture.decode(body[4:34].hex()),'side':side,'session_id':cap['session_id']}


def capture_pair(output, device_id, progress):
    for name, expected in HASHES.items():
        if capture.sha((NATIVE/name).read_bytes()) != expected:
            raise ValueError('SE reader integrity check failed: '+name)
    run=Path(output)/('capture-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    records=[]
    session=None
    # Share the same exclusive capture lock with the Pro path.
    with capture.LOCK.open('x') as lock:
        lock.write(str(os.getpid()))
    try:
        for side in ('left','right'):
            folder=run/side
            folder.mkdir()
            before=p.snapshot()
            p.save(folder/'preflight.json',{'snapshot':before})
            reason,ready=status(before,device_id)
            if not ready: raise ValueError(reason)
            observed=p.nexus_identity(before['conflicts'][0], installed=True)
            if session is not None and session != observed:
                raise ValueError('Nexus restarted between SE reads.')
            session=observed
            progress('Reading '+side+' SE calibration record...')
            with (folder/'stdout.txt').open('w') as stdout,(folder/'stderr.txt').open('w') as stderr:
                proc=subprocess.run(['dotnet',str(NATIVE/'ReceiveHost.dll'),'read-once','--side',side,'--output',str(folder/'capture'),'--seconds','5','--window','foreground'],stdout=stdout,stderr=stderr,creationflags=p.FLAGS,timeout=45)
            after=p.snapshot()
            p.save(folder/'postflight.json',{'snapshot':after})
            reason,ready=status(after,device_id)
            if not ready: raise ValueError(reason)
            if session != p.nexus_identity(after['conflicts'][0], installed=True):
                raise ValueError('Nexus restarted during SE capture.')
            row=record(folder,side)
            if proc.returncode: raise ValueError('SE reader exited unsuccessfully')
            records.append(row)
        path=run/'snapshot.json'
        p.save(path,{'schema':'g7se_calibration_record_capture_v1','device':device_id,'revision':'0664','records':records,'utc':capture.utc()})
        return path
    except (subprocess.TimeoutExpired,OSError,ValueError) as error:
        p.save(run/'failure.json',{'error':str(error)})
        raise ValueError(str(error)) from error
    finally:
        capture.LOCK.unlink()
