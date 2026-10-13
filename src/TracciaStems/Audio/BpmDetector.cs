using System;
using System.Threading;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Détection de tempo : enveloppe d'attaques par flux spectral, puis autocorrélation
    /// pondérée (avec harmoniques) sur la plage 60–200 BPM.
    /// </summary>
    public static class BpmDetector
    {
        private const int FftSize = 1024;
        private const int Hop = 128;
        public const double MinBpm = 60, MaxBpm = 200;

        public sealed record Result(double Bpm, double Confidence);

        /// <param name="interleaved">Audio stéréo entrelacé.</param>
        /// <param name="maxSeconds">Durée analysée au maximum (à partir du premier son non silencieux).</param>
        public static Result? Detect(float[] interleaved, int sampleRate, double maxSeconds = 120, CancellationToken ct = default)
        {
            // 1) mono + décimation éventuelle vers ~22 kHz (suffisant pour le rythme)
            long frames = interleaved.LongLength / 2;
            int decim = sampleRate >= 88200 ? 4 : sampleRate >= 44100 ? 2 : 1;
            double sr = (double)sampleRate / decim;

            // saute le silence initial
            long startFrame = 0;
            while (startFrame < frames && Math.Abs(interleaved[startFrame * 2]) + Math.Abs(interleaved[startFrame * 2 + 1]) < 1e-3f)
                startFrame++;
            long endFrame = Math.Min(frames, startFrame + (long)(maxSeconds * sampleRate));
            int n = (int)((endFrame - startFrame) / decim);
            if (n < FftSize * 8) return null;

            var mono = new float[n];
            for (int i = 0; i < n; i++)
            {
                long f = startFrame + (long)i * decim;
                float acc = 0;
                for (int d = 0; d < decim; d++) acc += interleaved[(f + d) * 2] + interleaved[(f + d) * 2 + 1];
                mono[i] = acc / (2 * decim);
            }

            // 2) flux spectral (log-magnitude, demi-onde)
            int nFrames = (n - FftSize) / Hop;
            var onset = new double[nFrames];
            var window = new double[FftSize];
            for (int i = 0; i < FftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize);
            var re = new double[FftSize];
            var im = new double[FftSize];
            int bins = FftSize / 2;
            int maxBin = Math.Min(bins, (int)(8000.0 / sr * FftSize)); // on ignore > 8 kHz
            var prev = new double[bins];
            for (int fr = 0; fr < nFrames; fr++)
            {
                if ((fr & 255) == 0) ct.ThrowIfCancellationRequested();
                int off = fr * Hop;
                for (int i = 0; i < FftSize; i++) { re[i] = mono[off + i] * window[i]; im[i] = 0; }
                Fft(re, im);
                double flux = 0;
                for (int b = 1; b < maxBin; b++)
                {
                    double mag = Math.Log(1 + 1000 * Math.Sqrt(re[b] * re[b] + im[b] * im[b]));
                    double diff = mag - prev[b];
                    if (diff > 0) flux += diff;
                    prev[b] = mag;
                }
                onset[fr] = flux;
            }

            // 3) normalisation : soustraction de la moyenne locale (~0,5 s), demi-onde
            double envRate = sr / Hop;
            int win = Math.Max(1, (int)(envRate * 0.25));
            var o = new double[nFrames];
            double sum = 0;
            int cnt = 0;
            // moyenne glissante centrée
            var prefix = new double[nFrames + 1];
            for (int i = 0; i < nFrames; i++) prefix[i + 1] = prefix[i] + onset[i];
            for (int i = 0; i < nFrames; i++)
            {
                int a = Math.Max(0, i - win), b = Math.Min(nFrames, i + win + 1);
                double mean = (prefix[b] - prefix[a]) / (b - a);
                o[i] = Math.Max(0, onset[i] - mean);
                sum += o[i]; cnt++;
            }
            if (sum <= 1e-9) return null;

            // 4) autocorrélation (retards jusqu'à ~6 s) sur l'enveloppe centrée
            double meanO = sum / cnt;
            for (int i = 0; i < nFrames; i++) o[i] -= meanO;
            int maxLag = Math.Min((int)(envRate * 6.0), nFrames / 2);
            var ac = new double[maxLag + 2];
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double s = 0;
                for (int i = lag; i < nFrames; i++) s += o[i] * o[i - lag];
                ac[lag] = s / (nFrames - lag);
            }
            ct.ThrowIfCancellationRequested();

            // 5) score par tempo : moyenne de l'autocorrélation sur tous les multiples de la période
            //    (jusqu'à 6 s) -> les longues périodes rendent l'estimation très précise.
            //    Léger a priori log-normal autour de 120 BPM pour départager les octaves.
            double Score(double bpm)
            {
                double period = envRate * 60.0 / bpm;
                double acc = 0; int k = 0;
                for (double lag = period; lag < maxLag; lag += period) { acc += Interp(ac, lag); k++; }
                return k == 0 ? 0 : acc / k;
            }
            double bestBpm = 0, bestScore = double.MinValue;
            for (double bpm = MinBpm; bpm <= MaxBpm; bpm += 0.02)
            {
                double prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120.0) / 1.0, 2));
                double score = Score(bpm) * (0.6 + 0.4 * prior);
                if (score > bestScore) { bestScore = score; bestBpm = bpm; }
            }
            if (bestBpm <= 0 || bestScore <= 0) return null;

            // correction d'octave : un tempo < 78 BPM est le plus souvent un demi-tempo
            // (caisse claire sur 2 et 4) ; on double si le tempo double est aussi bien soutenu.
            if (bestBpm < 78 && bestBpm * 2 <= MaxBpm)
            {
                double best2 = bestBpm * 2, s2 = double.MinValue;
                for (double b = bestBpm * 2 - 0.5; b <= bestBpm * 2 + 0.5; b += 0.02)
                {
                    double sc = Score(b);
                    if (sc > s2) { s2 = sc; best2 = b; }
                }
                if (s2 >= 0.5 * Score(bestBpm)) { bestBpm = best2; bestScore = s2; }
            }

            // 6) arrondi : la plupart des morceaux produits en DAW ont un tempo entier
            double rounded = Math.Round(bestBpm);
            double result = Math.Abs(rounded - bestBpm) <= 0.3 ? rounded : Math.Round(bestBpm, 1);

            // confiance : rapport entre le score retenu et le score moyen sur la plage
            double meanScore = 0; int ns = 0;
            for (double bpm = MinBpm; bpm <= MaxBpm; bpm += 1) { meanScore += Math.Max(0, Score(bpm)); ns++; }
            meanScore /= ns;
            double conf = meanScore > 0 ? Math.Clamp((bestScore / meanScore - 1) / 4, 0, 1) : 1;
            return new Result(result, conf);
        }

        // =====================================================================
        //  Grille rythmique : position des temps et du 1er temps de mesure
        // =====================================================================

        /// <param name="BeatOffsetSec">Position (s) d'un temps, ramenée dans [0, durée d'un temps[.</param>
        /// <param name="DownbeatOffsetSec">Position (s) d'un 1er temps de mesure, ramenée dans [0, durée d'une mesure[.</param>
        public sealed record GridResult(double Bpm, double BeatOffsetSec, double DownbeatOffsetSec, double Confidence);

        /// <summary>
        /// Calibrage : décalage entre l'instant d'une attaque et la trame d'enveloppe où le flux culmine
        /// (mesuré sur des attaques synthétiques ; dépend de FftSize/Hop).
        /// </summary>
        internal const double OnsetLatencySec = -0.017;

        /// <summary>
        /// Trouve la phase de la grille (temps et 1er temps de mesure) pour un tempo connu.
        /// Le 1er temps est choisi là où les attaques graves (grosse caisse) sont les plus fortes.
        /// </summary>
        public static GridResult? DetectGrid(float[] interleaved, int sampleRate, double bpm, int beatsPerBar, double maxSeconds = 120, CancellationToken ct = default)
        {
            if (bpm <= 0) return null;
            if (!ComputeEnvelopes(interleaved, sampleRate, maxSeconds, ct, out var full, out var low, out double envRate)) return null;
            int nFrames = full.Length;
            double period = envRate * 60.0 / bpm;
            if (nFrames < period * 4) return null;

            // phase des temps : somme de l'enveloppe aux instants φ + k·période
            double bestPhi = 0, bestScore = double.MinValue, meanScore = 0; int nPhi = 0;
            for (double phi = 0; phi < period; phi += 0.25)
            {
                double sc = 0;
                for (double t = phi; t < nFrames - 1; t += period) sc += Interp(full, t);
                if (sc > bestScore) { bestScore = sc; bestPhi = phi; }
                meanScore += sc; nPhi++;
            }
            meanScore /= Math.Max(1, nPhi);
            // affinage par barycentre autour du maximum
            {
                double num = 0, den = 0;
                for (double d = -1.0; d <= 1.0; d += 0.125)
                {
                    double phi = bestPhi + d, sc = 0;
                    for (double t = phi; t < nFrames - 1; t += period) if (t >= 0) sc += Interp(full, t);
                    double w = Math.Max(0, sc - meanScore);
                    num += w * phi; den += w;
                }
                if (den > 0) bestPhi = num / den;
            }

            // 1er temps : le temps de la mesure où les attaques graves sont les plus fortes
            int bpb = Math.Max(1, beatsPerBar);
            int bestK = 0; double bestLow = double.MinValue;
            for (int k = 0; k < bpb; k++)
            {
                double sc = 0;
                for (double t = bestPhi + k * period; t < nFrames - 1; t += period * bpb) if (t >= 0) sc += Interp(low, t);
                if (sc > bestLow + 1e-12) { bestLow = sc; bestK = k; }
            }

            double beatSec = 60.0 / bpm, barSec = beatSec * bpb;
            double beatTime = bestPhi / envRate - OnsetLatencySec;
            double downTime = (bestPhi + bestK * period) / envRate - OnsetLatencySec;
            double conf = meanScore > 0 ? Math.Clamp((bestScore / meanScore - 1) / 2, 0, 1) : 0;
            return new GridResult(bpm, Mod(beatTime, beatSec), Mod(downTime, barSec), conf);
        }

        private static double Mod(double x, double m) => m <= 0 ? 0 : ((x % m) + m) % m;

        /// <summary>
        /// Enveloppes d'attaques (flux spectral) : <paramref name="full"/> toutes fréquences (&lt; 8 kHz),
        /// <paramref name="low"/> graves (&lt; 150 Hz). L'indice i correspond à l'instant i / envRate depuis le début du fichier.
        /// </summary>
        private static bool ComputeEnvelopes(float[] interleaved, int sampleRate, double maxSeconds, CancellationToken ct,
            out double[] full, out double[] low, out double envRate)
        {
            full = low = Array.Empty<double>();
            long frames = interleaved.LongLength / 2;
            int decim = sampleRate >= 88200 ? 4 : sampleRate >= 44100 ? 2 : 1;
            double sr = (double)sampleRate / decim;
            envRate = sr / Hop;
            long endFrame = Math.Min(frames, (long)(maxSeconds * sampleRate));
            int n = (int)(endFrame / decim);
            if (n < FftSize * 8) return false;

            // on préfixe une demi-fenêtre de silence : la trame i est alors centrée sur l'instant i·Hop
            int pad = FftSize / 2;
            var mono = new float[n + pad];
            for (int i = 0; i < n; i++)
            {
                long f = (long)i * decim;
                float acc = 0;
                for (int d = 0; d < decim; d++) acc += interleaved[(f + d) * 2] + interleaved[(f + d) * 2 + 1];
                mono[pad + i] = acc / (2 * decim);
            }

            int nFrames = (n + pad - FftSize) / Hop;
            var onF = new double[nFrames];
            var onL = new double[nFrames];
            var window = new double[FftSize];
            for (int i = 0; i < FftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize);
            var re = new double[FftSize];
            var im = new double[FftSize];
            int bins = FftSize / 2;
            int maxBin = Math.Min(bins, (int)(8000.0 / sr * FftSize));
            int lowBin = Math.Max(2, (int)(150.0 / sr * FftSize));
            var prev = new double[bins];
            var prevLin = new double[lowBin + 1];
            for (int fr = 0; fr < nFrames; fr++)
            {
                if ((fr & 255) == 0) ct.ThrowIfCancellationRequested();
                int off = fr * Hop;
                for (int i = 0; i < FftSize; i++) { re[i] = mono[off + i] * window[i]; im[i] = 0; }
                Fft(re, im);
                double fl = 0, lo = 0;
                for (int b = 1; b < maxBin; b++)
                {
                    double lin = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                    double mag = Math.Log(1 + 1000 * lin);
                    double diff = mag - prev[b];
                    if (diff > 0) fl += diff;
                    prev[b] = mag;
                    // graves : magnitude linéaire (le log écraserait l'écart entre grosse caisse forte et faible)
                    if (b <= lowBin)
                    {
                        double dl = lin - prevLin[b];
                        if (dl > 0) lo += dl;
                        prevLin[b] = lin;
                    }
                }
                onF[fr] = fl; onL[fr] = lo;
            }
            full = Detrend(onF, envRate);
            low = Detrend(onL, envRate);
            return true;
        }

        /// <summary>Soustraction de la moyenne locale (±0,25 s) puis demi-onde.</summary>
        private static double[] Detrend(double[] x, double envRate)
        {
            int n = x.Length, win = Math.Max(1, (int)(envRate * 0.25));
            var prefix = new double[n + 1];
            for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + x[i];
            var o = new double[n];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - win), b = Math.Min(n, i + win + 1);
                o[i] = Math.Max(0, x[i] - (prefix[b] - prefix[a]) / (b - a));
            }
            return o;
        }

        private static double Interp(double[] a, double x)
        {
            int i = (int)x;
            if (i + 1 >= a.Length) return a[a.Length - 1];
            double f = x - i;
            return a[i] * (1 - f) + a[i + 1] * f;
        }

        /// <summary>FFT radix-2 en place.</summary>
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = t;
                    }
                }
            }
        }
    }
}
