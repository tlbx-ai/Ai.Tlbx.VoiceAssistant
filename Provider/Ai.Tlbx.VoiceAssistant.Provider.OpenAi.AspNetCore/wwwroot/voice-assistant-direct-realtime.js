const REALTIME_CALLS_URL = 'https://api.openai.com/v1/realtime/calls';
const TOOL_CALL_TIMEOUT_MS = 120000;
const RESPONSE_WATCHDOG_MS = 120000;

export class OpenAiDirectRealtimeClient
{
    constructor(options)
    {
        this.options = options;
        this.peerConnection = null;
        this.dataChannel = null;
        this.controlSocket = null;
        this.mediaStream = null;
        this.voiceSessionId = null;
        this.session = null;
        this.audioElement = null;
        this.ownsAudioElement = false;
        this.audioMonitor = null;
        this.recentChats = [];
        this.clientActionHandlers = new Map();
        this.pendingFunctionArgs = new Map();
        this.pendingFunctionNames = new Map();
        this.pendingRealtimeEvents = [];
        this.activeResponse = false;
        this.responseWatchdog = null;
        this.closed = false;
    }

    registerClientAction(action, handler)
    {
        this.clientActionHandlers.set(action, handler);
    }

    async start(config)
    {
        if (this.peerConnection || this.dataChannel || this.controlSocket || this.mediaStream || this.audioElement)
        {
            await this.stop();
        }

        this.pendingRealtimeEvents = [];
        this.pendingFunctionArgs.clear();
        this.pendingFunctionNames.clear();
        this.recentChats = [];
        this.activeResponse = false;
        this.closed = false;
        this.clearResponseWatchdog();

        this.emitConnectionPhase('session.requesting', 'Requesting OpenAI browser session...');
        const sessionResponse = await fetch(this.options.sessionUrl, {
            method: 'POST',
            credentials: this.options.credentials ?? 'include',
            headers: {
                'Content-Type': 'application/json',
                ...(this.options.headers?.() ?? {})
            },
            body: JSON.stringify(config ?? {})
        });

        if (!sessionResponse.ok)
        {
            throw new Error(`Direct realtime session failed: ${sessionResponse.status}${await formatSessionError(sessionResponse)}`);
        }

        this.session = await sessionResponse.json();
        this.voiceSessionId = this.session.voiceSessionId;
        this.emitConnectionPhase('session.ready', 'OpenAI browser session prepared');
        await this.openControlSocket(this.session.controlUrl);
        await this.openRealtimePeerConnection(config);
        this.emitConnectionPhase('session.connected', 'Connected to OpenAI');
    }

    async stop()
    {
        try
        {
            this.sendControl({ type: 'stop' });
        }
        catch
        {
        }

        if (this.dataChannel)
        {
            this.dataChannel.onopen = null;
            this.dataChannel.onmessage = null;
            this.dataChannel.onerror = null;
            this.dataChannel.onclose = null;
            this.dataChannel.close();
            this.dataChannel = null;
        }

        if (this.controlSocket)
        {
            this.controlSocket.onopen = null;
            this.controlSocket.onmessage = null;
            this.controlSocket.onerror = null;
            this.controlSocket.onclose = null;
            this.controlSocket.close();
            this.controlSocket = null;
        }

        if (this.peerConnection)
        {
            this.peerConnection.ontrack = null;
            this.peerConnection.onconnectionstatechange = null;
            this.peerConnection.oniceconnectionstatechange = null;
            this.peerConnection.close();
            this.peerConnection = null;
        }

        if (this.mediaStream)
        {
            for (const track of this.mediaStream.getTracks())
            {
                track.stop();
            }
            this.mediaStream = null;
        }

        this.stopAudioLevelMonitor();

        if (this.ownsAudioElement && this.audioElement)
        {
            this.audioElement.remove();
        }

        this.audioElement = null;
        this.ownsAudioElement = false;
        this.pendingRealtimeEvents = [];
        this.pendingFunctionArgs.clear();
        this.pendingFunctionNames.clear();
        this.activeResponse = false;
        this.clearResponseWatchdog();
        this.closed = true;

        this.emitStatus('Disconnected');
    }

    setMicrophoneEnabled(enabled)
    {
        if (!this.mediaStream)
        {
            return false;
        }

        for (const track of this.mediaStream.getAudioTracks())
        {
            track.enabled = enabled;
        }

        this.emitStatus(enabled ? 'Listening' : 'Connected');
        return true;
    }

    async openControlSocket(controlUrl)
    {
        this.emitConnectionPhase('control.connecting', 'Opening server control channel...');
        const url = this.toWebSocketUrl(controlUrl);
        this.controlSocket = new WebSocket(url);

        await new Promise((resolve, reject) =>
        {
            if (!this.controlSocket)
            {
                reject(new Error('Control socket was not created'));
                return;
            }

            const timeout = window.setTimeout(() =>
            {
                reject(new Error('Control socket connection timeout'));
                this.controlSocket?.close();
            }, 10000);

            this.controlSocket.onopen = () =>
            {
                window.clearTimeout(timeout);
                this.emitConnectionPhase('control.open', 'Server control channel open');
                resolve();
            };

            this.controlSocket.onerror = () =>
            {
                window.clearTimeout(timeout);
                const error = new Error('Control socket error');
                this.handleConnectionLost('control_socket.error', error.message);
                reject(error);
            };

            this.controlSocket.onclose = event =>
            {
                window.clearTimeout(timeout);
                if (!this.closed)
                {
                    this.handleConnectionLost('control_socket.closed', `Control socket closed (${event.code || 'unknown'})`);
                }
            };

            this.controlSocket.onmessage = event =>
            {
                if (typeof event.data === 'string')
                {
                    this.handleControlMessage(JSON.parse(event.data));
                }
            };
        });
    }

    async openRealtimePeerConnection(config)
    {
        this.emitConnectionPhase('webrtc.initializing', 'Preparing browser audio pipeline...');
        const audioElement = this.options.audioElement ?? document.createElement('audio');
        audioElement.autoplay = true;
        audioElement.playsInline = true;
        audioElement.muted = false;
        this.audioElement = audioElement;
        this.ownsAudioElement = !this.options.audioElement;
        if (this.ownsAudioElement)
        {
            audioElement.style.display = 'none';
            document.body.appendChild(audioElement);
        }

        this.peerConnection = new RTCPeerConnection(this.options.rtcConfiguration);
        this.peerConnection.ontrack = event =>
        {
            audioElement.srcObject = event.streams[0];
            void audioElement.play().catch(error =>
            {
                this.emitDiagnostic('remote_audio.play_failed', {
                    message: error instanceof Error ? error.message : String(error)
                });
            });
            this.emitStatus('Speaking');
        };
        this.peerConnection.onconnectionstatechange = () =>
        {
            const state = this.peerConnection?.connectionState;
            this.emitDiagnostic('peer_connection.state', {
                connectionState: state,
                iceConnectionState: this.peerConnection?.iceConnectionState
            });
            if (state === 'failed' || state === 'closed')
            {
                this.handleConnectionLost('peer_connection.connection_lost', `Peer connection ${state}`);
            }
        };
        this.peerConnection.oniceconnectionstatechange = () =>
        {
            const state = this.peerConnection?.iceConnectionState;
            this.emitDiagnostic('peer_connection.ice_state', {
                iceConnectionState: state
            });
            if (state === 'failed' || state === 'closed')
            {
                this.handleConnectionLost('peer_connection.ice_connection_lost', `ICE connection ${state}`);
            }
        };

        this.dataChannel = this.peerConnection.createDataChannel('oai-events');
        this.dataChannel.onmessage = event => this.handleRealtimeEvent(JSON.parse(event.data));
        this.dataChannel.onopen = () =>
        {
            this.emitConnectionPhase('datachannel.open', 'Realtime event channel open');
            this.flushPendingRealtimeEvents();
            this.emitStatus('Listening');
        };
        this.dataChannel.onerror = () =>
        {
            this.handleConnectionLost('datachannel.error', 'Realtime data channel error');
        };
        this.dataChannel.onclose = () =>
        {
            if (!this.closed)
            {
                this.handleConnectionLost('datachannel.closed', 'Realtime data channel closed');
            }
        };

        this.emitConnectionPhase('microphone.requesting', 'Requesting microphone stream...');
        this.mediaStream = await this.getMicrophoneStream(config);

        const audioTracks = this.mediaStream.getAudioTracks();
        if (audioTracks.length === 0)
        {
            throw new Error('Browser returned no microphone audio track');
        }

        for (const track of audioTracks)
        {
            this.peerConnection.addTrack(track, this.mediaStream);
            this.emitDiagnostic('microphone.track', {
                id: track.id,
                label: track.label,
                enabled: track.enabled,
                muted: track.muted,
                readyState: track.readyState,
                settings: track.getSettings?.() ?? null
            });
        }
        this.emitConnectionPhase('microphone.ready', 'Microphone stream ready');
        this.startAudioLevelMonitor(this.mediaStream);

        this.emitConnectionPhase('webrtc.offer.creating', 'Creating WebRTC offer...');
        const offer = await this.peerConnection.createOffer();
        await this.peerConnection.setLocalDescription(offer);

        this.emitConnectionPhase('openai.connecting', 'Connecting browser audio to OpenAI...');
        const sdpResponse = await fetch(REALTIME_CALLS_URL, {
            method: 'POST',
            body: offer.sdp,
            headers: {
                Authorization: `Bearer ${this.session.clientSecret}`,
                'Content-Type': 'application/sdp'
            }
        });

        if (!sdpResponse.ok)
        {
            throw new Error(`OpenAI Realtime WebRTC call failed: ${sdpResponse.status}${await formatSessionError(sdpResponse)}`);
        }

        await this.peerConnection.setRemoteDescription({
            type: 'answer',
            sdp: await sdpResponse.text()
        });
        this.emitConnectionPhase('webrtc.remote_description_set', 'OpenAI WebRTC answer accepted');
    }

    injectConversationHistory(messages)
    {
        if (!Array.isArray(messages))
        {
            return;
        }

        for (const message of messages)
        {
            if (!message?.content || (message.role !== 'user' && message.role !== 'assistant'))
            {
                continue;
            }

            this.sendRealtimeEvent({
                type: 'conversation.item.create',
                item: {
                    type: 'message',
                    role: message.role,
                    content: [
                        {
                            type: message.role === 'user' ? 'input_text' : 'output_text',
                            text: message.content
                        }
                    ]
                }
            });
        }
    }

    interrupt()
    {
        if (this.activeResponse)
        {
            this.sendRealtimeEvent({ type: 'response.cancel' });
        }

        this.activeResponse = false;
        this.emitStatus('Interrupted');
    }

    async getMicrophoneStream(config)
    {
        try
        {
            return await navigator.mediaDevices.getUserMedia({
                audio: this.buildAudioConstraints(config, true)
            });
        }
        catch (error)
        {
            const requestedDevice = config?.microphoneId ?? config?.deviceId;
            if (!requestedDevice)
            {
                throw error;
            }

            this.emitDiagnostic('microphone.selected_device_failed', {
                microphoneId: requestedDevice,
                message: error instanceof Error ? error.message : String(error)
            });

            return await navigator.mediaDevices.getUserMedia({
                audio: this.buildAudioConstraints(config, false)
            });
        }
    }

    buildAudioConstraints(config, exactDevice)
    {
        const deviceId = config?.microphoneId ?? config?.deviceId;
        const audio = {
            channelCount: { ideal: 1 },
            sampleRate: { ideal: 48000 },
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true
        };

        if (deviceId)
        {
            audio.deviceId = exactDevice ? { exact: deviceId } : { ideal: deviceId };
        }

        return audio;
    }

    handleRealtimeEvent(event)
    {
        this.options.onRealtimeEvent?.(event);

        switch (event.type)
        {
            case 'input_audio_buffer.speech_started':
                if (this.activeResponse)
                {
                    this.sendRealtimeEvent({ type: 'response.cancel' });
                    this.activeResponse = false;
                    this.emitStatus('Interrupted');
                    break;
                }

                this.emitStatus('Listening');
                break;

            case 'input_audio_buffer.speech_stopped':
            case 'input_audio_buffer.committed':
                this.emitStatus('Processing');
                break;

            case 'response.created':
                this.activeResponse = true;
                this.armResponseWatchdog(event);
                this.emitStatus('Processing');
                break;

            case 'response.output_audio.delta':
            case 'response.audio.delta':
                this.emitStatus('Speaking');
                break;

            case 'conversation.item.input_audio_transcription.completed':
                this.emitChat('user', event.transcript ?? '');
                this.emitTranscriptionUsage(event);
                break;

            case 'response.output_audio_transcript.done':
            case 'response.audio_transcript.done':
            case 'response.output_text.done':
                this.emitChat('assistant', event.transcript ?? event.text ?? '');
                break;

            case 'response.function_call_arguments.delta':
                this.appendFunctionArguments(event);
                break;

            case 'response.function_call_arguments.done':
                void this.executeServerTool(event);
                break;

            case 'error':
                this.activeResponse = false;
                this.clearResponseWatchdog();
                if (this.isBenignResponseCancelError(event))
                {
                    this.emitDiagnostic('response.cancel.ignored', {
                        message: formatRealtimeError(event)
                    });
                    break;
                }

                this.options.onError?.(formatRealtimeError(event));
                this.emitStatus('Error');
                break;

            case 'response.done':
                this.activeResponse = false;
                this.clearResponseWatchdog();
                this.emitAssistantMessagesFromResponse(event);
                this.emitUsage(event);
                this.emitStatus('Listening');
                break;
        }
    }

    isBenignResponseCancelError(event)
    {
        const error = event?.error ?? event;
        return error?.code === 'response_cancel_not_active' ||
            error?.type === 'response_cancel_not_active' ||
            (typeof error?.message === 'string' &&
                error.message.toLowerCase().includes('no active response'));
    }

    emitUsage(event)
    {
        const response = event?.response;
        const responseId = response?.id;
        const usage = response?.usage;
        if (typeof responseId !== 'string' || !responseId || !usage)
        {
            return;
        }

        this.sendControl({
            type: 'event',
            event: {
                type: 'usage',
                data: {
                    responseId,
                    modelApiName: response?.model,
                    usage
                }
            }
        });
    }

    emitTranscriptionUsage(event)
    {
        const itemId = event?.item_id;
        const usage = event?.usage;
        if (typeof itemId !== 'string' || !itemId || !usage)
        {
            return;
        }

        this.sendControl({
            type: 'event',
            event: {
                type: 'transcription_usage',
                data: {
                    itemId,
                    contentIndex: event?.content_index,
                    usage
                }
            }
        });
    }

    startAudioLevelMonitor(stream)
    {
        this.stopAudioLevelMonitor();

        const AudioContextClass = window.AudioContext ?? window.webkitAudioContext;
        if (!AudioContextClass)
        {
            return;
        }

        try
        {
            const audioContext = new AudioContextClass();
            const source = audioContext.createMediaStreamSource(stream);
            const analyser = audioContext.createAnalyser();
            analyser.fftSize = 512;
            source.connect(analyser);

            const data = new Uint8Array(analyser.fftSize);
            const startedAt = performance.now();
            let frameCount = 0;
            let meaningfulFrameCount = 0;
            let lastLevelEmit = 0;
            let warnedSilent = false;

            const tick = () =>
            {
                analyser.getByteTimeDomainData(data);
                let peak = 0;
                for (let index = 0; index < data.length; index++)
                {
                    peak = Math.max(peak, Math.abs(data[index] - 128) / 128);
                }

                frameCount++;
                if (peak > 0.015)
                {
                    meaningfulFrameCount++;
                }

                const now = performance.now();
                if (this.options.emitMicrophoneLevelDiagnostics === true && now - lastLevelEmit > 1000)
                {
                    this.emitDiagnostic('microphone.level', {
                        peak,
                        frameCount,
                        meaningfulFrameCount
                    });
                    lastLevelEmit = now;
                }

                if (!warnedSilent && now - startedAt > 4000 && meaningfulFrameCount === 0)
                {
                    warnedSilent = true;
                    this.emitDiagnostic('microphone.silent', {
                        frameCount,
                        meaningfulFrameCount
                    });
                }

                this.audioMonitor.raf = window.requestAnimationFrame(tick);
            };

            this.audioMonitor = {
                audioContext,
                source,
                raf: window.requestAnimationFrame(tick)
            };
        }
        catch (error)
        {
            this.emitDiagnostic('microphone.monitor_failed', {
                message: error instanceof Error ? error.message : String(error)
            });
        }
    }

    stopAudioLevelMonitor()
    {
        if (!this.audioMonitor)
        {
            return;
        }

        if (this.audioMonitor.raf)
        {
            window.cancelAnimationFrame(this.audioMonitor.raf);
        }

        try
        {
            this.audioMonitor.source?.disconnect?.();
        }
        catch
        {
        }

        void this.audioMonitor.audioContext?.close?.();
        this.audioMonitor = null;
    }

    emitAssistantMessagesFromResponse(event)
    {
        const outputs = event.response?.output;
        if (!Array.isArray(outputs))
        {
            return;
        }

        for (const output of outputs)
        {
            if (output?.type !== 'message' || output?.role !== 'assistant' || !Array.isArray(output.content))
            {
                continue;
            }

            for (const part of output.content)
            {
                const text = part?.transcript ?? part?.text;
                if (text)
                {
                    this.emitChat('assistant', text);
                }
            }
        }
    }

    appendFunctionArguments(event)
    {
        const key = event.call_id ?? event.item_id ?? 'current';
        const previous = this.pendingFunctionArgs.get(key) ?? '';
        this.pendingFunctionArgs.set(key, previous + (event.delta ?? ''));
        if (event.name)
        {
            this.pendingFunctionNames.set(key, event.name);
        }
    }

    async executeServerTool(event)
    {
        const callId = event.call_id;
        const key = callId ?? event.item_id ?? 'current';
        const name = event.name ?? this.pendingFunctionNames.get(key);
        const argsText = event.arguments ?? this.pendingFunctionArgs.get(key) ?? '{}';

        this.pendingFunctionArgs.delete(key);
        this.pendingFunctionNames.delete(key);

        if (!callId || !name)
        {
            return;
        }

        this.emitChat('assistant', `Calling tool: ${name}`, name, 'call');
        let result;
        try
        {
            result = await this.requestServerTool(callId, name, argsText);
        }
        catch (error)
        {
            const message = error instanceof Error ? error.message : String(error);
            this.emitDiagnostic('tool_call.failed', {
                callId,
                name,
                message
            });
            result = `Error: ${message}`;
        }
        this.emitChat('tool', result, name, 'answer');

        this.sendRealtimeEvent({
            type: 'conversation.item.create',
            item: {
                type: 'function_call_output',
                call_id: callId,
                output: result
            }
        });
        this.sendRealtimeEvent({ type: 'response.create' });
    }

    requestServerTool(callId, name, argsText)
    {
        return new Promise((resolve, reject) =>
        {
            if (this.controlSocket?.readyState !== WebSocket.OPEN)
            {
                reject(new Error(`Control socket is not open for tool call: ${name}`));
                return;
            }

            const requestId = callId;
            let settled = false;
            let timeoutId = null;

            const cleanup = () =>
            {
                this.controlSocket?.removeEventListener('message', listener);
                this.controlSocket?.removeEventListener('close', onClose);
                this.controlSocket?.removeEventListener('error', onError);
                if (timeoutId)
                {
                    window.clearTimeout(timeoutId);
                }
            };

            const settle = (action, value) =>
            {
                if (settled)
                {
                    return;
                }

                settled = true;
                cleanup();
                action(value);
            };

            const listener = event =>
            {
                if (typeof event.data !== 'string')
                {
                    return;
                }

                const message = JSON.parse(event.data);
                if (message.type !== 'tool_result' || message.requestId !== requestId)
                {
                    return;
                }

                settle(resolve, message.result ?? '');
            };

            const onClose = () =>
            {
                settle(reject, new Error(`Control socket closed while waiting for tool result: ${name}`));
            };

            const onError = () =>
            {
                settle(reject, new Error(`Control socket error while waiting for tool result: ${name}`));
            };

            timeoutId = window.setTimeout(() =>
            {
                settle(reject, new Error(`Tool call timed out after ${TOOL_CALL_TIMEOUT_MS}ms: ${name}`));
            }, TOOL_CALL_TIMEOUT_MS);

            this.controlSocket?.addEventListener('message', listener);
            this.controlSocket?.addEventListener('close', onClose);
            this.controlSocket?.addEventListener('error', onError);
            this.sendControl({
                type: 'tool_call',
                requestId,
                action: name,
                args: safeJsonParse(argsText)
            });
        });
    }

    async handleControlMessage(message)
    {
        switch (message.type)
        {
            case 'status':
                this.emitStatus(message.status);
                break;

            case 'client_action_request':
                await this.handleClientActionRequest(message);
                break;

            case 'error':
                this.options.onError?.(message.message ?? 'Direct realtime error');
                break;
        }
    }

    async handleClientActionRequest(message)
    {
        const handler = this.clientActionHandlers.get(message.action);
        if (!handler)
        {
            this.sendControl({
                type: 'client_action_response',
                requestId: message.requestId,
                error: `No client action handler registered for ${message.action}`
            });
            return;
        }

        try
        {
            const result = await handler(message.args ?? {}, message);
            this.sendControl({
                type: 'client_action_response',
                requestId: message.requestId,
                result: result ?? {},
                declined: result?.declined === true
            });
        }
        catch (error)
        {
            this.sendControl({
                type: 'client_action_response',
                requestId: message.requestId,
                error: error instanceof Error ? error.message : String(error)
            });
        }
    }

    sendRealtimeEvent(event)
    {
        if (this.closed)
        {
            this.emitDiagnostic('realtime_event.dropped_closed', {
                eventType: event?.type
            });
            return;
        }

        if (this.dataChannel?.readyState === 'open')
        {
            this.dataChannel.send(JSON.stringify(event));
            return;
        }

        if (this.dataChannel?.readyState === 'connecting')
        {
            this.pendingRealtimeEvents.push(event);
            return;
        }

        if (!this.dataChannel)
        {
            this.pendingRealtimeEvents.push(event);
            return;
        }

        this.emitDiagnostic('realtime_event.dropped', {
            eventType: event?.type,
            readyState: this.dataChannel.readyState
        });
        this.handleConnectionLost('datachannel.not_open', `Realtime data channel is ${this.dataChannel.readyState}`);
    }

    armResponseWatchdog(event)
    {
        this.clearResponseWatchdog();
        const responseId = event?.response?.id ?? event?.response_id ?? null;
        this.responseWatchdog = window.setTimeout(() =>
        {
            if (!this.activeResponse)
            {
                return;
            }

            this.emitDiagnostic('response.watchdog_timeout', {
                responseId,
                timeoutMs: RESPONSE_WATCHDOG_MS
            });

            try
            {
                this.sendRealtimeEvent({ type: 'response.cancel' });
            }
            catch
            {
            }

            this.activeResponse = false;
            this.emitStatus('Listening');
        }, RESPONSE_WATCHDOG_MS);
    }

    clearResponseWatchdog()
    {
        if (!this.responseWatchdog)
        {
            return;
        }

        window.clearTimeout(this.responseWatchdog);
        this.responseWatchdog = null;
    }

    handleConnectionLost(type, message)
    {
        if (this.closed)
        {
            return;
        }

        this.activeResponse = false;
        this.clearResponseWatchdog();
        this.pendingRealtimeEvents = [];
        this.emitDiagnostic(type, { message });
        this.options.onError?.(message);
        this.emitStatus('Error');
    }

    flushPendingRealtimeEvents()
    {
        if (this.dataChannel?.readyState !== 'open')
        {
            return;
        }

        const pending = this.pendingRealtimeEvents.splice(0);
        for (const event of pending)
        {
            this.dataChannel.send(JSON.stringify(event));
        }
    }

    sendControl(message)
    {
        if (this.controlSocket?.readyState === WebSocket.OPEN)
        {
            this.controlSocket.send(JSON.stringify(message));
        }
    }

    emitStatus(status)
    {
        this.options.onStatus?.(status);
        this.sendControl({
            type: 'event',
            event: {
                type: 'status',
                content: status
            }
        });
    }

    emitConnectionPhase(phase, status)
    {
        this.emitStatus(status);
        this.emitDiagnostic('connection.phase', {
            phase,
            status
        });
    }

    emitChat(role, content, toolName, toolCallType)
    {
        if (!content)
        {
            return;
        }

        const chat = {
            role,
            content,
            toolName,
            toolCallType,
            timestamp: new Date().toISOString()
        };
        if (this.isDuplicateChat(chat))
        {
            return;
        }

        this.recentChats.push(chat);
        this.recentChats = this.recentChats.slice(-8);

        this.options.onChatMessage?.(chat);
        this.sendControl({
            type: 'event',
            event: {
                type: 'chat',
                role,
                content,
                toolName,
                toolCallType
            }
        });
    }

    isDuplicateChat(chat)
    {
        const chatTime = Date.parse(chat.timestamp);
        return this.recentChats.some(existing =>
        {
            if (existing.role !== chat.role
                || existing.toolName !== chat.toolName
                || existing.toolCallType !== chat.toolCallType
                || existing.content.trim() !== chat.content.trim())
            {
                return false;
            }

            const existingTime = Date.parse(existing.timestamp);
            if (!Number.isFinite(chatTime) || !Number.isFinite(existingTime))
            {
                return true;
            }

            return Math.abs(chatTime - existingTime) <= 10000;
        });
    }

    emitDiagnostic(type, detail)
    {
        const diagnostic = {
            type,
            detail,
            timestamp: new Date().toISOString()
        };
        this.options.onDiagnostic?.(diagnostic);
        this.sendControl({
            type: 'event',
            event: {
                type: 'diagnostic',
                content: type,
                data: detail
            }
        });
    }

    toWebSocketUrl(path)
    {
        if (path.startsWith('ws://') || path.startsWith('wss://'))
        {
            return path;
        }

        const absolute = new URL(path, window.location.href);
        absolute.protocol = absolute.protocol === 'https:' ? 'wss:' : 'ws:';
        return absolute.toString();
    }
}

export function createOpenAiDirectRealtimeClient(options, dotNetReference)
{
    return new OpenAiDirectRealtimeClient({
        sessionUrl: options?.sessionUrl ?? '/api/voice/direct/session',
        credentials: options?.credentials ?? 'include',
        rtcConfiguration: options?.rtcConfiguration,
        emitMicrophoneLevelDiagnostics: options?.emitMicrophoneLevelDiagnostics === true,
        headers: () => options?.headers ?? {},
        onStatus: status => invokeDotNet(dotNetReference, 'OnDirectRealtimeStatus', status),
        onError: error => invokeDotNet(dotNetReference, 'OnDirectRealtimeError', error),
        onChatMessage: chat => invokeDotNet(dotNetReference, 'OnDirectRealtimeChatMessage', JSON.stringify(chat)),
        onDiagnostic: diagnostic => invokeDotNet(dotNetReference, 'OnDirectRealtimeDiagnostic', JSON.stringify(diagnostic)),
        onRealtimeEvent: event =>
        {
            if (event?.type === 'input_audio_buffer.speech_started')
            {
                invokeDotNet(dotNetReference, 'OnDirectRealtimeSpeechStarted');
            }

            const usage = event?.response?.usage;
            if (event?.type === 'response.done' && usage)
            {
                invokeDotNet(dotNetReference, 'OnDirectRealtimeUsage', JSON.stringify(usage));
            }
        }
    });
}

function invokeDotNet(dotNetReference, methodName, ...args)
{
    try
    {
        const result = dotNetReference?.invokeMethodAsync?.(methodName, ...args);
        if (result?.catch)
        {
            result.catch(error => console.warn(`[OpenAI Direct] .NET callback failed: ${methodName}`, error));
        }
    }
    catch (error)
    {
        console.warn(`[OpenAI Direct] .NET callback failed: ${methodName}`, error);
    }
}

function safeJsonParse(text)
{
    try
    {
        return JSON.parse(text);
    }
    catch
    {
        return {};
    }
}

function formatRealtimeError(event)
{
    const error = event.error ?? event;
    return formatErrorValue(error) ?? 'OpenAI Realtime error';
}

async function formatSessionError(response)
{
    let text = '';
    try
    {
        text = await response.text();
    }
    catch
    {
        return '';
    }

    if (!text)
    {
        return '';
    }

    const json = safeJsonParse(text);
    const message = formatErrorValue(json.error) ?? formatErrorValue(json.message) ?? formatErrorValue(json.title) ?? text;
    return message ? ` - ${message}` : '';
}

function formatErrorValue(value)
{
    if (!value)
    {
        return '';
    }

    if (typeof value === 'string')
    {
        return value;
    }

    if (typeof value !== 'object')
    {
        return String(value);
    }

    const parts = [];
    const message = value.message;
    const code = value.code;
    const type = value.type;

    if (typeof message === 'string' && message.trim())
    {
        parts.push(message.trim());
    }

    if (typeof code === 'string' && code.trim())
    {
        parts.push(`code=${code.trim()}`);
    }

    if (typeof type === 'string' && type.trim())
    {
        parts.push(`type=${type.trim()}`);
    }

    if (parts.length > 0)
    {
        return parts.join(' ');
    }

    try
    {
        return JSON.stringify(value);
    }
    catch
    {
        return String(value);
    }
}
