"""List what changes over time in an LS-NET recording: numeric record status/value, block headers,
and the answers to the polled requests.
usage: python -I lsnet_changes.py <recording.raw.txt> [from HH:MM:SS]
"""
import sys

path = sys.argv[1]
since = sys.argv[2] if len(sys.argv) > 2 else ""
last = {}
events = []


def note(t, key, value, show=None):
    if last.get(key) != value:
        if key in last:
            events.append((t, key, show(last[key], value) if show else f"{last[key]} -> {value}"))
        else:
            events.append((t, key, f"first seen: {value}"))
        last[key] = value


def diff(a, b):
    if len(a) != len(b):
        return f"length {len(a)} -> {len(b)}"
    d = [f"[{i}] {x:02X}->{y:02X}" for i, (x, y) in enumerate(zip(a, b)) if x != y]
    return ", ".join(d[:24]) + (f" ... (+{len(d) - 24} more)" if len(d) > 24 else "")


def numeric_block(t, b, at):
    n = b[at]
    note(t, "wave list", b[at + 2:at + 2 + 2 * n].hex())
    p = at + 2 + 2 * n
    note(t, "block header (36 bytes)", bytes(b[p:p + 36]), diff)
    count = b[p + 36]
    q = p + 38
    codes = []
    for _ in range(count):
        if q + 10 > len(b):
            break
        code = b[q:q + 2].hex().upper()
        codes.append(code)
        note(t, f"{code} status", b[q + 2:q + 8].hex(" ").upper())
        v = int.from_bytes(b[q + 8:q + 10], "big", signed=True)
        note(t, f"{code} value", "---" if v == -32768 else v)
        q += 10
    note(t, "codes present", " ".join(codes))


counts = {}
for line in open(path):
    p = line.split()
    if len(p) < 6 or p[1] != "IN":
        continue
    t = p[0]
    b = bytes(int(x, 16) for x in p[4:])
    kind = b[:2].hex().upper()
    if kind == "020B":
        note(t, "wave pkt bytes 20-22", b[20:23].hex(" "))      # byte 23 only says whether numerics follow
        hdr = bytearray(b[26:36]); hdr[2] = 0          # byte 28 is the beat marker, it changes every beat
        note(t, "wave pkt bytes 26-35 (beat marker masked)", bytes(hdr), diff)
        if b[23] == 1:
            at = 36
            for _ in range(b[22]):
                at += 4 + 2 * b[at + 3]
            numeric_block(t, b, at)
    elif kind == "0212":
        numeric_block(t, b, 24)
    elif len(b) >= 32:
        typ = b[12:14].hex().upper()
        key = f"{kind}/{typ}"
        counts[key] = counts.get(key, 0) + 1
        if b[0] == 0x02 or b[0] == 0x05:
            note(t, f"{key} payload ({len(b) - 32} bytes)", bytes(b[32:]), diff)

print("polled / notified kinds:", counts)
shown = [e for e in events if e[0] >= since]
first = [e for e in shown if e[2].startswith("first seen")]
changes = [e for e in shown if not e[2].startswith("first seen")]
print(f"{len(changes)} changes" + (f" since {since}" if since else "") + f" (and {len(first)} first sightings)")
for t, key, what in changes:
    print(f"{t}  {key}: {what[:400]}")
