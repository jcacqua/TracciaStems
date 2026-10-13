using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Audio d'un stem entièrement chargé en mémoire (stéréo entrelacé, float, à la fréquence du projet).
    /// Le chargement complet rend la lecture, les boucles, le déplacement et la détection de BPM instantanés.
    /// </summary>
    public sealed class StemData
    {
        public const int PeakBlock = 256;

        public string FilePath { get; }
        public float[] Samples { get; }
        public int SampleRate { get; }
        public long Frames => Samples.LongLength / 2;
        public double DurationSec => (double)Frames / SampleRate;

        /// <summary>Crête absolue (L/R confondus) par bloc de <see cref="PeakBlock"/> frames, pour l'affichage.</summary>
        public float[] Peaks { get; }

        public StemData(string filePath, float[] samples, int sampleRate)
        {
            FilePath = filePath;
            Samples = samples;
            SampleRate = sampleRate;
            Peaks = ComputePeaks(samples);
        }

        /// <summary>Lit la fréquence d'échantillonnage d'un fichier sans le charger.</summary>
        public static int ProbeSampleRate(string path)
        {
            using var reader = new AudioFileReader(path);
            return reader.WaveFormat.SampleRate;
        }

        /// <summary>Charge un WAV/MP3 (ou tout format lisible par Windows), convertit en stéréo et rééchantillonne si besoin.</summary>
        public static StemData Load(string path, int targetSampleRate)
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider sp = reader;
            if (sp.WaveFormat.Channels == 1)
                sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > 2)
                sp = new FirstTwoChannelsProvider(sp);
            if (sp.WaveFormat.SampleRate != targetSampleRate)
                sp = new WdlResamplingSampleProvider(sp, targetSampleRate);

            // estimation de la taille pour limiter les réallocations
            long estFrames = (long)(reader.TotalTime.TotalSeconds * targetSampleRate) + targetSampleRate;
            var buffer = new float[Math.Max(4096, estFrames * 2)];
            long written = 0;
            var chunk = new float[targetSampleRate * 2];
            int read;
            while ((read = sp.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (written + read > buffer.LongLength)
                {
                    var bigger = new float[Math.Max(buffer.LongLength * 2, written + read)];
                    Array.Copy(buffer, bigger, written);
                    buffer = bigger;
                }
                Array.Copy(chunk, 0, buffer, written, read);
                written += read;
            }
            written -= written % 2;
            if (written != buffer.LongLength)
                Array.Resize(ref buffer, (int)written);
            return new StemData(path, buffer, targetSampleRate);
        }

        private static float[] ComputePeaks(float[] s)
        {
            long frames = s.LongLength / 2;
            int n = (int)((frames + PeakBlock - 1) / PeakBlock);
            var peaks = new float[n];
            for (int b = 0; b < n; b++)
            {
                long start = (long)b * PeakBlock * 2;
                long end = Math.Min(s.LongLength, start + PeakBlock * 2);
                float m = 0;
                for (long i = start; i < end; i++)
                {
                    float v = Math.Abs(s[i]);
                    if (v > m) m = v;
                }
                peaks[b] = m;
            }
            return peaks;
        }

        /// <summary>Ne garde que les deux premiers canaux d'un fichier multicanal.</summary>
        private sealed class FirstTwoChannelsProvider : ISampleProvider
        {
            private readonly ISampleProvider _src;
            private readonly int _ch;
            private float[] _tmp = Array.Empty<float>();
            public WaveFormat WaveFormat { get; }
            public FirstTwoChannelsProvider(ISampleProvider src)
            {
                _src = src; _ch = src.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
            }
            public int Read(float[] buffer, int offset, int count)
            {
                int frames = count / 2;
                int need = frames * _ch;
                if (_tmp.Length < need) _tmp = new float[need];
                int got = _src.Read(_tmp, 0, need) / _ch;
                for (int i = 0; i < got; i++)
                {
                    buffer[offset + i * 2] = _tmp[i * _ch];
                    buffer[offset + i * 2 + 1] = _tmp[i * _ch + 1];
                }
                return got * 2;
            }
        }
    }
}
