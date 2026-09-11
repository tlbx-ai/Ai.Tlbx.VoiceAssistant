const delay = ms => new Promise(r => setTimeout(r, ms));
const until = async (test, label, timeout = 45000) => {
    const end = Date.now() + timeout;
    while (!test()) { if (Date.now() > end) throw new Error(label + ': ' + document.body.innerText); await delay(100); }
};
const peers = [], captures = [], requests = [];
const OriginalPeer = window.RTCPeerConnection;
window.RTCPeerConnection = class extends OriginalPeer { constructor(...args) { super(...args); peers.push(this); } };
const originalFetch = window.fetch;
window.fetch = async (...args) => { requests.push(String(args[0])); return originalFetch(...args); };
const audio = new AudioContext();
await audio.resume();
const oscillator = audio.createOscillator();
const gain = audio.createGain(); gain.gain.value = 0;
oscillator.connect(gain); oscillator.start();
navigator.mediaDevices.getUserMedia = async () => {
    const destination = audio.createMediaStreamDestination();
    gain.connect(destination); captures.push(destination.stream); return destination.stream;
};
await until(() => document.querySelector('#live-start'), 'demo ready');
document.querySelector('#live-start').click();
try {
    await until(() => !document.querySelector('#live-stop').disabled || document.querySelector('#live-status').textContent.includes('Error:'), 'startup');
    if (document.querySelector('#live-status').textContent.includes('Error:')) throw new Error(document.querySelector('#live-status').textContent);
    await until(() => document.querySelector('#live-assistant').textContent.trim().length > 3, 'assistant speech');
    document.querySelector('#live-mute').click();
    await until(() => document.querySelector('#live-mute').textContent.includes('Unmute'), 'mute');
    if (captures[0].getAudioTracks()[0].enabled) throw new Error('microphone track was not muted');
    document.querySelector('#live-mute').click();
    await until(() => document.querySelector('#live-mute').textContent.includes('Mute'), 'unmute');
    document.querySelector('#live-tool').click();
    await until(() => /Completed tool calls: [1-9]/.test(document.querySelector('#live-tools').textContent), 'backend tool');
    await delay(1500);
    const stats = [...(await peers[0].getStats()).values()].filter(s => s.type === 'inbound-rtp' || s.type === 'outbound-rtp').map(s => ({type:s.type, kind:s.kind, bytesSent:s.bytesSent, bytesReceived:s.bytesReceived}));
    if (!stats.some(s => s.type === 'inbound-rtp' && s.bytesReceived > 0) || !stats.some(s => s.type === 'outbound-rtp' && s.bytesSent > 0)) throw new Error('Missing bidirectional WebRTC RTP');
    document.querySelector('#live-stop').click();
    await until(() => document.querySelector('#live-status').textContent === 'Stopped', 'graceful close');
    if (!document.querySelector('#live-usage').textContent.includes('True')) throw new Error('Final usage not confirmed');
    if (peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Media resources leaked');
    const first = {stats, usage:document.querySelector('#live-usage').textContent, tools:document.querySelector('#live-tools').textContent, transcript:document.querySelector('#live-assistant').textContent};
    if (!first.tools.includes('Completed tool calls: 1')) throw new Error('Duplicate tool execution');
    document.querySelector('#live-start').click();
    await until(() => !document.querySelector('#live-stop').disabled, 'restart');
    await until(() => document.querySelector('#live-assistant').textContent.trim().length > 3, 'restarted speech');
    document.querySelector('#live-stop').click();
    await until(() => document.querySelector('#live-status').textContent === 'Stopped', 'restarted close');
    if (!document.querySelector('#live-usage').textContent.includes('True')) throw new Error('Restarted final usage missing');
    if (peers.length !== 2 || peers.some(p => p.connectionState !== 'closed') || captures.some(s => s.getTracks().some(t => t.readyState !== 'ended'))) throw new Error('Restart leaked media');
    return {passed:true, ...first, requests, restarted:true, mediaClosed:true};
} finally { if (!document.querySelector('#live-stop').disabled) document.querySelector('#live-stop').click(); oscillator.stop(); await audio.close(); }
