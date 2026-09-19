#!/usr/bin/env python3
"""
Decode Mindray uMec10 HL7 ORU (from TCP 4601) into readable vitals.

Three modes:
  python3 decode.py --live [ip]     LIVE dashboard: connect once, archive raw
                                    HL7 to hl7_messages/, redraw decoded vitals
                                    in real time, append changes to a CSV.
  python3 decode.py [ip]            one snapshot -> printed report + CSV
  python3 decode.py somefile.log    decode a previously saved hl7_*.log

The monitor allows only ONE TCP connection at a time, so run --live INSTEAD of
the ./read logger (this mode archives the raw stream itself).

Notes:
  * Cyrillic labels are byte-shifted +0x10 ('ЗББ' -> 'ЧСС' = HR); we un-shift.
  * The HL7 feed carries patient info, alarm limits, and episodic NIBP. It does
    NOT carry continuously-streaming HR/SpO2 numbers (those are binary-only),
    so the live view refreshes whenever a value like NIBP changes.
"""
import socket, sys, time, os, re

MONITOR_IP = "192.168.0.100"
MONITOR_PORT = 4601
OUTDIR = "hl7_messages"

PARAM = {
    "101": ("Heart Rate (HR)", "bpm"),
    "102": ("PVCs", "/min"),
    "105": ("ST-I", "mV"), "106": ("ST-II", "mV"), "107": ("ST-III", "mV"),
    "108": ("ST-aVR", "mV"), "109": ("ST-aVL", "mV"), "110": ("ST-aVF", "mV"),
    "117": ("ST-V", "mV"),
    "151": ("Respiration Rate (RR)", "rpm"),
    "160": ("SpO2", "%"),
    "161": ("Pulse Rate (PR)", "bpm"),
    "162": ("Perfusion Index (PI)", "%"),
    "170": ("NIBP Systolic", "mmHg"),
    "171": ("NIBP Diastolic", "mmHg"),
    "172": ("NIBP Mean", "mmHg"),
    "200": ("Temperature T1", "°C"),
    "201": ("Temperature T2", "°C"),
    "202": ("Temperature TD", "°C"),
}
LVL = {"0": "high", "1": "med", "2": "low", "3": "off"}
# Mindray "no valid measurement" sentinels.
INVALID = {"-100", "-1000", "-10000"}


def is_valid(v: str) -> bool:
    return v not in INVALID


def fmt_meas(v: str) -> str:
    return v if is_valid(v) else "—"


def deshift(raw: bytes) -> str:
    out = bytearray()
    for x in raw:
        out.append(x + 0x10 if 0x80 <= x <= 0xEF else x)
    return out.decode("cp1251", "replace")


class State:
    def __init__(self):
        self.labelmap = {}
        self.measured = {}
        self.hi, self.lo, self.lvl, self.onoff = {}, {}, {}, {}
        self.patient = {"name": "", "id": "", "dob": "", "sex": "",
                        "bed": "", "height": "", "weight": ""}
        self.msg_count = 0
        self.last_change = None

    def name(self, pid):
        if pid in PARAM:
            return PARAM[pid][0]
        if pid in self.labelmap and self.labelmap[pid].strip():
            return self.labelmap[pid]
        return "param %s" % pid

    def unit(self, pid):
        return PARAM[pid][1] if pid in PARAM else ""


def apply_message(st, msg):
    st.msg_count += 1
    for seg in msg.split(b"\r"):
        f = seg.split(b"|")
        tag = f[0]
        if tag == b"PID" and len(f) > 5:
            nm = f[5].split(b"^")
            st.patient["name"] = deshift(b" ".join(x for x in nm if x)).strip()
            st.patient["id"] = f[3].decode("latin1", "replace")
            if len(f) > 7:
                st.patient["dob"] = f[7].decode("latin1", "replace")
            if len(f) > 8:
                st.patient["sex"] = f[8].decode("latin1", "replace")
        elif tag == b"PV1" and len(f) > 3:
            for p in f[3].split(b"^"):
                if p.isdigit() and len(p) <= 4:
                    st.patient["bed"] = p.decode()
                    break
        elif tag == b"OBX" and len(f) > 5:
            code3 = f[3]
            code = code3.split(b"^")[0].decode("latin1")
            label = code3.split(b"^")[1] if b"^" in code3 else b""
            sub = f[4].decode("latin1")
            vs = f[5].decode("latin1", "replace")
            if code == "2025":
                pid = vs.split("^")[0]
                nm = f[5].split(b"^")[1] if b"^" in f[5] else b""
                st.labelmap[pid] = deshift(nm)
            elif code == "2002":
                st.hi[sub] = vs
            elif code == "2003":
                st.lo[sub] = vs
            elif code == "2009":
                st.lvl[sub] = vs.split("^")[0]
            elif code == "2004":
                st.onoff[sub] = vs.split("^")[0]
            elif code == "2301":
                st.patient["bed"] = vs
            elif code == "51":
                st.patient["weight"] = vs
            elif code == "52":
                st.patient["height"] = vs
            elif code in PARAM and label:
                if st.measured.get(code) != vs:
                    st.measured[code] = vs
                    st.last_change = time.time()
            elif code.isdigit() and label and int(code) < 1000:
                st.measured.setdefault(code, vs)


def split_mllp(buf):
    msgs, i = [], 0
    while True:
        a = buf.find(b"\x0b", i)
        if a < 0:
            break
        b = buf.find(b"\x1c", a)
        if b < 0:
            break
        msgs.append(buf[a + 1:b])
        i = b + 1
    return msgs, buf[i:]


def render(st, src, live=False):
    L = []
    L.append("=" * 62)
    hdr = " Mindray uMec10 — decoded vitals   (%s)" % src
    L.append(hdr)
    if live:
        upd = time.strftime("%H:%M:%S", time.localtime(st.last_change)) if st.last_change else "--:--:--"
        L.append(" msgs:%d   last value change:%s   %s"
                 % (st.msg_count, upd, time.strftime("%H:%M:%S")))
    L.append("=" * 62)
    p = st.patient
    L.append(" Patient : %s   ID:%s" % (p["name"] or "(none)", p["id"][:24]))
    L.append(" Sex:%s  DOB:%s  Bed:%s  H:%s cm  W:%s kg"
             % (p["sex"], p["dob"], p["bed"], p["height"], p["weight"]))
    L.append("-" * 62)
    L.append(" MEASURED VITALS")
    meas = [k for k in st.measured if k not in ("51", "52")]
    if meas:
        for pid in sorted(meas, key=lambda x: int(x) if x.isdigit() else 9999):
            L.append("   %-26s %8s %s"
                     % (st.name(pid), fmt_meas(st.measured[pid]), st.unit(pid)))
    else:
        L.append("   (none yet — take an NIBP reading to populate)")
    L.append("-" * 62)
    L.append(" ALARM LIMITS")
    L.append("   %-26s %8s %8s  %-5s %s"
             % ("parameter", "low", "high", "level", "unit"))
    for pid in sorted(set(list(st.hi) + list(st.lo)),
                      key=lambda x: int(x) if x.isdigit() else 9999):
        L.append("   %-26s %8s %8s  %-5s %s"
                 % (st.name(pid), st.lo.get(pid, "-"), st.hi.get(pid, "-"),
                    LVL.get(st.lvl.get(pid, ""), st.lvl.get(pid, "")),
                    st.unit(pid)))
    return "\n".join(L)


def write_csv(st, path):
    with open(path, "w", encoding="utf-8") as fp:
        fp.write("param_id,parameter,measured,low_limit,high_limit,alarm_level,unit\n")
        ids = sorted(set(list(st.measured) + list(st.hi) + list(st.lo)),
                     key=lambda x: int(x) if x.isdigit() else 9999)
        for pid in ids:
            if pid in ("51", "52"):
                continue
            mv = st.measured.get(pid, "")
            fp.write("%s,%s,%s,%s,%s,%s,%s\n"
                     % (pid, st.name(pid), mv if is_valid(mv) else "",
                        st.lo.get(pid, ""), st.hi.get(pid, ""),
                        LVL.get(st.lvl.get(pid, ""), ""), st.unit(pid)))


def append_trend(st, path):
    """Append current measured vitals as one timestamped row (for trends)."""
    ids = sorted([k for k in st.measured if k not in ("51", "52")],
                 key=lambda x: int(x) if x.isdigit() else 9999)
    new = not os.path.exists(path)
    with open(path, "a", encoding="utf-8") as fp:
        if new:
            fp.write("timestamp," + ",".join(st.name(i) for i in ids) + "\n")
        fp.write(time.strftime("%Y-%m-%d %H:%M:%S") + ","
                 + ",".join(fmt_meas(st.measured.get(i, "")).replace("—", "")
                            for i in ids) + "\n")


def run_live(ip):
    os.makedirs(OUTDIR, exist_ok=True)
    st = State()
    src = "%s:%d LIVE" % (ip, MONITOR_PORT)
    trend = os.path.join(OUTDIR, "vitals_trend.csv")
    last_render = 0.0
    last_meas_sig = None
    while True:
        try:
            s = socket.socket(); s.settimeout(5)
            s.connect((ip, MONITOR_PORT))
        except Exception as e:
            sys.stdout.write("\033[2J\033[H connecting to %s ... (%s)\n" % (ip, e))
            sys.stdout.flush(); time.sleep(2); continue
        buf = b""
        logpath = os.path.join(OUTDIR, "hl7_" + time.strftime("%Y-%m-%d") + ".log")
        try:
            while True:
                try:
                    d = s.recv(16384)
                except socket.timeout:
                    d = None            # connection alive, just idle
                else:
                    if d == b"":        # EOF: monitor closed -> reconnect
                        break
                if d:
                    buf += d
                    msgs, buf = split_mllp(buf)
                    with open(logpath, "ab") as lf:
                        for m in msgs:
                            if b"MSH" not in m:
                                continue
                            apply_message(st, m)
                            pretty = m.replace(b"\r", b"\n")
                            lf.write(b"----- " + time.strftime("%H:%M:%S").encode()
                                     + b" -----\n" + pretty + b"\n\n")
                    # record a trend row whenever measured vitals change
                    sig = tuple(sorted(st.measured.items()))
                    if sig != last_meas_sig and any(k not in ("51", "52") for k in st.measured):
                        last_meas_sig = sig
                        append_trend(st, trend)
                        write_csv(st, os.path.join(OUTDIR, "decoded_latest.csv"))
                now = time.time()
                if now - last_render >= 1.0:
                    frame = render(st, src, live=True)
                    sys.stdout.write("\033[2J\033[H" + frame
                                     + "\n\n (Ctrl-C to stop) trend:%s\n" % trend)
                    sys.stdout.flush()
                    # always keep readable results on disk, updated every second
                    with open(os.path.join(OUTDIR, "decoded_latest.txt"),
                              "w", encoding="utf-8") as tf:
                        tf.write(frame + "\n")
                    write_csv(st, os.path.join(OUTDIR, "decoded_latest.csv"))
                    last_render = now
        except (ConnectionResetError, BrokenPipeError, OSError):
            pass
        finally:
            try: s.close()
            except Exception: pass
        time.sleep(1)  # reconnect


def run_once(src_arg):
    if src_arg and os.path.exists(src_arg):
        with open(src_arg, "rb") as fp:
            raw = fp.read()
        blocks = re.split(rb"-----[^\n]*-----\n", raw)
        msgs = [b.strip().replace(b"\n", b"\r") for b in blocks
                if b.strip().startswith(b"MSH")]
        src = "file %s" % src_arg
    else:
        ip = src_arg or MONITOR_IP
        s = socket.socket(); s.settimeout(4); s.connect((ip, MONITOR_PORT))
        buf = b""; t0 = time.time()
        while time.time() - t0 < 8:
            try:
                d = s.recv(16384)
                if not d: break
                buf += d
            except socket.timeout:
                break
        s.close()
        msgs, _ = split_mllp(buf)
        src = "%s:%d" % (ip, MONITOR_PORT)
    st = State()
    for m in msgs:
        if b"MSH" in m:
            apply_message(st, m)
    print(render(st, src))
    os.makedirs(OUTDIR, exist_ok=True)
    out = os.path.join(OUTDIR, "decoded_%s.csv" % time.strftime("%Y-%m-%d_%H%M%S"))
    write_csv(st, out)
    print("-" * 62)
    print(" CSV written: %s" % out)


def main():
    args = sys.argv[1:]
    if args and args[0] == "--live":
        run_live(args[1] if len(args) > 1 else MONITOR_IP)
    else:
        run_once(args[0] if args else "")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nstopped.")
