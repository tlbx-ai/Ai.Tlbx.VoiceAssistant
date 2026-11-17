import assert from 'node:assert/strict';
import { normalizeWebRtcAudioOptions as normalize, webRtcMicrophoneConstraints, webRtcPeerConfiguration, createWebRtcAudioController } from '../../Provider/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/wwwroot/webrtc-audio.js';
import { createOpenAiDirectLiveClient } from '../../Provider/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/wwwroot/voice-assistant-direct-live.js';
import { OpenAiDirectRealtimeClient, createOpenAiDirectRealtimeClient } from '../../Provider/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/wwwroot/voice-assistant-direct-realtime.js';

const flush = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };
const defaults = normalize();
assert.deepEqual(webRtcMicrophoneConstraints(defaults), { echoCancellation: true, noiseSuppression: true, autoGainControl: true });
assert.equal(webRtcPeerConfiguration(defaults), undefined);
for (const bad of [-1, 4001, NaN, Infinity, '100'])
    assert.throws(() => normalize({ jitterBufferTargetMilliseconds: bad }), RangeError);
for (const bad of [0, 499, 60001, Infinity])
    assert.throws(() => normalize({ statisticsIntervalMs: bad }), RangeError);
assert.throws(() => normalize({ microphone: { echoCancellation: 'false' } }), TypeError);
assert.throws(() => normalize({ peerConnection: { iceTransportPolicy: 'bogus' } }), RangeError);
assert.throws(() => normalize({ peerConnection: { iceCandidatePoolSize: 256 } }), RangeError);
for (const target of [0, 125, 4000]) assert.equal(normalize({ jitterBufferTargetMilliseconds: target }).jitterBufferTargetMilliseconds, target);
assert.deepEqual(webRtcPeerConfiguration(normalize({ peerConnection: { iceCandidatePoolSize: 2 } }), { bundlePolicy: 'balanced' }),
    { bundlePolicy: 'balanced', iceCandidatePoolSize: 2 });

// Deterministic timers exercise congestion/cleanup without sleeping or real networks.
const originalSetTimeout = globalThis.setTimeout, originalClearTimeout = globalThis.clearTimeout;
let clock = 0, nextTimer = 0;
const timers = new Map();
globalThis.setTimeout = (callback, delay) => { const id = ++nextTimer; timers.set(id, { at: clock + delay, callback }); return id; };
globalThis.clearTimeout = id => timers.delete(id);
async function advance(ms) {
    const end = clock + ms;
    while (true) {
        const due = [...timers].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        clock = due[1].at; timers.delete(due[0]); due[1].callback(); await flush();
    }
    clock = end; await flush();
}
const snapshots = [];
const peer = { connectionState: 'connected', iceConnectionState: 'completed' };
const silent = createWebRtcAudioController(peer, defaults, s => snapshots.push(s));
await advance(60000);
assert.equal(snapshots.length, 0, 'defaults do not emit new diagnostics');
assert.equal(timers.size, 0, 'periodic statistics are opt-in');
silent.stop();

let assignments = 0;
const untouched = { track: { kind: 'audio' }, get jitterBufferTarget() { return null; }, set jitterBufferTarget(_) { assignments++; } };
const nativeDefault = createWebRtcAudioController(peer, normalize({ diagnosticsEnabled: true }), s => snapshots.push(s));
nativeDefault.remoteTrack({ receiver: untouched, track: untouched.track }); await flush();
assert.equal(assignments, 0, 'null leaves receiver default untouched');
assert.ok(snapshots.some(s => s.jitterBufferTargetStatus === 'NotRequested'));
nativeDefault.stop();

snapshots.length = 0;
const capability = createWebRtcAudioController(peer, normalize({ diagnosticSessionId: 'capability', jitterBufferTargetMilliseconds: 125 }), s => snapshots.push(s));
const supported = { track: { kind: 'audio' }, jitterBufferTarget: null };
const unsupported = { track: { kind: 'audio' } };
const rejected = { track: { kind: 'audio' }, set jitterBufferTarget(_) { throw new Error('private-ip-secret'); } };
capability.remoteTrack({ receiver: supported, track: supported.track });
capability.remoteTrack({ receiver: supported, track: supported.track });
capability.remoteTrack({ receiver: unsupported, track: unsupported.track });
capability.remoteTrack({ receiver: rejected, track: rejected.track });
await flush();
assert.equal(supported.jitterBufferTarget, 125);
assert.deepEqual(snapshots.filter(s => s.eventType === 'ReceiverConfiguration').map(s => s.jitterBufferTargetStatus),
    ['Applied', 'Unsupported', 'Rejected'], 'capabilities survive event coalescing; receivers configured once');
assert.ok(!JSON.stringify(snapshots).includes('private-ip-secret'));
capability.stop();

snapshots.length = 0;
const absent = createWebRtcAudioController(peer, normalize({ statisticsIntervalMs: 500 }), s => snapshots.push(s));
await advance(500);
assert.equal(snapshots.filter(s => s.errorCode === 'get-stats-unsupported').length, 1);
assert.equal(timers.size, 0, 'missing getStats stops sampling without a session error');
absent.stop();

const report = new Map([
    ['audio', { type: 'inbound-rtp', kind: 'audio', timestamp: 10, packetsReceived: 50, packetsLost: -1, packetsDiscarded: 2,
        jitter: .02, concealedSamples: 960, concealmentEvents: 2, jitterBufferDelay: 120, jitterBufferTargetDelay: 150,
        jitterBufferEmittedCount: 48000, insertedSamplesForDeceleration: 7, removedSamplesForAcceleration: 8,
        address: 'private-address', sdp: 'private-sdp', token: 'private-token' }],
    ['video', { type: 'inbound-rtp', kind: 'video', packetsLost: 999 }],
    ['playout', { type: 'media-playout', kind: 'audio', timestamp: 11, synthesizedSamplesDuration: .01,
        synthesizedSamplesEvents: 1, totalSamplesDuration: 1, totalPlayoutDelay: 480, totalSamplesCount: 48000 }],
    ['transport', { type: 'transport', selectedCandidatePairId: 'pair' }],
    ['pair', { type: 'candidate-pair', localCandidateId: 'local', remoteCandidateId: 'remote', currentRoundTripTime: .03 }],
    ['local', { protocol: 'udp', candidateType: 'relay', address: 'private-address', port: 1234, url: 'private-url' }],
    ['remote', { candidateType: 'host', address: 'private-address' }]
]);
let statsCalls = 0;
const measured = { ...peer, getStats: async () => { statsCalls++; return report; } };
snapshots.length = 0;
const collecting = createWebRtcAudioController(measured, normalize({ statisticsIntervalMs: 1000, diagnosticSessionId: 'stats' }), s => snapshots.push(s));
await advance(999); assert.equal(statsCalls, 0);
await advance(1);
const stat = snapshots.find(s => s.eventType === 'Statistics');
assert.equal(stat.inboundAudio.length, 1);
assert.equal(stat.inboundAudio[0].packetsLost, -1);
assert.equal(stat.inboundAudio[0].silentConcealedSamples, null, 'unsupported fields remain null');
assert.equal(stat.audioPlayout[0].synthesizedSamplesEvents, 1);
assert.deepEqual(stat.selectedTransport, { protocol: 'udp', localCandidateType: 'relay', remoteCandidateType: 'host', currentRoundTripTimeSeconds: .03 });
for (const secret of ['private-address', 'private-sdp', 'private-token', 'private-url', 'localCandidateId', 'selectedCandidatePairId'])
    assert.ok(!JSON.stringify(snapshots).includes(secret), 'privacy allowlist excludes ' + secret);
collecting.stop(); await advance(10000); assert.equal(statsCalls, 1); assert.equal(timers.size, 0);

// Pending getStats never overlaps and its late completion cannot escape stop().
let resolveStats;
statsCalls = 0; snapshots.length = 0;
const pendingPeer = { ...peer, getStats() { statsCalls++; return new Promise(r => { resolveStats = r; }); } };
const stalled = createWebRtcAudioController(pendingPeer, normalize({ statisticsIntervalMs: 500 }), s => snapshots.push(s));
await advance(5000); assert.equal(statsCalls, 1);
stalled.stop(); resolveStats(report); await flush();
assert.equal(snapshots.filter(s => s.eventType === 'Statistics').length, 0);
assert.equal(timers.size, 0);

// Rejected callbacks and getStats errors remain diagnostic failures only.
const failures = [];
const broken = createWebRtcAudioController({ ...peer, getStats: async () => { throw new Error('private-address'); } },
    normalize({ statisticsIntervalMs: 500 }), s => { failures.push(s); throw new Error('consumer failure'); });
await advance(1000);
assert.equal(failures.filter(s => s.errorCode === 'get-stats-failed').length, 2);
broken.stop(); assert.equal(timers.size, 0);
let unblock;
let callbacks = 0;
const backpressure = createWebRtcAudioController(measured, normalize({ statisticsIntervalMs: 500 }), () => {
    callbacks++; if (callbacks === 1) return new Promise(r => { unblock = r; });
});
await advance(10000); assert.equal(callbacks, 1);
for (let i = 0; i < 100; i++) backpressure.connectionStateChanged();
unblock(); await flush();
assert.ok(callbacks <= 17, 'one in flight plus bounded pending diagnostics');
backpressure.stop(); assert.equal(timers.size, 0);
// The actual .NET bridge must return its promise so the shared bound also covers SignalR.
let bridgeRelease, bridgeCalls = 0;
const bridgeClient = createOpenAiDirectRealtimeClient({}, { invokeMethodAsync() {
    bridgeCalls++; if (bridgeCalls === 1) return new Promise(r => { bridgeRelease = r; });
    return Promise.resolve();
} });
const bridged = createWebRtcAudioController(measured, normalize({ statisticsIntervalMs: 500 }), bridgeClient.options.onWebRtcDiagnostics);
await advance(5000); assert.equal(bridgeCalls, 1, 'no unbounded .NET invocations while a callback is pending');
bridgeRelease(); await flush(); assert.ok(bridgeCalls <= 2, 'pending statistics coalesced across the .NET bridge');
bridged.stop(); assert.equal(timers.size, 0);
globalThis.setTimeout = originalSetTimeout; globalThis.clearTimeout = originalClearTimeout;

// Exercise both actual clients' construction and teardown with browser fakes.
const peers = [], capture = [], diagnostics = [];
globalThis.window = globalThis;
globalThis.document = { body: { appendChild() {} }, createElement: () => ({ style: {}, setAttribute() {}, play: async () => {}, pause() {}, remove() {} }) };
Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaDevices: { getUserMedia: async constraints => {
    capture.push(constraints); const track = { kind: 'audio', enabled: true, stop() {}, getSettings: () => ({}) };
    return { getTracks: () => [track], getAudioTracks: () => [track] };
} } } });
class Peer {
    iceGatheringState = 'complete'; connectionState = 'connected'; iceConnectionState = 'completed';
    constructor(configuration) { this.configuration = configuration; peers.push(this); this.receiver = { track: { kind: 'audio' }, jitterBufferTarget: null }; }
    addTrack() {} getReceivers() { return [this.receiver]; }
    createDataChannel() { return this.channel = { readyState: 'open', close() {} }; }
    async createOffer() { return { sdp: 'offer' }; }
    async setLocalDescription(value) { this.localDescription = value; }
    async setRemoteDescription() {
        this.ontrack?.({ receiver: this.receiver, track: this.receiver.track, streams: [{}] });
        this.onconnectionstatechange?.();
        this.channel.onopen?.();
        this.channel.onmessage?.({ data: JSON.stringify({ type: 'session.started' }) });
    }
    close() { this.connectionState = 'closed'; }
}
globalThis.RTCPeerConnection = Peer;
globalThis.fetch = async () => ({ ok: true, json: async () => ({ session: { id: 'live' }, transport: { sdp: 'answer' } }), text: async () => 'answer' });
const audio = { diagnosticSessionId: 'client', diagnosticsEnabled: true, jitterBufferTargetMilliseconds: 125,
    microphone: { echoCancellation: false, noiseSuppression: false, autoGainControl: false }, peerConnection: { iceCandidatePoolSize: 2 } };
const live = createOpenAiDirectLiveClient({ browserAudio: audio }, { invokeMethodAsync: async (method, json) => {
    if (method === 'OnWebRtcDiagnosticsReceived') diagnostics.push(JSON.parse(json));
} });
await live.start(); await flush();
assert.equal(peers.at(-1).receiver.jitterBufferTarget, 125);
assert.equal(peers.at(-1).configuration.iceCandidatePoolSize, 2);
assert.equal(capture.at(-1).audio.echoCancellation, false);
live.dispose();
const realtime = new OpenAiDirectRealtimeClient({ browserAudio: audio, onWebRtcDiagnostics: s => diagnostics.push(s) });
realtime.startAudioLevelMonitor = () => {};
realtime.sendControl = () => {};
realtime.session = { clientSecret: 'test-only', model: 'test' };
await realtime.openRealtimePeerConnection({ microphoneId: 'test-device' });
await flush();
assert.equal(peers.at(-1).receiver.jitterBufferTarget, 125);
assert.equal(capture.at(-1).audio.autoGainControl, false);
assert.equal(capture.at(-1).audio.deviceId.exact, 'test-device');
await realtime.stop();
assert.ok(diagnostics.filter(s => s.eventType === 'RemoteAudioTrackReceived').length >= 2, 'both providers report track arrival');
assert.ok(peers.every(p => p.connectionState === 'closed'));
console.log('WebRTC audio: both clients, defaults, validation, hints/capabilities, native filters, private-field exclusion, sampling/backpressure and stop isolation passed.');
