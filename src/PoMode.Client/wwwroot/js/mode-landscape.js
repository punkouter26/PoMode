// Orbitable 3D "mode landscape": the song's modal timeline as a ribbon of box segments laid
// along X. Each analysis window becomes one box — width proportional to its duration, height to
// its confidence, colour to its mode — over a thin dark ground plane.
//
// Rendering is strictly on demand: one frame at init, one (rAF-coalesced) frame per interaction
// or resize. There is no animation loop, so an untouched landscape costs zero CPU. Blazor only
// calls init/dispose; all pointer and wheel handling lives here.

import * as THREE from '../lib/three/three.module.min.js';

const states = new Map();

/// Colour per mode tag. Unknown or null windows fall back to a neutral gray so gaps in the
/// analysis read as "no opinion" rather than as a wrong answer.
const MODE_COLORS = {
    ionian: 0x2563eb,
    dorian: 0x059669,
    phrygian: 0xdc2626,
    lydian: 0x7c3aed,
    mixolydian: 0xd97706,
    aeolian: 0x475569,
    locrian: 0xbe185d,
    minorpentatonic: 0x0891b2,
    majorpentatonic: 0x65a30d,
};

const UNKNOWN_COLOR = 0x3a3a44;

/// The whole timeline is normalized to this many world units along X, whatever the song length,
/// so camera distances and clamps behave identically for a 30-second clip and a 10-minute track.
const RIBBON_LENGTH = 10;

const RIBBON_DEPTH = 1;
const MIN_HEIGHT = 0.15;
const HEIGHT_SPAN = 1.6;

/// Orbit limits. Pitch stays off the ground and short of the pole; dolly stays outside the ribbon
/// and close enough that it never disappears.
const PITCH_MIN = 0.1;
const PITCH_MAX = 1.4;
const DISTANCE_MIN = 3;
const DISTANCE_MAX = 40;

const ROTATE_SPEED = 0.005;
const DOLLY_SPEED = 1.1;

/// Positions the camera from the orbit spherical coordinates and coalesces a single render
/// through requestAnimationFrame, so a burst of pointermove/wheel events costs one frame.
function requestRender(state) {
    if (state.frame !== 0) {
        return;
    }
    state.frame = requestAnimationFrame(() => {
        state.frame = 0;
        renderNow(state);
    });
}

function renderNow(state) {
    const { camera, orbit } = state;
    const cosPitch = Math.cos(orbit.pitch);
    camera.position.set(
        orbit.target.x + orbit.distance * cosPitch * Math.sin(orbit.yaw),
        orbit.target.y + orbit.distance * Math.sin(orbit.pitch),
        orbit.target.z + orbit.distance * cosPitch * Math.cos(orbit.yaw),
    );
    camera.lookAt(orbit.target);
    state.renderer.render(state.scene, camera);
}

/// Builds the ribbon meshes for the given windows. Total duration maps to RIBBON_LENGTH units,
/// centred on the origin so the orbit target is simply (0, 0, 0).
function buildScene(scene, windows) {
    const totalSeconds = windows.reduce(
        (sum, w) => sum + Math.max(w.endSec - w.startSec, 0), 0);
    const scale = totalSeconds > 0 ? RIBBON_LENGTH / totalSeconds : 0;

    let x = -RIBBON_LENGTH / 2;
    for (const w of windows) {
        const width = Math.max(w.endSec - w.startSec, 0) * scale;
        if (width <= 0) {
            continue;
        }
        const tag = typeof w.modeTag === 'string' ? w.modeTag.toLowerCase() : null;
        const color = MODE_COLORS[tag] ?? UNKNOWN_COLOR;
        const height = MIN_HEIGHT + HEIGHT_SPAN * (w.confidence ?? 0);
        const mesh = new THREE.Mesh(
            new THREE.BoxGeometry(width, height, RIBBON_DEPTH),
            new THREE.MeshStandardMaterial({ color, roughness: 0.65, metalness: 0.05 }));
        mesh.position.set(x + width / 2, height / 2, 0);
        scene.add(mesh);
        x += width;
    }

    const ground = new THREE.Mesh(
        new THREE.BoxGeometry(RIBBON_LENGTH + 2, 0.05, RIBBON_DEPTH + 2),
        new THREE.MeshStandardMaterial({ color: 0x14141a, roughness: 0.9, metalness: 0 }));
    ground.position.set(0, -0.05, 0);
    scene.add(ground);

    scene.add(new THREE.AmbientLight(0xffffff, 0.55));
    const sun = new THREE.DirectionalLight(0xffffff, 1.4);
    sun.position.set(6, 10, 8);
    scene.add(sun);
}

/// Small custom orbit: drag rotates yaw (unbounded) and pitch (clamped), wheel dollies the
/// distance. Deliberately not OrbitControls — this is a fraction of the size and renders only
/// on demand.
function attachControls(state) {
    const canvas = state.canvas;

    const onPointerDown = (event) => {
        if (!event.isPrimary) {
            return;
        }
        state.dragging = true;
        state.lastX = event.clientX;
        state.lastY = event.clientY;
        canvas.setPointerCapture(event.pointerId);
    };

    const onPointerMove = (event) => {
        if (!state.dragging || !event.isPrimary) {
            return;
        }
        const orbit = state.orbit;
        orbit.yaw -= (event.clientX - state.lastX) * ROTATE_SPEED;
        orbit.pitch = Math.min(Math.max(
            orbit.pitch + (event.clientY - state.lastY) * ROTATE_SPEED, PITCH_MIN), PITCH_MAX);
        state.lastX = event.clientX;
        state.lastY = event.clientY;
        requestRender(state);
    };

    const onPointerUp = (event) => {
        state.dragging = false;
        if (canvas.hasPointerCapture(event.pointerId)) {
            canvas.releasePointerCapture(event.pointerId);
        }
    };

    const onWheel = (event) => {
        event.preventDefault();
        const factor = event.deltaY > 0 ? DOLLY_SPEED : 1 / DOLLY_SPEED;
        state.orbit.distance = Math.min(Math.max(
            state.orbit.distance * factor, DISTANCE_MIN), DISTANCE_MAX);
        requestRender(state);
    };

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    canvas.addEventListener('wheel', onWheel, { passive: false });

    state.removeListeners = () => {
        canvas.removeEventListener('pointerdown', onPointerDown);
        canvas.removeEventListener('pointermove', onPointerMove);
        canvas.removeEventListener('pointerup', onPointerUp);
        canvas.removeEventListener('pointercancel', onPointerUp);
        canvas.removeEventListener('wheel', onWheel);
    };
}

function resize(state) {
    const width = Math.max(state.canvas.clientWidth, 1);
    const height = Math.max(state.canvas.clientHeight, 1);
    state.camera.aspect = width / height;
    state.camera.updateProjectionMatrix();
    // false: keep the CSS size the layout chose; only the drawing buffer follows it.
    state.renderer.setSize(width, height, false);
    requestRender(state);
}

/// Builds the landscape onto `canvas` for `windows` = [{ startSec, endSec, modeTag, confidence }].
/// Re-initialising an already-initialised canvas disposes the old scene first, so a rebuild is
/// simply init again. Returns false (and leaves nothing behind) when WebGL is unavailable.
export function init(canvas, windows) {
    dispose(canvas);

    let renderer;
    try {
        renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    } catch {
        return false;
    }
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));

    const scene = new THREE.Scene();
    buildScene(scene, Array.isArray(windows) ? windows : []);

    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 200);

    const state = {
        canvas,
        renderer,
        scene,
        camera,
        orbit: {
            target: new THREE.Vector3(0, 0.5, 0),
            yaw: 0.5,
            pitch: 0.6,
            distance: RIBBON_LENGTH * 1.2,
        },
        frame: 0,
        dragging: false,
        lastX: 0,
        lastY: 0,
        removeListeners: null,
        observer: null,
    };
    states.set(canvas, state);

    attachControls(state);
    state.observer = new ResizeObserver(() => resize(state));
    state.observer.observe(canvas);

    resize(state);
    renderNow(state);
    return true;
}

/// Tears the landscape down: cancels any pending frame, detaches listeners and the observer,
/// disposes every geometry, material and the renderer, and forgets the canvas.
export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.frame !== 0) {
        cancelAnimationFrame(state.frame);
    }
    state.removeListeners?.();
    state.observer?.disconnect();
    state.scene.traverse((object) => {
        object.geometry?.dispose?.();
        object.material?.dispose?.();
    });
    state.renderer.dispose();
    states.delete(canvas);
}
