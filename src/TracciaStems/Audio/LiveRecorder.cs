using System;
using System.Collections.Concurrent;
using System.Threading;
using NAudio.Wave;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Enregistre en direct ce qui sort du master (y compris les mouvements de faders faits pendant la lecture).
    /// Le thread audio ne fait qu'une copie mémoire ; l'écriture disque se fait sur un thread séparé.
    /// </summary>
    public sealed class LiveRecorder : IDisposable
    {
        private readonly BlockingCollection<float[]> _queue = new(new ConcurrentQueue<float[]>());
        private readonly Thread _thread;
        private readonly WaveFileWriter _writer;
        private long _samplesWritten;

        public string WavPath { get; }
        public int SampleRate { get; }
        public double RecordedSeconds => Interlocked.Read(ref _samplesWritten) / 2.0 / SampleRate;

        public LiveRecorder(string wavPath, int sampleRate, int bitsPerSample = 24)
        {
            WavPath = wavPath;
            SampleRate = sampleRate;
            var fmt = bitsPerSample == 32
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)
                : new WaveFormat(sampleRate, bitsPerSample, 2);
            _writer = new WaveFileWriter(wavPath, fmt);
            _thread = new Thread(WriterLoop) { IsBackground = true, Name = "LiveRecorder" };
            _thread.Start();
        }

        /// <summary>Appelé depuis le thread audio.</summary>
        public void Write(float[] buffer, int offset, int count)
        {
            if (_queue.IsAddingCompleted) return;
            var copy = new float[count];
            // copie élément par élément : le tampon de la carte son est un byte[] vu comme float[]
            for (int i = 0; i < count; i++) copy[i] = buffer[offset + i];
            try { _queue.Add(copy); } catch (InvalidOperationException) { }
        }

        private void WriterLoop()
        {
            foreach (var block in _queue.GetConsumingEnumerable())
            {
                for (int i = 0; i < block.Length; i++)
                    block[i] = Math.Clamp(block[i], -1f, 1f);
                _writer.WriteSamples(block, 0, block.Length);
                Interlocked.Add(ref _samplesWritten, block.Length);
            }
        }

        /// <summary>Termine l'écriture et ferme le fichier.</summary>
        public void Stop()
        {
            if (_queue.IsAddingCompleted) return;
            _queue.CompleteAdding();
            _thread.Join();
            _writer.Dispose();
        }

        public void Dispose() => Stop();
    }
}
