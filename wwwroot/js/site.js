import Main from './main.js';
import Sensors from './sensors.js';

document.addEventListener('DOMContentLoaded', () => {
    const app = new Main();
    const target = document.getElementById('main');
    app.init(target);
    new Sensors().init(target);
});