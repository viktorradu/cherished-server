import Main from './main.js';

document.addEventListener('DOMContentLoaded', () => {
    const app = new Main();
    if(document){
        const canvas = document.getElementById('canvas');
        app.init(canvas);
    }
});