export default class Main {
    constructor() {
        this.slideshowIntervalMs = 8000;
        this.intervalId = null;
        this.target = null;
        this.updateSlide = this.updateSlide.bind(this);
        this.poolSize = 0;
        this.poolUpdateRequested = false;
    }

    init(target) {
        this.intervalId = setInterval(this.updateSlide, this.slideshowIntervalMs);
        this.target = target;
        console.log("init");
        console.log(this.target);
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
            this.target.src = `image?key=${key}`;
        }
    }
}