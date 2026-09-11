// Inject window.__VOICE_DEMO_SMOKE_WAV_BASE64__ with a spoken current-time request before running.
const delay = ms => new Promise(r => setTimeout(r, ms));
const until = async (test, label) => {
    const end = Date.now() + 45000;
    while (!test()) { if (Date.now() > end) throw new Error(label + ': ' + document.body.innerText + '\nLive events: ' + JSON.stringify(events.filter(e => !e.type.includes('audio')).slice(-12)) + '\nRTP: ' + JSON.stringify(await Promise.all(peers.map(async p => [...(await p.getStats()).values()].filter(s => /rtp$/.test(s.type)))))); await delay(100); }
};
const button = text => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === text);
const selectModel = async value => {
    const model = document.querySelector('#voice-model');
    model.value = value; model.dispatchEvent(new Event('change', { bubbles: true })); await delay(500);
};
const peers = [], captures = [], requests = [], events = [];
const OriginalPeer = window.RTCPeerConnection;
window.RTCPeerConnection = class extends OriginalPeer {
    constructor(...args) { super(...args); peers.push(this); }
    createDataChannel(...args) { const dc = super.createDataChannel(...args); dc.addEventListener('message', e => { try { events.push(JSON.parse(e.data)); } catch {} }); return dc; }
};
const originalFetch = window.fetch;
window.fetch = async (...args) => { requests.push(String(args[0])); return originalFetch(...args); };
const audio = new AudioContext(); await audio.resume();
const sourceGain = audio.createGain();
const silence = audio.createOscillator(), silentGain = audio.createGain();
silentGain.gain.value = 0; silence.connect(silentGain); silentGain.connect(sourceGain); silence.start();
navigator.mediaDevices.getUserMedia = async () => {
    const destination = audio.createMediaStreamDestination(); sourceGain.connect(destination); captures.push(destination.stream); return destination.stream;
};
await until(() => document.querySelector('#voice-model'), 'main demo');
const realtime = document.querySelector('#voice-model').value;
await selectModel('gpt-live-1');
if (!document.querySelector('#live-session-usage') || document.body.innerText.includes('Tool preambles')) throw new Error('Live model controls not applied');
button('Deselect All').click(); await delay(300);
document.querySelector('#tool-get_current_time').click(); await delay(300);
const debug = [...document.querySelectorAll('label')].find(l => l.textContent.includes('Normal Mode'))?.querySelector('input');
if (debug) { debug.click(); await delay(300); }
button('Start').click();
try {
    await until(() => !button('Stop').disabled, 'Live startup');
    await delay(1500);
    const wav = Uint8Array.from(atob(window.__VOICE_DEMO_SMOKE_WAV_BASE64__), c => c.charCodeAt(0));
    const speech = audio.createBufferSource(); speech.buffer = await audio.decodeAudioData(wav.buffer); speech.connect(sourceGain); speech.start();
    await until(() => document.querySelector('#chat-messages').innerText.includes('The current time is:'), 'enabled time tool result');
    await until(() => events.some(e => e.type === 'session.output_transcript.delta' && /\d/.test(e.delta)), 'spoken time result');
    if (events.some(e => e.type === 'error')) throw new Error('Live API error: ' + JSON.stringify(events.filter(e => e.type === 'error')));
    const conversation = document.querySelector('#chat-messages').innerText;
    if (!conversation.includes('(Request)') || !conversation.includes('(Response)')) throw new Error('Tool debug entries missing');
    const stats = [...(await peers[0].getStats()).values()].filter(s => /^(inbound|outbound)-rtp$/.test(s.type));
    if (!stats.some(s => s.bytesReceived > 0) || !stats.some(s => s.bytesSent > 0)) throw new Error('Missing audio transport');
    button('Stop').click(); await until(() => !button('Start').disabled, 'stop');
    if (!document.querySelector('#live-session-usage').textContent.includes('True')) throw new Error('Final usage missing');
    await selectModel(realtime);
    if (document.querySelector('#live-session-usage') || !document.body.innerText.includes('Tool preambles')) throw new Error('Realtime controls not restored');
    await selectModel('gpt-live-1');
    button('Start').click(); await until(() => !button('Stop').disabled, 'Live restart after model switch');
    button('Stop').click(); await until(() => !button('Start').disabled, 'restarted stop');
    if (peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Media leak');
    return { passed: true, conversation, requests, modelSwitch: true, restarted: true, mediaClosed: true };
} finally { if (button('Stop') && !button('Stop').disabled) button('Stop').click(); silence.stop(); await audio.close(); }
