// Inject __SUPPORT_WAVS__ = { weather, tower, closing } (base64 WAV) before running.
// Natural two-person conversation fragments, without tool names or commands to the AI.
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const events = [], peers = [], captures = [];
const until = async (test, label) => {
    const end = Date.now() + 45000;
    while (!test()) {
        if (/Direct realtime session failed|Failed to fetch dynamically imported module/.test(document.body.innerText))
            throw new Error(label + ': ' + document.body.innerText);
        if (events.some(e => e.type === 'error')) throw new Error(JSON.stringify(events.filter(e => e.type === 'error')));
        if (Date.now() > end) throw new Error(label + ': ' + document.body.innerText);
        await delay(100);
    }
};
const button = text => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === text);
const OriginalPeer = window.RTCPeerConnection;
window.RTCPeerConnection = class extends OriginalPeer {
    constructor(...args) { super(...args); peers.push(this); }
    createDataChannel(...args) {
        const dc = super.createDataChannel(...args);
        dc.addEventListener('message', e => { try { events.push(JSON.parse(e.data)); } catch {} });
        return dc;
    }
};
const audio = new AudioContext(); await audio.resume();
const input = audio.createGain();
const silence = audio.createOscillator(), mute = audio.createGain();
mute.gain.value = 0; silence.connect(mute); mute.connect(input); silence.start();
navigator.mediaDevices.getUserMedia = async () => {
    const destination = audio.createMediaStreamDestination(); input.connect(destination);
    captures.push(destination.stream); return destination.stream;
};
const play = async name => {
    const bytes = Uint8Array.from(atob(window.__SUPPORT_WAVS__[name]), c => c.charCodeAt(0));
    const source = audio.createBufferSource(); source.buffer = await audio.decodeAudioData(bytes.buffer);
    source.connect(input);
    await new Promise(resolve => { source.onended = resolve; source.start(); });
};
await until(() => document.querySelector('#conversation-support-tab'), 'demo ready');
document.querySelector('#conversation-support-tab').click();
await until(() => document.querySelector('#conversation-hints'), 'support tab');
if (document.querySelector('#voice-model option[value="gpt-live-1"]')) throw new Error('Unsupported model offered');
button('Start').click();
try {
    await until(() => button('Stop') && !button('Stop').disabled, 'start');
    await play('weather');
    await until(() => events.some(e => e.type === 'response.function_call_arguments.done' && e.name === 'get_weather'), 'weather tool selected');
    await until(() => /Demo-Wetter/i.test(document.querySelector('#conversation-hints').textContent), 'weather hint');
    await play('tower');
    await until(() => /Eiffel/i.test(document.querySelector('#conversation-hints').textContent) && /\d/.test(document.querySelector('#conversation-hints').textContent.replace(/Demo-Wetter[^\n]*/g, '')), 'technical context');
    await play('closing');
    await until(() => /Freitag|Angebot/.test(document.querySelector('#conversation-hints').textContent), 'next steps');
    await until(() => events.filter(e => e.type === 'response.done').length >= 3 && !document.querySelector('#streaming-hint'), 'completed text');
    const errors = events.filter(e => e.type === 'error');
    if (errors.length) throw new Error(JSON.stringify(errors));
    const modalities = events.filter(e => e.type === 'session.created').map(e => e.session.output_modalities);
    if (!modalities.some(m => m?.length === 1 && m[0] === 'text')) throw new Error('Not text-only: ' + JSON.stringify(modalities));
    const usage = events.filter(e => e.type === 'response.done').map(e => e.response.usage);
    if (usage.some(u => (u?.output_token_details?.audio_tokens ?? 0) > 0)) throw new Error('Generated audio output');
    const hints = document.querySelector('#conversation-hints').innerText;
    const transcriptAndTools = document.querySelector('#support-transcript').textContent;
    const toolCalls = events.filter(e => e.type === 'response.function_call_arguments.done').map(e => ({name:e.name, arguments:e.arguments}));
    if (!transcriptAndTools.includes('get_weather')) throw new Error('Tool not visible in UI');
    button('Stop').click(); await until(() => !button('Start').disabled, 'stop');
    // Repeat start/stop to verify the mode and microphone lifecycle survive reuse.
    button('Start').click(); await until(() => button('Stop') && !button('Stop').disabled, 'restart');
    button('Stop').click(); await until(() => !button('Start').disabled, 'second stop');
    button('Voice Chat').click(); await delay(500);
    if (document.querySelector('#conversation-hints')) throw new Error('Support view leaked into voice mode');
    if (peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Media leak');
    return { passed: true, hints, transcriptAndTools, toolCalls, usage, modalities, restarted: true, mediaClosed: true };
} finally {
    if (button('Stop') && !button('Stop').disabled) button('Stop').click();
    silence.stop(); await audio.close(); window.RTCPeerConnection = OriginalPeer;
}
