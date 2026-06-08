const REALTIME_CALLS_URL = 'https://api.openai.com/v1/realtime/calls';

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
        this.clientActionHandlers = new Map();
        this.pendingFunctionArgs = new Map();
        this.pendingFunctionNames = new Map();
    }

    registerClientAction(action, handler)
    {
        this.clientActionHandlers.set(action, handler);
    }

    async start(config)
    {
        this.emitStatus('Connecting');
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
        await this.openControlSocket(this.session.controlUrl);
        await this.openRealtimePeerConnection(config);
        this.emitStatus('Connected');
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
            this.dataChannel.close();
            this.dataChannel = null;
        }

        if (this.controlSocket)
        {
            this.controlSocket.close();
            this.controlSocket = null;
        }

        if (this.peerConnection)
        {
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
                resolve();
            };

            this.controlSocket.onerror = () =>
            {
                window.clearTimeout(timeout);
                reject(new Error('Control socket error'));
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
        const audioElement = this.options.audioElement ?? document.createElement('audio');
        audioElement.autoplay = true;

        this.peerConnection = new RTCPeerConnection(this.options.rtcConfiguration);
        this.peerConnection.ontrack = event =>
        {
            audioElement.srcObject = event.streams[0];
            this.emitStatus('Speaking');
        };

        this.dataChannel = this.peerConnection.createDataChannel('oai-events');
        this.dataChannel.onmessage = event => this.handleRealtimeEvent(JSON.parse(event.data));
        this.dataChannel.onopen = () => this.emitStatus('Listening');
        this.dataChannel.onerror = () => this.emitStatus('Error');

        this.mediaStream = await navigator.mediaDevices.getUserMedia({
            audio: this.buildAudioConstraints(config)
        });

        for (const track of this.mediaStream.getAudioTracks())
        {
            this.peerConnection.addTrack(track, this.mediaStream);
        }

        const offer = await this.peerConnection.createOffer();
        await this.peerConnection.setLocalDescription(offer);

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
    }

    buildAudioConstraints(config)
    {
        const deviceId = config?.microphoneId ?? config?.deviceId;
        const audio = {
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true
        };

        if (deviceId)
        {
            audio.deviceId = { exact: deviceId };
        }

        return audio;
    }

    handleRealtimeEvent(event)
    {
        this.options.onRealtimeEvent?.(event);

        switch (event.type)
        {
            case 'input_audio_buffer.speech_started':
                this.emitStatus('Listening');
                break;

            case 'response.created':
                this.emitStatus('Processing');
                break;

            case 'response.audio.delta':
                this.emitStatus('Speaking');
                break;

            case 'conversation.item.input_audio_transcription.completed':
                this.emitChat('user', event.transcript ?? '');
                break;

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

            case 'response.done':
                this.emitStatus('Listening');
                break;
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
        const result = await this.requestServerTool(callId, name, argsText);
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
        return new Promise(resolve =>
        {
            const requestId = callId;
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

                this.controlSocket?.removeEventListener('message', listener);
                resolve(message.result ?? '');
            };

            this.controlSocket?.addEventListener('message', listener);
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
        if (this.dataChannel?.readyState === 'open')
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

    toWebSocketUrl(path)
    {
        if (path.startsWith('ws://') || path.startsWith('wss://'))
        {
            return path;
        }

        const absolute = new URL(path, this.options.sessionUrl);
        absolute.protocol = absolute.protocol === 'https:' ? 'wss:' : 'ws:';
        return absolute.toString();
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
    const message = json.error ?? json.message ?? json.title ?? text;
    return message ? ` - ${message}` : '';
}
