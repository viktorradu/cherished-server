import Main from './main.js';

document.addEventListener('DOMContentLoaded', () => {
    const app = new Main();
    if(document){
        const target = document.getElementById('main');
        app.init(target);
    }
});