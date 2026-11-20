"""One fixed Pro calibration-record read. No retry, update, mode switch or calibration."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import preflight as p

ACCESS_REFERENCE = p.ROOT/'reference-checks/windows-g7-pro-access.json'
ACCESS_REFERENCE_SHA256 = '6F143CC18D4453580094EE73E99FD62DE09E0BCB68B77187648B10EA1DF91A72'

def command(output, side, foreground=True):
    if side not in ('left','right'): raise ValueError('one fixed side required')
    return ['dotnet',str(p.ROOT/'src/bin/Release/net10.0/ProCalibrationReadHost.dll'),
            'read-once','--side',side,'--output',str(output/'capture'),
            '--seconds','5'] + (['--window','foreground'] if foreground else [])

def supervise(output, side, foreground=True):
    argv=command(output,side,foreground)
    with (output/'stdout.txt').open('xb') as stdout, (output/'stderr.txt').open('xb') as stderr:
        with subprocess.Popen(argv,cwd=p.ROOT,stdout=stdout,stderr=stderr,creationflags=p.FLAGS) as child:
            timed_out=False
            try: code=child.wait(timeout=45)  # 30s readiness + 5s reply budget + startup/cleanup
            except subprocess.TimeoutExpired:
                timed_out=True; child.kill(); code=child.wait()
            return {'pid':child.pid,'exit_code':code,'timed_out':timed_out,'argv':argv}

def attempt(output, side, take_snapshot=p.snapshot, launch=supervise):
    if side not in ('left','right'): raise ValueError('one fixed side required')
    output.mkdir()
    before=take_snapshot()
    decision=p.evaluate(before,with_nexus=True, installed=True)
    p.save(output/'preflight.json',{'utc':datetime.now(timezone.utc).isoformat(),'snapshot':before,
           'decision':decision,'authority':'User: D4X already active, just do the calibration pull',
           'side':side,'persistent_write':False,'calibration_command':False,'mode_switch':False})
    result={'decision':decision,'side':side,'native_started':False,'automatic_retry':False,
            'qualification':'No received calibration record established by launcher alone'}
    if decision=='ELIGIBLE_CAPABILITY_ONLY':
        try:
            result['process']=launch(output,side); result['native_started']=True
        except (OSError,subprocess.SubprocessError) as error:
            result['native_started']=None
            result['process']={'exit_code':3,'error':str(error)}
        try:
            after=take_snapshot()
            result['postflight_decision']=p.evaluate(after,with_nexus=True, installed=True)
            if result['postflight_decision']=='ELIGIBLE_CAPABILITY_ONLY' and p.nexus_identity(before['conflicts'][0], installed=True)!=p.nexus_identity(after['conflicts'][0], installed=True):
                result['postflight_decision']='BLOCKED_NEXUS_SESSION_CHANGED'
            p.save(output/'postflight.json',{'utc':datetime.now(timezone.utc).isoformat(),'snapshot':after})
        except (OSError,subprocess.SubprocessError,ValueError) as error:
            result['postflight_decision']='BLOCKED_POSTFLIGHT_ERROR'; result['postflight_error']=str(error)
    p.save(output/'result.json',result)
    return result

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--receipt',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--side',choices=['left','right'],required=True)
    args=parser.parse_args()
    pin=p.verify_receipt(args.receipt)
    if hashlib.sha256(ACCESS_REFERENCE.read_bytes()).hexdigest().upper()!=ACCESS_REFERENCE_SHA256:
        raise ValueError('capability proof hash mismatch')
    if json.loads(ACCESS_REFERENCE.read_text())['terminal']!='CapabilityOnlyComplete':
        raise ValueError('capability proof not complete')
    result=attempt(args.output.resolve(),args.side)
    p.save(args.output.resolve()/'offline-receipt.json',{'path':str(args.receipt.resolve()),'sha256':pin,
           'capability_terminal':str(ACCESS_REFERENCE),'capability_sha256':ACCESS_REFERENCE_SHA256})
    print(json.dumps(result))
    if not result['native_started']: return 2
    return result['process']['exit_code'] or (0 if result['postflight_decision']=='ELIGIBLE_CAPABILITY_ONLY' else 1)

if __name__=='__main__': raise SystemExit(main())
