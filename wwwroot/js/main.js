export default class Main {
    constructor() {
        this.slideshowIntervalMs = 3000;
        this.timeoutId = null;
        this.target = null;
        this.mainImage = null;
        this.background = null;
        this.statusBox = null;
        this.updateSlide = this.updateSlide.bind(this);
        this.poolSize = 0;
        this.poolUpdateRequested = false;
        this.history = new Array(100).fill(null);
        this.historyHead = 0;
        this.historyOffset = 0;
    }

    init(target) {
        this.target = target;
        this.target.className = 'ch-container';
        this.mainImage = this.target.querySelector('.ch-image img');
        this.background = this.target.querySelector('.ch-background img');
        this.statusBox = this.target.querySelector('.ch-status');
        const buttonNext = this.target.querySelector('.ch-button-next');
        buttonNext.addEventListener('click', (e) => {
            clearTimeout(this.timeoutId);
            this.updateSlide(1);
            e.stopPropagation();
        });
        const buttonPrev = this.target.querySelector('.ch-button-prev');
        buttonPrev.addEventListener('click', (e) => {
            clearTimeout(this.timeoutId);
            this.updateSlide(-1);
            e.stopPropagation();
        });
        const buttonHide = this.target.querySelector('.ch-button-hide');
        buttonHide.addEventListener('click', (e) => {
            this.setHidden(this.getKey());
            e.stopPropagation();
        });
        const buttonDelete = this.target.querySelector('.ch-button-delete');
        buttonDelete.addEventListener('click', (e) => {
            this.setDeleted(this.getKey());
            e.stopPropagation();
        });
        this.target.addEventListener('click', () => {
            if(this.target.classList.contains('paused')) {
                this.flashStatus('Running');
                this.target.classList.remove('paused');
                clearTimeout(this.timeoutId);
                this.updateSlide();
            } else {
                this.flashStatus('Paused');
                this.target.classList.add('paused');
                clearTimeout(this.timeoutId);
            }
        });
        window.addEventListener('keyup', (event) => {
            if (event.key === 'ArrowRight') {
                clearTimeout(this.timeoutId);
                this.updateSlide(1);
            }
            if (event.key === 'ArrowLeft') {
                clearTimeout(this.timeoutId);
                this.updateSlide(-1);
            }
        });
        if (this.poolSize === 0 && !this.poolUpdateRequested) {
            this.poolUpdateRequested = true;
            fetch("image/loadpool")
                .then(response => response.json())
                .then(data => {
                    this.poolSize = data.poolSize;
                    this.slideshowIntervalMs = data.slideshowIntervalMs;
                    if (this.poolSize > 0) {
                        this.updateSlide();
                    }
                });
        }
    }

    updateSlide(push = 0) {
        if (this.poolSize > 0) {
            let key = null;

            if (push >= 0) {
                if (this.historyOffset > 0) {
                    this.historyOffset--;
                }
            } else {
                this.historyOffset++;
            }

            if (this.historyOffset > 0) {
                const historyIndex = this.historyHead - this.historyOffset;
                if (historyIndex < 0) {
                    historyIndex = this.history.length + historyIndex;
                }
                key = this.history[this.historyHead - this.historyOffset];
            }

            if (key === null) {
                key = Math.floor(Math.random() * this.poolSize);
                this.historyHead = (this.historyHead + 1) % this.history.length;
                this.history[this.historyHead] = key;
            }

            let image_uri = `image?key=${key}`
            const img = new Image();
            img.src = image_uri;

            const self = this;
            img.onload = () => {
                this.background.src = image_uri;
                this.mainImage.src = image_uri;
                this.timeoutId = setTimeout(this.updateSlide, this.slideshowIntervalMs);
                EXIF.getData(img, function(){
                    const label_bytes = EXIF.getTag(this, 'ImageDescription') || '';
                    self.setStatus(decodeURIComponent(label_bytes));
                });
            };

            img.onerror = () => {
                console.log(key);
                this.timeoutId = setTimeout(this.updateSlide, 100);
            };
        }
    }

    setHidden(key){
        fetch(`image/hide?key=${key}`, { method: 'POST' })
            .then(response => {
                if (response.ok) {
                    this.flashStatus('Hidden');
                } else {
                    this.flashStatus('Failed to hide image');
                }
            })
            .catch(error => {
                this.flashStatus(error.message);
            });
    }

    setDeleted(key){
        fetch(`image/delete?key=${key}`, { method: 'POST' })
            .then(response => {
                if (response.ok) {
                    this.flashStatus('Deleted');
                } else {
                    this.flashStatus('Failed to delete image');
                }
            })
            .catch(error => {
                this.flashStatus(error.message);
            });
    }

    getKey(){
        return this.history[this.historyHead];
    }

    setStatus(message){
        this.statusBox.innerText = message;
    }

    flashStatus(message){
        this.statusBox.innerText = message;
        setTimeout(()=>{
            this.statusBox.innerText = '';
        }, 2000);
    }
}