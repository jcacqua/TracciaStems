using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using TracciaStems.Audio.Dsp;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Moteur de mixage : somme des pistes + bus de réverbération + master (volume, limiteur).
    /// Utilisé en temps réel (lu par la carte son) ou hors temps réel (export).
    /// </summary>
    public sealed class MixEngine : ISampleProvider
    {
        private TrackProcessor[] _tracks = Array.Empty<TrackProcessor>();
        private readonly Freeverb _reverb;
        private readonly Limiter _limiter;
        private float[] _mix = new float[8192], _send = new float[8192], _wet = new float[8192];
        private long _position;
        private long _seekRequest = -1;
        private float _masterGain = -1;

        public WaveFormat WaveFormat { get; }
        public int SampleRate { get; }
        public MasterSettings Master { get; }

        /// <summary>Si vrai, Read() renvoie du silence sans avancer (pause).</summary>
        public volatile bool Paused = true;

        /// <summary>Copie optionnelle de la sortie (enregistrement en direct).</summary>
        public volatile LiveRecorder? Recorder;

        public volatile float PeakL, PeakR;
        public volatile bool Clipped;

        public MixEngine(int sampleRate, MasterSettings master)
        {
            SampleRate = sampleRate;
            Master = master;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            _reverb = new Freeverb(sampleRate);
            _limiter = new Limiter(sampleRate);
        }

        public IReadOnlyList<TrackProcessor> Tracks => _tracks;

        public void SetTracks(IEnumerable<(StemData stem, TrackSettings settings)> tracks)
        {
            // réutilise les processeurs existants pour garder les états DSP
            var old = _tracks;
            var list = new List<TrackProcessor>();
            foreach (var (stem, settings) in tracks)
            {
                var existing = old.FirstOrDefault(t => ReferenceEquals(t.Settings, settings) && ReferenceEquals(t.Stem, stem));
                list.Add(existing ?? new TrackProcessor(stem, settings));
            }
            Volatile.Write(ref _tracks, list.ToArray());
        }

        public long Position => Interlocked.Read(ref _position);
        public double PositionSec => (double)Position / SampleRate;

        public void Seek(long frame) => Interlocked.Exchange(ref _seekRequest, Math.Max(0, frame));

        /// <summary>Longueur du morceau en frames (stem le plus long, ou durée forcée).</summary>
        public long LengthFrames
        {
            get
            {
                if (Master.DurationOverrideSec > 0) return (long)(Master.DurationOverrideSec * SampleRate);
                var t = Volatile.Read(ref _tracks);
                if (t.Length == 0) return 0;
                // les stems répétés ne fixent pas la durée, sauf s'il n'y a qu'eux
                var normal = t.Where(x => !x.Settings.RepeatWholeFile).ToArray();
                return (normal.Length > 0 ? normal : t).Max(x => x.Stem.Frames);
            }
        }

        /// <summary>Remet à zéro les états internes (queue de reverb, filtres, compresseurs).</summary>
        public void ResetDsp()
        {
            _reverb.Reset();
            _limiter.Reset();
            foreach (var t in Volatile.Read(ref _tracks)) t.ResetDsp();
        }

        // Fondu anti-clic (~8 ms) appliqué à la pause, à la reprise et lors des sauts de position.
        private float _fade;            // gain de fondu courant 0..1
        private const int ChunkFrames = 256;

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            float step = 1f / (0.008f * SampleRate);
            int done = 0;
            while (done < frames)
            {
                int n = Math.Min(ChunkFrames, frames - done);
                int o = offset + done * 2;
                bool seekPending = Interlocked.Read(ref _seekRequest) >= 0;
                float target = Paused || seekPending ? 0f : 1f;

                if (_fade <= 0f && target == 0f)
                {
                    // silence complet : on peut appliquer le saut sans clic
                    long seek = Interlocked.Exchange(ref _seekRequest, -1);
                    if (seek >= 0)
                    {
                        Interlocked.Exchange(ref _position, seek);
                        ResetDsp();
                        if (!Paused) continue;   // reprise immédiate (fondu d'entrée)
                    }
                    // ATTENTION : le tampon fourni par NAudio est un byte[] vu comme float[] (WaveBuffer).
                    // Array.Clear/Array.Copy y travaillent en octets -> on remplit à la main.
                    for (int i = 0; i < n * 2; i++) buffer[o + i] = 0f;
                    done += n;
                    continue;
                }

                Render(buffer, o, n * 2);
                if (_fade != 1f || target != 1f)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (_fade < target) _fade = Math.Min(target, _fade + step);
                        else if (_fade > target) _fade = Math.Max(target, _fade - step);
                        buffer[o + i * 2] *= _fade;
                        buffer[o + i * 2 + 1] *= _fade;
                    }
                }
                Recorder?.Write(buffer, o, n * 2);
                done += n;
            }
            return count;
        }

        /// <summary>Calcule count échantillons (stéréo entrelacé) à la position courante et avance.</summary>
        public void Render(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            if (_mix.Length < count)
            {
                _mix = new float[count]; _send = new float[count]; _wet = new float[count];
            }
            Array.Clear(_mix, 0, count);
            Array.Clear(_send, 0, count);

            var tracks = Volatile.Read(ref _tracks);
            bool anySolo = false;
            foreach (var t in tracks) if (t.Settings.Solo) { anySolo = true; break; }

            long pos = Position;
            foreach (var t in tracks)
                t.Process(pos, frames, _mix, _send, anySolo);

            // bus réverbération
            var m = Master;
            _reverb.SetParameters(Math.Clamp(m.ReverbRoom, 0f, 1f), Math.Clamp(m.ReverbDamp, 0f, 1f));
            _reverb.Process(_send, _wet, frames);
            float ret = TrackSettings.DbToGain(m.ReverbReturnDb);

            // master avec rampe de gain
            float target = TrackSettings.DbToGain(m.VolumeDb);
            if (_masterGain < 0) _masterGain = target;
            float g = _masterGain, dg = (target - _masterGain) / frames;
            for (int i = 0; i < count; i += 2)
            {
                g += dg;
                buffer[offset + i] = (_mix[i] + _wet[i] * ret) * g;
                buffer[offset + i + 1] = (_mix[i + 1] + _wet[i + 1] * ret) * g;
            }
            _masterGain = target;

            if (m.LimiterEnabled) _limiter.Process(buffer, offset, frames);

            float pl = 0, pr = 0;
            for (int i = 0; i < count; i += 2)
            {
                float l = Math.Abs(buffer[offset + i]), r = Math.Abs(buffer[offset + i + 1]);
                if (l > pl) pl = l;
                if (r > pr) pr = r;
            }
            if (pl > PeakL) PeakL = pl;
            if (pr > PeakR) PeakR = pr;
            if (pl > 1f || pr > 1f) Clipped = true;

            Interlocked.Add(ref _position, frames);
        }
    }
}
