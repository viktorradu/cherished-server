const sensorNames = {
    insideTemperature: 'Inside temperature',
    insideHumidity: 'Inside humidity',
    outsideTemperature: 'Outside temperature',
    outsideHumidity: 'Outside humidity'
};

export default class Sensors {
    init(target) {
        this.target = target;
        this.panel = target.querySelector('.ch-sensors');
        this.status = target.querySelector('.ch-sensor-status');
        this.cells = Object.fromEntries(Object.keys(sensorNames)
            .map(key => [key, target.querySelector(`[data-sensor="${key}"]`)]));
        this.stream = new EventSource('sensors/events');
        this.stream.onmessage = event => {
            if (this.closed) return;
            let snapshot;
            try {
                snapshot = JSON.parse(event.data);
                if (!snapshot || typeof snapshot.enabled !== 'boolean' || typeof snapshot.connected !== 'boolean' ||
                    typeof snapshot.status !== 'string' || !snapshot.readings ||
                    Object.values(snapshot.readings).some(reading => !reading ||
                        typeof reading.available !== 'boolean' ||
                        (reading.value !== null && !Number.isFinite(reading.value)) ||
                        (reading.unit !== null && typeof reading.unit !== 'string')))
                    throw new Error('Invalid sensor snapshot');
            } catch {
                console.error('Invalid sensor snapshot received.');
                this.disconnected();
                return;
            }
            this.snapshot = snapshot;
            this.transportConnected = true;
            this.render();
            if (!snapshot.enabled && snapshot.status !== 'starting') {
                this.closed = true;
                this.stream.close();
            }
        };
        this.stream.onerror = () => {
            if (!this.closed) this.disconnected();
        };
    }

    disconnected() {
        this.transportConnected = false;
        this.render();
    }

    render() {
        const snapshot = this.snapshot;
        if (!snapshot) return;
        this.panel.hidden = !snapshot.enabled;
        this.target.classList.toggle('has-sensors', snapshot.enabled);
        const connected = this.transportConnected && snapshot.connected;
        this.panel.classList.toggle('is-stale', !connected);
        this.status.textContent = !this.transportConnected ? 'Sensor connection lost - stale readings' :
            snapshot.status === 'configuration-error' ? 'Home Assistant configuration error' :
            connected || ['starting', 'connecting', 'disabled'].includes(snapshot.status) ? '' :
            'Home Assistant disconnected - stale readings';
        this.status.hidden = !this.status.textContent;
        for (const [key, cell] of Object.entries(this.cells)) {
            const reading = snapshot.readings[key];
            const available = reading?.available && Number.isFinite(reading.value);
            const value = available
                ? `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(reading.value)}${reading.unit ? ` ${reading.unit}` : ''}`
                : 'Unavailable';
            const reported = this.time(reading?.lastReported);
            const updated = this.time(reading?.lastUpdated);
            const times = `Last reported: ${reported}. Last updated: ${updated}.`;
            cell.textContent = value;
            cell.title = times;
            cell.setAttribute('aria-label', `${sensorNames[key]}: ${value}. ${times}${connected ? '' : ' Stale: connection unavailable.'}`);
            cell.dataset.available = String(Boolean(available));
        }
    }

    time(value) {
        if (!value) return 'not supplied by Home Assistant';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? 'not supplied by Home Assistant' : date.toLocaleString();
    }
}
