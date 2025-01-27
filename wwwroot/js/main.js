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
        this.state = 'running';
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
        this.target.addEventListener('click', ()=>{
            if(this.state === 'running'){
                this.state = 'paused';
                this.flashStatus('Paused');
            }else{
                this.state = 'running';
                this.flashStatus('Running');
            }
        });
        window.addEventListener('keyup', (event)=>{
            if(event.key === 'ArrowRight'){
                clearTimeout(this.timeoutId);
                this.updateSlide(1);
            }
            if(event.key === 'ArrowLeft'){
                clearTimeout(this.timeoutId);
                this.updateSlide(-1);
            }
        });
        if(this.poolSize === 0 && !this.poolUpdateRequested){
            this.poolUpdateRequested = true;
            fetch("image/loadpool")
            .then(response => response.json())
            .then(data => {
                this.poolSize = data;
                if(this.poolSize > 0){
                    this.updateSlide();
                }
            });
        }
    }

    updateSlide(push = 0){
        if(this.poolSize > 0 && this.state === 'running'){
            let key = null;

            if(push >= 0){
                if(this.historyOffset > 0){
                    this.historyOffset--;
                }
            }else{
                this.historyOffset++;
            }

            if(this.historyOffset > 0){
                const historyIndex = this.historyHead - this.historyOffset;
                if(historyIndex < 0){
                    historyIndex = this.history.length + historyIndex;
                }
                key = this.history[this.historyHead - this.historyOffset];
            }
            
            if(key === null){
                key = Math.floor(Math.random() * this.poolSize);
                this.historyHead = (this.historyHead + 1) % this.history.length;
                this.history[this.historyHead] = key;
            }

            let image_uri = `image?key=${key}`
            const img = new Image();
            img.src = image_uri;

            img.onload = () => {
                this.background.style.backgroundImage = "url('" + image_uri + "')";
                this.mainImage.src = image_uri;
                this.timeoutId = setTimeout(this.updateSlide, this.slideshowIntervalMs);
            }; 
        }
    }

    getKey(){
        return this.history[this.historyHead];
    }

    flashStatus(message){
        this.statusBox.innerText = message;
        setTimeout(()=>{
            this.statusBox.innerText = '';
        }, 2000);
    }
}