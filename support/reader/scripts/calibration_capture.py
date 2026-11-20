"""On-demand fixed Pro calibration pair + automatic JSON/byte/field comparison.

No polling or setting changes. Two independent sessions, not an atomic CalState.
The native sender and its foreground/readiness rules are deliberately unchanged.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

import preflight as p
import run_read_once as reader

ROOT = p.ROOT
LOCK = ROOT/'capture.lock'
DEFAULT_RECEIPT = ROOT/'validation/reader-integrity.json'
DEFAULT_OUTPUT = ROOT/'captures'
FIELDS = ('axis_1_reference', 'axis_2_reference', 'axis_1_lower', 'axis_1_upper',
          'axis_2_lower', 'axis_2_upper', *(f'direction_{i}' for i in range(8)))
BOUNDARY = ('Matching record replies only; storage success/freshness independently unproven. '
            'A66A is a marker, not a CRC. Sequential LS/RS reads are not an atomic snapshot '
            'or a full runtime CalState. Unchanged bytes do not establish unchanged runtime behavior.')


def utc():
    return datetime.now(timezone.utc).isoformat()


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def load_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def decode(record_hex):
    raw = bytes.fromhex(record_hex)
    if len(raw) != 30 or raw[-2:] != b'\xA6\x6A':
        raise ValueError('Expected a 30-byte record with A66A marker')
    words = [int.from_bytes(raw[i:i+2], 'big') for i in range(0, 28, 2)]
    return {'record_hex': raw.hex().upper(), 'record_length': len(raw),
            'record_sha256': sha(raw), 'marker_hex': 'A66A',
            'words_be16': words, 'fields': dict(zip(FIELDS, words))}


def extract_run(run, side):
    """Validate retained host receipts; never turn an incomplete read into success."""
    run = Path(run)
    if side not in ('left', 'right'):
        raise ValueError('Only fixed left/right reads supported')
    result = load_json(run/'result.json')
    if (result.get('side') != side or result.get('native_started') is not True
            or result.get('decision') != 'ELIGIBLE_CAPABILITY_ONLY'
            or result.get('postflight_decision') != 'ELIGIBLE_CAPABILITY_ONLY'
            or result.get('process', {}).get('exit_code') != 0
            or result.get('process', {}).get('timed_out') is not False):
        raise ValueError('Read process or pre/postflight did not complete')
    before = load_json(run/'preflight.json')['snapshot']
    after = load_json(run/'postflight.json')['snapshot']
    if any(p.evaluate(s, with_nexus=True, installed=True) != 'ELIGIBLE_CAPABILITY_ONLY' for s in (before, after)):
        raise ValueError('Device/Nexus identity not qualified')
    nexus = p.nexus_identity(before['conflicts'][0], installed=True)
    if nexus != p.nexus_identity(after['conflicts'][0], installed=True):
        raise ValueError('Nexus changed during capture')

    manifest = load_json(run/'capture/manifest.json')
    entries = manifest.get('files', [])
    if len(entries) != 2 or {e['path'] for e in entries} != {'events.jsonl', 'terminal.json'}:
        raise ValueError('Unexpected native evidence manifest')
    evidence = {}
    for entry in entries:
        data = (run/'capture'/entry['path']).read_bytes()
        if len(data) != entry['size'] or sha(data) != entry['sha256']:
            raise ValueError('Native evidence hash/size mismatch')
        evidence[entry['path']] = entry['sha256']
    terminal = load_json(run/'capture/terminal.json')
    if (terminal.get('terminal') != 'ReadReplyObservedUnqualified'
            or terminal.get('source') != 'windows_callback' or terminal.get('rejected_events') != 0):
        raise ValueError('No qualifying native read terminal')
    events = [json.loads(line) for line in (run/'capture/events.jsonl').read_text().splitlines()]
    if len(events) != terminal['event_count']:
        raise ValueError('Incomplete event stream')
    for ordinal, event in enumerate(events, 1):
        if (event['host_ordinal'] != str(ordinal) or event['session_id'] != terminal['session_id']
                or event['source'] != 'windows_callback'
                or sha(bytes.fromhex(event['payload_hex'])) != event['payload_sha256']):
            raise ValueError('Mixed session, ordinal or payload hash mismatch')
    begins = [e for e in events if e['kind'] == 'stock_read_begin']
    if len(begins) != 1 or json.loads(begins[0]['detail_json'])['side'] != side:
        raise ValueError('Expected exactly one matching fixed read begin')
    offset = 0 if side == 'left' else 0x20
    expected = bytes([0x14, 0x17, offset, 30]) + bytes(56)
    dispatches = [a for a in terminal['native_audit'] if a['api'] == 'StockRead.FixedGipDispatch']
    if (len(dispatches) != 1 or dispatches[0]['h_result'] < 0
            or bytes.fromhex(dispatches[0]['arguments']) != expected):
        raise ValueError('Fixed dispatch differs or failed')
    messages = [e for e in events if e['kind'] == 'message' and not
                ((e['message_class'] == 1 and e['message_id'] == 0 and e['declared_length'] == 32) or
                 (e['message_class'] == 0 and e['message_id'] == 16 and e['declared_length'] == 60
                  and len(e['payload_hex']) == 120 and e['payload_hex'][:2].upper() == 'E0'))]
    if len(messages) != 1:
        raise ValueError('Unexpected/duplicate non-stream messages')
    response = messages[0]
    body = bytes.fromhex(response['payload_hex'])
    if (int(response['host_ordinal']) <= int(begins[0]['host_ordinal'])
            or response['message_class'] not in (0, 1, 2) or response['message_id'] != 0x10
            or response['declared_length'] != 60 or len(body) != 60
            or body[:4] != bytes([0x15, 0x17, offset, 30]) or any(body[34:])):
        raise ValueError('Wrong/stale response envelope')
    return {**decode(body[4:34].hex()), 'side': side,
            'storage_address': '0x1700' if side == 'left' else '0x1720',
            'utc': response['host_utc'], 'source_run': str(run.resolve()),
            'session_id': terminal['session_id'], 'terminal': terminal['terminal'],
            'response_body_hex': body.hex().upper(), 'response_body_sha256': sha(body),
            'response_class': response['message_class'], 'response_sequence': response['sequence'],
            'nexus_identity': list(nexus), 'evidence_hashes': evidence,
            'manifest_sha256': sha((run/'capture/manifest.json').read_bytes())}


def validate_snapshot(value):
    if (value.get('schema') != 'g7pro_calibration_record_capture_v1'
            or value.get('device') != p.TARGET or value.get('revision') != '0231'
            or value.get('provider_firmware_tuple') != [1, 2, 561, 2]):
        raise ValueError('Snapshot is not the pinned Pro/REV0231 identity')
    records = value.get('records', [])
    if len(records) != 2 or {r['side'] for r in records} != {'left', 'right'}:
        raise ValueError('Snapshot must contain exactly one record per side')
    for record in records:
        decoded = decode(record['record_hex'])
        if record.get('record_sha256') != decoded['record_sha256']:
            raise ValueError('Record hash mismatch')
        # Recompute derived fields; imported cached values are not authoritative.
        record.update(decoded)
    return value


def load_snapshot(path):
    return validate_snapshot(load_json(path))


def compare(before, after):
    before, after = validate_snapshot(before), validate_snapshot(after)
    result = {'schema': 'g7pro_calibration_record_diff_v1', 'utc': utc(),
              'baseline_utc': before['utc'], 'after_utc': after['utc'],
              'runtime_equivalence_established': False, 'qualification': BOUNDARY,
              'changed_byte_count': 0, 'sides': {}}
    for side in ('left', 'right'):
        a = next(r for r in before['records'] if r['side'] == side)
        b = next(r for r in after['records'] if r['side'] == side)
        changes = [{'offset': i, 'before': x, 'after': y} for i, (x, y) in
                   enumerate(zip(bytes.fromhex(a['record_hex']), bytes.fromhex(b['record_hex']))) if x != y]
        fields = [{'field': name, 'offset': i*2, 'before': x, 'after': y, 'delta': y-x}
                  for i, (name, x, y) in enumerate(zip(FIELDS, a['words_be16'], b['words_be16'])) if x != y]
        result['sides'][side] = {'changed': bool(changes), 'bytes': changes, 'fields': fields,
                                 'before_sha256': a['record_sha256'], 'after_sha256': b['record_sha256']}
        result['changed_byte_count'] += len(changes)
    return result


def verify_authority(receipt):
    pin = p.verify_receipt(receipt)
    if (sha(reader.ACCESS_REFERENCE.read_bytes()) != reader.ACCESS_REFERENCE_SHA256
            or load_json(reader.ACCESS_REFERENCE)['terminal'] != 'CapabilityOnlyComplete'):
        raise ValueError('Pinned native capability proof changed')
    return pin


def read_one(output, side, receipt, *, console=False):
    # Launch the existing supervisor in-process. Its native child is bounded to
    # 45 seconds, each identity query to 20 seconds. No new transport semantics.
    launch = (lambda out, stick: reader.supervise(out, stick, foreground=False)) if console else reader.supervise
    result = reader.attempt(output, side, launch=launch)
    if (result.get('native_started') is not True or result['process']['exit_code'] != 0
            or result.get('postflight_decision') != 'ELIGIBLE_CAPABILITY_ONLY'):
        decision = result.get('postflight_decision') or result['decision']
        terminal = output/'capture/terminal.json'
        if terminal.is_file():
            decision += ' / ' + str(load_json(terminal).get('terminal'))
        raise ValueError(f'{side} read failed ({decision}); details: {output}/result.json')


def capture_pair(output_root, receipt, label, baseline=None, *, read_one=read_one, progress=lambda s: None):
    if not isinstance(label, str) or not label.strip() or len(label) > 500:
        raise ValueError('Describe the settings in 1..500 characters')
    baseline_data = load_snapshot(baseline) if baseline else None
    baseline_hash = sha(Path(baseline).read_bytes()) if baseline else None
    pin = verify_authority(receipt)
    # Exclusive global lock across panel/CLI instances. A crash leaves it for
    # manual inspection; never infer that a leftover lock is safe to ignore.
    lock_fd = os.open(LOCK, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    run = None
    try:
        with os.fdopen(lock_fd, 'w') as lock:
            lock.write(json.dumps({'pid': os.getpid(), 'utc': utc()}))
        output_root = Path(output_root)
        output_root.mkdir(parents=True, exist_ok=True)
        run = output_root/('capture-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:8])
        run.mkdir()
        start = utc()
        p.save(run/'request.json', {'utc': start, 'label_user_reported': label,
               'authority': 'Explicit user-requested fixed calibration pair', 'baseline': str(baseline) if baseline else None,
               'baseline_sha256': baseline_hash, 'receipt': str(Path(receipt).resolve()), 'receipt_sha256': pin})
        records = []
        for side in ('left', 'right'):
            if verify_authority(receipt) != pin:
                raise ValueError('Offline receipt changed between reads')
            progress(f'Reading {side} calibration record...')
            read_one(run/side, side, receipt)
            record = extract_run(run/side, side)
            p.save(run/f'{side}.json', record)
            records.append(record)
            if len(records) == 2 and records[0]['nexus_identity'] != record['nexus_identity']:
                raise ValueError('Nexus restarted between sides; pair is incomplete')
        snapshot = {'schema': 'g7pro_calibration_record_capture_v1', 'utc': utc(), 'started_utc': start,
                    'device': p.TARGET, 'revision': '0231', 'provider_firmware_tuple': [1, 2, 561, 2],
                    'firmware_image_authenticity': 'unproven; version tuple is not an image hash',
                    'settings_label_user_reported': label, 'settings_verified': False,
                    'persistent_writes': 0, 'calibration_commands': 0, 'read_requests': 2,
                    'records': records, 'qualification': BOUNDARY, 'offline_receipt_sha256': pin}
        validate_snapshot(snapshot)
        diff = compare(baseline_data, snapshot) if baseline_data else None
        if diff is not None:
            diff['baseline_path'] = str(Path(baseline).resolve())
            diff['baseline_sha256'] = baseline_hash
            p.save(run/'comparison.json', diff)
        path = run/'snapshot.json'
        p.save(path, snapshot)
        return path
    except Exception as error:
        if run is not None:
            p.save(run/'failure.json', {'utc': utc(), 'status': 'INCOMPLETE', 'error': str(error),
                                      'retry': False, 'complete_snapshot': False})
        raise
    finally:
        LOCK.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--receipt', type=Path, default=DEFAULT_RECEIPT)
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument('--label', required=True)
    parser.add_argument('--baseline', type=Path)
    args = parser.parse_args()
    path = capture_pair(args.output, args.receipt, args.label, args.baseline, progress=print)
    print(f'Saved: {path}')


if __name__ == '__main__': main()

