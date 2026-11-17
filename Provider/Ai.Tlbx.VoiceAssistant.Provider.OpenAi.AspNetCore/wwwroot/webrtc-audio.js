// Shared native WebRTC controls. No PCM interception, SDP logging or raw getStats forwarding.
let diagnosticSessionSequence = 0;
const finite = value => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
const counter = (value, signed = false) => Number.isSafeInteger(value) && (signed || value >= 0) ? value : null;
const allowed = (value, values) => values.includes(value) ? value : null;

export function normalizeWebRtcAudioOptions(value = {}) {
    const options = typeof value === 'string' ? JSON.parse(value) : (value ?? {});
    const target = options.jitterBufferTargetMilliseconds;
    if (target != null && (finite(target) == null || target > 4000))
        throw new RangeError('jitterBufferTargetMilliseconds must be null or 0–4000.');
    const interval = options.statisticsIntervalMs;
    if (interval != null && (finite(interval) == null || interval < 500 || interval > 60000))
        throw new RangeError('statisticsIntervalMs must be null or 500–60000.');
    const microphone = options.microphone ?? {};
    for (const name of ['echoCancellation', 'noiseSuppression', 'autoGainControl'])
        if (microphone[name] != null && typeof microphone[name] !== 'boolean')
            throw new TypeError(name + ' must be null or boolean.');
    const peer = options.peerConnection ?? {};
    if (peer.iceTransportPolicy != null && !['all', 'relay'].includes(peer.iceTransportPolicy))
        throw new RangeError('Unsupported iceTransportPolicy.');
    if (peer.iceCandidatePoolSize != null && (!Number.isInteger(peer.iceCandidatePoolSize) || peer.iceCandidatePoolSize < 0 || peer.iceCandidatePoolSize > 255))
        throw new RangeError('iceCandidatePoolSize must be 0–255.');
    return {
        diagnosticSessionId: options.diagnosticSessionId ?? String(++diagnosticSessionSequence),
        diagnosticsEnabled: options.diagnosticsEnabled === true,
        jitterBufferTargetMilliseconds: target ?? null,
        statisticsIntervalMs: interval ?? null,
        microphone: { ...microphone },
        peerConnection: {
            ...(peer.iceTransportPolicy != null ? { iceTransportPolicy: peer.iceTransportPolicy } : {}),
            ...(peer.iceCandidatePoolSize != null ? { iceCandidatePoolSize: peer.iceCandidatePoolSize } : {})
        }
    };
}

export function webRtcMicrophoneConstraints(options) {
    return {
        echoCancellation: options.microphone.echoCancellation ?? true,
        noiseSuppression: options.microphone.noiseSuppression ?? true,
        autoGainControl: options.microphone.autoGainControl ?? true
    };
}

export function webRtcPeerConfiguration(options, existingConfiguration) {
    const configured = Object.keys(options.peerConnection).length > 0;
    return configured ? { ...existingConfiguration, ...options.peerConnection } : existingConfiguration;
}

export function createWebRtcAudioController(peer, options, onSnapshot) {
    let stopped = false, timer = null, deliveryInFlight = false, pending = [];
    let receiverIndex = 0;
    const receivers = new WeakSet();
    const startedAt = performance.now();
    const enabled = options.diagnosticsEnabled || options.statisticsIntervalMs != null || options.jitterBufferTargetMilliseconds != null;

    // One callback in flight, at most 16 pending lifecycle events, and coalesced pending statistics.
    function deliver(snapshot) {
        if (stopped || !enabled || typeof onSnapshot !== 'function') return;
        if (deliveryInFlight) {
            const priorStatistics = pending.findIndex(item => item.eventType === 'Statistics');
            if (snapshot.eventType === 'Statistics' && priorStatistics >= 0) pending[priorStatistics] = snapshot;
            else {
                if (pending.length >= 16) pending.splice(priorStatistics >= 0 ? priorStatistics : 0, 1);
                pending.push(snapshot);
            }
            return;
        }
        deliveryInFlight = true;
        Promise.resolve().then(() => { if (!stopped) return onSnapshot(snapshot); })
            .catch(() => {}) // Diagnostic consumers must never terminate media or tool work.
            .finally(() => {
                deliveryInFlight = false;
                const next = pending.shift();
                if (next && !stopped) deliver(next);
            });
    }

    function emit(eventType, detail = {}) {
        if (stopped || !enabled) return;
        deliver({
            diagnosticSessionId: options.diagnosticSessionId,
            eventType, timestampUnixMilliseconds: Date.now(),
            elapsedMilliseconds: Math.max(0, performance.now() - startedAt),
            connectionState: allowed(peer?.connectionState, ['new', 'connecting', 'connected', 'disconnected', 'failed', 'closed']),
            iceConnectionState: allowed(peer?.iceConnectionState, ['new', 'checking', 'connected', 'completed', 'disconnected', 'failed', 'closed']),
            statisticsSupported: typeof peer?.getStats === 'function',
            ...detail
        });
    }

    function configureReceiver(receiver) {
        if (stopped || !receiver || receivers.has(receiver) || (receiver.track?.kind && receiver.track.kind !== 'audio')) return;
        receivers.add(receiver);
        const index = receiverIndex++;
        const supported = 'jitterBufferTarget' in receiver;
        let status = 'NotRequested', readback = null;
        if (options.jitterBufferTargetMilliseconds != null) {
            if (!supported) status = 'Unsupported';
            else {
                try {
                    receiver.jitterBufferTarget = options.jitterBufferTargetMilliseconds;
                    readback = finite(receiver.jitterBufferTarget);
                    status = readback === options.jitterBufferTargetMilliseconds ? 'Applied' : 'Rejected';
                } catch { status = 'Rejected'; }
            }
        }
        emit('ReceiverConfiguration', {
            receiverIndex: index, jitterBufferTargetSupported: supported, jitterBufferTargetStatus: status,
            requestedJitterBufferTargetMilliseconds: options.jitterBufferTargetMilliseconds,
            receiverJitterBufferTargetMilliseconds: readback
        });
    }

    function remoteTrack(event) {
        if (stopped || (event.track?.kind && event.track.kind !== 'audio')) return;
        if (event.receiver) configureReceiver(event.receiver);
        else for (const receiver of peer.getReceivers?.() ?? []) configureReceiver(receiver);
        // This is track arrival, never a claim of first audible speech.
        emit('RemoteAudioTrackReceived');
    }

    async function sample() {
        timer = null;
        if (stopped) return;
        try {
            if (typeof peer.getStats !== 'function') {
                emit('StatisticsUnavailable', { errorCode: 'get-stats-unsupported' });
                return; // Capability absence does not need repeated polling.
            }
            const report = await peer.getStats();
            if (stopped) return;
            const inboundAudio = [], audioPlayout = [];
            let pair = null;
            for (const stat of report.values()) {
                if (stat.type === 'inbound-rtp' && (stat.kind ?? stat.mediaType) === 'audio' && inboundAudio.length < 16) {
                    inboundAudio.push({
                        streamIndex: inboundAudio.length, timestampMilliseconds: finite(stat.timestamp),
                        packetsReceived: counter(stat.packetsReceived), packetsLost: counter(stat.packetsLost, true),
                        packetsDiscarded: counter(stat.packetsDiscarded), jitterSeconds: finite(stat.jitter),
                        concealedSamples: counter(stat.concealedSamples), silentConcealedSamples: counter(stat.silentConcealedSamples),
                        concealmentEvents: counter(stat.concealmentEvents), totalSamplesReceived: counter(stat.totalSamplesReceived),
                        insertedSamplesForDeceleration: counter(stat.insertedSamplesForDeceleration),
                        removedSamplesForAcceleration: counter(stat.removedSamplesForAcceleration),
                        jitterBufferDelaySeconds: finite(stat.jitterBufferDelay),
                        jitterBufferTargetDelaySeconds: finite(stat.jitterBufferTargetDelay),
                        jitterBufferEmittedCount: counter(stat.jitterBufferEmittedCount)
                    });
                }
                // "media-playout" is the standards name; tolerate the browser's older "audio-playout" label.
                if (['media-playout', 'audio-playout'].includes(stat.type) && stat.kind === 'audio' && audioPlayout.length < 16) {
                    audioPlayout.push({
                        playoutIndex: audioPlayout.length, timestampMilliseconds: finite(stat.timestamp),
                        synthesizedSamplesDurationSeconds: finite(stat.synthesizedSamplesDuration),
                        synthesizedSamplesEvents: counter(stat.synthesizedSamplesEvents),
                        totalSamplesDurationSeconds: finite(stat.totalSamplesDuration),
                        totalPlayoutDelaySeconds: finite(stat.totalPlayoutDelay), totalSamplesCount: counter(stat.totalSamplesCount)
                    });
                }
                if (stat.type === 'transport' && stat.selectedCandidatePairId) pair = report.get(stat.selectedCandidatePairId) ?? pair;
            }
            if (!pair) for (const stat of report.values())
                if (stat.type === 'candidate-pair' && stat.state === 'succeeded' && (stat.selected || stat.nominated)) { pair = stat; break; }
            let selectedTransport = null;
            if (pair) {
                const local = report.get(pair.localCandidateId), remote = report.get(pair.remoteCandidateId);
                selectedTransport = {
                    protocol: allowed(local?.protocol, ['udp', 'tcp']),
                    localCandidateType: allowed(local?.candidateType, ['host', 'srflx', 'prflx', 'relay']),
                    remoteCandidateType: allowed(remote?.candidateType, ['host', 'srflx', 'prflx', 'relay']),
                    currentRoundTripTimeSeconds: finite(pair.currentRoundTripTime)
                };
            }
            emit('Statistics', { inboundAudio, audioPlayout, audioPlayoutStatisticsAvailable: audioPlayout.length > 0, selectedTransport });
        } catch {
            emit('StatisticsUnavailable', { errorCode: 'get-stats-failed' });
        } finally {
            if (!stopped && typeof peer?.getStats === 'function' && options.statisticsIntervalMs != null)
                timer = setTimeout(sample, options.statisticsIntervalMs);
        }
    }

    emit('PeerCreated');
    if (options.statisticsIntervalMs != null) timer = setTimeout(sample, options.statisticsIntervalMs);
    return {
        remoteTrack,
        connectionStateChanged: () => emit('ConnectionStateChanged'),
        stop() { stopped = true; clearTimeout(timer); timer = null; pending = []; peer = null; }
    };
}
