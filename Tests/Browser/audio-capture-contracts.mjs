import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs/promises';
import { AudioCaptureDelivery } from '../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/audioCaptureDelivery.js';

const sent = [], errors = [];
const acknowledgments = [];
const firstPacket = Buffer.from([1, 0]).toString('base64');
const tailPacket = Buffer.from([2, 0, 3, 0]).toString('base64');
const delivery = new AudioCaptureDelivery(async audio => {
    sent.push(audio);
    await new Promise(resolve => { acknowledgments.push(resolve); });
}, error => errors.push(error));
delivery.enqueue(firstPacket);
await new Promise(resolve => setTimeout(resolve, 0));
assert.deepEqual(sent, [firstPacket], 'the first packet is sent without waiting for a batch');
delivery.enqueue(tailPacket);
delivery.enqueue(firstPacket);
let drained = false;
const stop = delivery.drain().then(result => { drained = true; return result; });
await new Promise(resolve => setTimeout(resolve, 5));
assert.equal(sent.length, 1, 'next packets wait for the outstanding acknowledgment');
assert.equal(drained, false, 'stop waits for the final .NET callback');
acknowledgments.shift()();
await new Promise(resolve => setTimeout(resolve, 0));
assert.deepEqual(Buffer.from(sent[1], 'base64'), Buffer.from([2, 0, 3, 0, 1, 0]), 'join PCM bytes across base64 padding boundaries');
assert.equal(drained, false, 'drain also waits for the catch-up batch');
acknowledgments.shift()();
assert.equal(await stop, true);
delivery.enqueue('late');
assert.equal(sent.length, 2, 'closed delivery cannot leak into another session');

const broken = new AudioCaptureDelivery(() => Promise.reject(new Error('disconnected')), error => errors.push(error));
broken.enqueue('lost');
assert.equal(await broken.drain(), false);
const stalled = new AudioCaptureDelivery(() => new Promise(() => {}), error => errors.push(error), { maxPending: 2, timeoutMs: 10 });
stalled.enqueue('blocked');
assert.equal(await stalled.drain(), false, 'missing acknowledgment is bounded and reported');
const overflow = new AudioCaptureDelivery(() => Promise.resolve(), error => errors.push(error), { maxPending: 2 });
overflow.enqueue('a'); overflow.enqueue('b'); overflow.enqueue('c');
assert.equal(await overflow.drain(), false, 'delivery queue is bounded');
assert.equal(errors.length, 3);

// Simulate ten minutes of real 20 ms PCM frames and interop round trips without
// spending ten minutes on wall-clock sleeps. Check every sample, not just calls.
const deliverySource = (await fs.readFile(new URL('../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/audioCaptureDelivery.js', import.meta.url), 'utf8'))
    .replace('export class AudioCaptureDelivery', 'globalThis.AudioCaptureDelivery = class AudioCaptureDelivery');
async function verifySustainedCapture(roundTrip, label) {
    let now = 0, nextTimer = 0, inFlight = 0, peakPending = 0, maxDelay = 0, deliveredSamples = 0;
    const timers = new Map(), failures = [];
    const deliveryContext = vm.createContext({
        atob, btoa,
        setTimeout(callback, delay) { const id = ++nextTimer; timers.set(id, { at: now + delay, callback }); return id; },
        clearTimeout(id) { timers.delete(id); }
    });
    vm.runInContext(deliverySource, deliveryContext);
    const stream = new deliveryContext.AudioCaptureDelivery(audio => {
        assert.equal(++inFlight, 1, 'callbacks remain ordered with only one in flight');
        const bytes = Buffer.from(audio, 'base64');
        assert.ok(bytes.length <= 480 * 2 * 10, 'a catch-up packet holds at most 200 ms');
        for (let offset = 0; offset < bytes.length; offset += 2) {
            if (bytes.readUInt16LE(offset) !== deliveredSamples % 65536) assert.fail(`${label}: PCM sample ${deliveredSamples} lost, duplicated or reordered`);
            deliveredSamples++;
        }
        const lastFrame = Math.floor(deliveredSamples / 480) - 1;
        maxDelay = Math.max(maxDelay, now - lastFrame * 20);
        return new Promise(resolve => deliveryContext.setTimeout(() => { inFlight--; resolve(); }, roundTrip(now)));
    }, error => failures.push(error));
    async function flushMicrotasks() { for (let i = 0; i < 20; i++) await Promise.resolve(); }
    async function advance(to) {
        while (true) {
            await flushMicrotasks();
            const next = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0];
            if (!next || next[1].at > to) break;
            now = next[1].at;
            timers.delete(next[0]);
            next[1].callback();
        }
        now = to;
    }
    for (let frame = 0; frame < 30000; frame++) {
        await advance(frame * 20);
        const pcm = Buffer.alloc(960);
        for (let sample = 0; sample < 480; sample++) {
            const value = (frame * 480 + sample) % 65536;
            pcm.writeUInt16LE(value, sample * 2);
        }
        stream.enqueue(pcm.toString('base64'));
        peakPending = Math.max(peakPending, stream.pending);
        assert.deepEqual(failures, [], `${label}: capture must not stop at ${now / 1000} s as acknowledgment latency accumulates`);
    }
    let drainResult;
    const drain = stream.drain().then(result => { drainResult = result; });
    await advance(now + 10000);
    await drain;
    assert.equal(drainResult, true);
    assert.equal(deliveredSamples, 30000 * 480, `${label}: complete PCM arrives once, in order`);
    assert.equal(timers.size, 0, 'no pending timeout after drain');
    assert.ok(peakPending < 40, `${label}: pending audio stays bounded over ten minutes`);
    assert.ok(maxDelay < 750, `${label}: delay does not grow with session length`);
    console.log(`Capture soak ${label}: 600 s PCM, peak ${peakPending} frames, max newest-frame delay ${maxDelay} ms.`);
}
await verifySustainedCapture(() => 20.2, '20.2 ms round trip');
await verifySustainedCapture(now => now % 10000 < 100 ? 500 : 75, '75 ms round trip with 500 ms stalls');

// Execute the real worklet, including stop without another rendering quantum.
let Recorder;
const workletEvents = [];
let onWorkletEvent = event => workletEvents.push(event);
const context = vm.createContext({
    sampleRate: 48000, console: { log() {}, warn() {} },
    AudioWorkletProcessor: class { constructor() { this.port = { postMessage: event => onWorkletEvent(event) }; } },
    registerProcessor(name, constructor) { if (name === 'audio-recorder-processor') Recorder = constructor; }
});
await vm.runInContext(await fs.readFile(new URL('../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/audio-processor.js', import.meta.url), 'utf8'), context);
const recorder = new Recorder({ processorOptions: { targetSampleRate: 24000 } });
recorder.process([[new Float32Array(128).fill(0.3)]], []);
recorder.port.onmessage({ data: { command: 'stop' } });
assert.equal(workletEvents.at(-1).stopped, true);
assert.equal(workletEvents[0].audioData.length, 64, 'last partial PCM packet is flushed before stop acknowledgment');
assert.equal(recorder.process([[new Float32Array(128)]], []), false);
assert.equal(workletEvents.length, 2, 'no post-stop audio');

for (const [sourceRate, targetRate] of [[48000, 24000], [44100, 24000], [48000, 16000]]) {
    context.sampleRate = sourceRate;
    const continuous = new Recorder({ processorOptions: { targetSampleRate: targetRate } });
    let emittedSamples = 0;
    onWorkletEvent = event => { emittedSamples += event.audioData?.length ?? 0; };
    const input = new Float32Array(128).fill(0.3);
    const totalSamples = sourceRate * 600;
    for (let offset = 0; offset < totalSamples; offset += input.length) {
        continuous.process([[input.subarray(0, Math.min(input.length, totalSamples - offset))]], []);
    }
    continuous.port.onmessage({ data: { command: 'stop' } });
    assert.ok(Math.abs(emittedSamples - targetRate * 600) <= 1, `${sourceRate} -> ${targetRate}: ten minutes without sample-clock drift`);
    assert.ok(continuous.resampleBuffer.length < 8, 'resampler retains only boundary samples, not session history');
}

// Exercise the actual stopRecording implementation with a delayed .NET tail acknowledgment.
const source = (await fs.readFile(new URL('../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/webAudioAccess.js', import.meta.url), 'utf8'))
    .replace(/^import .*;\r?\n/, '').replace(/export \{[^}]*\};\s*$/, '');
const stopContext = vm.createContext({
    AudioCaptureDelivery, window: { addEventListener() {} }, setTimeout, clearTimeout, clearInterval,
    console: { log() {}, warn() {} }, tailAck: null, trackStopped: false
});
vm.runInContext(source + `
    isRecording = true;
    mediaStream = { getTracks: () => [{ stop: () => { globalThis.trackStopped = true; } }] };
    dotNetReference = { invokeMethodAsync: async () => {} };
    captureDelivery = new AudioCaptureDelivery(() => new Promise(resolve => { globalThis.tailAck = resolve; }), () => {});
    audioWorkletNode = {
        disconnect() {},
        port: {
            onmessage: event => captureDelivery.enqueue(event.data.audioData),
            postMessage() {
                this.onmessage({ data: { audioData: 'last-packet' } });
                this.onmessage({ data: { stopped: true } });
            }
        }
    };
    globalThis.stopResult = stopRecording();
`, stopContext);
assert.equal(stopContext.trackStopped, true, 'physical microphone is released before delivery waits');
let actualStopped = false;
stopContext.stopResult.then(() => { actualStopped = true; });
await new Promise(resolve => setTimeout(resolve, 5));
assert.equal(actualStopped, false);
stopContext.tailAck();
assert.equal(await stopContext.stopResult, true, 'worklet and .NET acknowledgments both required');
console.log('Browser audio capture: ordered delivery, final-block acknowledgment, bounded failure/queue and worklet flush passed.');
