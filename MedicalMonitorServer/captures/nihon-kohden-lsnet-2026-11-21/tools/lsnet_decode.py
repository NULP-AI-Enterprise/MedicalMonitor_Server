"""Decode an LS-NET raw capture (lines: time dir ports len hex...) written by the lsnet-*.ps1 tools.
usage: python -I lsnet_decode.py <capture.raw.txt>
"""
import sys
from datetime import datetime

NAMES = {
    (0x01, 0x00): ("HR", 1), (0x02, 0x00): ("VPC", 1),
    (0x07, 0x00): ("SpO2", 1), (0x07, 0x01): ("PR(spo2)", 1),
    (0x09, 0x00): ("NIBP S", 1), (0x09, 0x01): ("NIBP D", 1), (0x09, 0x02): ("NIBP M", 1),
    (0x0B, 0x00): ("TSKIN", 0.1), (0x0E, 0x00): ("TRECT", 0.1),
    (0x2C, 0x00): ("ART S", 1), (0x2C, 0x01): ("ART D", 1), (0x2C, 0x02): ("ART M", 1), (0x2C, 0x06): ("PR(art)", 1),
    (0x35, 0x00): ("CVP S", 1), (0x35, 0x01): ("CVP D", 1), (0x35, 0x02): ("CVP M", 1),
}


def s16(b, i):
    v = (b[i] << 8) | b[i + 1]
    return v - 65536 if v & 0x8000 else v


def numerics(b, at):
    """Numeric block: [n][00] n*code(2) ... [count][00] then count*10-byte records."""
    n = b[at]
    waves = [(b[at + 2 + 2 * i], b[at + 3 + 2 * i]) for i in range(n)]
    p = at + 2 + 2 * n
    count_at = p + 36
    count = b[count_at]
    out = {}
    q = count_at + 2
    for _ in range(count):
        code = (b[q], b[q + 1]); value = s16(b, q + 8); status = b[q + 2:q + 8].hex()
        out[code] = (None if value == -32768 else value, status)
        q += 10
    return waves, out, b[p:count_at + 2]


rows = []
for line in open(sys.argv[1]):
    p = line.split()
    if len(p) < 5 or p[1] != "IN":
        continue
    rows.append((datetime.strptime(p[0], "%H:%M:%S.%f"), p[2], bytes(int(x, 16) for x in p[4:])))

waves = [(t, b) for t, ports, b in rows if b[:2] == b"\x02\x0b" and len(b) >= 40]
print(f"waveform packets: {len(waves)}")
if waves:
    seqs = [(b[24] << 8) | b[25] for _, b in waves]
    span = (waves[-1][0] - waves[0][0]).total_seconds()
    lost = sum(max(0, ((b - a) & 0xFFFF) - 1) for a, b in zip(seqs, seqs[1:]))
    print(f"  sequence {seqs[0]:#06x}..{seqs[-1]:#06x}, {span:.1f} s, lost {lost}")
    channels = {}
    for t, b in waves:
        nblocks = b[22]
        at = 36
        for _ in range(nblocks):
            code = (b[at], b[at + 1]); fmt = b[at + 2]; n = b[at + 3]
            samples = [s16(b, at + 4 + 2 * i) for i in range(n)]
            channels.setdefault((code, fmt), []).extend(samples)
            at += 4 + 2 * n
    for (code, fmt), samples in channels.items():
        rate = len(samples) / span if span else 0
        print(f"  channel {code[0]:02X}{code[1]:02X} fmt {fmt:#04x}: {len(samples)} samples, ~{rate:.0f}/s, min {min(samples)}, max {max(samples)}")
        # crude beat count: rising through 60% of the maximum
        thr = min(samples) + 0.6 * (max(samples) - min(samples))
        peaks = [i for i in range(1, len(samples)) if samples[i - 1] < thr <= samples[i]]
        if len(peaks) > 2:
            gaps = [b - a for a, b in zip(peaks, peaks[1:])]
            print(f"    {len(peaks)} peaks, mean gap {sum(gaps) / len(gaps):.1f} samples -> at 125/s that is {60 * 125 / (sum(gaps) / len(gaps)):.0f} per minute")
        print("    first 40:", samples[:40])
    hdr = {}
    for _, b in waves:
        hdr.setdefault(b[20:24].hex(" ") + " | " + b[26:36].hex(" "), 0)
        hdr[b[20:24].hex(" ") + " | " + b[26:36].hex(" ")] += 1
    print("  header variants (bytes 20-23 | 26-35):")
    for k, v in sorted(hdr.items(), key=lambda kv: -kv[1])[:8]:
        print(f"    {v:5d} x {k}")

nums = [(t, b, 24) for t, ports, b in rows if b[:2] == b"\x02\x12"]
for t, b in waves:
    if b[23] == 1:
        at = 36
        for _ in range(b[22]):
            at += 4 + 2 * b[at + 3]
        nums.append((t, b, at))
nums.sort(key=lambda x: x[0])
print(f"numeric blocks: {len(nums)}")
if nums:
    gaps = [(b[0] - a[0]).total_seconds() for a, b in zip(nums, nums[1:])]
    if gaps:
        print(f"  interval: mean {sum(gaps) / len(gaps):.2f} s, min {min(gaps):.2f}, max {max(gaps):.2f}")
    wl, vals, mid = numerics(nums[-1][1], nums[-1][2])
    print("  waveform list:", " ".join(f"{a:02X}{b:02X}" for a, b in wl))
    print("  bytes between list and records:", mid.hex(" "))
    for code, (value, status) in vals.items():
        name, scale = NAMES.get(code, ("", 1))
        shown = "---" if value is None else (f"{value * scale:g}")
        print(f"    {code[0]:02X}{code[1]:02X} (HL7 {code[0]:03d}{code[1]:03d}) {name:9s} {shown:>7}  status {status}")
