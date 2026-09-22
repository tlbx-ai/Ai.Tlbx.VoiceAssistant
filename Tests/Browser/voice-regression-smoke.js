// Inject __VOICE_WAVS__ = { weather, explanation, interrupt } with base64 WAV audio.
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const events = [], sent = [], peers = [], captures = [];
const button = text => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === text);
const until = async (test, label) => {
    const end = Date.now() + 45000;
    while (!test()) {
        if (/Direct realtime session failed|Failed to fetch dynamically imported module/.test(document.body.innerText))
            throw new Error(document.body.innerText);
        const errors = events.filter(e => e.type === 'error' && e.error?.code !== 'response_cancel_not_active');
        if (errors.length) throw new Error(JSON.stringify(errors));
        if (Date.now() > end) throw new Error(label + ': ' + JSON.stringify(events.slice(-10)));
        await delay(100);
    }
};
const OriginalPeer = window.RTCPeerConnection;
window.RTCPeerConnection = class extends OriginalPeer {
    constructor(...args) { super(...args); peers.push(this); }
    createDataChannel(...args) {
        const dc = super.createDataChannel(...args), originalSend = dc.send.bind(dc);
        dc.send = data => { try { sent.push(JSON.parse(data)); } catch {} originalSend(data); };
        dc.addEventListener('message', e => { try { events.push(JSON.parse(e.data)); } catch {} });
        return dc;
    }
};
const audio = new AudioContext(); await audio.resume();
const input = audio.createGain(), silence = audio.createOscillator(), mute = audio.createGain();
mute.gain.value = 0; silence.connect(mute); mute.connect(input); silence.start();
navigator.mediaDevices.getUserMedia = async () => {
    const destination = audio.createMediaStreamDestination(); input.connect(destination);
    captures.push(destination.stream); return destination.stream;
};
const play = async name => {
    const bytes = Uint8Array.from(atob(window.__VOICE_WAVS__[name]), c => c.charCodeAt(0));
    const source = audio.createBufferSource(); source.buffer = await audio.decodeAudioData(bytes.buffer); source.connect(input);
    await new Promise(resolve => { source.onended = resolve; source.start(); });
};
const transcript = e => e.transcript ?? e.text ?? '';
await until(() => document.querySelector('#voice-model'), 'demo ready');
button('Start').click();
try {
    await until(() => button('Stop') && !button('Stop').disabled, 'voice start');
    await play('weather');
    await until(() => events.some(e => e.type === 'response.function_call_arguments.done' && e.name === 'get_weather'), 'weather tool');
    await until(() => events.some(e => e.type === 'response.done' && e.response?.usage?.output_token_details?.audio_tokens > 0), 'spoken weather');
    await until(() => events.some(e => e.type === 'output_audio_buffer.stopped'), 'weather playback complete');
    const explanationStart = events.length;
    await play('explanation');
    await until(() => events.slice(explanationStart).some(e => e.type === 'output_audio_buffer.started'), 'long speech playback');
    await delay(500);
    const interruptionStart = events.length;
    await play('interrupt');
    await until(() => events.slice(interruptionStart).some(e => e.type === 'input_audio_buffer.speech_started'), 'barge-in detected');
    await until(() => events.slice(interruptionStart).some(e =>
        e.type === 'output_audio_buffer.cleared' || (e.type === 'response.done' && e.response?.status === 'cancelled')), 'speech interrupted');
    await until(() => events.slice(interruptionStart).some(e =>
        /audio_transcript.done$/.test(e.type) && /56|sechsundfünfzig/i.test(transcript(e))), 'spoken follow-up answer');
    const stats = [...(await peers[0].getStats()).values()].filter(s => /^(inbound|outbound)-rtp$/.test(s.type));
    if (!stats.some(s => s.bytesReceived > 0) || !stats.some(s => s.bytesSent > 0)) throw new Error('No bidirectional audio transport');
    const modalities = events.filter(e => e.type === 'session.created').map(e => e.session.output_modalities);
    if (!modalities.some(m => m?.length === 1 && m[0] === 'audio')) throw new Error('Voice mode not audio');
    const usage = events.filter(e => e.type === 'response.done').map(e => e.response.usage);
    const replies = events.filter(e => /audio_transcript.done$/.test(e.type)).map(transcript);
    button('Stop').click(); await until(() => !button('Start').disabled, 'stop');
    button('Start').click(); await until(() => button('Stop') && !button('Stop').disabled, 'restart');
    button('Stop').click(); await until(() => !button('Start').disabled, 'restarted stop');
    if (peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Media leak');
    return { passed: true, replies, modalities, usage, toolNames: events.filter(e => e.type === 'response.function_call_arguments.done').map(e => e.name),
        interrupted: true, restarted: true, mediaClosed: true, bytesReceived: stats.filter(s => s.type === 'inbound-rtp').map(s => s.bytesReceived) };
} finally {
    if (button('Stop') && !button('Stop').disabled) button('Stop').click();
    silence.stop(); await audio.close(); window.RTCPeerConnection = OriginalPeer;
}
