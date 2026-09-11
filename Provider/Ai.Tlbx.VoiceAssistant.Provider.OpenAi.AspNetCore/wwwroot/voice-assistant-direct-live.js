export function createOpenAiDirectLiveClient(configuration, dotNet) {
    const options = typeof configuration === "string" ? JSON.parse(configuration) : configuration;
    let peer, events, microphone, playback, disconnectTimer;
    let disposed = false, finalized = false;
    const abort = new AbortController();
    const timers = new Set();
    const notify = (method, ...args) => { if (dotNet) void dotNet.invokeMethodAsync(method, ...args).catch(() => {}); };
    const bounded = (promise, ms, message) => new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(message)), ms);
        timers.add(timer);
        const cancelled = () => reject(new Error("Live browser startup cancelled"));
        abort.signal.addEventListener("abort", cancelled, { once: true });
        if (abort.signal.aborted) cancelled();
        promise.then(resolve, reject).finally(() => {
            clearTimeout(timer); timers.delete(timer);
            abort.signal.removeEventListener("abort", cancelled);
        });
    });
    function dispose() {
        if (disposed) return;
        disposed = true;
        abort.abort();
        for (const timer of timers) clearTimeout(timer);
        timers.clear();
        clearTimeout(disconnectTimer);
        if (events) { events.onclose = null; events.onerror = null; events.close(); }
        if (peer) { peer.onconnectionstatechange = null; peer.close(); }
        microphone?.getTracks().forEach(track => track.stop());
        if (playback) { playback.pause(); playback.srcObject = null; playback.remove(); }
    }
    function fail(message) {
        if (disposed || finalized) return;
        notify("OnLiveBrowserError", message);
        dispose();
    }
    async function start() {
        if (disposed || peer) throw new Error("Create a fresh Live browser client for each session");
        let resolveStarted, rejectStarted;
        const started = new Promise((resolve, reject) => { resolveStarted = resolve; rejectStarted = reject; });
        // Attach the rejection handler before WebRTC can deliver an early error.
        started.catch(() => {});
        try {
            peer = new RTCPeerConnection();
            playback = document.createElement("audio");
            playback.autoplay = true;
            playback.controls = true; // Allows the caller to resume playback if autoplay is blocked.
            playback.setAttribute("aria-label", "GPT-Live audio playback");
            document.body.appendChild(playback);
            peer.ontrack = event => {
                if (disposed) return;
                playback.srcObject = event.streams[0] || new MediaStream([event.track]);
                void playback.play().catch(() => { /* Native playback controls remain available. */ });
            };
            peer.onconnectionstatechange = () => {
                clearTimeout(disconnectTimer);
                if (peer.connectionState === "failed") fail("Live WebRTC media connection failed");
                if (peer.connectionState === "disconnected") disconnectTimer = setTimeout(() => fail("Live WebRTC media connection lost"), 5000);
            };
            const capture = navigator.mediaDevices.getUserMedia({ audio: {
                echoCancellation: true, noiseSuppression: true, autoGainControl: true,
                ...(options.microphoneId ? { deviceId: { exact: options.microphoneId } } : {})
            } });
            // getUserMedia cannot be cancelled; stop a stream that arrives after disposal.
            capture.then(stream => { if (disposed) stream.getTracks().forEach(track => track.stop()); }, () => {});
            microphone = await bounded(capture, options.timeoutMs || 20000, "Microphone permission timed out");
            if (disposed) throw new Error("Live browser startup cancelled");
            for (const track of microphone.getTracks()) peer.addTrack(track, microphone);
            events = peer.createDataChannel("oai-events");
            events.onmessage = ({ data }) => {
                let event;
                try { event = JSON.parse(data); } catch { fail("Invalid Live data-channel event"); return; }
                if (event.type === "session.started") resolveStarted();
                if (event.type === "error" && !finalized) {
                    // Server sideband owns detailed error reporting and backend commands.
                    rejectStarted(new Error(event.error?.message || "Live startup rejected"));
                }
                if (event.type === "session.closed") {
                    finalized = true;
                    rejectStarted(new Error("Live session ended during startup"));
                    notify("OnLiveBrowserClosed");
                    dispose();
                }
            };
            events.onerror = () => { rejectStarted(new Error("Live data channel failed")); fail("Live data channel failed"); };
            events.onclose = () => { if (!finalized) fail("Live data channel closed without final usage"); };
            await peer.setLocalDescription(await peer.createOffer());
            if (peer.iceGatheringState !== "complete") {
                await bounded(new Promise(resolve => {
                    const changed = () => {
                        if (peer.iceGatheringState === "complete") { peer.removeEventListener("icegatheringstatechange", changed); resolve(); }
                    };
                    peer.addEventListener("icegatheringstatechange", changed);
                    changed();
                }), 10000, "Live ICE gathering timed out");
            }
            const response = await bounded(fetch(options.sessionUrl, {
                method: "POST", credentials: "same-origin", signal: abort.signal,
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ preparedSessionId: options.preparedSessionId, sdp: peer.localDescription.sdp })
            }), options.timeoutMs || 20000, "Live session creation timed out");
            if (!response.ok) throw new Error(`Live session creation failed (${response.status})`);
            const result = await response.json();
            if (!result.session?.id || !result.transport?.sdp) throw new Error("Invalid Live session response");
            await peer.setRemoteDescription({ type: "answer", sdp: result.transport.sdp });
            await bounded(started, options.timeoutMs || 20000, "Live session.started timed out");
            // HTTP started this session. Never send session.start or microphone JSON on the data channel.
            if (disposed) throw new Error("Live session closed before startup completed");
        } catch (error) {
            dispose();
            throw error;
        }
    }
    function setMicrophoneEnabled(enabled) { microphone?.getAudioTracks().forEach(track => { track.enabled = !!enabled; }); }
    function setPlaybackMuted(muted) { if (playback) playback.muted = !!muted; }
    return { start, dispose, setMicrophoneEnabled, setPlaybackMuted };
}
