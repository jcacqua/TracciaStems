using System;

namespace TracciaStems.Audio.Dsp
{
    /// <summary>
    /// Limiteur crête de sortie (attaque instantanée, relâchement 80 ms) — évite la saturation numérique du master.
    /// </summary>
    public sealed class Limiter
    {
        private readonly double _releaseCoef;
        private double _gain = 1.0;
        public float CeilingDb { get; set; } = -0.3f;
        public float GainReductionDb { get; private set; }

        public Limiter(int sampleRate)
        {
            _releaseCoef = Math.Exp(-1.0 / (0.080 * sampleRate));
        }

        public void Reset() { _gain = 1; }

        public void Process(float[] buf, int offset, int frames)
        {
            double ceiling = Math.Pow(10, CeilingDb / 20.0);
            double minGain = 1;
            for (int i = 0; i < frames; i++)
            {
                int j = offset + i * 2;
                double peak = Math.Max(Math.Abs(buf[j]), Math.Abs(buf[j + 1]));
                double needed = peak * _gain > ceiling ? ceiling / peak : 1.0;
                if (needed < _gain) _gain = needed;                      // attaque instantanée
                else _gain = 1.0 + _releaseCoef * (_gain - 1.0);           // relâchement vers 1
                if (_gain > 1) _gain = 1;
                if (peak * _gain > ceiling) _gain = ceiling / peak;
                buf[j] = (float)(buf[j] * _gain);
                buf[j + 1] = (float)(buf[j + 1] * _gain);
                if (_gain < minGain) minGain = _gain;
            }
            GainReductionDb = (float)(-20 * Math.Log10(minGain));
        }
    }
}
