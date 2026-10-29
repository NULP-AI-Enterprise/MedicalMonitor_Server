'use strict';

// ---------------------------------------------------------------------------
// Contract — mirrors Monitoring.Shared/HubMethods.cs. Enums travel as strings
// because the hub registers JsonStringEnumConverter.
// ---------------------------------------------------------------------------
const Hub = {
    route: '/hubs/vitals',
    receiveVitals: 'ReceiveVitals',
    patientAdded: 'PatientAdded',
    patientRemoved: 'PatientRemoved',
    alertTriggered: 'AlertTriggered',
    alertCleared: 'AlertCleared',
    alertAcknowledged: 'AlertAcknowledged',
    acknowledgeAlert: 'AcknowledgeAlert',
    getPatients: 'GetPatients',
    getActiveAlerts: 'GetActiveAlerts'
};

// Which numeric block on the card a given vital sign colours.
const VITAL_SLOT = {
    HeartRate: 'hr',
    SpO2: 'spo2',
    Systolic: 'nibp',
    Diastolic: 'nibp',
    RespiratoryRate: 'resp',
    Temperature: 'temp'
};

// How many seconds of trace are visible before the sweep wraps. Every lane uses the
// same span so the three traces stay time-aligned with each other.
const SWEEP_SECONDS = 5;

const TRACES = [
    { key: 'ecg',   waveform: 'ECG_II',      label: 'ECG II', color: '#39FF14', sampleRate: 250 },
    { key: 'pleth', waveform: 'Pleth',       label: 'PLETH',  color: '#00FFFF', sampleRate: 50 },
    { key: 'resp',  waveform: 'Respiration', label: 'RESP',   color: '#FFFF00', sampleRate: 50 }
];

// ---------------------------------------------------------------------------
// WaveformDrawer — a sweeping ring-buffer trace on a canvas.
// ---------------------------------------------------------------------------
class WaveformDrawer {
    constructor(canvas, color, sampleRate) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.color = color;
        this.sampleRate = sampleRate;

        this.size = Math.max(2, Math.round(sampleRate * SWEEP_SECONDS));
        this.buffer = new Float32Array(this.size);
        this.filled = new Uint8Array(this.size);
        this.head = 0;

        // Samples arrive in 500 ms batches but must be *played out* at the real sample
        // rate; writing a whole batch at once makes the sweep lurch twice a second.
        this.pending = [];
        this.carry = 0;

        this.width = 0;
        this.height = 0;
        this.resize();
    }

    resize() {
        const rect = this.canvas.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        const cssWidth = Math.max(1, Math.round(rect.width));
        const cssHeight = Math.max(1, Math.round(rect.height));

        const backingWidth = Math.round(cssWidth * dpr);
        const backingHeight = Math.round(cssHeight * dpr);
        if (this.canvas.width === backingWidth && this.canvas.height === backingHeight) return;

        this.canvas.width = backingWidth;
        this.canvas.height = backingHeight;
        this.width = cssWidth;
        this.height = cssHeight;
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    enqueue(samples) {
        if (!samples || !samples.length) return;
        for (let i = 0; i < samples.length; i++) this.pending.push(samples[i]);

        // Never hoard more than a couple of seconds of backlog: if the tab was
        // hidden or the network stalled, showing stale trace is worse than skipping.
        const maxBacklog = this.sampleRate * 2;
        if (this.pending.length > maxBacklog) {
            this.pending.splice(0, this.pending.length - maxBacklog);
        }
    }

    /** Moves up to `dtSeconds` worth of queued samples into the ring buffer. */
    advance(dtSeconds) {
        if (!this.pending.length) return;

        let quota = dtSeconds * this.sampleRate;
        // Gently catch up when a backlog has built rather than letting it drift.
        if (this.pending.length > this.sampleRate) quota *= 1.5;

        this.carry += quota;
        let count = Math.floor(this.carry);
        if (count <= 0) return;
        this.carry -= count;

        count = Math.min(count, this.pending.length);
        for (let i = 0; i < count; i++) {
            this.buffer[this.head] = this.pending[i];
            this.filled[this.head] = 1;
            this.head = (this.head + 1) % this.size;
        }
        this.pending.splice(0, count);
    }

    clear() {
        this.buffer.fill(0);
        this.filled.fill(0);
        this.pending.length = 0;
        this.carry = 0;
        this.head = 0;
    }

    drawBackground() {
        const { ctx, width, height } = this;
        ctx.fillStyle = '#0a0a0a';
        ctx.fillRect(0, 0, width, height);

        ctx.strokeStyle = '#1e1e1e';
        ctx.lineWidth = 1;
        ctx.beginPath();
        const step = 25;
        for (let y = step; y < height; y += step) {
            ctx.moveTo(0, y + 0.5);
            ctx.lineTo(width, y + 0.5);
        }
        for (let x = step; x < width; x += step) {
            ctx.moveTo(x + 0.5, 0);
            ctx.lineTo(x + 0.5, height);
        }
        ctx.stroke();
    }

    draw() {
        this.resize();
        this.drawBackground();

        const { ctx, width, height, size, head } = this;
        const stepX = width / size;
        // Blank a slice just ahead of the write head — the erase bar of a real monitor.
        const gap = Math.max(2, Math.round(size * 0.04));

        ctx.beginPath();

        let column = -1;
        let columnMin = 0;
        let columnMax = 0;
        let started = false;

        const flush = () => {
            if (column < 0) return;
            if (!started) {
                ctx.moveTo(column, columnMin);
                started = true;
            } else {
                ctx.lineTo(column, columnMin);
            }
            // Denser than one sample per pixel: draw the column's full extent so a
            // narrow R spike survives instead of being decimated away.
            if (columnMax !== columnMin) ctx.lineTo(column, columnMax);
        };

        for (let i = 0; i < size; i++) {
            const distanceToHead = (i - head + size) % size;
            if (distanceToHead < gap || !this.filled[i]) {
                flush();
                column = -1;
                started = false;
                continue;
            }

            const y = (height / 2) - (this.buffer[i] * (height * 0.42));
            const x = Math.round(i * stepX);

            if (x !== column) {
                flush();
                column = x;
                columnMin = y;
                columnMax = y;
            } else {
                if (y < columnMin) columnMin = y;
                if (y > columnMax) columnMax = y;
            }
        }
        flush();

        // A translucent wide pass under a crisp thin pass reads as a CRT glow and is
        // far cheaper than canvas shadowBlur, which would stall the whole grid.
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ctx.strokeStyle = this.color;
        ctx.globalAlpha = 0.25;
        ctx.lineWidth = 4;
        ctx.stroke();
        ctx.globalAlpha = 1;
        ctx.lineWidth = 1.6;
        ctx.stroke();
    }
}

// ---------------------------------------------------------------------------
// Formatting helpers
// ---------------------------------------------------------------------------
const fmt = {
    int: (v) => (Number.isFinite(v) ? String(Math.round(v)) : '--'),
    temp: (v) => (Number.isFinite(v) ? v.toFixed(1) : '--')
};

function severityRank(severity) {
    return severity === 'Critical' ? 2 : severity === 'Warning' ? 1 : 0;
}

/** Highest-severity unacknowledged alert, falling back to the highest acknowledged one. */
function dominantAlert(alerts) {
    let best = null;
    for (const alert of alerts.values()) {
        if (!best) { best = alert; continue; }
        const bestScore = severityRank(best.severity) + (best.isAcknowledged ? 0 : 10);
        const score = severityRank(alert.severity) + (alert.isAcknowledged ? 0 : 10);
        if (score > bestScore) best = alert;
    }
    return best;
}

// ---------------------------------------------------------------------------
// PatientCard — one bed in the grid.
// ---------------------------------------------------------------------------
class PatientCard {
    constructor(info, parent) {
        this.patientId = info.patientId;
        this.info = info;
        this.build(parent);
    }

    build(parent) {
        this.card = document.createElement('div');
        this.card.className = 'patient-card';
        this.card.dataset.patientId = this.patientId;

        this.card.innerHTML = `
            <div class="card-header">
                <span class="bed-id">BED ${String(this.info.bedNumber).padStart(2, '0')}</span>
                <span class="patient-name"></span>
                <span class="patient-type"></span>
            </div>
            <div class="card-body">
                <div class="waveforms">
                    ${TRACES.map(t => `
                        <div class="waveform-container" data-trace="${t.key}">
                            <span class="trace-label color-${t.key}">${t.label}</span>
                            <canvas class="${t.key}-canvas"></canvas>
                        </div>`).join('')}
                </div>
                <div class="numerics">
                    <div class="numeric-block color-ecg" data-slot="hr">
                        <div class="numeric-label">HR <span class="numeric-unit">bpm</span></div>
                        <div class="numeric-value hr-val">--</div>
                    </div>
                    <div class="numeric-block color-spo2" data-slot="spo2">
                        <div class="numeric-label">SpO2 <span class="numeric-unit">%</span></div>
                        <div class="numeric-value spo2-val">--</div>
                        <div class="numeric-sub">PR <span class="pr-val">--</span></div>
                    </div>
                    <div class="numeric-block color-nibp" data-slot="nibp">
                        <div class="numeric-label">NIBP <span class="numeric-unit">mmHg</span></div>
                        <div class="numeric-value"><span class="sys-val">--</span>/<span class="dia-val">--</span></div>
                        <div class="numeric-sub">MAP (<span class="map-val">--</span>)</div>
                    </div>
                    <div class="numeric-block color-resp" data-slot="resp">
                        <div class="numeric-label">RESP <span class="numeric-unit">rpm</span></div>
                        <div class="numeric-value resp-val">--</div>
                    </div>
                    <div class="numeric-block color-temp" data-slot="temp">
                        <div class="numeric-label">TEMP <span class="numeric-unit">&deg;C</span></div>
                        <div class="numeric-value temp-val">--</div>
                    </div>
                </div>
            </div>
            <div class="alerts-container">
                <span class="alert-text"></span>
                <button class="ack-btn" type="button" hidden>SILENCE</button>
            </div>
        `;

        parent.appendChild(this.card);

        const q = (sel) => this.card.querySelector(sel);
        this.els = {
            name: q('.patient-name'),
            type: q('.patient-type'),
            hr: q('.hr-val'),
            spo2: q('.spo2-val'),
            pr: q('.pr-val'),
            sys: q('.sys-val'),
            dia: q('.dia-val'),
            map: q('.map-val'),
            resp: q('.resp-val'),
            temp: q('.temp-val'),
            alertText: q('.alert-text'),
            ackBtn: q('.ack-btn')
        };
        this.slots = {};
        for (const block of this.card.querySelectorAll('.numeric-block[data-slot]')) {
            this.slots[block.dataset.slot] = block;
        }

        this.drawers = {};
        for (const trace of TRACES) {
            this.drawers[trace.key] = new WaveformDrawer(q(`.${trace.key}-canvas`), trace.color, trace.sampleRate);
        }

        this.applyInfo(this.info);

        // Opening the detail view is the card's job; the silence button must not also
        // trigger it, so it stops the click from bubbling.
        this.card.addEventListener('click', () => app.openDetail(this.patientId));
        this.els.ackBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            app.acknowledgePatient(this.patientId);
        });
    }

    applyInfo(info) {
        this.info = info;
        this.els.name.textContent = info.patientName || info.patientId;
        this.els.type.textContent = info.patientType || '';
        this.els.type.title = info.diagnosis || '';
    }

    updateVitals(update) {
        const v = update.vitals;
        if (!v) return;

        this.els.hr.textContent = fmt.int(v.heartRate);
        this.els.spo2.textContent = fmt.int(v.spO2);
        this.els.pr.textContent = fmt.int(v.pulseRate);
        this.els.resp.textContent = fmt.int(v.respiratoryRate);
        this.els.temp.textContent = fmt.temp(v.temperature);

        if (v.nibp) {
            this.els.sys.textContent = fmt.int(v.nibp.systolic);
            this.els.dia.textContent = fmt.int(v.nibp.diastolic);
            this.els.map.textContent = fmt.int(v.nibp.meanArterialPressure);
        }

        this.enqueueWaveforms(update.waveforms);
    }

    enqueueWaveforms(waveforms) {
        if (!waveforms) return;
        for (const trace of TRACES) {
            const wave = waveforms.find(w => w.type === trace.waveform);
            if (wave) this.drawers[trace.key].enqueue(wave.samples);
        }
    }

    renderAlerts(alerts) {
        for (const key of Object.keys(this.slots)) {
            this.slots[key].classList.remove('vital-warning', 'vital-critical');
        }

        for (const alert of alerts.values()) {
            const slot = this.slots[VITAL_SLOT[alert.vitalSign]];
            if (!slot) continue;
            const cls = alert.severity === 'Critical' ? 'vital-critical' : 'vital-warning';
            // Critical must win when systolic and diastolic disagree on the same block.
            if (cls === 'vital-critical') slot.classList.remove('vital-warning');
            else if (slot.classList.contains('vital-critical')) continue;
            slot.classList.add(cls);
        }

        const top = dominantAlert(alerts);
        this.card.classList.remove('critical-alert', 'warning-alert', 'acknowledged');

        if (!top) {
            this.els.alertText.textContent = '';
            this.els.ackBtn.hidden = true;
            return;
        }

        const extra = alerts.size > 1 ? ` (+${alerts.size - 1})` : '';
        this.els.alertText.textContent = top.message + extra;
        this.card.classList.add(top.severity === 'Critical' ? 'critical-alert' : 'warning-alert');

        const allAcknowledged = [...alerts.values()].every(a => a.isAcknowledged);
        if (allAcknowledged) this.card.classList.add('acknowledged');
        this.els.ackBtn.hidden = allAcknowledged;
    }

    advance(dt) {
        for (const key in this.drawers) this.drawers[key].advance(dt);
    }

    render() {
        for (const key in this.drawers) this.drawers[key].draw();
    }

    setVisible(visible) {
        this.card.classList.toggle('hidden', !visible);
        if (!visible) for (const key in this.drawers) this.drawers[key].clear();
    }

    destroy() {
        this.card.remove();
    }
}

// ---------------------------------------------------------------------------
// DetailView — the zoomed single-patient overlay.
// ---------------------------------------------------------------------------
const detail = {
    patientId: null,
    modal: document.getElementById('detailed-modal'),
    drawers: {
        ecg: new WaveformDrawer(document.getElementById('detail-ecg-canvas'), '#39FF14', 250),
        pleth: new WaveformDrawer(document.getElementById('detail-pleth-canvas'), '#00FFFF', 50),
        resp: new WaveformDrawer(document.getElementById('detail-resp-canvas'), '#FFFF00', 50)
    },
    els: {
        name: document.getElementById('detail-patient-name'),
        meta: document.getElementById('detail-patient-meta'),
        hr: document.getElementById('detail-hr'),
        spo2: document.getElementById('detail-spo2'),
        pr: document.getElementById('detail-pr'),
        sys: document.getElementById('detail-sys'),
        dia: document.getElementById('detail-dia'),
        map: document.getElementById('detail-map'),
        resp: document.getElementById('detail-resp'),
        temp: document.getElementById('detail-temp'),
        alerts: document.getElementById('detail-alerts')
    },
    slots: {},

    init() {
        for (const block of this.modal.querySelectorAll('.numeric-block[data-slot]')) {
            this.slots[block.dataset.slot] = block;
        }
        document.getElementById('close-modal-btn').addEventListener('click', () => this.close());
        this.modal.addEventListener('click', (e) => {
            if (e.target === this.modal) this.close();
        });
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && this.patientId) this.close();
        });
    },

    open(patientId, info) {
        this.patientId = patientId;
        for (const key in this.drawers) this.drawers[key].clear();

        this.els.name.textContent = info ? `${info.patientName} — Bed ${String(info.bedNumber).padStart(2, '0')}` : patientId;
        this.els.meta.textContent = info
            ? [info.diagnosis || 'No diagnosis recorded', info.patientType].filter(Boolean).join(' · ')
            : '';

        this.modal.classList.remove('hidden');
    },

    close() {
        this.patientId = null;
        this.modal.classList.add('hidden');
    },

    get isOpen() {
        return this.patientId !== null;
    },

    updateVitals(update) {
        const v = update.vitals;
        if (!v) return;

        this.els.hr.textContent = fmt.int(v.heartRate);
        this.els.spo2.textContent = fmt.int(v.spO2);
        this.els.pr.textContent = fmt.int(v.pulseRate);
        this.els.resp.textContent = fmt.int(v.respiratoryRate);
        this.els.temp.textContent = fmt.temp(v.temperature);
        if (v.nibp) {
            this.els.sys.textContent = fmt.int(v.nibp.systolic);
            this.els.dia.textContent = fmt.int(v.nibp.diastolic);
            this.els.map.textContent = fmt.int(v.nibp.meanArterialPressure);
        }

        if (!update.waveforms) return;
        for (const trace of TRACES) {
            const wave = update.waveforms.find(w => w.type === trace.waveform);
            if (wave) this.drawers[trace.key].enqueue(wave.samples);
        }
    },

    renderAlerts(alerts) {
        for (const key of Object.keys(this.slots)) {
            this.slots[key].classList.remove('vital-warning', 'vital-critical');
        }
        for (const alert of alerts.values()) {
            const slot = this.slots[VITAL_SLOT[alert.vitalSign]];
            if (!slot) continue;
            const cls = alert.severity === 'Critical' ? 'vital-critical' : 'vital-warning';
            if (cls === 'vital-critical') slot.classList.remove('vital-warning');
            else if (slot.classList.contains('vital-critical')) continue;
            slot.classList.add(cls);
        }

        const list = this.els.alerts;
        list.textContent = '';

        if (!alerts.size) {
            const none = document.createElement('div');
            none.className = 'alert-none';
            none.textContent = 'No active alerts';
            list.appendChild(none);
            return;
        }

        const sorted = [...alerts.values()].sort((a, b) => severityRank(b.severity) - severityRank(a.severity));
        for (const alert of sorted) {
            const row = document.createElement('div');
            row.className = `alert-row ${alert.severity === 'Critical' ? 'critical' : 'warning'}`;
            if (alert.isAcknowledged) row.classList.add('acknowledged');

            const badge = document.createElement('span');
            badge.className = 'alert-badge';
            badge.textContent = alert.severity === 'Critical' ? 'CRIT' : 'WARN';

            const text = document.createElement('span');
            text.className = 'alert-message';
            text.textContent = alert.message;

            row.append(badge, text);

            if (alert.isAcknowledged) {
                const done = document.createElement('span');
                done.className = 'alert-acked';
                done.textContent = 'SILENCED';
                row.appendChild(done);
            } else {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'ack-btn';
                btn.textContent = 'ACK';
                btn.addEventListener('click', () => app.acknowledgeAlert(alert.alertId));
                row.appendChild(btn);
            }

            list.appendChild(row);
        }
    },

    advance(dt) {
        if (!this.isOpen) return;
        for (const key in this.drawers) this.drawers[key].advance(dt);
    },

    render() {
        if (!this.isOpen) return;
        for (const key in this.drawers) this.drawers[key].draw();
    }
};

// ---------------------------------------------------------------------------
// Application
// ---------------------------------------------------------------------------
const app = {
    /** patientId -> { info, card, alerts: Map<alertId, AlertDto>, visible } */
    beds: new Map(),
    grid: document.getElementById('monitor-grid'),
    roster: document.getElementById('patient-toggles'),
    connection: null,
    lastFrame: 0,

    start() {
        detail.init();

        document.getElementById('silence-all-btn')
            .addEventListener('click', () => this.acknowledgeAll());

        this.tickClock();
        setInterval(() => this.tickClock(), 1000);

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(Hub.route)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 15000])
            .build();

        this.connection.on(Hub.receiveVitals, (u) => this.onVitals(u));
        this.connection.on(Hub.patientAdded, (p) => this.onPatientAdded(p));
        this.connection.on(Hub.patientRemoved, (id) => this.onPatientRemoved(id));
        this.connection.on(Hub.alertTriggered, (a) => this.onAlert(a));
        this.connection.on(Hub.alertAcknowledged, (a) => this.onAlert(a));
        this.connection.on(Hub.alertCleared, (a) => this.onAlertCleared(a));

        this.connection.onreconnecting(() => this.setStatus('RECONNECTING…', 'connecting'));
        this.connection.onreconnected(() => {
            this.setStatus('CONNECTED', 'connected');
            this.syncState();
        });
        this.connection.onclose(() => {
            this.setStatus('DISCONNECTED', 'disconnected');
            this.connect();
        });

        // The render loop runs regardless of connection state so the UI stays
        // responsive (and traces drain cleanly) while we are reconnecting.
        requestAnimationFrame((t) => this.loop(t));
        this.connect();
    },

    async connect() {
        this.setStatus('CONNECTING…', 'connecting');
        try {
            await this.connection.start();
            this.setStatus('CONNECTED', 'connected');
            await this.syncState();
        } catch (err) {
            console.error('Hub connection failed:', err);
            this.setStatus('DISCONNECTED', 'disconnected');
            setTimeout(() => this.connect(), 5000);
        }
    },

    /** Pulls roster and any alerts that were already active before we joined. */
    async syncState() {
        try {
            const patients = await this.connection.invoke(Hub.getPatients);
            for (const info of patients) this.onPatientAdded(info);

            const alerts = await this.connection.invoke(Hub.getActiveAlerts);
            for (const bed of this.beds.values()) bed.alerts.clear();
            for (const alert of alerts) this.onAlert(alert, { silentRender: true });
            for (const bed of this.beds.values()) this.renderBedAlerts(bed);
            this.updateTally();
        } catch (err) {
            console.error('Failed to sync station state:', err);
        }
    },

    setStatus(text, cls) {
        const el = document.getElementById('connection-status');
        el.textContent = text;
        el.className = cls;
    },

    tickClock() {
        document.getElementById('clock').textContent = new Date().toLocaleTimeString();
    },

    // ── Roster ────────────────────────────────────────────────
    onPatientAdded(info) {
        const existing = this.beds.get(info.patientId);
        if (existing) {
            existing.info = info;
            existing.card.applyInfo(info);
            this.renderRoster();
            return;
        }

        const bed = { info, card: null, alerts: new Map(), visible: true };
        this.beds.set(info.patientId, bed);
        bed.card = new PatientCard(info, this.grid);

        this.reorderGrid();
        this.renderRoster();
        this.updateLayout();
    },

    onPatientRemoved(patientId) {
        const bed = this.beds.get(patientId);
        if (!bed) return;

        bed.card.destroy();
        this.beds.delete(patientId);
        if (detail.patientId === patientId) detail.close();

        this.renderRoster();
        this.updateLayout();
        this.updateTally();
    },

    /** Keeps cards in bed order no matter what order the server announced them in. */
    reorderGrid() {
        const ordered = [...this.beds.values()].sort((a, b) => a.info.bedNumber - b.info.bedNumber);
        for (const bed of ordered) this.grid.appendChild(bed.card.card);
    },

    renderRoster() {
        this.roster.textContent = '';

        if (!this.beds.size) {
            const empty = document.createElement('div');
            empty.className = 'roster-empty';
            empty.textContent = 'Waiting for roster…';
            this.roster.appendChild(empty);
            return;
        }

        const ordered = [...this.beds.values()].sort((a, b) => a.info.bedNumber - b.info.bedNumber);
        for (const bed of ordered) {
            const label = document.createElement('label');

            const box = document.createElement('input');
            box.type = 'checkbox';
            box.checked = bed.visible;
            box.addEventListener('change', () => {
                bed.visible = box.checked;
                bed.card.setVisible(box.checked);
                if (!box.checked && detail.patientId === bed.info.patientId) detail.close();
                this.updateLayout();
            });

            const bedNo = document.createElement('span');
            bedNo.className = 'roster-bed';
            bedNo.textContent = String(bed.info.bedNumber).padStart(2, '0');

            const name = document.createElement('span');
            name.className = 'roster-name';
            name.textContent = bed.info.patientName;

            label.append(box, bedNo, name);
            this.roster.appendChild(label);
        }
    },

    updateLayout() {
        const visible = [...this.beds.values()].filter(b => b.visible).length;
        this.grid.className = visible <= 1 ? 'cols-1' : visible <= 4 ? 'cols-2' : 'cols-3';
        document.getElementById('bed-count').textContent = String(visible);
    },

    // ── Streaming ─────────────────────────────────────────────
    onVitals(update) {
        const bed = this.beds.get(update.patientId);
        if (!bed) return;

        // Only feed traces that are actually on screen — a hidden card's canvas is
        // never drawn, so queuing its samples would just grow the backlog.
        if (bed.visible) bed.card.updateVitals(update);
        if (detail.patientId === update.patientId) detail.updateVitals(update);
    },

    onAlert(alert, opts) {
        const bed = this.beds.get(alert.patientId);
        if (!bed) return;

        bed.alerts.set(alert.alertId, alert);
        if (opts && opts.silentRender) return;

        this.renderBedAlerts(bed);
        this.updateTally();
    },

    onAlertCleared(alert) {
        const bed = this.beds.get(alert.patientId);
        if (!bed) return;

        bed.alerts.delete(alert.alertId);
        this.renderBedAlerts(bed);
        this.updateTally();
    },

    renderBedAlerts(bed) {
        bed.card.renderAlerts(bed.alerts);
        if (detail.patientId === bed.info.patientId) detail.renderAlerts(bed.alerts);
    },

    // ── Detail view ───────────────────────────────────────────
    openDetail(patientId) {
        const bed = this.beds.get(patientId);
        if (!bed || !bed.visible) return;

        detail.open(patientId, bed.info);
        // The overlay starts from the bed's current alert state, not from empty.
        detail.renderAlerts(bed.alerts);
    },

    updateTally() {
        let critical = 0;
        let warning = 0;
        let unacknowledged = 0;

        for (const bed of this.beds.values()) {
            for (const alert of bed.alerts.values()) {
                if (alert.severity === 'Critical') critical++; else warning++;
                if (!alert.isAcknowledged) unacknowledged++;
            }
        }

        document.getElementById('tally-critical').textContent = String(critical);
        document.getElementById('tally-warning').textContent = String(warning);
        document.getElementById('silence-all-btn').disabled = unacknowledged === 0;
    },

    // ── Acknowledgement ───────────────────────────────────────
    async acknowledgeAlert(alertId) {
        if (this.connection.state !== signalR.HubConnectionState.Connected) return;
        try {
            await this.connection.invoke(Hub.acknowledgeAlert, alertId);
        } catch (err) {
            console.error('Acknowledge failed:', err);
        }
    },

    async acknowledgePatient(patientId) {
        const bed = this.beds.get(patientId);
        if (!bed) return;
        for (const alert of [...bed.alerts.values()]) {
            if (!alert.isAcknowledged) await this.acknowledgeAlert(alert.alertId);
        }
    },

    async acknowledgeAll() {
        for (const bed of [...this.beds.values()]) {
            await this.acknowledgePatient(bed.info.patientId);
        }
    },

    // ── Render loop ───────────────────────────────────────────
    loop(timestamp) {
        // Clamp the delta so a backgrounded tab does not fast-forward the whole sweep
        // in a single frame when it becomes visible again.
        const dt = this.lastFrame ? Math.min((timestamp - this.lastFrame) / 1000, 0.25) : 0;
        this.lastFrame = timestamp;

        for (const bed of this.beds.values()) {
            if (!bed.visible) continue;
            bed.card.advance(dt);
            bed.card.render();
        }

        detail.advance(dt);
        detail.render();

        requestAnimationFrame((t) => this.loop(t));
    }
};

app.start();
