using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TracciaStems.Audio
{
    public enum ExportFormat { Wav16, Wav24, Wav32Float, Mp3 }

    /// <summary>Export hors temps réel du mix (rendu exact, indépendant de la carte son).</summary>
    public static class Exporter
    {
        public const double ReverbTailSec = 2.5;

        public static void Export(
            IEnumerable<(StemData stem, TrackSettings settings)> tracks,
            MasterSettings master, int sampleRate,
            string outputPath, ExportFormat format, int mp3BitrateKbps,
            IProgress<double>? progress, CancellationToken ct)
        {
            if (format == ExportFormat.Mp3)
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"tracciastems_{Guid.NewGuid():N}.wav");
                try
                {
                    RenderToWav(tracks, master, sampleRate, tmp, ExportFormat.Wav16, p => progress?.Report(p * 0.85), ct);
                    ct.ThrowIfCancellationRequested();
                    WavToMp3(tmp, outputPath, mp3BitrateKbps);
                    progress?.Report(1.0);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            else
            {
                RenderToWav(tracks, master, sampleRate, outputPath, format, p => progress?.Report(p), ct);
            }
        }

        public static void RenderToWav(
            IEnumerable<(StemData stem, TrackSettings settings)> tracks,
            MasterSettings master, int sampleRate, string path, ExportFormat format,
            Action<double>? progress, CancellationToken ct)
        {
            var engine = new MixEngine(sampleRate, master) { Paused = false };
            engine.SetTracks(tracks);
            long total = engine.LengthFrames + (long)(ReverbTailSec * sampleRate);
            if (total <= 0) throw new InvalidOperationException("Aucun audio à exporter.");

            WaveFormat fmt = format switch
            {
                ExportFormat.Wav16 => new WaveFormat(sampleRate, 16, 2),
                ExportFormat.Wav32Float => WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
                _ => new WaveFormat(sampleRate, 24, 2),
            };

            var rnd = new Random(1234);
            bool dither = format == ExportFormat.Wav16;
            const float lsb = 1f / 32768f;

            try
            {
                using var writer = new WaveFileWriter(path, fmt);
                var buf = new float[4096 * 2];
                long done = 0;
                int lastPct = -1;
                while (done < total)
                {
                    ct.ThrowIfCancellationRequested();
                    int frames = (int)Math.Min(4096, total - done);
                    engine.Render(buf, 0, frames * 2);
                    for (int i = 0; i < frames * 2; i++)
                    {
                        float v = buf[i];
                        if (dither) v += (float)(rnd.NextDouble() - rnd.NextDouble()) * lsb; // dither TPDF
                        buf[i] = Math.Clamp(v, -1f, 1f);
                    }
                    writer.WriteSamples(buf, 0, frames * 2);
                    done += frames;
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct) { lastPct = pct; progress?.Invoke(done / (double)total); }
                }
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        }

        /// <summary>Encode un WAV en MP3 avec l'encodeur Media Foundation intégré à Windows.</summary>
        public static void WavToMp3(string wavPath, string mp3Path, int bitrateKbps)
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(wavPath);
            IWaveProvider input = reader;
            int sr = reader.WaveFormat.SampleRate;
            if (sr != 44100 && sr != 48000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            {
                // l'encodeur MP3 de Windows n'accepte que du 16 bits 44,1 / 48 kHz
                ISampleProvider sp = reader.ToSampleProvider();
                if (sr != 44100 && sr != 48000) sp = new WdlResamplingSampleProvider(sp, sr > 48000 ? 48000 : 44100);
                input = new SampleToWaveProvider16(sp);
            }
            if (File.Exists(mp3Path)) File.Delete(mp3Path);
            MediaFoundationEncoder.EncodeToMp3(input, mp3Path, bitrateKbps * 1000);
        }
    }
}
