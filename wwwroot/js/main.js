export default class Main {
    constructor() {
        this.state = 'waiting-for-pool';
        this.paused = false;
        this.slideshowIntervalMs = 3000;
        this.loadDeadlineMs = 15000;
        this.slowRetryMs = 30000;
        this.maxImageAttempts = 5;
        this.historyLimit = 100;
        this.catalogMaxAgeMs = 30000;
        this.catalogLoadedAt = null;
        this.images = new Map();
        this.hiddenIds = new Set();
        this.poolReady = false;
        this.history = [];
        this.historyIndex = -1;
        this.currentKey = null;
        this.label = '';
        this.generation = 0;
        this.displayVersion = 0;
        this.statusVersion = 0;
        this.metadataFailures = 0;
        this.wakeId = null;
        this.cancelLoad = null;
        this.pendingKey = null;
        this.cycle = null;
    }

    init(target) {
        if (!target || !target.querySelector('.ch-image img')) return;
        this.target = target;
        this.mainImage = target.querySelector('.ch-image img');
        this.background = target.querySelector('.ch-background img');
        this.statusBox = target.querySelector('.ch-status-label');
        this.pauseIndicator = target.querySelector('.ch-pause-indicator');
        const bind = (selector, action) => {
            target.querySelector(selector).addEventListener('click', event => {
                event.stopPropagation();
                action();
            });
        };
        bind('.ch-button-next', () => this.advance(1, true));
        bind('.ch-button-prev', () => this.advance(-1, true));
        bind('.ch-button-hide', () => this.setHidden());
        bind('.ch-button-flag', () => this.setFlagged());
        target.addEventListener('click', () => this.togglePause());
        window.addEventListener('keyup', event => {
            if (event.key === 'ArrowRight') this.advance(1, true);
            if (event.key === 'ArrowLeft') this.advance(-1, true);
        });
        this.advance();
    }

    invalidate() {
        this.generation++;
        clearTimeout(this.wakeId);
        this.wakeId = null;
        if (this.cancelLoad) this.cancelLoad();
        this.cancelLoad = null;
        this.pendingKey = null;
        return this.generation;
    }

    schedule(delay, action) {
        clearTimeout(this.wakeId);
        const generation = this.generation;
        this.wakeId = setTimeout(() => {
            this.wakeId = null;
            if (generation === this.generation) action();
        }, delay);
    }

    setState(state, message) {
        this.state = state;
        this.target.dataset.state = state;
        this.target.classList.toggle('paused', this.paused);
        this.pauseIndicator.setAttribute('aria-hidden', String(!this.paused));
        if (message !== undefined) this.setStatus(message);
    }

    setStatus(message) {
        this.statusVersion++;
        const status = this.paused && this.label && message !== this.label
            ? `${this.label} - ${message}` : message;
        this.statusBox.innerText = status;
    }

    settle() {
        this.cycle = null;
        this.setState(this.paused ? 'paused' : 'showing', this.label);
        if (!this.paused)
            this.schedule(this.slideshowIntervalMs, () => this.advance());
    }

    togglePause() {
        this.paused = !this.paused;
        this.invalidate();
        if (this.paused) {
            this.cycle = null;
            this.setState('paused', this.label);
        } else if (this.currentKey !== null) {
            this.settle();
        } else {
            this.advance();
        }
    }

    advance(direction = 1, manual = false) {
        if (this.paused && !manual) return;
        this.invalidate();
        this.cycle = { direction, attempts: 0, failed: new Set() };
        if (direction < 0 && this.historyIndex <= 0) {
            if (this.currentKey !== null) this.settle();
            else if (!this.paused) {
                this.cycle.direction = 1;
                this.loadPool();
            } else this.setState('paused', this.label);
            return;
        }
        if (!this.poolReady || this.images.size === 0 ||
            Date.now() - this.catalogLoadedAt >= this.catalogMaxAgeMs) this.loadPool();
        else this.loadCandidate();
    }

    async loadPool() {
        const generation = this.invalidate();
        const controller = new AbortController();
        this.cancelLoad = () => controller.abort();
        this.setState('waiting-for-pool', this.currentKey === null ? 'Waiting for images...' : undefined);
        this.schedule(this.loadDeadlineMs, () => {
            this.invalidate();
            this.poolFailure('Catalog request timed out');
        });
        let data;
        try {
            const response = await fetch('image/loadpool', { signal: controller.signal, cache: 'no-store' });
            if (generation !== this.generation) return;
            if (!response.ok) throw new Error(`Catalog unavailable (${response.status})`);
            data = await response.json();
            if (generation !== this.generation) return;
            if (!data || !Array.isArray(data.images) ||
                !Number.isInteger(data.poolSize) || data.poolSize !== data.images.length ||
                !Number.isInteger(data.slideshowIntervalMs) ||
                data.slideshowIntervalMs < 250 || data.slideshowIntervalMs > 86400000 ||
                data.images.some(image => !image || typeof image.id !== 'string' || !image.id ||
                    typeof image.label !== 'string') ||
                new Set(data.images.map(image => image.id)).size !== data.poolSize)
                throw new Error('Invalid catalog response');
        } catch (error) {
            if (generation !== this.generation) return;
            this.invalidate();
            this.poolFailure(error.message);
            return;
        }
        this.invalidate();
        this.images = new Map(data.images
            .filter(image => !this.hiddenIds.has(image.id))
            .map(image => [image.id, image]));
        this.poolReady = true;
        this.catalogLoadedAt = Date.now();
        this.slideshowIntervalMs = data.slideshowIntervalMs;
        if (this.images.size === 0) {
            this.poolFailure('No images available');
            return;
        }
        this.metadataFailures = 0;
        this.loadCandidate();
    }

    poolFailure(message) {
        this.poolReady = false;
        this.metadataFailures++;
        const delay = Math.min(30000,
            1000 * 2 ** Math.min(this.metadataFailures - 1, 5) * (0.8 + Math.random() * 0.4));
        this.setState(this.paused ? 'paused' : 'waiting-for-pool',
            this.paused ? message : `${message}. Retrying...`);
        if (!this.paused) this.schedule(delay, () => this.loadPool());
    }

    candidate() {
        const { direction, failed } = this.cycle;
        for (let index = this.historyIndex + direction;
            index >= 0 && index < this.history.length; index += direction) {
            const entry = this.history[index];
            if (this.images.has(entry.id) && !failed.has(entry.id))
                return { entry, index };
        }
        if (direction < 0) return null;
        let entries = [...this.images.values()].filter(entry => !failed.has(entry.id));
        if (entries.length > 1) entries = entries.filter(entry => entry.id !== this.currentKey);
        if (entries.length === 0) return null;
        return { entry: entries[Math.floor(Math.random() * entries.length)], index: null };
    }

    loadCandidate() {
        const candidate = this.candidate();
        if (!candidate && this.cycle.direction < 0) {
            this.settle();
            return;
        }
        if (!candidate || this.cycle.attempts >= this.maxImageAttempts) {
            this.retrySlowly();
            return;
        }
        const generation = this.invalidate();
        const image = new Image();
        const { entry, index } = candidate;
        this.pendingKey = entry.id;
        this.cycle.attempts++;
        this.cancelLoad = () => {
            image.onload = null;
            image.onerror = null;
            image.removeAttribute('src');
        };
        this.setState('loading');
        const failed = message => {
            if (generation !== this.generation) return;
            this.invalidate();
            this.cycle.failed.add(entry.id);
            if (this.cycle.attempts >= this.maxImageAttempts || !this.candidate()) {
                this.retrySlowly();
                return;
            }
            this.setState(this.paused ? 'paused' : 'retrying',
                this.paused ? message : `${message}. Retrying...`);
            // A manual navigation may finish while paused, but never start an automatic retry.
            if (!this.paused) this.schedule(
                Math.min(2000, 250 * 2 ** (this.cycle.attempts - 1)) * (0.8 + Math.random() * 0.4),
                () => this.loadCandidate());
        };
        image.onload = () => {
            if (generation !== this.generation) return;
            this.cancelLoad = null;
            this.invalidate();
            image.onload = null;
            image.onerror = null;
            this.mainImage.replaceWith(image);
            this.mainImage = image;
            this.background.src = image.src;
            this.currentKey = entry.id;
            this.displayVersion++;
            this.label = entry.label;
            if (index === null) {
                this.history.push(entry);
                if (this.history.length > this.historyLimit) this.history.shift();
                this.historyIndex = this.history.length - 1;
            } else {
                this.historyIndex = index;
            }
            this.settle();
        };
        image.onerror = () => failed('Image unavailable');
        this.schedule(this.loadDeadlineMs, () => failed('Image request timed out'));
        image.src = `image?key=${encodeURIComponent(entry.id)}`;
    }

    retrySlowly() {
        this.poolReady = false;
        this.setState(this.paused ? 'paused' : 'retrying',
            this.paused ? 'Images unavailable' : 'Images unavailable. Retrying in 30 seconds...');
        if (!this.paused) this.schedule(this.slowRetryMs, () => {
            this.cycle = { direction: 1, attempts: 0, failed: new Set() };
            this.loadPool();
        });
    }

    getKey() {
        return this.currentKey;
    }

    setHidden() {
        return this.mark('hide', 'Hidden');
    }

    setFlagged() {
        return this.mark('flag', 'Flagged');
    }

    async mark(action, successMessage) {
        const key = this.currentKey;
        if (key === null) return;
        const displayVersion = this.displayVersion;
        const generation = this.generation;
        const statusVersion = ++this.statusVersion;
        const isCurrent = () => displayVersion === this.displayVersion &&
            generation === this.generation && statusVersion === this.statusVersion;
        try {
            const response = await fetch(`image/${action}?key=${encodeURIComponent(key)}`, {
                method: 'POST', signal: AbortSignal.timeout(this.loadDeadlineMs)
            });
            if (!response.ok) throw new Error(`Failed to ${action} image (${response.status})`);
        } catch (error) {
            if (isCurrent()) this.setStatus(error.message);
            return;
        }
        if (action === 'hide') {
            this.hiddenIds.add(key);
            this.images.delete(key);
            if (this.pendingKey === key) this.advance(1, true);
        }
        if (isCurrent()) this.setStatus(successMessage);
    }
}
