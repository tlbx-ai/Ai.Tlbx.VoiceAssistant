import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/audio-processor.js', import.meta.url), 'utf8');
function create(rate = 48000) {
    let Playback;
    vm.runInNewContext(source, {
        sampleRate: rate, Float32Array, ArrayBuffer,
        console: { log() {}, warn() {} },
        AudioWorkletProcessor: class { constructor() { this.port = {}; } },
        registerProcessor(name, ctor) { if (name === 'playback-processor') Playback = ctor; }
    });
    return new Playback();
}
function send(p, samples) { p.port.onmessage({ data: { audioData: samples } }); }
function render(p, count) {
    const result = new Float32Array(count);
    for (let offset = 0; offset < count; offset += 128) {
        const block = result.subarray(offset, Math.min(offset + 128, count));
        assert.equal(p.process([], [[block]], {}), true);
    }
    assert.ok(result.every(Number.isFinite), 'rendered PCM is finite');
    return result;
}
const constant = (length, value) => new Float32Array(length).fill(value);
const near = (a, b, message) => assert.ok(Math.abs(a - b) < 1e-6, `${message}: ${a} vs ${b}`);

// Packet boundaries, including an exactly empty queue between render quanta,
// must not change a single output sample or the duration of contiguous speech.
const signal = Float32Array.from({ length: 4800 }, (_, i) => 0.4 * Math.cos(2 * Math.PI * 1000 * i / 24000));
const whole = create(); send(whole, signal);
const expected = render(whole, signal.length * 2);
for (const packetSize of [64, 480, 1, 137]) {
    const p = create(), actual = [];
    for (let offset = 0; offset < signal.length; offset += packetSize) {
        const chunk = signal.subarray(offset, offset + packetSize);
        send(p, chunk);
        actual.push(...render(p, chunk.length * 2));
    }
    assert.deepEqual(new Float32Array(actual), expected, `sample-exact contiguous playback with ${packetSize}-sample packets`);
}

for (const rate of [44100, 48000]) {
    const fadeSamples = Math.round(rate * 0.005);
    for (const gapLength of [1, 64, fadeSamples, fadeSamples + 128]) {
        const p = create(rate);
        send(p, constant(480, 0.4));
        near(render(p, 960).at(-1), 0.4, 'PCM ends at original level');
        const tail = render(p, gapLength);
        for (let i = 0; i < tail.length; i++) {
            near(tail[i], 0.4 * Math.max(0, 1 - (i + 1) / fadeSamples), 'tail decays to zero within 5 ms');
        }
        const last = tail.at(-1);
        send(p, constant(480, -0.4));
        const resumed = render(p, 960);
        near(resumed[0], last * (1 - 1 / fadeSamples), 'first resumed sample blends the interpolated zero crossing from current level');
        assert.ok(resumed[1] < resumed[0], 'new PCM starts in the next quantum without waiting');
        assert.ok(Math.abs(resumed[0] - last) <= 0.8 / fadeSamples + 1e-6, 'bounded first step during early/late resume');
        near(resumed[fadeSamples - 1], -0.4, 'resume reaches unmodified PCM after 5 ms');
        near(resumed.at(-1), -0.4, 'no PCM duration lost during fade');
        const silence = render(p, fadeSamples + 256);
        assert.ok(silence.slice(fadeSamples - 1).every(x => x === 0), 'tail ends in silence without replaying speech');
    }
}

// Underrun starts part-way through a normal 128-sample quantum.
const partial = create(); send(partial, constant(480, 0.4));
render(partial, 896);
const boundary = render(partial, 128);
near(boundary[63], 0.4, 'last available PCM');
near(boundary[64], 0.4 * 239 / 240, 'first missing sample fades rather than jumps');
near(boundary[127], 0.4 * 176 / 240, 'fade survives the remainder of the render quantum');

// Empty packets must preserve interpolation and active transitions.
for (const gap of [0, 64, 300]) {
    const a = create(), b = create();
    for (const p of [a, b]) { send(p, constant(480, 0.4)); render(p, 960); render(p, gap); }
    send(a, new Float32Array(0)); send(a, new ArrayBuffer(0));
    send(a, constant(480, -0.4)); send(b, constant(480, -0.4));
    assert.deepEqual(render(a, 960), render(b, 960), 'empty packets leave resampler and fades intact');
}
for (const phase of ['queued', 'fadeout', 'fadein']) {
    const p = create(); send(p, constant(480, 0.4));
    if (phase !== 'queued') { render(p, 960); render(p, 64); }
    if (phase === 'fadein') { send(p, constant(480, -0.4)); render(p, 64); }
    p.port.onmessage({ data: { command: 'clear' } });
    assert.ok(render(p, 512).every(x => x === 0), 'clear removes queued audio and transition tails');
    send(p, constant(480, -0.2));
    const fresh = create(); send(fresh, constant(480, -0.2));
    assert.deepEqual(render(p, 960), render(fresh, 960), `clear resets interpolation and transitions during ${phase}`);
}

// Neutral default and the explicit Clarity option use the real module functions.
const eqSource = (await fs.readFile(new URL('../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/webAudioAccess.js', import.meta.url), 'utf8'))
    .replace(/^import .*;\r?\n/, '').replace(/export \{[^}]*\};\s*$/, '');
const eq = vm.createContext({ window: { addEventListener() {} }, console: { log() {}, warn() {} } });
vm.runInContext(eqSource, eq);
assert.equal(vm.runInContext('currentEqPreset', eq), 'flat');
vm.runInContext("setPlaybackEqPreset('clarity')", eq);
assert.equal(vm.runInContext('currentEqPreset', eq), 'clarity');
vm.runInContext("setPlaybackEqPreset('flat')", eq);
assert.equal(vm.runInContext('currentEqPreset', eq), 'flat');
console.log('Browser playback: sample-exact chunk continuity, 5 ms underrun/resume, immediate audio, clear/empty packets and Flat/Clarity passed.');
