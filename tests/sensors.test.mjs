import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../wwwroot/js/sensors.js', import.meta.url), 'utf8');
const { default: Sensors } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const keys = ['insideTemperature', 'insideHumidity', 'outsideTemperature', 'outsideHumidity'];
const snapshot = () => ({
    enabled: true, connected: true, status: 'connected',
    readings: Object.fromEntries(keys.map((key, index) => [key, {
        value: index ? 0 : 21.5, unit: key.endsWith('Humidity') ? '%' : 'C', available: true,
        lastReported: '2020-01-01T00:00:00Z', lastUpdated: '2020-01-01T00:00:00Z'
    }]))
});

function setup(t) {
    const original = globalThis.EventSource;
    const streams = [];
    globalThis.EventSource = class {
        constructor(url) { this.url = url; streams.push(this); }
        close() { this.closed = true; }
        emit(value) { this.onmessage({ data: JSON.stringify(value) }); }
    };
    t.after(() => { globalThis.EventSource = original; });
    const elements = new Map();
    const create = () => ({
        dataset: {}, attributes: {}, hidden: true, textContent: '',
        classList: { values: new Map(), toggle(key, value) { this.values.set(key, value); } },
        setAttribute(name, value) { this.attributes[name] = value; }
    });
    const target = create();
    target.querySelector = selector => {
        if (!elements.has(selector)) elements.set(selector, create());
        return elements.get(selector);
    };
    const app = new Sensors();
    app.init(target);
    return { app, target, stream: streams[0], streams };
}

test('sensor panel is optional and never controls slideshow state', t => {
    const { app, target, stream } = setup(t);
    target.dataset.state = 'paused';
    stream.emit({ enabled: false, connected: false, status: 'starting', readings: {} });
    assert.equal(app.panel.hidden, true);
    assert.notEqual(stream.closed, true, 'startup initialization cannot prematurely close enabled integration');
    stream.emit({ enabled: false, connected: false, status: 'disabled', readings: {} });
    assert.equal(app.panel.hidden, true);
    assert.equal(stream.closed, true);
    assert.equal(target.dataset.state, 'paused');
});

test('renders four readings and units safely, including zero and unavailable states', t => {
    const { app, stream } = setup(t);
    const data = snapshot();
    stream.emit(data);
    assert.equal(app.panel.hidden, false);
    assert.equal(app.cells.insideTemperature.textContent, '21.5 C');
    assert.equal(app.cells.insideHumidity.textContent, '0 %');
    assert.match(app.cells.insideTemperature.title, /Last reported:/);
    data.readings.insideTemperature.unit = '<script>not executable</script>';
    data.readings.outsideTemperature = { value: null, unit: 'C', available: false, lastReported: null, lastUpdated: null };
    stream.emit(data);
    assert.equal(app.cells.insideTemperature.textContent, '21.5 <script>not executable</script>');
    assert.equal(app.cells.outsideTemperature.textContent, 'Unavailable');
    assert.equal(app.cells.outsideTemperature.dataset.available, 'false');
    assert.match(app.cells.outsideTemperature.title, /not supplied/);
});

test('only sensor errors show status and recovery removes the status row', t => {
    const { app, stream } = setup(t);
    const data = snapshot();
    data.connected = false;
    data.status = 'connecting';
    stream.emit(data);
    assert.equal(app.status.textContent, '');
    assert.equal(app.status.hidden, true);
    data.connected = true;
    data.status = 'connected';
    stream.emit(data);
    assert.equal(app.status.textContent, '');
    assert.equal(app.status.hidden, true);
    stream.onerror();
    assert.match(app.status.textContent, /connection lost/);
    assert.equal(app.status.hidden, false);
    stream.emit(data);
    assert.equal(app.status.textContent, '');
    assert.equal(app.status.hidden, true);
    data.connected = false;
    data.status = 'disconnected';
    stream.emit(data);
    assert.equal(app.status.hidden, false);
    assert.match(app.status.textContent, /disconnected/);
    data.status = 'configuration-error';
    stream.emit(data);
    assert.equal(app.status.hidden, false);
    assert.match(app.status.textContent, /configuration error/);
});

test('disconnect retains stale readings; full reconnect replaces them without a quiet-sensor timeout', t => {
    const { app, stream, streams, target } = setup(t);
    target.dataset.state = 'showing';
    stream.emit(snapshot());
    assert.equal(app.panel.classList.values.get('is-stale'), false, 'old reading time is not a disconnect');
    stream.onerror();
    assert.equal(app.cells.insideTemperature.textContent, '21.5 C');
    assert.equal(app.panel.classList.values.get('is-stale'), true);
    assert.match(app.status.textContent, /stale/);
    const next = snapshot();
    next.readings.insideTemperature.value = 25;
    stream.emit(next);
    assert.equal(app.cells.insideTemperature.textContent, '25 C');
    assert.equal(app.panel.classList.values.get('is-stale'), false);
    next.connected = false;
    stream.emit(next);
    assert.match(app.status.textContent, /Home Assistant disconnected/);
    assert.equal(app.panel.classList.values.get('is-stale'), true);
    assert.equal(streams.length, 1, 'native EventSource owns reconnect, not slideshow timers or extra streams');
    assert.equal(target.dataset.state, 'showing');
});

test('configuration errors stay visible without inventing measurements', t => {
    const { app, stream } = setup(t);
    stream.emit({ enabled: true, connected: false, status: 'configuration-error', readings: {} });
    assert.match(app.status.textContent, /configuration error/);
    assert.ok(Object.values(app.cells).every(cell => cell.textContent === 'Unavailable'));
});

test('malformed snapshots retain the last readings with explicit stale feedback', t => {
    const { app, stream } = setup(t);
    stream.emit(snapshot());
    const original = console.error;
    const errors = [];
    console.error = message => errors.push(message);
    try {
        const invalid = snapshot();
        invalid.readings.insideTemperature.value = 'not numeric';
        stream.emit(invalid);
    } finally { console.error = original; }
    assert.equal(app.cells.insideTemperature.textContent, '21.5 C');
    assert.equal(app.panel.classList.values.get('is-stale'), true);
    assert.match(app.status.textContent, /connection lost/);
    assert.equal(errors.length, 1);
    stream.emit(snapshot());
    assert.equal(app.panel.classList.values.get('is-stale'), false);
});
