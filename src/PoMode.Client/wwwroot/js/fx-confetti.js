// A tiny dependency-free confetti burst for celebrations (new karaoke best). Fixed-position
// coloured squares animated with the Web Animations API, removed when done — no canvas, no loop,
// no leaks. Skipped entirely under prefers-reduced-motion.

const COLOURS = ['#6750a4', '#10b981', '#f59e0b', '#ec4899', '#60a5fa'];

export function burst() {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        return;
    }
    const originX = window.innerWidth / 2;
    const originY = window.innerHeight * 0.35;
    for (let i = 0; i < 60; i++) {
        const piece = document.createElement('div');
        const size = 5 + ((i * 7) % 6);
        piece.style.cssText = `position:fixed;left:${originX}px;top:${originY}px;width:${size}px;`
            + `height:${size * 0.6}px;background:${COLOURS[i % COLOURS.length]};`
            + 'pointer-events:none;z-index:9999;border-radius:1px;';
        document.body.appendChild(piece);

        const angle = (i / 60) * Math.PI * 2;
        const distance = 120 + ((i * 37) % 160);
        const dx = Math.cos(angle) * distance;
        const dy = (Math.sin(angle) * distance * 0.5) + 260; // gravity pulls every piece down
        const spin = 360 + ((i * 53) % 540);
        piece.animate(
            [
                { transform: 'translate(0,0) rotate(0deg)', opacity: 1 },
                { transform: `translate(${dx}px,${dy}px) rotate(${spin}deg)`, opacity: 0 },
            ],
            { duration: 1200 + ((i * 31) % 600), easing: 'cubic-bezier(0.15, 0.6, 0.4, 1)' },
        ).onfinish = () => piece.remove();
    }
}
