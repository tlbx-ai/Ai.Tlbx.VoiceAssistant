import assert from 'node:assert/strict';
import { createOpenAiDirectTranslationClient } from '../../Provider/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/wwwroot/voice-assistant-direct-live.js';
import { OpenAiDirectRealtimeClient } from '../../Provider/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/wwwroot/voice-assistant-direct-realtime.js';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const tracks = [], peers = [], delivered = [];
let omitFinal = false;
globalThis.document = { body: { appendChild() {} }, createElement: () => ({ setAttribute() {}, pause() {}, remove() {} }) };
Object.defineProperty(globalThis, 'navigator', { value: { mediaDevices: { getUserMedia: async () => {
    const track = { enabled: true, readyState: 'live', stop() { this.readyState = 'ended'; } }; tracks.push(track);
    return { getTracks: () => [track], getAudioTracks: () => [track] };
} } }, configurable: true });
globalThis.fetch = async () => ({ ok: true, json: async () => ({ session: { id: 'translation' }, transport: { sdp: 'answer' } }) });
class Events extends EventTarget {
    readyState = 'open';
    emit(event) { const message = new MessageEvent('message', { data: JSON.stringify(event) }); this.onmessage?.(message); this.dispatchEvent(message); }
    send(json) {
        assert.equal(JSON.parse(json).type, 'session.close');
        setTimeout(() => {
            this.emit({ type: 'session.output_transcript.delta', delta: 'final tail' });
            if (!omitFinal) this.emit({ type: 'session.closed' });
        }, 5);
    }
    close() { this.readyState = 'closed'; }
}
globalThis.RTCPeerConnection = class extends EventTarget {
    iceGatheringState = 'complete'; connectionState = 'connected';
    constructor() { super(); peers.push(this); }
    addTrack() {} createDataChannel() { return this.channel = new Events(); }
    async createOffer() { return { sdp: 'offer' }; } async setLocalDescription(offer) { this.localDescription = offer; }
    async setRemoteDescription() { setTimeout(() => this.channel.emit({ type: 'session.created' }), 0); }
    close() { this.connectionState = 'closed'; }
};
const dotNet = { async invokeMethodAsync(method, json) { if (method === 'OnTranslationEvent') { await delay(10); delivered.push(JSON.parse(json)); } } };
const client = createOpenAiDirectTranslationClient({ closeTimeoutMs: 300 }, dotNet);
await client.start(); await client.stopTranslation();
assert.deepEqual(delivered.map(e => e.type), ['session.created', 'session.output_transcript.delta', 'session.closed'], 'await callbacks in wire order before disposing');
assert.ok(peers.every(p => p.connectionState === 'closed') && tracks.every(t => t.readyState === 'ended'));
omitFinal = true;
const broken = createOpenAiDirectTranslationClient({ closeTimeoutMs: 50 }, dotNet);
await broken.start(); await assert.rejects(broken.stopTranslation(), /timed out/);
assert.ok(peers.every(p => p.connectionState === 'closed') && tracks.every(t => t.readyState === 'ended'), 'timeout releases media');
globalThis.window = globalThis;
const realtime = new OpenAiDirectRealtimeClient({}); const commands = [];
realtime.dataChannel = { readyState: 'open' }; realtime.sendRealtimeEvent = event => commands.push(event);
realtime.activeResponse = true; realtime.pendingTools = 1;
realtime.sendImage('https://example.com/image.png', 'Describe');
assert.equal(commands.length, 1, 'image can arrive while tool owns response');
realtime.activeResponse = false; realtime.flushRequestedResponse(); assert.equal(commands.length, 1, 'image cannot prematurely continue missing tool');
realtime.pendingTools = 0; realtime.flushRequestedResponse(); realtime.flushRequestedResponse();
assert.deepEqual(commands.map(e => e.type), ['conversation.item.create', 'response.create']);
console.log('Browser translation final callbacks, timeout cleanup and image/tool response coalescing passed.');
