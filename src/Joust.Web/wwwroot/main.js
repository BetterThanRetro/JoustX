// JoustX in the browser: runs the game (Joust.Web.WebGame, C# compiled to WebAssembly) at the
// board's 60.1 frames a second, draws it (gl.js), plays its sound (audio.js) and reads the
// keyboard and gamepads. The saved settings (CMOS) and display choices live in localStorage.
import { dotnet } from './_framework/dotnet.js';
import { Renderer } from './gl.js';

const FRAME_MS = 1000 / 60.0959;
const status = document.getElementById('status');
const say = (text) => { status.textContent = text; };

// ------------------------------------------------------------------ start-up

say('Loading the game…');
const { getAssemblyExports, getConfig } = await dotnet.create();
const game = (await getAssemblyExports(getConfig().mainAssemblyName)).Joust.Web.WebGame;
const image = new Uint8Array(await (await fetch('joust.img')).arrayBuffer());

const store = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { /* private window: not kept */ } },
};
const toBase64 = (bytes) => btoa(String.fromCharCode(...bytes));
const fromBase64 = (text) => Uint8Array.from(atob(text), (c) => c.charCodeAt(0));

say('Starting the board…');
const saved = store.get('joustx.cmos');
let lastCmos = '';
function saveCmos() {
    const cmos = new Uint8Array(game.Cmos());
    if (!cmos.length) return;
    const text = toBase64(cmos);
    if (text !== lastCmos) { store.set('joustx.cmos', text); lastCmos = text; }
}
if (game.Start(image, saved ? fromBase64(saved) : new Uint8Array(0))) saveCmos();
else lastCmos = saved ?? '';

const display = Object.assign({ hd: true, crt: false, widescreen: true, reverb: true },
    JSON.parse(store.get('joustx.display') ?? '{}'));
const saveDisplay = () => store.set('joustx.display', JSON.stringify(display));
game.SetReverb(display.reverb);

const canvas = document.getElementById('screen');
const renderer = new Renderer(canvas);
renderer.setBackdrop(new Uint8Array(game.Backdrop()));

// ------------------------------------------------------------------ input

const held = new Set();
const BUTTONS = {
    ArrowLeft: 0, KeyA: 0, ArrowRight: 1, KeyD: 1, Space: 2, ControlLeft: 2,
    KeyJ: 3, KeyL: 4, KeyK: 5, ControlRight: 5,
    Digit1: 6, Digit2: 7, Digit5: 8, Digit6: 9, F2: 10, F4: 11,
};
const ACTIONS = {
    F3: () => game.Reset(),
    F5: () => { display.hd = !display.hd; saveDisplay(); },
    F6: () => { display.crt = !display.crt; saveDisplay(); },
    F7: () => { display.reverb = !display.reverb; game.SetReverb(display.reverb); saveDisplay(); },
    F8: () => { display.widescreen = !display.widescreen; saveDisplay(); },
    F10: () => { padInfo.hidden = !padInfo.hidden; },
    F11: () => document.fullscreenElement ? document.exitFullscreen() : canvas.requestFullscreen(),
};

// F10: what the browser reports for each gamepad, and the buttons the game gets (to find why a pad does nothing).
const padInfo = document.getElementById('padinfo');
const padEvents = [];
// Some browsers share pads with a page more readily once it listens for them.
addEventListener('gamepadconnected', (e) => padEvents.push(`connected ${e.gamepad.index}: ${e.gamepad.id}`));
addEventListener('gamepaddisconnected', (e) => padEvents.push(`disconnected ${e.gamepad.index}`));
function showPads(bits) {
    if (padInfo.hidden) return;
    const lines = [...(navigator.getGamepads?.() ?? [])].map((p, slot) => !p ? `slot ${slot}: empty` :
        `slot ${slot}: ${p.id} | mapping "${p.mapping}" | connected ${p.connected} | ` +
        `held [${p.buttons.map((b, i) => b.pressed ? i : -1).filter((i) => i >= 0).join(' ')}] | ` +
        `axes [${p.axes.map((a) => a.toFixed(2)).join(' ')}]`);
    lines.push(`game buttons: ${bits.toString(2).padStart(13, '0')} (bit 0 left, 1 right, 2 flap, 6 start, 8 coin)`);
    lines.push(`secure ${isSecureContext} | focused ${document.hasFocus()} | ${document.visibilityState} | ` +
        `interacted ${navigator.userActivation?.hasBeenActive} | ${navigator.userAgent.match(/(Chrome|Firefox|Edg)\/[\d.]+/)?.[0] ?? ''}`);
    lines.push(`pad events: ${padEvents.length ? padEvents.slice(-4).join('; ') : 'none'}`);
    padInfo.textContent = lines.join('\n');
}
// A press counts for at least 4 frames, however short: the board reads its switches once a
// frame and debounces them, so a quick tap between two frames would otherwise be lost.
const tapped = new Map();
addEventListener('keydown', (e) => {
    if (e.code in BUTTONS || e.code in ACTIONS) e.preventDefault();     // no scrolling, no F5 reload
    if (!e.repeat && e.code in ACTIONS) ACTIONS[e.code]();
    held.add(e.code);
    if (!e.repeat) tapped.set(e.code, 4);
});
addEventListener('keyup', (e) => held.delete(e.code));
addEventListener('blur', () => held.clear());

/** Xbox-style pads (the standard mapping): stick or D-pad steers, A/B/X flap, Start, Back = coin. */
function padBits(pad, shift) {
    if (!pad) return 0;
    const b = (i) => pad.buttons[i]?.pressed;
    let bits = 0;
    if (b(14) || pad.axes[0] < -0.35) bits |= 1 << shift;
    if (b(15) || pad.axes[0] > 0.35) bits |= 2 << shift;
    if (b(0) || b(1) || b(2)) bits |= 4 << shift;
    if (b(9)) bits |= 1 << (shift === 0 ? 6 : 7);
    if (b(8)) bits |= 1 << (shift === 0 ? 8 : 9);
    return bits;
}

function buttons() {
    let bits = 0;
    for (const code of held) if (code in BUTTONS) bits |= 1 << BUTTONS[code];
    for (const [code, left] of tapped) {
        if (code in BUTTONS) bits |= 1 << BUTTONS[code];
        if (left <= 1) tapped.delete(code); else tapped.set(code, left - 1);
    }
    // The first pad the browser reports plays player 1 and the next player 2, whatever their
    // slots: a stick can sit in slot 2 with slot 1 empty.
    const pads = [...(navigator.getGamepads?.() ?? [])].filter((p) => p?.connected);
    return bits | padBits(pads[0], 0) | padBits(pads[1], 3);
}

// ------------------------------------------------------------------ sound

let feed = null, context = null;
async function startSound() {
    context = new AudioContext({ sampleRate: 48000 });
    await context.audioWorklet.addModule('audio.js');
    feed = new AudioWorkletNode(context, 'joust-feed', { outputChannelCount: [2] });
    feed.connect(context.destination);
    await context.resume();
}

// Browsers let sound start only from a key or a click (not a gamepad button): if the game was
// started from a pad, the sound comes on at the first key or click.
const wake = () => { if (context?.state === 'suspended') context.resume(); };
addEventListener('pointerdown', wake);
addEventListener('keydown', wake);

// ------------------------------------------------------------------ the loop

let frames = 0;
function step() {
    const bits = buttons();
    showPads(bits);
    game.Frame(bits);
    const bytes = new Uint8Array(game.Audio());
    if (feed && bytes.length) feed.port.postMessage(new Float32Array(bytes.buffer, 0, bytes.length / 4));
    if (++frames % 600 === 0) saveCmos();                    // every 10 s, as the settings change
}

function draw() {
    if (display.hd) {
        const low = new Uint8Array(game.PrepareHd(true));
        renderer.hd(low, game.HdState(), display);
    } else {
        renderer.classicFrame(new Uint8Array(game.Classic()));
    }
}

let last = 0, owed = 0;
function tick(now) {
    // The board's own rate: catch up after a slow frame, but after a long stall (a hidden tab)
    // carry on from here rather than rush through the lost time.
    let elapsed = now - last;
    last = now;
    if (elapsed > 100) elapsed = FRAME_MS;
    owed += elapsed;
    for (let n = 0; owed >= FRAME_MS && n < 4; n++) { step(); owed -= FRAME_MS; }
    if (owed > FRAME_MS) owed = 0;
    const halted = game.Halted();
    if (halted) {                                            // a copy-protection check found the program changed
        const note = document.createElement('div');
        note.id = 'cover';
        note.textContent = 'JoustX stopped: ' + halted;
        document.body.append(note);
        return;
    }
    draw();
    requestAnimationFrame(tick);
}

addEventListener('pagehide', saveCmos);

/** For checking from the browser's console: times <n> game frames (and HD frames drawn) in ms each. */
globalThis.joustx = {
    benchmark(n = 600) {
        let t = performance.now();
        for (let i = 0; i < n; i++) { game.Frame(0); game.Audio(); }
        const frameMs = (performance.now() - t) / n;
        t = performance.now();
        for (let i = 0; i < n / 10; i++) draw();
        renderer.gl.finish();
        return { frameMs, drawMs: (performance.now() - t) / (n / 10) };
    },
    /** Plays <n> frames now, with the real input handling, and draws the last (when the page is hidden). */
    run(n) { for (let i = 0; i < n; i++) step(); draw(); },
};
say('Click, press a key or press a gamepad button to play');
let begun = false;
const begin = async () => {
    if (begun) return;
    begun = true;
    removeEventListener('pointerdown', begin);
    removeEventListener('keydown', begin);
    document.getElementById('cover').remove();
    try { await startSound(); } catch (e) { console.warn('no sound', e); }
    last = performance.now();
    requestAnimationFrame(tick);
};
addEventListener('pointerdown', begin);
addEventListener('keydown', begin);
// A gamepad shows up to the page only once one of its buttons is pressed: watch for that too,
// and say on the start screen which pads the browser has shown (to check one is seen).
(function watchPads() {
    if (begun) return;
    const pads = [...(navigator.getGamepads?.() ?? [])].filter((p) => p);
    document.getElementById('pads').textContent = pads.length
        ? 'Gamepad: ' + pads.map((p) => p.id + (p.mapping === 'standard' ? '' : ' (unusual layout)')).join(', ')
        : 'No gamepad seen yet: press a button on it';
    for (const pad of pads)
        if (pad.buttons.some((b) => b.pressed)) { begin(); return; }
    requestAnimationFrame(watchPads);
})();
