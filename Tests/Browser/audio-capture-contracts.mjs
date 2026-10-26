import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs/promises';
import { AudioCaptureDelivery } from '../../Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/audioCaptureDelivery.js';

const sent = [], errors = [];
let acknowledge;
const delivery = new AudioCaptureDelivery(async audio => {
    sent.push(audio);
    if (audio === 'tail') await new Promise(resolve => { acknowledge = resolve; });
}, error => errors.push(error));
delivery.enqueue('first');
delivery.enqueue('tail');
let drained = false;
const stop = delivery.drain().then(result => { drained = true; return result; });
await new Promise(resolve => setTimeout(resolve, 5));
assert.deepEqual(sent, ['first', 'tail']);
assert.equal(drained, false, 'stop waits for the final .NET callback');
acknowledge();
assert.equal(await stop, true);
delivery.enqueue('late');
assert.deepEqual(sent, ['first', 'tail'], 'closed delivery cannot leak into another session');

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

// Execute the real worklet, including stop without another rendering quantum.
let Recorder;
const workletEvents = [];
const context = vm.createContext({
    sampleRate: 48000, console: { log() {}, warn() {} },
    AudioWorkletProcessor: class { constructor() { this.port = { postMessage: event => workletEvents.push(event) }; } },
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
