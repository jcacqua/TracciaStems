using System;
using System.Threading;

namespace TracciaStems.Audio.Dsp
{
    /// <summary>
    /// Étirement temporel sans changement de hauteur (WSOLA : recouvrement-addition avec recherche
    /// du meilleur raccord par corrélation). Adapté aux variations de tempo modérées (±20 %).
    /// </summary>
    public static class TimeStretch
    {
        private const int N = 2048;          // taille de trame (~46 ms à 44,1 kHz)
        private const int Hs = N / 2;        // pas de synthèse (recouvrement 50 %)
        private const int Tol = 512;         // recherche du raccord : ±512 échantillons

        /// <summary>
        /// Étire une boucle (stéréo entrelacé) pour qu'elle dure exactement <paramref name="outFrames"/> frames,
        /// en gardant un raccord continu entre la fin et le début (traitement circulaire).
        /// </summary>
        public static float[] StretchLoop(float[] loop, long outFrames, CancellationToken ct = default)
        {
            long inFrames = loop.LongLength / 2;
            if (inFrames < N * 2 || outFrames < 64)
                return ResampleLinear(loop, outFrames);   // trop court pour WSOLA

            // 3 copies à la suite -> on garde la copie du milieu, dont les bords sont « dans » le flux
            var tiled = new float[inFrames * 3 * 2];
            for (int c = 0; c < 3; c++) Array.Copy(loop, 0, tiled, c * inFrames * 2, inFrames * 2);
            var stretched = Stretch(tiled, outFrames * 3, ct);
            var result = new float[outFrames * 2];
            Array.Copy(stretched, outFrames * 2, result, 0, outFrames * 2);
            return result;
        }

        /// <summary>Étire un signal stéréo entrelacé à <paramref name="outFrames"/> frames.</summary>
        public static float[] Stretch(float[] x, long outFrames, CancellationToken ct = default)
        {
            long inFrames = x.LongLength / 2;
            double ratio = (double)outFrames / inFrames;
            double ha = Hs / ratio;                      // pas d'analyse

            var window = new float[N];
            for (int i = 0; i < N; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); // Hann périodique : somme = 1 à 50 %

            var mono = new float[inFrames];
            for (long i = 0; i < inFrames; i++) mono[i] = 0.5f * (x[i * 2] + x[i * 2 + 1]);

            long maxStart = inFrames - N;
            var y = new float[(outFrames + N) * 2];
            long prevStart = 0;
            int k = 0;
            for (long outPos = 0; outPos < outFrames; outPos += Hs, k++)
            {
                if ((k & 63) == 0) ct.ThrowIfCancellationRequested();
                long nominal = (long)Math.Round(k * ha);
                long start = Math.Clamp(nominal, 0, maxStart);
                if (k > 0)
                {
                    // continuation « naturelle » de la trame précédente : c'est ce qu'on veut prolonger sans cassure
                    long natural = Math.Min(prevStart + Hs, maxStart);
                    long lo = Math.Max(0, nominal - Tol), hi = Math.Min(maxStart, nominal + Tol);
                    long best = start;
                    double bestScore = double.MinValue;
                    // recherche grossière (pas de 4, corrélation sous-échantillonnée) puis fine
                    for (long c = lo; c <= hi; c += 4)
                    {
                        double sc = Corr(mono, natural, c, Hs, 4);
                        if (sc > bestScore) { bestScore = sc; best = c; }
                    }
                    long lo2 = Math.Max(lo, best - 4), hi2 = Math.Min(hi, best + 4);
                    bestScore = double.MinValue;
                    for (long c = lo2; c <= hi2; c++)
                    {
                        double sc = Corr(mono, natural, c, Hs, 1);
                        if (sc > bestScore) { bestScore = sc; best = c; }
                    }
                    start = best;
                }
                for (int i = 0; i < N; i++)
                {
                    long src = (start + i) * 2, dst = (outPos + i) * 2;
                    float w = window[i];
                    y[dst] += x[src] * w;
                    y[dst + 1] += x[src + 1] * w;
                }
                prevStart = start;
            }

            var result = new float[outFrames * 2];
            Array.Copy(y, result, outFrames * 2);
            // la toute première demi-trame n'a qu'une fenêtre montante : on la compense
            for (int i = 0; i < Hs && i < outFrames; i++)
            {
                float w = window[i];
                if (w > 0.05f) { result[i * 2] /= w; result[i * 2 + 1] /= w; }
            }
            return result;
        }

        /// <summary>Corrélation normalisée entre a[pa..pa+len[ et a[pb..pb+len[.</summary>
        private static double Corr(float[] a, long pa, long pb, int len, int stride)
        {
            double xy = 0, yy = 0;
            for (int i = 0; i < len; i += stride)
            {
                float u = a[pa + i], v = a[pb + i];
                xy += u * v; yy += v * v;
            }
            return yy > 1e-12 ? xy / Math.Sqrt(yy) : 0;
        }

        /// <summary>Rééchantillonnage linéaire (repli pour les fichiers très courts — change la hauteur).</summary>
        public static float[] ResampleLinear(float[] x, long outFrames)
        {
            long inFrames = x.LongLength / 2;
            var y = new float[outFrames * 2];
            if (inFrames == 0) return y;
            double step = (double)inFrames / outFrames;
            for (long i = 0; i < outFrames; i++)
            {
                double p = i * step;
                long a = (long)p; long b = Math.Min(inFrames - 1, a + 1);
                float f = (float)(p - a);
                y[i * 2] = x[a * 2] * (1 - f) + x[b * 2] * f;
                y[i * 2 + 1] = x[a * 2 + 1] * (1 - f) + x[b * 2 + 1] * f;
            }
            return y;
        }
    }
}
