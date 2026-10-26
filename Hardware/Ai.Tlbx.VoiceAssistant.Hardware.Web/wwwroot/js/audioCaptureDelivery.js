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
        this.tail = this.tail.then(async () => {
            if (this.failed) return;
            let timer;
            try {
                await Promise.race([
                    this.send(audio),
                    new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Microphone delivery acknowledgment timed out.')), this.timeoutMs); })
                ]);
            } catch {
                this.fail('Microphone audio delivery failed or timed out.');
            } finally {
                clearTimeout(timer);
            }
        }).finally(() => { this.pending--; });
    }

    async drain() {
        this.closed = true;
        await this.tail;
        return !this.failed;
    }
}
