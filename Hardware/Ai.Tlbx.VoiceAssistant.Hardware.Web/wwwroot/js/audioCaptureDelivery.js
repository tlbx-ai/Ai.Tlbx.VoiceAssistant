// Ordered, bounded .NET delivery. Drain acknowledges the final PCM callback, not only the worklet.
export class AudioCaptureDelivery {
    constructor(send, onError, { maxPending = 100, timeoutMs = 2000 } = {}) {
        this.send = send;
        this.onError = onError;
        this.maxPending = maxPending;
        this.timeoutMs = timeoutMs;
        this.pending = 0;
        this.failed = false;
        this.closed = false;
        this.tail = Promise.resolve();
        this.queue = [];
        this.sending = false;
    }

    fail(message) {
        if (this.failed) return;
        this.failed = true;
        this.onError(message);
    }

    enqueue(audio) {
        if (this.closed || this.failed) return;
        if (this.pending >= this.maxPending) {
            this.fail('Microphone delivery queue is full.');
            return;
        }
        this.pending++;
        this.queue.push(audio);
        if (!this.sending) {
            this.sending = true;
            this.tail = Promise.resolve().then(() => this.flush());
        }
    }

    async flush() {
        try {
            while (this.queue.length && !this.failed) {
                // One acknowledged callback per 20 ms frame drifts whenever the
                // round trip exceeds 20 ms. Catch up by joining already queued
                // PCM, without a batching timer or concurrent interop calls.
                // Cap each callback at ten frames (200 ms at the capture rate).
                const batch = this.queue.splice(0, 10);
                let timer;
                try {
                    const audio = batch.length === 1 ? batch[0] : btoa(batch.map(chunk => atob(chunk)).join(''));
                    await Promise.race([
                        this.send(audio),
                        new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Microphone delivery acknowledgment timed out.')), this.timeoutMs); })
                    ]);
                } catch {
                    this.fail('Microphone audio delivery failed or timed out.');
                } finally {
                    clearTimeout(timer);
                    this.pending -= batch.length;
                }
            }
        } finally {
            this.pending -= this.queue.length;
            this.queue = [];
            this.sending = false;
        }
    }

    async drain() {
        this.closed = true;
        await this.tail;
        return !this.failed;
    }
}
