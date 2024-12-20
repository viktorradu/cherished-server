export default class Main {
    constructor() {
        this.slideshowIntervalMs = 8000;
        this.intervalId = null;
        this.target = null;
        this.mainImage = null;
        this.background = null;
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
    }

    updateSlide() {
        if(this.poolSize === 0 && !this.poolUpdateRequested){
            this.poolUpdateRequested = true;
            fetch("image/loadpool")
            .then(response => response.json())
            .then(data => {
                this.poolSize = data;
                
            });
        }
        if(this.poolSize > 0 && this.state === 'running'){
            let key = Math.floor(Math.random() * this.poolSize);
            let image_uri = `image?key=${key}`
            const img = new Image();
            img.src = image_uri;
            img.onload = () => {
                this.background.style.backgroundImage = "url('" + image_uri + "')";
                this.mainImage.src = image_uri;
            }; 
        }
    }

    flashStatus(message){
        this.statusBox.innerText = message;
        setTimeout(()=>{
            this.statusBox.innerText = '';
        }, 2000);
    }
}