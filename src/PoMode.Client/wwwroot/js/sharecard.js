// Renders a shareable 1200x630 PNG of one analysis and triggers a download. Pure drawing:
// every musical decision (note roles, labels, modes, key, tempo) arrives pre-made from the
// server via the Blazor caller — this module only maps numbers to pixels.

const WIDTH = 1200;
const HEIGHT = 630;

// Mirrors the app's dark theme palette (app.css) so the card matches the product.
const palette = {
    bg: "#121214",
    surface: "#1d1d21",
    border: "#33333c",
    fg: "#e8e8ec",
    muted: "#a0a0ab",
    accent: "#cfbcff",
    chordBlock: "#2a2a33",
    // Indexed by the server's NoteRole enum: ChordTone, InMode, Characteristic, Outside.
    noteRoles: ["#60a5fa", "#94a3b8", "#fbbf24", "#f87171"],
    modes: {
        ionian: "#2563eb", dorian: "#059669", phrygian: "#dc2626", lydian: "#7c3aed",
        mixolydian: "#d97706", aeolian: "#475569", locrian: "#be185d",
        minorpentatonic: "#0891b2", majorpentatonic: "#65a30d",
    },
};

export function downloadCard(card) {
    const canvas = document.createElement("canvas");
    canvas.width = WIDTH;
    canvas.height = HEIGHT;
    const ctx = canvas.getContext("2d");

    drawBackground(ctx);
    drawHeader(ctx, card);
    drawPianoRoll(ctx, card, { x: 48, y: 170, width: WIDTH - 96, height: 260 });
    drawChordRow(ctx, card, { x: 48, y: 446, width: WIDTH - 96, height: 40 });
    drawModeTimeline(ctx, card, { x: 48, y: 502, width: WIDTH - 96, height: 26 });
    drawLegend(ctx, card, { x: 48, y: 560 });

    canvas.toBlob(blob => {
        if (!blob) {
            return;
        }
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = `${(card.fileName || "analysis").replace(/\.[^.]+$/, "")}-pomode.png`;
        link.click();
        URL.revokeObjectURL(link.href);
    }, "image/png");
}

function drawBackground(ctx) {
    ctx.fillStyle = palette.bg;
    ctx.fillRect(0, 0, WIDTH, HEIGHT);
    ctx.strokeStyle = palette.border;
    ctx.lineWidth = 2;
    ctx.strokeRect(1, 1, WIDTH - 2, HEIGHT - 2);
}

function drawHeader(ctx, card) {
    ctx.fillStyle = palette.fg;
    ctx.font = "600 40px system-ui, sans-serif";
    ctx.fillText(truncate(ctx, card.fileName || "Untitled", WIDTH - 96), 48, 76);

    const tempo = card.tempoBpm ? `${Math.round(card.tempoBpm)} BPM${card.tempoEstimated ? " (est.)" : ""}` : "";
    const key = card.tonicName ? `${card.tonicName} ${card.primaryMode || ""}`.trim() : "Key unclear";
    ctx.fillStyle = palette.accent;
    ctx.font = "500 28px system-ui, sans-serif";
    ctx.fillText([key, tempo].filter(Boolean).join("  ·  "), 48, 122);

    ctx.fillStyle = palette.muted;
    ctx.font = "400 20px system-ui, sans-serif";
    ctx.textAlign = "right";
    ctx.fillText("PoMode", WIDTH - 48, 76);
    ctx.textAlign = "left";
}

function drawPianoRoll(ctx, card, rect) {
    ctx.fillStyle = palette.surface;
    ctx.fillRect(rect.x, rect.y, rect.width, rect.height);

    const duration = Math.max(card.durationSec || 0, 0.001);
    const span = Math.max((card.maxPitch ?? 72) - (card.minPitch ?? 48), 1);
    for (const note of card.notes || []) {
        const x = rect.x + (note.startSec / duration) * rect.width;
        const w = Math.max((note.durationSec / duration) * rect.width, 2);
        const rowHeight = rect.height / (span + 1);
        const y = rect.y + rect.height - ((note.midiPitch - card.minPitch + 1) * rowHeight);
        ctx.fillStyle = palette.noteRoles[note.role] || palette.muted;
        ctx.fillRect(x, y, w, Math.max(rowHeight - 1, 2));
    }

    ctx.strokeStyle = palette.border;
    ctx.strokeRect(rect.x, rect.y, rect.width, rect.height);
}

function drawChordRow(ctx, card, rect) {
    const duration = Math.max(card.durationSec || 0, 0.001);
    ctx.font = "500 16px system-ui, sans-serif";
    for (const chord of card.chords || []) {
        const x = rect.x + (chord.startSec / duration) * rect.width;
        const w = Math.max(((chord.endSec - chord.startSec) / duration) * rect.width - 2, 2);
        ctx.fillStyle = palette.chordBlock;
        ctx.fillRect(x, rect.y, w, rect.height);
        if (w > 34) {
            ctx.fillStyle = palette.fg;
            ctx.fillText(chord.symbol, x + 6, rect.y + 26);
        }
    }
}

function drawModeTimeline(ctx, card, rect) {
    const duration = Math.max(card.durationSec || 0, 0.001);
    for (const window of card.windows || []) {
        const x = rect.x + (window.startSec / duration) * rect.width;
        const w = Math.max(((window.endSec - window.startSec) / duration) * rect.width, 1);
        const mode = (window.modeTag || "").toLowerCase();
        ctx.fillStyle = palette.modes[mode] || palette.chordBlock;
        ctx.fillRect(x, rect.y, w, rect.height);
    }
    ctx.strokeStyle = palette.border;
    ctx.strokeRect(rect.x, rect.y, rect.width, rect.height);
}

function drawLegend(ctx, card, at) {
    const seen = [];
    for (const window of card.windows || []) {
        const mode = window.modeTag;
        if (mode && !seen.includes(mode)) {
            seen.push(mode);
        }
    }
    ctx.font = "400 18px system-ui, sans-serif";
    let x = at.x;
    for (const mode of seen) {
        ctx.fillStyle = palette.modes[mode.toLowerCase()] || palette.chordBlock;
        ctx.fillRect(x, at.y - 14, 16, 16);
        ctx.fillStyle = palette.muted;
        ctx.fillText(mode, x + 22, at.y);
        x += 22 + ctx.measureText(mode).width + 26;
    }
}

function truncate(ctx, text, maxWidth) {
    if (ctx.measureText(text).width <= maxWidth) {
        return text;
    }
    let cut = text;
    while (cut.length > 1 && ctx.measureText(cut + "…").width > maxWidth) {
        cut = cut.slice(0, -1);
    }
    return cut + "…";
}
