// A MIDI keyboard in the Mode Lab: each key pressed sounds through the player's own room (so it sits
// with the loop) and lights its body in the home-note view, so playing along shows which notes of the
// mode you are touching, and counts the ones the mode does not contain. Web MIDI, notes only (no
// SysEx, so no extra permission). Connected on a click, never on load.

import { audition } from './modal-player.js';
import { pulsePitch } from './mode-gravity.js';

let access = null;
let target = null;
let dotNet = null;

export function supported() {
    return typeof navigator !== 'undefined' && typeof navigator.requestMIDIAccess === 'function';
}

/// Asks for MIDI access and listens on every input. Answers the input names, or null when refused.
export async function connect(dotNetRef, gravityCanvas) {
    if (!supported()) {
        return null;
    }
    try {
        access ??= await navigator.requestMIDIAccess({ sysex: false });
    } catch {
        return null;
    }
    dotNet = dotNetRef;
    target = gravityCanvas;
    const listen = () => {
        for (const input of access.inputs.values()) {
            input.onmidimessage = onMessage;
        }
        publish();
    };
    access.onstatechange = listen;
    listen();
    return names();
}

export function disconnect() {
    if (access) {
        for (const input of access.inputs.values()) {
            input.onmidimessage = null;
        }
        access.onstatechange = null;
    }
    dotNet = null;
    target = null;
    publish();
}

function names() {
    return access ? [...access.inputs.values()].map((input) => input.name || 'MIDI input') : [];
}

function publish() {
    try {
        document.body.dataset.midiInputs = String(dotNet ? names().length : 0);
    } catch { }
    dotNet?.invokeMethodAsync('OnMidiInputs', names()).catch(() => { });
}

function onMessage(event) {
    const [status, note, velocity] = event.data;
    if ((status & 0xf0) !== 0x90 || velocity === 0) {
        return; // note-off, or a note-on at zero velocity, which is a note-off
    }
    // Spread across the stereo field by pitch, like the bodies on the clock: low left, high right.
    audition(note, Math.max(-0.8, Math.min(0.8, (note - 60) / 30)));
    const inMode = target ? pulsePitch(target, note) : true;
    dotNet?.invokeMethodAsync('OnMidiNote', note, inMode).catch(() => { });
}
