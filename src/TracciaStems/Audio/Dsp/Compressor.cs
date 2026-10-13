using System;

namespace TracciaStems.Audio.Dsp
{
    /// <summary>
    /// Compresseur stéréo lié (feed-forward, détection crête, genou souple de 6 dB).
    /// </summary>
    public sealed class Compressor
    {
        private const double KneeDb = 6.0;
        private readonly int _sampleRate;
        private double _grDb;              // réduction de gain lissée (dB, >= 0)
        private float _attackMs = -1, _releaseMs = -1;
        private double _attackCoef, _releaseCoef;

        /// <summary>Réduction de gain maximale observée depuis la dernière lecture (pour l'affichage).</summary>
        public float GainReductionDb { get; private set; }

        public Compressor(int sampleRate) { _sampleRate = sampleRate; }

        public void Reset() { _grDb = 0; GainReductionDb = 0; }

        public void Process(float[] buf, int frames, float thresholdDb, float ratio, float attackMs, float releaseMs, float makeupDb)
        {
            if (attackMs != _attackMs) { _attackMs = attackMs; _attackCoef = Math.Exp(-1.0 / (Math.Max(0.05, attackMs) * 0.001 * _sampleRate)); }
            if (releaseMs != _releaseMs) { _releaseMs = releaseMs; _releaseCoef = Math.Exp(-1.0 / (Math.Max(1, releaseMs) * 0.001 * _sampleRate)); }
            ratio = Math.Max(1f, ratio);
            double slope = 1.0 - 1.0 / ratio;
            double makeup = makeupDb;
            float maxGr = 0;

            for (int i = 0; i < frames; i++)
            {
                int j = i * 2;
                double level = Math.Max(Math.Abs(buf[j]), Math.Abs(buf[j + 1]));
                double levelDb = level > 1e-9 ? 20 * Math.Log10(level) : -180;
                double over = levelDb - thresholdDb;

                double target;
                if (over <= -KneeDb / 2) target = 0;
                else if (over >= KneeDb / 2) target = over * slope;
                else
                {
                    double x = over + KneeDb / 2;
                    target = slope * x * x / (2 * KneeDb);
                }

                double coef = target > _grDb ? _attackCoef : _releaseCoef;
                _grDb = target + coef * (_grDb - target);
                if (_grDb < 1e-6) _grDb = 0;

                float gain = (float)Math.Pow(10, (makeup - _grDb) / 20);
                buf[j] *= gain;
                buf[j + 1] *= gain;
                if (_grDb > maxGr) maxGr = (float)_grDb;
            }
            GainReductionDb = maxGr;
        }
    }
}
