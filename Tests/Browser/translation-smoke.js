const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (check, label, timeout = 45000) => {
    const end = Date.now() + timeout;
    while (!check()) { if (Date.now() > end) throw new Error(`${label}: ${document.body.innerText}`); await delay(100); }
};
const peers = [], captures = [];
const OriginalPeer = window.RTCPeerConnection;
window.RTCPeerConnection = class extends OriginalPeer { constructor(...args) { super(...args); peers.push(this); } };
const context = new AudioContext(); await context.resume();
const wav = Uint8Array.from(atob('__INPUT_WAV__'), c => c.charCodeAt(0));
const decoded = await context.decodeAudioData(wav.buffer);
let source;
const silence = context.createOscillator(); const silenceGain = context.createGain();
silence.frequency.value = 30; silenceGain.gain.value = 0.001; silence.connect(silenceGain); silence.start();
navigator.mediaDevices.getUserMedia = async () => {
    const destination = context.createMediaStreamDestination(); captures.push(destination.stream);
    source = context.createBufferSource(); source.buffer = decoded; source.connect(destination);
    silenceGain.connect(destination);
    return destination.stream;
};
await until(() => document.querySelector('#translation-start'), 'demo ready');
try {
    document.querySelector('#translation-start').click();
    await until(() => document.querySelector('#translation-status').textContent === 'Translating' || document.body.innerText.includes('Error:'), 'start');
    if (document.body.innerText.includes('Error:')) throw new Error(document.body.innerText);
    document.querySelector('#translation-mute').click(); await until(() => !captures[0].getAudioTracks()[0].enabled, 'mute');
    document.querySelector('#translation-mute').click(); await until(() => captures[0].getAudioTracks()[0].enabled, 'unmute');
    source.start();
    await until(() => document.querySelector('#translation-target').textContent.length > 5, 'translated speech');
    await delay((decoded.duration + 3) * 1000);
    const stats = [...(await peers[0].getStats()).values()].filter(s => s.type === 'inbound-rtp' || s.type === 'outbound-rtp').map(s => ({ type: s.type, bytesSent: s.bytesSent, bytesReceived: s.bytesReceived }));
    if (!stats.some(s => s.bytesReceived > 0) || !stats.some(s => s.bytesSent > 0)) throw new Error('Missing bidirectional audio RTP');
    document.querySelector('#translation-stop').click();
    await until(() => ['Stopped'].includes(document.querySelector('#translation-status').textContent) || document.body.innerText.includes('Error:'), 'close');
    if (document.body.innerText.includes('Error:')) throw new Error(document.body.innerText);
    if (!document.querySelector('#translation-final').textContent.includes('True')) throw new Error('Final output missing');
    if (!document.querySelector('#translation-target').textContent.includes('nine')) throw new Error('Final translation tail missing: ' + document.body.innerText);
    if (peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Media leak');
    const first = { source: document.querySelector('#translation-source').textContent, target: document.querySelector('#translation-target').textContent, stats };
    document.querySelector('#translation-start').click();
    await until(() => document.querySelector('#translation-status').textContent === 'Translating', 'restart');
    document.querySelector('#translation-stop').click();
    await until(() => document.querySelector('#translation-status').textContent === 'Stopped', 'restarted close');
    if (peers.length !== 2 || peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Restart leak');
    return { passed: true, ...first, restarted: true, mediaClosed: true };
} finally { try { source?.stop(); silence.stop(); } catch {} await context.close(); }
