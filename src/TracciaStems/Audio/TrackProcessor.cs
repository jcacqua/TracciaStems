using System;
using TracciaStems.Audio.Dsp;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Traitement d'une piste pour une instance de moteur : lecture (avec boucle), EQ, compresseur, volume/pan, envoi reverb.
    /// Chaque moteur (lecture temps réel, export) a ses propres processeurs, donc ses propres états DSP.
    /// </summary>
    public sealed class TrackProcessor
    {
        private const int LoopFadeFrames = 96; // ~2 ms : évite les clics au point de bouclage

        public StemData Stem { get; }
        public TrackSettings Settings { get; }

        private readonly ThreeBandEq _eq;
        private readonly Compressor _comp;
        private float[] _buf = new float[8192];
        private float _gainL, _gainR;     // gains effectifs du bloc précédent (pour rampes)
        private bool _first = true;

        // mesures pour l'interface (écrites par le thread audio)
        public volatile float PeakL, PeakR;
        public float GainReductionDb => _comp.GainReductionDb;

        public TrackProcessor(StemData stem, TrackSettings settings)
        {
            Stem = stem;
            Settings = settings;
            _eq = new ThreeBandEq(stem.SampleRate);
            _comp = new Compressor(stem.SampleRate);
        }

        /// <summary>
        /// Convertit une position du morceau (frames) en position dans le fichier source, en tenant compte de la boucle.
        /// La piste joue normalement jusqu'à la fin de boucle, puis répète la zone [début, fin[ indéfiniment.
        /// </summary>
        public static long MapPosition(long pos, bool loop, long loopStart, long loopEnd)
        {
            if (!loop || loopEnd <= loopStart || pos < loopEnd) return pos;
            long len = loopEnd - loopStart;
            return loopStart + (pos - loopStart) % len;
        }

        /// <summary>Stem répété : position dans le fichier = (position du morceau − phase) modulo la longueur.</summary>
        public static long MapRepeat(long pos, long phase, long len)
        {
            if (len <= 0) return pos;
            long r = (pos - phase) % len;
            return r < 0 ? r + len : r;
        }

        public void ResetDsp()
        {
            _eq.Reset();
            _comp.Reset();
        }

        public void GetLoopFrames(out bool loop, out long start, out long end)
        {
            var s = Settings;
            int sr = Stem.SampleRate;
            if (s.RepeatWholeFile && Stem.Frames >= 64)
            {
                // fichier entier répété depuis le début : même mécanique qu'une boucle [0, fin du fichier[
                loop = true; start = 0; end = Stem.Frames;
                return;
            }
            start = (long)Math.Round(Math.Max(0, s.LoopStartSec) * sr);
            end = (long)Math.Round(Math.Max(0, s.LoopEndSec) * sr);
            end = Math.Min(end, Stem.Frames);
            loop = s.LoopEnabled && end - start >= 64;
        }

        public void Process(long position, int frames, float[] mix, float[] send, bool anySolo)
        {
            if (_buf.Length < frames * 2) _buf = new float[frames * 2];
            var s = Settings;
            var src = Stem.Samples;
            long stemFrames = Stem.Frames;
            GetLoopFrames(out bool loop, out long ls, out long le);
            bool repeat = s.RepeatWholeFile && stemFrames >= 64;
            long phase = (long)Math.Round(s.RepeatPhaseSec * Stem.SampleRate);

            // 1) lecture avec boucle
            for (int i = 0; i < frames; i++)
            {
                if (repeat)
                {
                    long rp = MapRepeat(position + i, phase, stemFrames);
                    float rg = 1f;
                    if (rp < LoopFadeFrames) rg = rp / (float)LoopFadeFrames;
                    else if (stemFrames - rp < LoopFadeFrames) rg = (stemFrames - rp) / (float)LoopFadeFrames;
                    _buf[i * 2] = src[rp * 2] * rg;
                    _buf[i * 2 + 1] = src[rp * 2 + 1] * rg;
                    continue;
                }
                long p = position + i;
                long sp = MapPosition(p, loop, ls, le);
                if (sp < 0 || sp >= stemFrames)
                {
                    _buf[i * 2] = 0; _buf[i * 2 + 1] = 0;
                    continue;
                }
                float g = 1f;
                if (loop && sp >= ls)
                {
                    long toEnd = le - sp;
                    if (toEnd < LoopFadeFrames) g = toEnd / (float)LoopFadeFrames;
                    if (p >= le)
                    {
                        long fromStart = sp - ls;
                        if (fromStart < LoopFadeFrames) g = Math.Min(g, fromStart / (float)LoopFadeFrames);
                    }
                }
                long k = sp * 2;
                _buf[i * 2] = src[k] * g;
                _buf[i * 2 + 1] = src[k + 1] * g;
            }

            // 2) EQ
            _eq.Update(s.EqLowDb, s.EqMidDb, s.EqHighDb);
            _eq.Process(_buf, frames);

            // 3) compresseur
            if (s.CompEnabled)
                _comp.Process(_buf, frames, s.CompThresholdDb, s.CompRatio, s.CompAttackMs, s.CompReleaseMs, s.CompMakeupDb);
            else
                _comp.Reset();

            // 4) volume / pan (loi "balance", centre = 0 dB) / mute / solo avec rampe anti-zip
            bool silent = s.Mute || (anySolo && !s.Solo);
            float vol = silent ? 0f : TrackSettings.DbToGain(s.VolumeDb);
            float pan = Math.Clamp(s.Pan, -1f, 1f);
            float tl = vol * (pan <= 0 ? 1f : 1f - pan);
            float tr = vol * (pan >= 0 ? 1f : 1f + pan);
            if (_first) { _gainL = tl; _gainR = tr; _first = false; }
            float send0 = Math.Clamp(s.ReverbSend, 0f, 1f);

            float pkL = 0, pkR = 0;
            float dl = (tl - _gainL) / frames, dr = (tr - _gainR) / frames;
            float gl = _gainL, gr = _gainR;
            for (int i = 0; i < frames; i++)
            {
                gl += dl; gr += dr;
                int j = i * 2;
                float l = _buf[j] * gl, r = _buf[j + 1] * gr;
                mix[j] += l; mix[j + 1] += r;
                if (send0 > 0) { send[j] += l * send0; send[j + 1] += r * send0; }
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > pkL) pkL = al;
                if (ar > pkR) pkR = ar;
            }
            _gainL = tl; _gainR = tr;
            if (pkL > PeakL) PeakL = pkL;
            if (pkR > PeakR) PeakR = pkR;
        }
    }
}
