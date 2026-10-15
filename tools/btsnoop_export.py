#!/usr/bin/env python3
"""btsnoop_hci.log -> GAIA frame fixtures. Usage:
  python tools/btsnoop_export.py btsnoop.log --actions actions.csv --out OUTDIR [--fw 6.21.5]
actions.csv: lines `epoch_ms,label` (phone clock; written by tools/capture-log.ps1)."""
import argparse, csv, json, struct, sys, os
from collections import defaultdict

EPOCH_DELTA_US = 0x00DCDDB30F2F8000  # btsnoop (0000-01-01) -> unix epoch, microseconds

def records(path):
    with open(path, 'rb') as f:
        h = f.read(16)
        if h[:8] != b'btsnoop\0':
            sys.exit('not a btsnoop file')
        while True:
            r = f.read(24)
            if len(r) < 24:
                return
            orig, incl, flags, _drops, ts = struct.unpack('>IIIIq', r)
            data = f.read(incl)
            yield (ts - EPOCH_DELTA_US) // 1000, flags & 1, data  # ms, dir(0=tx host->ctrl,1=rx), H4 bytes

def rfcomm_payloads(path):
    """yield (ts_ms, dir, dlci, payload) for RFCOMM data frames."""
    acl_buf = {}                       # (handle,dir) -> [expected_total, bytearray]
    rfcomm_cids = defaultdict(set)     # handle -> {cid}
    fc_buf = {}
    for ts, d, data in records(path):
        if not data or data[0] != 2 or len(data) < 5:
            continue
        hf, ln = struct.unpack('<HH', data[1:5])
        handle, pb = hf & 0x0FFF, (hf >> 12) & 3
        body = data[5:5 + ln]
        key = (handle, d)
        if pb in (0, 2):
            if len(body) < 4:
                continue
            l2len = struct.unpack('<H', body[:2])[0]
            acl_buf[key] = [l2len + 4, bytearray(body)]
        elif pb == 1 and key in acl_buf:
            acl_buf[key][1].extend(body)
        else:
            continue
        exp, buf = acl_buf[key]
        if len(buf) < exp:
            continue
        del acl_buf[key]
        l2len, cid = struct.unpack('<HH', bytes(buf[:4]))
        pl = bytes(buf[4:4 + l2len])
        if cid == 1:  # signalling: map RFCOMM (PSM 3) channels
            i = 0
            while i + 4 <= len(pl):
                code, _id, sl = pl[i], pl[i + 1], struct.unpack('<H', pl[i + 2:i + 4])[0]
                p = pl[i + 4:i + 4 + sl]
                if code == 2 and len(p) >= 4 and struct.unpack('<H', p[:2])[0] == 3:
                    rfcomm_cids[handle].add(struct.unpack('<H', p[2:4])[0])
                    fc_buf[(handle, _id)] = True
                elif code == 3 and len(p) >= 8 and fc_buf.get((handle, _id)):
                    dcid, scid, result = struct.unpack('<HHH', p[:6])
                    if result == 0:
                        rfcomm_cids[handle].update((dcid, scid))
                i += 4 + sl
            continue
        if cid not in rfcomm_cids[handle] or len(pl) < 4:
            continue
        addr, ctrl = pl[0], pl[1] & 0xEF
        dlci = addr >> 2
        if ctrl != 0xEF or dlci < 2:
            continue
        pos = 3 if pl[2] & 1 else 4
        if (pl[1] & 0x10) and True:   # P/F set on UIH => credit byte (credit-based flow control)
            pos += 1
        payload = pl[pos:len(pl) - 1]  # strip FCS
        if payload:
            yield ts, d, dlci, payload

def gaia_frames(path):
    bufs = defaultdict(bytearray)
    for ts, d, dlci, payload in rfcomm_payloads(path):
        b = bufs[(d, dlci)]
        b.extend(payload)
        while True:
            while b and b[0] != 0xFF:
                del b[0]
            if len(b) < 8:
                break
            if b[1] not in (3, 4):
                del b[0]
                continue
            n = struct.unpack('>H', bytes(b[2:4]))[0]
            if len(b) < 8 + n:
                break
            fr = bytes(b[:8 + n])
            del b[:8 + n]
            yield ts, d, fr

REDACT = {'0495': {'140E', '1491', '150D', '150E', '1513', '1514', '2902', '1202', '1203'}, '001D': {'0103'}}  # MACs, device names, serial

def redact(fr):
    cmd = f'{fr[6]:02X}{fr[7]:02X}'
    if cmd in REDACT.get(f'{fr[4]:02X}{fr[5]:02X}', ()) or (fr[4:6] == b'\x04\x95' and fr[6] == 0x29):
        return fr[:8] + bytes(len(fr) - 8), True
    return fr, False

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log'); ap.add_argument('--actions'); ap.add_argument('--out', required=True)
    ap.add_argument('--ts-offset-ms', type=int, default=0, help='add to frame ts (Android btsnoop stamps local time: use -7200000 for CEST)')
    ap.add_argument('--device', default='M5AEBT'); ap.add_argument('--fw', default='')
    a = ap.parse_args()
    acts = []
    if a.actions:
        for row in csv.reader(open(a.actions, encoding='utf-8')):
            if len(row) >= 2:
                acts.append((int(float(row[0])), row[1]))
        acts.sort()
    out, j = [], 0
    for ts, d, fr in gaia_frames(a.log):
        ts += a.ts_offset_ms
        while j + 1 < len(acts) and acts[j + 1][0] <= ts:
            j += 1
        act = acts[j][1] if acts and acts[j][0] <= ts else ''
        fr, red = redact(fr)
        out.append({'ts': ts, 'dir': 'tx' if d == 0 else 'rx', 'vendor': f'{fr[4]:02X}{fr[5]:02X}',
                    'cmd': f'{fr[6]:02X}{fr[7]:02X}', 'payload': ' '.join(f'{x:02X}' for x in fr[8:]),
                    'frame': ' '.join(f'{x:02X}' for x in fr), 'action': act, **({'redacted': True} if red else {})})
    os.makedirs(a.out, exist_ok=True)
    json.dump(out, open(os.path.join(a.out, 'frames.json'), 'w'), indent=1)
    json.dump({'device': a.device, 'fw': a.fw, 'frames': len(out), 'actions': [x[1] for x in acts]},
              open(os.path.join(a.out, 'meta.json'), 'w'), indent=1)
    print(f'{len(out)} GAIA frames -> {a.out}')

if __name__ == '__main__':
    main()
