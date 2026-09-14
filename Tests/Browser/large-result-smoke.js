// Inject __VOICE_DEMO_SMOKE_WAV_BASE64__. Optional __LARGE_RESULT_DOSSIER__ and __LARGE_RESULT_REALTIME__.
const delay = ms => new Promise(r => setTimeout(r, ms));
const events = [], peers = [], captures = [];
const button = text => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === text);
const until = async (test, label) => {
    const end = Date.now() + 90000;
    while (!test()) {
        if (Date.now() > end) throw new Error(label + ': ' + document.body.innerText.slice(-1800));
        await delay(100);
    }
};
const OriginalPeer = window.RTCPeerConnection;
window.RTCPeerConnection = class extends OriginalPeer {
    constructor(...args) { super(...args); peers.push(this); }
    createDataChannel(...args) {
        const dc = super.createDataChannel(...args);
        dc.addEventListener('message', e => { try { const v = JSON.parse(e.data); if (!v.type.includes('audio.delta')) events.push(v); } catch {} });
        return dc;
    }
};
const audio = new AudioContext(); await audio.resume();
const gain = audio.createGain();
const oscillator = audio.createOscillator(), mute = audio.createGain();
mute.gain.value = 0; oscillator.connect(mute); mute.connect(gain); oscillator.start();
navigator.mediaDevices.getUserMedia = async () => {
    const destination = audio.createMediaStreamDestination(); gain.connect(destination); captures.push(destination.stream); return destination.stream;
};
const dossier = !!window.__LARGE_RESULT_DOSSIER__, realtime = !!window.__LARGE_RESULT_REALTIME__;
const code = dossier ? '8426' : '7319', quantity = dossier ? '863' : '417';
const tool = dossier ? 'get_large_project_dossier' : 'get_large_catalog';
const fullCode = dossier ? 'ZEDER-8426' : 'KUPFER-7319';
await until(() => document.querySelector('#voice-model'), 'demo ready');
const model = document.querySelector('#voice-model');
const realtimeModel = model.value;
model.value = realtime ? realtimeModel : 'gpt-live-1'; model.dispatchEvent(new Event('change', { bubbles: true })); await delay(500);
if (!realtime && document.querySelector('#live-backend-mode')?.value !== 'client') throw new Error('Explicit client backend selector missing');
button('Deselect All').click(); await delay(200); document.querySelector('#tool-' + tool).click(); await delay(200);
const debug = [...document.querySelectorAll('label')].find(l => l.textContent.includes('Normal Mode'))?.querySelector('input');
if (debug) { debug.click(); await delay(200); }
button('Start').click();
try {
    await until(() => button('Stop') && !button('Stop').disabled, 'start');
    await delay(1000);
    const wav = Uint8Array.from(atob(window.__VOICE_DEMO_SMOKE_WAV_BASE64__), c => c.charCodeAt(0));
    const speech = audio.createBufferSource(); speech.buffer = await audio.decodeAudioData(wav.buffer); speech.connect(gain); speech.start();
    await until(() => document.querySelector('#chat-messages')?.innerText.includes(fullCode), 'full tool result in UI');
    const spoken = () => events.filter(e => e.type === 'session.output_transcript.delta' || e.type === 'response.output_audio_transcript.done')
        .map(e => e.delta || e.transcript || '').join('');
    await until(() => spoken().includes(code) && spoken().includes(quantity), 'spoken facts from end of large document');
    let followupResult = null;
    if (window.__LARGE_RESULT_FOLLOWUP_WAV_BASE64__) {
        await delay(2000);
        const before = spoken().length;
        const callsBefore = document.querySelectorAll('#chat-messages .text-amber-500').length;
        const followupWav = Uint8Array.from(atob(window.__LARGE_RESULT_FOLLOWUP_WAV_BASE64__), c => c.charCodeAt(0));
        const followupSpeech = audio.createBufferSource();
        followupSpeech.buffer = await audio.decodeAudioData(followupWav.buffer); followupSpeech.connect(gain); followupSpeech.start();
        const expectedDefects = dossier ? /\b(3|drei)\b/i : /\b(2|zwei)\b/i;
        await until(() => expectedDefects.test(spoken().slice(before)) && /mängel|mangel/i.test(spoken().slice(before)), 'followup uses retained full tool result');
        if (document.querySelectorAll('#chat-messages .text-amber-500').length !== callsBefore) throw new Error('Followup unexpectedly repeated tool execution');
        followupResult = spoken().slice(before);
    }
    if (events.some(e => e.type === 'error')) throw new Error('API error: ' + JSON.stringify(events.filter(e => e.type === 'error')));
    const stats = [...(await peers[0].getStats()).values()].filter(s => /^(inbound|outbound)-rtp$/.test(s.type));
    if (!stats.some(s => s.bytesReceived > 0) || !stats.some(s => s.bytesSent > 0)) throw new Error('No bidirectional audio');
    const spokenResult = spoken();
    button('Stop').click(); await until(() => !button('Start').disabled, 'stop');
    if (peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Leaked browser media');
    return { passed: true, mode: realtime ? 'realtime' : 'live-client', tool, spokenResult, followupResult, fullResultVisible: true,
        mediaClosed: true, finalUsage: document.querySelector('#live-session-usage')?.textContent || null };
} finally {
    if (button('Stop') && !button('Stop').disabled) button('Stop').click();
    oscillator.stop(); await audio.close();
}
