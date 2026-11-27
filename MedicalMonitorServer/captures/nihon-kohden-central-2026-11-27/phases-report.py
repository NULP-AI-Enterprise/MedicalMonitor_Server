"""Reads a central-phases capture and says, per phase: how many stream packets a second came,
how many were missing by their sequence numbers, what the packets looked like, and which
answers the monitor gave to which requests.

usage: phases-report.py capture.raw.txt
"""
import sys
from collections import Counter, OrderedDict

phases = []          # [name, stats]
current = None


def new_phase(name):
    global current
    current = {"name": name, "per_second": OrderedDict(), "gaps": [], "sizes": Counter(), "blocks": Counter(),
               "req": Counter(), "answers": Counter(), "first_gap": None, "start": None, "subs": 0}
    phases.append(current)


last_seq = None
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    if line.startswith("#"):
        if " phase " in line:
            parts = line.split(" phase ", 1)
            new_phase(parts[1].strip())
            current["start"] = parts[0].split()[1]
        continue
    p = line.split()
    if len(p) < 4 or current is None:
        continue
    time = p[0]
    if p[1] == "REQ":
        if p[2] == "0222":
            current["subs"] += 1
        else:
            current["req"][p[2]] += 1
        continue
    if p[1] != "IN":
        continue
    raw = bytes(int(x, 16) for x in p[4:])
    if raw[0] == 0x02 and raw[1] in (0x0B, 0x12):
        sec = time[:8]
        d = current["per_second"].setdefault(sec, [0, 0])
        d[0] += 1
        if raw[1] == 0x0B:
            d[1] += 1 if raw[23] == 1 else 0
            seq = (raw[24] << 8) | raw[25]
            if last_seq is not None:
                missed = ((seq - last_seq) & 0xFFFF) - 1
                if missed:
                    current["gaps"].append((time, missed))
                    if current["first_gap"] is None:
                        current["first_gap"] = time
            last_seq = seq
            current["sizes"][len(raw)] += 1
            current["blocks"][(raw[22], raw[23])] += 1
        else:
            current["sizes"][("0212", len(raw))] += 1
    else:
        kind = "%02X%02X" % (raw[0], raw[1])
        typ = "%02X%02X" % (raw[12], raw[13]) if len(raw) >= 14 else "----"
        current["answers"][f"{kind}/{typ}/{len(raw)}"] += 1

for ph in phases:
    total = sum(a for a, _ in ph["per_second"].values())
    seconds = len(ph["per_second"])
    print(f"\n== phase {ph['name']}  (from {ph['start']})")
    print("   stream packets per second (all/with numerics):",
          " ".join(f"{s[-2:]}:{a}/{b}" for s, (a, b) in ph["per_second"].items()))
    print(f"   {total} packets in {seconds} clock seconds; missing by sequence: {sum(m for _, m in ph['gaps'])}"
          f" in {len(ph['gaps'])} gaps" + (f", first at {ph['first_gap']}" if ph["first_gap"] else ""))
    print("   packet sizes:", dict(ph["sizes"]), " (wave blocks, numerics flag):", dict(ph["blocks"]))
    print("   subscribe requests sent:", ph["subs"], " central requests sent:", dict(ph["req"]))
    print("   control datagrams from the monitor (kind/type/length):", dict(ph["answers"]))
