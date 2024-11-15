export default class Main {
    constructor() {
        this.slideshowIntervalMs = 3000;
        this.intervalId = null;
        this.target = null;
        this.updateSlide = this.updateSlide.bind(this);
    }

    init(target) {
        this.intervalId = setInterval(this.updateSlide, this.slideshowIntervalMs);
        this.target = target;
        console.log("init");
        console.log(this.target);
    }

    updateSlide() {
        console.log(this.target);
        this.target.src = "image?key=5";
    }
}