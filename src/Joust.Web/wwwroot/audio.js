// Plays the game's sound: each frame's interleaved stereo samples arrive by message and are
// played in order. Short of samples it plays silence; more than about 100 ms queued, it drops
// the oldest frame rather than let the delay grow.
class JoustFeed extends AudioWorkletProcessor {
    constructor() {
        super();
        this.queue = [];
        this.at = 0;
        this.port.onmessage = (e) => {
            this.queue.push(e.data);
            let queued = -this.at;
            for (const b of this.queue) queued += b.length;
            while (queued > sampleRate * 2 * 0.1 && this.queue.length > 1) {
                queued -= this.queue.shift().length - this.at;
                this.at = 0;
            }
        };
    }

    process(inputs, outputs) {
        const [left, right] = outputs[0];
        for (let i = 0; i < left.length; i++) {
            const b = this.queue[0];
            if (!b) { left[i] = right[i] = 0; continue; }
            left[i] = b[this.at];
            right[i] = b[this.at + 1];
            this.at += 2;
            if (this.at >= b.length) { this.queue.shift(); this.at = 0; }
        }
        return true;
    }
}

registerProcessor('joust-feed', JoustFeed);
