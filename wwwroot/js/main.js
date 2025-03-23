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
        this.mainImage = this.target.querySelector('img');
        this.background = this.target.querySelector('.ch-background');
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
        const buttonFlag = this.target.querySelector('.ch-button-flag');
        buttonFlag.addEventListener('click', (e) => {
            alert('Flag not implemented yet');
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

                this.background.style.backgroundImage = "url('" + image_uri + "')";
                this.mainImage.src = image_uri;
                this.timeoutId = setTimeout(this.updateSlide, this.slideshowIntervalMs);
                EXIF.getData(this.mainImage, function(){
                    const label = EXIF.getTag(this, 'ImageDescription') || '';
                    self.setStatus(label);
                });
            };

            img.onerror = () => {
                this.flashStatus('Failed to load image');
                this.timeoutId = setTimeout(this.updateSlide, this.slideshowIntervalMs);
            };
        }
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