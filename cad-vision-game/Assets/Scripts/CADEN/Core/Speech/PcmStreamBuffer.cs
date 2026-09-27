#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Speech
{
    // Bounded mono PCM16 FIFO. Network producer and Unity audio-thread consumer.
    public sealed class PcmStreamBuffer
    {
        private readonly object gate = new object();
        private readonly float[] samples = new float[24000 * 10];
        private int head, count, lowByte = -1;
        private bool completed;
        public int Count { get { lock (gate) return count; } }
        public bool Drained { get { lock (gate) return completed && count == 0; } }
        public async Task AppendAsync(byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (completed) throw new InvalidOperationException("PCM buffer already completed.");
                    while (offset < bytes.Length && count < samples.Length)
                    {
                        if (lowByte < 0) lowByte = bytes[offset++];
                        else
                        {
                            samples[(head + count) % samples.Length] = (short)(lowByte | bytes[offset++] << 8) / 32768f;
                            lowByte = -1; count++;
                        }
                    }
                }
                if (offset < bytes.Length) await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
        public void Complete()
        {
            lock (gate)
            {
                if (lowByte >= 0) throw new InvalidDataException("ELEVENLABS_INVALID_AUDIO: truncated PCM sample.");
                completed = true;
            }
        }
        public void Read(float[] output)
        {
            lock (gate)
            {
                int take = Math.Min(output.Length, count);
                for (int i = 0; i < take; i++) { output[i] = samples[head]; head = (head + 1) % samples.Length; }
                count -= take;
                Array.Clear(output, take, output.Length - take);
            }
        }
    }
}
