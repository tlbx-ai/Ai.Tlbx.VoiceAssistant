// audio-processor.js
// AudioRecorderProcessor: captures at the browser's real AudioWorklet sampleRate,
// applies a light anti-aliasing filter, and resamples to provider PCM16 rate.

class AudioRecorderProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();

        const requestedTargetRate = Number(options?.processorOptions?.targetSampleRate || 24000);
        this.targetRate = normalizeTargetRate(requestedTargetRate);
        this.sourceRate = sampleRate;
        this.resampleStep = this.sourceRate / this.targetRate;

        console.log(`[AudioProcessor] ${this.sourceRate}Hz -> ${this.targetRate}Hz PCM16 (step ${this.resampleStep.toFixed(6)})`);

        const targetChunkMs = 80;
        const bufferSize = Math.max(320, Math.round(this.targetRate * targetChunkMs / 1000));
        this.buffer = new Int16Array(bufferSize);
        this.bufferIndex = 0;
        this.isActive = true;
        this.isStopping = false;
        this.stopCountdown = 0;

        const cutoff = Math.min(this.targetRate * 0.45, this.sourceRate * 0.45);
        const rc = 1 / (2 * Math.PI * cutoff);
        const dt = 1 / this.sourceRate;
        this.lowPassAlpha = dt / (rc + dt);
        this.lowPassState = 0;
        this.resampleBuffer = [];
        this.sourceCursor = 0;

        this.port.onmessage = (event) => {
            if (event.data?.command === 'stop') {
                console.log("[AudioProcessor] Received stop command");
                this.isStopping = true;
                this.stopCountdown = 3;
            }
        };
    }

    applyAntiAliasingFilter(sample) {
        this.lowPassState += this.lowPassAlpha * (sample - this.lowPassState);
        return this.lowPassState;
    }

    emitPcmSample(sample) {
        const clamped = Math.max(-1, Math.min(1, sample));
        const pcmValue = clamped < 0 ? clamped * 32768 : clamped * 32767;
        this.buffer[this.bufferIndex++] = Math.round(pcmValue);

        if (this.bufferIndex >= this.buffer.length) {
            this.port.postMessage({
                audioData: this.buffer.slice(0)
            });
            this.bufferIndex = 0;
        }
    }

    resampleInput(input) {
        for (let i = 0; i < input.length; i++) {
            this.resampleBuffer.push(this.applyAntiAliasingFilter(input[i]));
        }

        while (this.sourceCursor + 1 < this.resampleBuffer.length) {
            const index = Math.floor(this.sourceCursor);
            const fraction = this.sourceCursor - index;
            const current = this.resampleBuffer[index];
            const next = this.resampleBuffer[index + 1];
            this.emitPcmSample(current + ((next - current) * fraction));
            this.sourceCursor += this.resampleStep;
        }

        const dropCount = Math.max(0, Math.floor(this.sourceCursor) - 1);
        if (dropCount > 0) {
            this.resampleBuffer = this.resampleBuffer.slice(dropCount);
            this.sourceCursor -= dropCount;
        }
    }

    process(inputs, outputs) {
        // Handle graceful shutdown
        if (this.isStopping) {
            if (this.stopCountdown > 0) {
                this.stopCountdown--;
                if (this.bufferIndex > 0 && this.stopCountdown === 0) {
                    this.port.postMessage({
                        audioData: this.buffer.slice(0, this.bufferIndex)
                    });
                    this.bufferIndex = 0;
                }
            } else {
                console.log("[AudioProcessor] Graceful shutdown complete");
                this.port.postMessage({ stopped: true });
                this.isActive = false;
                return false;
            }
        }

        const input = inputs[0]?.[0];
        if (!input || input.length === 0) {
            return true;
        }

        this.resampleInput(input);

        return true;
    }
}

function normalizeTargetRate(value) {
    if (value === 16000 || value === 24000 || value === 44100 || value === 48000) {
        return value;
    }

    console.warn(`[AudioProcessor] Unsupported target rate: ${value}Hz. Using 24000Hz.`);
    return 24000;
}

registerProcessor('audio-recorder-processor', AudioRecorderProcessor);

// --- Playback Processor ---
// Receives 24kHz audio, upsamples to 48kHz with linear interpolation

const BUFFER_SIZE = 8640000; // ~180s (3 min) at 48kHz – ~34MB, prevents overflow on very long responses
const CROSSFADE_SAMPLES = 256; // Number of samples for crossfade (doubled for 48kHz)
const MIN_START_BUFFER = 9600; // ~200 ms @ 48 kHz – buffer before starting playback

class PlaybackProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this._buffer = new Float32Array(BUFFER_SIZE);
        this._writeIndex = 0;
        this._readIndex = 0;
        this._bufferFill = 0; // How many samples are currently in the buffer
        this._isPlaying = false; // Start paused until data arrives
        this._isStopping = false; // Flag to indicate stop request

        // Upsampling state: linear interpolation from 24kHz to 48kHz
        this._lastInputSample = 0; // Last sample from previous chunk for interpolation

        // Crossfading state
        this._crossfadeBuffer = new Float32Array(CROSSFADE_SAMPLES).fill(0);
        this._isCrossfadingIn = false;
        this._crossfadeIndex = 0;

        // Audio enhancement state
        this._prevSample = 0; // For interpolation
        this._eqHistory = new Float32Array(4).fill(0); // For simple EQ
        this._noiseGate = 0.002; // Simple noise gate threshold
        this._enhancementEnabled = false; // Disabled by default for stability

        this.port.onmessage = (event) => {
            if (event.data.command === 'stop') {
                console.log('[PlaybackProcessor] Received stop command');
                this._isStopping = true; // Signal to stop after buffer drains or immediately if forced
                // Option: Clear buffer immediately? Depends on desired stop behavior.
                // this._readIndex = this._writeIndex;
                // this._bufferFill = 0;
                // this._isPlaying = false;
            } else if (event.data.command === 'clear') {
                 console.log('[PlaybackProcessor] Received clear command');
                 this._readIndex = this._writeIndex;
                 this._bufferFill = 0;
                 this._isPlaying = false;
                 this._isStopping = false;
                 this._crossfadeIndex = 0;
                 this._isCrossfadingIn = false;
                 this._crossfadeBuffer.fill(0);
                 this._lastInputSample = 0; // Reset upsampling state
                 this._buffer.fill(0); // Clear the entire buffer
            } else if (event.data.command === 'setEnhancement') {
                this._enhancementEnabled = !!event.data.enabled;
                console.log('[PlaybackProcessor] Audio enhancement:', this._enhancementEnabled ? 'enabled' : 'disabled');
            }
             else if (event.data.audioData) {
                this._handleAudioData(event.data.audioData);
                // Start playing only when we have a bit of buffered audio to avoid underruns
                if (!this._isPlaying && this._bufferFill >= MIN_START_BUFFER) {
                    this._isPlaying = true;
                }
                this._isStopping = false; // Resume playing if stopped
            }
        };
    }

    _handleAudioData(audioData) {
        const inputData = audioData instanceof ArrayBuffer ? new Float32Array(audioData) : new Float32Array(audioData.buffer);

        // Upsample 24kHz → 48kHz (2:1) with linear interpolation
        // Each input sample produces 2 output samples
        const upsampledLength = inputData.length * 2;

        if (this._bufferFill + upsampledLength > BUFFER_SIZE) {
            console.warn('[PlaybackProcessor] Buffer overflow, dropping new data.');
            return; // skip this chunk to preserve already queued audio
        }

        // Prepare for crossfade-in if buffer was empty or starting fresh
        if (this._bufferFill === 0) {
             this._isCrossfadingIn = true;
             this._crossfadeIndex = 0;
             this._crossfadeBuffer.fill(0);
        }

        // Upsample with linear interpolation and copy into ring buffer
        // For each input sample at index i:
        //   - Output[2i] = original sample
        //   - Output[2i+1] = interpolated midpoint to next sample
        for (let i = 0; i < inputData.length; i++) {
            const curr = inputData[i];
            const prev = (i === 0) ? this._lastInputSample : inputData[i - 1];

            // Interpolated sample (midpoint between previous and current)
            const interpolated = (prev + curr) * 0.5;

            // Write interpolated sample first, then original
            this._buffer[this._writeIndex] = interpolated;
            this._writeIndex = (this._writeIndex + 1) % BUFFER_SIZE;

            this._buffer[this._writeIndex] = curr;
            this._writeIndex = (this._writeIndex + 1) % BUFFER_SIZE;
        }

        // Save last sample for interpolation with next chunk
        this._lastInputSample = inputData[inputData.length - 1];
        this._bufferFill += upsampledLength;
    }

    // Simple linear crossfade
    _applyCrossfade(sample, index, totalSamples, fadeIn, fadeOutBuffer) {
        const fadeOutGain = 1.0 - (index / totalSamples);
        const fadeInGain = index / totalSamples;
        return (fadeOutBuffer[index] * fadeOutGain) + (sample * fadeInGain);
    }
    
    // Audio enhancement: Simple interpolation for smoother playback
    _interpolate(currentSample) {
        // Linear interpolation between samples
        const interpolated = (this._prevSample + currentSample) * 0.5;
        this._prevSample = currentSample;
        return interpolated;
    }
    
    // Audio enhancement: Simple voice EQ (boosts mid frequencies for clarity)
    _applyVoiceEQ(sample) {
        // Simplified one-pole filter for voice enhancement
        // This is more stable and compatible
        const alpha = 0.15; // Filter coefficient
        const boost = 1.1; // Slight boost factor
        
        // Apply simple high-pass to remove DC and boost mids
        const filtered = sample - this._eqHistory[0];
        this._eqHistory[0] = this._eqHistory[0] + alpha * filtered;
        
        return sample + (filtered * boost * 0.3);
    }
    
    // Audio enhancement: Simple de-emphasis filter to reduce harshness
    _applyDeEmphasis(sample) {
        // Gentle high-frequency roll-off
        const alpha = 0.85;
        return sample * (1 - alpha) + this._prevSample * alpha;
    }
    
    // Audio enhancement: Process sample through enhancement chain
    _enhanceAudio(sample) {
        // Apply noise gate
        if (Math.abs(sample) < this._noiseGate) {
            sample = 0;
        }
        
        // Apply voice EQ for clarity
        sample = this._applyVoiceEQ(sample);
        
        // Apply de-emphasis to reduce harshness
        sample = this._applyDeEmphasis(sample);
        
        // Simple clipping protection
        if (sample > 1.0) {
            sample = 1.0;
        } else if (sample < -1.0) {
            sample = -1.0;
        }
        
        return sample;
    }


    process(inputs, outputs, parameters) {
        const output = outputs[0];
        const channel = output[0]; // mono

        // If we're stopped or have no channel, output silence
        if (!channel || this._isStopping) {
            if (channel) channel.fill(0);
            return true;
        }

        // Decide whether we should start or pause playback based on buffer level
        if (!this._isPlaying) {
            if (this._bufferFill >= MIN_START_BUFFER) {
                this._isPlaying = true; // Enough buffered, start playback
            } else {
                // Not enough buffered yet – output silence and wait
                if (channel) channel.fill(0);
                return true;
            }
        }

        if (channel === undefined) {
            return true;
        }

        let generatedSamples = 0;
        for (let i = 0; i < channel.length; i++) {
            if (this._bufferFill > 0) {
                let sample = this._buffer[this._readIndex];

                 // Apply crossfade-in if starting a new block after silence
                 if (this._isCrossfadingIn && this._crossfadeIndex < CROSSFADE_SAMPLES) {
                     sample = this._applyCrossfade(sample, this._crossfadeIndex, CROSSFADE_SAMPLES, true, this._crossfadeBuffer);
                     this._crossfadeIndex++;
                 } else {
                     this._isCrossfadingIn = false; // Done crossfading in
                 }


                // Apply audio enhancement if enabled
                if (this._enhancementEnabled) {
                    sample = this._enhanceAudio(sample);
                }
                
                channel[i] = sample;
                this._readIndex = (this._readIndex + 1) % BUFFER_SIZE;
                this._bufferFill--;
                generatedSamples++;


            } else {
                // Buffer underrun - fill with silence
                channel[i] = 0.0;
                 // Not enough data – pause playback until buffer refills
                 if (this._bufferFill < MIN_START_BUFFER) {
                     this._isPlaying = false;
                 }
                  // Store the last samples for potential crossfade next time
                 if (generatedSamples > 0) {
                     const start = (this._readIndex - Math.min(generatedSamples, CROSSFADE_SAMPLES) + BUFFER_SIZE) % BUFFER_SIZE;
                      for(let j = 0; j < CROSSFADE_SAMPLES; j++) {
                          this._crossfadeBuffer[j] = this._buffer[(start + j) % BUFFER_SIZE] ?? 0.0;
                      }
                 } else {
                     this._crossfadeBuffer.fill(0); // No previous samples, fade from silence
                 }

                // continue filling remaining samples with silence
                continue;
            }
        }
         // Fill remaining output buffer with silence if we stopped early due to underrun
         for (let i = generatedSamples; i < channel.length; i++) {
            channel[i] = 0.0;
         }


         // If stopping command received and buffer is now empty, request termination
         if (this._isStopping && this._bufferFill === 0) {
             console.log('[PlaybackProcessor] Stopping after draining buffer.');
             this._isPlaying = false;
             return false; // Request termination
         }


        return true; // Keep processor alive
    }
}

registerProcessor('playback-processor', PlaybackProcessor);
