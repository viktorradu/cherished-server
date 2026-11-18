import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../wwwroot/js/main.js', import.meta.url), 'utf8');
const { default: Main } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const flush = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
    let resolve, reject;
    const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
};
const catalog = (ids = ['a', 'b', 'c']) => ({
    poolSize: ids.length, slideshowIntervalMs: 3000,
    images: ids.map(id => ({ id, label: `Label ${id}` }))
});
const response = data => ({ ok: true, status: 200, json: async () => data });

function setup(t, data = catalog()) {
    const originals = {
        Image: globalThis.Image, window: globalThis.window, fetch: globalThis.fetch,
        setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout,
        random: Math.random, now: Date.now
    };
    const runtime = {
        now: 0, nextId: 0, timers: new Map(), images: [], requests: [], random: 0,
        fetch: async () => response(data)
    };
    globalThis.setTimeout = (callback, delay) => {
        const id = ++runtime.nextId;
        runtime.timers.set(id, { callback, due: runtime.now + delay });
        assert.ok(runtime.timers.size <= 1, 'only one authoritative wake-up');
        return id;
    };
    globalThis.clearTimeout = id => runtime.timers.delete(id);
    globalThis.fetch = (url, options) => {
        runtime.requests.push({ url, options });
        return runtime.fetch(url, options);
    };
    Math.random = () => runtime.random;
    Date.now = () => runtime.now;
    globalThis.window = { addEventListener() {} };
    globalThis.Image = class {
        constructor() { runtime.images.push(this); }
        set src(value) {
            assert.equal(typeof this.onload, 'function', 'onload installed before src');
            assert.equal(typeof this.onerror, 'function', 'onerror installed before src');
            this.url = value;
        }
        get src() { return this.url; }
        removeAttribute() { this.url = ''; }
        replaceWith() {}
        succeed() { this.onload?.(); }
        fail() { this.onerror?.(); }
    };
    const elements = new Map();
    const target = {
        dataset: {}, classList: { toggle() {} }, addEventListener() {},
        querySelector(selector) {
            if (!elements.has(selector)) {
                const attributes = new Map();
                elements.set(selector, {
                    addEventListener() {}, replaceWith() {},
                    setAttribute(name, value) { attributes.set(name, value); },
                    getAttribute(name) { return attributes.get(name); }
                });
            }
            return elements.get(selector);
        }
    };
    const app = new Main();
    app.init(target);
    runtime.app = app;
    runtime.lastImage = () => runtime.images.at(-1);
    runtime.nextDelay = () => Math.min(...[...runtime.timers.values()].map(timer => timer.due - runtime.now));
    runtime.tick = async milliseconds => {
        const until = runtime.now + milliseconds;
        while (true) {
            const next = [...runtime.timers.entries()].sort((a, b) => a[1].due - b[1].due)[0];
            if (!next || next[1].due > until) break;
            runtime.now = next[1].due;
            runtime.timers.delete(next[0]);
            next[1].callback();
            await flush();
        }
        runtime.now = until;
    };
    t.after(() => {
        app.invalidate();
        Object.assign(globalThis, {
            Image: originals.Image, window: originals.window, fetch: originals.fetch,
            setTimeout: originals.setTimeout, clearTimeout: originals.clearTimeout
        });
        Math.random = originals.random;
        Date.now = originals.now;
    });
    return runtime;
}

test('only the winning successful image changes display and history', async t => {
    const r = setup(t);
    await flush();
    const first = r.lastImage();
    assert.equal(r.app.state, 'loading');
    assert.equal(r.app.currentKey, null);
    assert.equal(r.app.history.length, 0);
    const staleLoad = first.onload;
    const staleError = first.onerror;
    r.app.advance(1, true);
    const winning = r.lastImage();
    const winningLoad = winning.onload;
    const winningError = winning.onerror;
    staleLoad();
    staleError();
    assert.equal(r.app.history.length, 0);
    winning.succeed();
    assert.equal(r.app.currentKey, 'a');
    assert.equal(r.app.history.length, 1);
    winningLoad();
    winningError();
    assert.equal(r.app.history.length, 1, 'settled callbacks must not commit or fail twice');
    assert.equal(r.nextDelay(), 3000);
    const displayed = r.app.mainImage;
    r.app.advance(1, true);
    r.lastImage().fail();
    assert.equal(r.app.mainImage, displayed);
    assert.equal(r.app.currentKey, 'a');
    assert.equal(r.app.history.length, 1);
    assert.equal(r.app.state, 'retrying');
});

test('ordinary image loads retain the visible image, background and label until the winning switch', async t => {
    const r = setup(t);
    await flush();
    assert.equal(r.app.statusBox.innerText, 'Waiting for images...');
    r.lastImage().succeed();
    const displayed = r.app.mainImage;
    const background = r.app.background.src;
    await r.tick(3000);
    assert.equal(r.app.state, 'loading');
    assert.equal(r.app.mainImage, displayed);
    assert.equal(r.app.background.src, background);
    assert.equal(r.app.statusBox.innerText, 'Label a');
    const staleLoad = r.lastImage().onload;
    r.random = 0.99;
    r.app.advance(1, true);
    staleLoad();
    assert.equal(r.app.mainImage, displayed);
    assert.equal(r.app.background.src, background);
    assert.equal(r.app.statusBox.innerText, 'Label a');
    const next = r.lastImage();
    next.succeed();
    assert.equal(r.app.mainImage, next);
    assert.equal(r.app.background.src, next.src);
    assert.equal(r.app.currentKey, 'c');
    assert.equal(r.app.statusBox.innerText, 'Label c');
});

test('catalog refresh retains the displayed label through metadata and image loading but reports failures', async t => {
    const r = setup(t, catalog(['original']));
    await flush();
    r.lastImage().succeed();
    r.app.catalogMaxAgeMs = 0;
    const metadata = deferred();
    r.fetch = () => metadata.promise;
    await r.tick(3000);
    const displayed = r.app.mainImage;
    const background = r.app.background.src;
    assert.equal(r.app.state, 'waiting-for-pool');
    assert.equal(r.app.statusBox.innerText, 'Label original');
    metadata.resolve(response(catalog(['new'])));
    await flush();
    assert.equal(r.app.state, 'loading');
    assert.equal(r.app.mainImage, displayed);
    assert.equal(r.app.background.src, background);
    assert.equal(r.app.statusBox.innerText, 'Label original');
    r.lastImage().succeed();
    assert.equal(r.app.background.src, r.app.mainImage.src);
    assert.equal(r.app.currentKey, 'new');
    assert.equal(r.app.statusBox.innerText, 'Label new');
    r.fetch = async () => ({ ok: false, status: 503 });
    await r.tick(3000);
    assert.match(r.app.statusBox.innerText, /Catalog unavailable \(503\).*Retrying/);
});

test('pause invalidates pending automatic work; manual navigation stays paused', async t => {
    const r = setup(t);
    await flush();
    r.lastImage().succeed();
    await r.tick(3000);
    const staleLoad = r.lastImage().onload;
    r.app.togglePause();
    staleLoad();
    assert.equal(r.app.currentKey, 'a');
    assert.equal(r.app.statusBox.innerText, 'Label a');
    assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'false');
    assert.equal(r.app.state, 'paused');
    assert.equal(r.timers.size, 0);
    r.app.advance(1, true);
    assert.equal(r.app.statusBox.innerText, 'Label a', 'manual preload must preserve paused label');
    assert.equal(r.app.currentKey, 'a');
    r.lastImage().succeed();
    assert.equal(r.app.currentKey, 'b');
    assert.equal(r.app.statusBox.innerText, 'Label b');
    assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'false');
    assert.equal(r.app.state, 'paused');
    assert.equal(r.timers.size, 0);
    r.app.togglePause();
    assert.equal(r.app.state, 'showing');
    assert.equal(r.app.statusBox.innerText, 'Label b');
    assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'true');
    assert.equal(r.nextDelay(), 3000);
    await r.tick(3000);
    assert.equal(r.app.state, 'loading');
    r.app.togglePause();
    r.app.advance(1, true);
    r.lastImage().fail();
    assert.equal(r.timers.size, 0, 'paused failure must not schedule an automatic retry');
    assert.equal(r.app.statusBox.innerText, 'Label b - Image unavailable');
    assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'false');
});

test('paused history and metadata keep committed labels independent of the pause indicator', async t => {
    const r = setup(t);
    await flush();
    r.lastImage().succeed();
    r.app.advance(1, true);
    r.lastImage().succeed();
    r.app.togglePause();
    for (let i = 0; i < 3; i++) {
        r.app.togglePause();
        assert.equal(r.app.statusBox.innerText, 'Label b');
        assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'true');
        r.app.togglePause();
        assert.equal(r.app.statusBox.innerText, 'Label b');
        assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'false');
    }
    r.app.advance(-1, true);
    assert.equal(r.app.statusBox.innerText, 'Label b');
    r.lastImage().succeed();
    assert.equal(r.app.statusBox.innerText, 'Label a');
    r.app.catalogMaxAgeMs = 0;
    const metadata = deferred();
    r.fetch = () => metadata.promise;
    r.app.advance(1, true);
    assert.equal(r.app.statusBox.innerText, 'Label a');
    metadata.resolve(response(catalog()));
    await flush();
    assert.equal(r.app.statusBox.innerText, 'Label a');
    r.lastImage().succeed();
    assert.equal(r.app.statusBox.innerText, 'Label b');
    assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'false');
    assert.equal(r.timers.size, 0);
});

test('back/forward history and actions refer to the committed visible image', async t => {
    const r = setup(t);
    await flush();
    r.lastImage().succeed(); // a
    r.app.advance(1, true);
    r.lastImage().succeed(); // b
    r.random = 0.99;
    r.app.advance(1, true);
    r.lastImage().succeed(); // c
    r.app.advance(-1, true);
    assert.equal(r.app.currentKey, 'c', 'history index not committed before load');
    r.lastImage().succeed();
    assert.equal(r.app.currentKey, 'b');
    const action = deferred();
    r.fetch = () => action.promise;
    const pending = r.app.setFlagged();
    assert.equal(r.requests.at(-1).url, 'image/flag?key=b');
    r.app.advance(1, true);
    r.lastImage().succeed();
    assert.equal(r.app.currentKey, 'c');
    assert.equal(r.app.history.length, 3, 'forward traversal must not append duplicate history');
    action.resolve({ ok: false, status: 503 });
    await pending;
    assert.equal(r.app.statusBox.innerText, 'Label c', 'stale action must not change current status');
    r.app.advance(-1, true);
    r.lastImage().succeed();
    r.fetch = async () => ({ ok: true });
    await r.app.setHidden();
    assert.equal(r.requests.at(-1).url, 'image/hide?key=b');
    assert.equal(r.app.images.has('b'), false);
    assert.equal(r.app.images.has('c'), true);
    r.app.advance(-1, true);
    r.lastImage().succeed();
    const count = r.images.length;
    r.app.advance(-1, true);
    assert.equal(r.images.length, count, 'back at the start must not wrap or pick a random image');
    assert.equal(r.app.currentKey, 'a');
});

test('history remains bounded even after many successful advances', async t => {
    const r = setup(t);
    await flush();
    r.app.historyLimit = 3;
    r.lastImage().succeed();
    for (let i = 0; i < 10; i++) {
        r.app.advance(1, true);
        r.lastImage().succeed();
    }
    assert.equal(r.app.history.length, 3);
    for (let i = 0; i < 2; i++) {
        r.app.advance(-1, true);
        r.lastImage().succeed();
    }
    const count = r.images.length;
    r.app.advance(-1, true);
    assert.equal(r.images.length, count);
});

test('startup recovers from not-ready, network, invalid JSON, invalid metadata and empty catalogs', async t => {
    const r = setup(t);
    // Replace the already-issued response before its continuation reads JSON.
    r.app.togglePause();
    const outcomes = [
        () => ({ ok: false, status: 503 }),
        () => Promise.reject(new Error('offline')),
        () => ({ ok: true, json: async () => { throw new SyntaxError('invalid JSON'); } }),
        () => response({ ...catalog(), slideshowIntervalMs: 0 }),
        () => response(catalog([])),
        () => response(catalog(['recovered']))
    ];
    let calls = 0;
    r.fetch = async () => outcomes[calls++]();
    r.app.togglePause();
    await flush();
    let previousDelay = 0;
    for (let i = 0; i < 5; i++) {
        assert.equal(r.app.state, 'waiting-for-pool');
        const delay = r.nextDelay();
        assert.ok(delay >= previousDelay && delay <= 30000);
        previousDelay = delay;
        await r.tick(delay);
    }
    assert.equal(calls, 6);
    assert.equal(r.app.state, 'loading');
    r.lastImage().succeed();
    assert.equal(r.app.currentKey, 'recovered');
    assert.equal(r.nextDelay(), 3000);
});

test('metadata backoff is capped and a hanging request has a deadline', async t => {
    const r = setup(t);
    r.app.togglePause();
    const hanging = deferred();
    let signal;
    r.fetch = (_, options) => { signal = options.signal; return hanging.promise; };
    r.app.togglePause();
    await flush();
    await r.tick(r.app.loadDeadlineMs);
    assert.equal(signal.aborted, true);
    assert.equal(r.app.state, 'waiting-for-pool');
    assert.match(r.app.statusBox.innerText, /timed out/);
    r.fetch = async () => { throw new Error('offline'); };
    r.random = 0.999;
    for (let i = 0; i < 12; i++) await r.tick(r.nextDelay());
    assert.equal(r.nextDelay(), 30000);
    const status = r.app.statusBox.innerText;
    hanging.resolve(response(catalog(['stale'])));
    await flush();
    assert.equal(r.app.statusBox.innerText, status);
    assert.equal(r.images.length, 0);
});

test('stale metadata body cannot replace a newer catalog or status', async t => {
    const r = setup(t);
    r.app.togglePause();
    const body = deferred();
    r.fetch = async () => ({ ok: true, json: () => body.promise });
    r.app.togglePause();
    await flush();
    r.fetch = async () => response(catalog(['new']));
    r.app.advance(1, true);
    await flush();
    r.lastImage().succeed();
    body.resolve({ ...catalog(['old']), slideshowIntervalMs: 7000 });
    await flush();
    assert.equal(r.app.currentKey, 'new');
    assert.equal(r.app.slideshowIntervalMs, 3000);
    assert.equal(r.app.statusBox.innerText, 'Label new');
});

test('hanging and unavailable images exhaust a bounded budget then refresh slowly', async t => {
    const r = setup(t, catalog(['a', 'b', 'c', 'd', 'e', 'f', 'g']));
    await flush();
    r.lastImage().succeed();
    const displayed = r.app.mainImage;
    r.app.advance();
    await r.tick(r.app.loadDeadlineMs);
    for (let attempt = 1; attempt < r.app.maxImageAttempts; attempt++) {
        await r.tick(r.nextDelay());
        r.lastImage().fail();
    }
    assert.equal(r.images.length, 1 + r.app.maxImageAttempts);
    assert.equal(r.app.state, 'retrying');
    assert.equal(r.nextDelay(), 30000);
    assert.equal(r.app.mainImage, displayed);
    assert.equal(r.app.history.length, 1);
    const requests = r.requests.length;
    await r.tick(29999);
    assert.equal(r.requests.length, requests);
    await r.tick(1);
    assert.equal(r.requests.length, requests + 1);
    assert.equal(r.app.state, 'loading');
    r.lastImage().succeed();
    assert.equal(r.nextDelay(), 3000);
});

test('a single unavailable image cannot spin and hiding targets the displayed history entry', async t => {
    const r = setup(t, catalog(['a']));
    await flush();
    r.lastImage().fail();
    assert.equal(r.images.length, 1);
    assert.equal(r.nextDelay(), 30000);
    r.app.advance(1, true);
    await flush();
    r.lastImage().succeed();
    r.app.togglePause();
    r.fetch = async () => ({ ok: true });
    await r.app.setHidden();
    assert.equal(r.requests.at(-1).url, 'image/hide?key=a');
    assert.equal(r.app.currentKey, 'a');
    assert.equal(r.app.images.has('a'), false);
    assert.equal(r.app.statusBox.innerText, 'Label a - Hidden');
    assert.equal(r.app.pauseIndicator.getAttribute('aria-hidden'), 'false');
    assert.equal(r.timers.size, 0);
});

test('pause cancels startup and manual startup cannot create automatic work', async t => {
    const r = setup(t, catalog([]));
    r.app.togglePause();
    await flush();
    assert.equal(r.timers.size, 0);
    assert.equal(r.app.state, 'paused');
    r.app.advance(1, true);
    await flush();
    assert.equal(r.app.state, 'paused');
    assert.equal(r.timers.size, 0);
});

test('previous during startup does not enter showing before any successful image', async t => {
    const r = setup(t);
    r.app.advance(-1, true);
    await flush();
    assert.equal(r.app.state, 'loading');
    assert.equal(r.app.currentKey, null);
    assert.equal(r.nextDelay(), r.app.loadDeadlineMs);
    r.lastImage().succeed();
    assert.equal(r.app.state, 'showing');
});

test('hide during a stale catalog request cannot reintroduce a hidden image', async t => {
    const r = setup(t);
    await flush();
    r.lastImage().succeed();
    const body = deferred();
    r.fetch = async url => url === 'image/loadpool'
        ? { ok: true, json: () => body.promise }
        : { ok: true };
    r.app.poolReady = false;
    r.app.advance();
    await flush();
    await r.app.setHidden();
    body.resolve(catalog());
    await flush();
    assert.equal(r.app.images.has('a'), false);
    assert.match(r.lastImage().src, /key=b$/);
    r.lastImage().succeed();
    assert.equal(r.app.currentKey, 'b');
});

test('ordinary advances refresh metadata so added images become selectable', async t => {
    const r = setup(t, catalog(['original']));
    await flush();
    r.lastImage().succeed();
    r.app.togglePause();
    await r.tick(30000);
    r.fetch = async () => response(catalog(['original', 'added']));
    r.app.advance(1, true);
    await flush();
    assert.equal(r.app.images.has('added'), true);
    assert.match(r.lastImage().src, /key=added$/);
    r.lastImage().succeed();
    assert.equal(r.app.currentKey, 'added');
    assert.equal(r.app.state, 'paused');
    assert.equal(r.timers.size, 0);
});
