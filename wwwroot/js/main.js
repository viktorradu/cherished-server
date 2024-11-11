export default class Main {
    constructor() {
        this.slideshowIntervalMs = 10000;
        this.intervalId = null;
        this.target = null;
    }

    init(target) {
        this.intervalId = setInterval(this.updateSlide, this.slideshowIntervalMs);
        this.target = target;
    }

    updateSlide() {
        this.target.src = "Image?key=5&size=1000,800";
    }
}