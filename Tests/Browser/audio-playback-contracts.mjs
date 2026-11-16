import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/audio-processor.js', import.meta.url), 'utf8');
function create(rate = 48000, buffered = false) {
    let Playback;
    vm.runInNewContext(source, {
        sampleRate: rate, Float32Array, ArrayBuffer,
        console: { log() {}, warn() {} },
        AudioWorkletProcessor: class { constructor() { this.messages = []; this.port = { postMessage: message => this.messages.push(message) }; } },
        registerProcessor(name, ctor) { if (name === 'playback-processor') Playback = ctor; }
    });
    const p = new Playback();
    // Legacy signal/fade assertions isolate resampling; buffering is tested below.
    if (!buffered) { p._prebufferSamples = 0; p._prebufferRemaining = 0; }
    return p;
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
console.log('Browser playback: sample-exact chunk continuity, 5 ms underrun/resume, unbuffered signal checks, clear/empty packets and Flat/Clarity passed.');

// Real default: 60 ms target and a 60 ms maximum hold for short utterances.
const buffered = create(48000, true);
send(buffered, constant(480, .4));
assert.ok(render(buffered, 960).every(x => x === 0));
send(buffered, constant(480, .4));
assert.ok(render(buffered, 960).every(x => x === 0));
send(buffered, constant(480, .4));
assert.ok(render(buffered, 128).some(x => x !== 0), 'start as soon as 60 ms is queued');
const short = create(48000, true); send(short, constant(120, .4));
assert.ok(render(short, 2880).every(x => x === 0));
assert.ok(render(short, 128).some(x => x !== 0), 'short audio starts after at most 60 ms');

const fence = create(48000, true); send(fence, constant(24000, .4));
fence.port.onmessage({ data: { command: 'drain', requestId: 1 } });
render(fence, 4800);
assert.equal(fence.messages.length, 0, '100 ms is not a completion acknowledgement for 1 s of PCM');
render(fence, 43200);
assert.equal(fence.messages.length, 0, 'wait for the final transition tail');
render(fence, 256);
assert.deepEqual(fence.messages.map(x => [x.requestId, x.drained]), [[1, true]]);
const tiny = create(48000, true); send(tiny, constant(120, .4));
tiny.port.onmessage({ data: { command: 'drain', requestId: 2 } });
assert.ok(render(tiny, 128).some(x => x !== 0), 'drain flushes short buffered tails immediately');
tiny.port.onmessage({ data: { command: 'clear' } });
assert.deepEqual(tiny.messages.map(x => [x.requestId, x.drained]), [[2, false]], 'clear cancels pending fences');

// Same average input rate, alternating 30/10 ms arrivals: prebuffer absorbs jitter.
const jitter = create(48000, true);
let underruns = 0;
for (let packet = 0; packet < 100; packet++) {
    send(jitter, constant(480, .4));
    const frames = packet % 2 === 0 ? 1440 : 480;
    for (let i = 0; i < frames; i += 128) {
        const wasPlaying = !jitter._inUnderrun;
        render(jitter, Math.min(128, frames - i));
        if (wasPlaying && jitter._inUnderrun) underruns++;
    }
}
assert.equal(underruns, 0, 'bounded arrival jitter does not interrupt continuous speech');
console.log('Default 60 ms prebuffer, bounded short tails, jitter absorption and rendered drain fences passed.');

// Exercise the public JS delivery functions, including calls suspended in resume().
function deliveryContext() {
    const posted = [], node = { port: { postMessage(message) { posted.push(message); } } };
    const context = vm.createContext({
        window: { addEventListener() {} }, console: { log() {}, warn() {}, error() {} },
        setTimeout, clearTimeout, Float32Array, ArrayBuffer, Uint8Array, Int16Array,
        atob: value => Buffer.from(value, 'base64').toString('binary')
    });
    vm.runInContext(eqSource, context);
    context.testNode = node;
    vm.runInContext("audioContext = { state: 'running', sampleRate: 48000 }; playbackWorkletNode = testNode; playbackNodeConnected = true;", context);
    return { context, posted, node, run: code => vm.runInContext(code, context) };
}
const d = deliveryContext();
d.context.packet = Buffer.from(new Int16Array(480).fill(4000).buffer).toString('base64');
let resume;
d.context.pause = new Promise(resolve => { resume = resolve; });
d.run('ensureAudioContextResumed = async () => { await pause; return true; }');
const first = d.run('playAudio(packet, 24000, 1, 1)');
const second = d.run('playAudio(packet, 24000, 1, 2)');
await new Promise(resolve => setImmediate(resolve));
assert.equal(d.posted.filter(x => x.audioData).length, 0);
resume();
assert.deepEqual(await Promise.all([first, second]), [true, true]);
assert.equal(d.posted.filter(x => x.audioData).length, 2, 'overlapping callers are serialized');
assert.equal(await d.run('playAudio(packet, 24000, 1, 2)'), false, 'duplicate sequence rejected');
assert.equal(await d.run('waitForPlaybackDrain(1, 100)'), false, 'sequence failure cannot report success');
assert.equal(await d.run('playAudio(packet, 24000, 2, 1)'), true);
const beforeLateStop = d.posted.length;
await d.run('stopAudioPlayback(2)');
assert.equal(d.posted.length, beforeLateStop, 'late clear for already activated generation does not erase new audio');
assert.equal(await d.run('playAudio(packet, 24000, 1, 3)'), false, 'old generation rejected');
assert.equal(await d.run('playAudio(packet, 24000, 2, 3)'), false, 'missing sequence rejected rather than playing out of order');

let resumeOld;
d.context.pause = new Promise(resolve => { resumeOld = resolve; });
const old = d.run('playAudio(packet, 24000, 3, 1)');
await new Promise(resolve => setImmediate(resolve));
await d.run('stopAudioPlayback(4)');
const audioBefore = d.posted.filter(x => x.audioData).length;
resumeOld();
assert.equal(await old, false, 'clear invalidates a chunk awaiting AudioContext resume');
assert.equal(d.posted.filter(x => x.audioData).length, audioBefore);
assert.equal(await d.run('playAudio(packet, 24000, 4, 1)'), true);
let completed = false;
const drained = d.run('waitForPlaybackDrain(4, 1000)').then(value => { completed = true; return value; });
await new Promise(resolve => setImmediate(resolve));
assert.equal(completed, false);
const request = d.posted.at(-1);
assert.equal(request.command, 'drain');
d.node.port.onmessage({ data: { requestId: request.requestId, drained: true } });
assert.equal(await drained, true, 'only the worklet acknowledgement completes drain');
const cancelled = d.run('waitForPlaybackDrain(4, 1000)');
await new Promise(resolve => setImmediate(resolve));
await d.run('stopAudioPlayback(5)');
assert.equal(await cancelled, false, 'clear cancels drain');
assert.equal(await d.run('waitForPlaybackDrain(5, 10)'), false, 'unresponsive worklet times out');
console.log('JS delivery: serialized callers, stale/duplicate/missing sequences, late clear, resume race, drain acknowledgement and timeout passed.');
