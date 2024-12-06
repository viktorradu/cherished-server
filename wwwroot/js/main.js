export default class Main {
    constructor() {
        this.slideshowIntervalMs = 8000;
        this.intervalId = null;
        this.target = null;
        this.mainImage = null;
        this.statusBox = null;
        this.updateSlide = this.updateSlide.bind(this);
        this.poolSize = 0;
        this.poolUpdateRequested = false;
        this.state = 'running';
    }

    init(target) {
        this.intervalId = setInterval(this.updateSlide, this.slideshowIntervalMs);
        this.target = target;
        this.target.className = 'ch-container';
        this.mainImage = document.createElement('img');
        this.mainImage.className = 'ch-image-h';
        this.mainImage.title = 'Loading image...';
        this.target.appendChild(this.mainImage);
        this.statusBox = document.createElement('div');
        this.statusBox.className = 'ch-status';
        this.target.appendChild(this.statusBox);
        target.addEventListener('click', ()=>{
            if(this.state === 'running'){
                this.state = 'paused';
                this.flashStatus('Paused');
            }else{
                this.state = 'running';
                this.flashStatus('Running');
            }
        });
    }

    updateSlide() {
        if(this.poolSize === 0 && !this.poolUpdateRequested){
            fetch("image/loadpool")
            .then(response => response.json())
            .then(data => {
                this.poolSize = data;
                this.poolUpdateRequested = true;
            });
        }
        if(this.poolSize > 0){
            let key = Math.floor(Math.random() * this.poolSize);
            this.mainImage.src = `image?key=${key}`;
        }
    }

    flashStatus(message){
        this.statusBox.innerText = message;
        setTimeout(()=>{
            this.statusBox.innerText = '';
        }, 2000);
    }
}