using System;

namespace TracciaStems.Audio.Dsp
{
    /// <summary>
    /// Filtre biquad (formules RBJ "Audio EQ Cookbook").
    /// Les coefficients peuvent être modifiés sans réinitialiser l'état -> pas de clic quand on bouge un réglage.
    /// </summary>
    public sealed class Biquad
    {
        private double _b0 = 1, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public void SetLowShelf(double sampleRate, double freq, double gainDb, double slope = 1.0)
        {
            double a = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * freq / sampleRate;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
            double sq = 2 * Math.Sqrt(a) * alpha;
            double b0 = a * ((a + 1) - (a - 1) * cos + sq);
            double b1 = 2 * a * ((a - 1) - (a + 1) * cos);
            double b2 = a * ((a + 1) - (a - 1) * cos - sq);
            double a0 = (a + 1) + (a - 1) * cos + sq;
            double a1 = -2 * ((a - 1) + (a + 1) * cos);
            double a2 = (a + 1) + (a - 1) * cos - sq;
            Set(b0, b1, b2, a0, a1, a2);
        }

        public void SetHighShelf(double sampleRate, double freq, double gainDb, double slope = 1.0)
        {
            double a = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * freq / sampleRate;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
            double sq = 2 * Math.Sqrt(a) * alpha;
            double b0 = a * ((a + 1) + (a - 1) * cos + sq);
            double b1 = -2 * a * ((a - 1) + (a + 1) * cos);
            double b2 = a * ((a + 1) + (a - 1) * cos - sq);
            double a0 = (a + 1) - (a - 1) * cos + sq;
            double a1 = 2 * ((a - 1) - (a + 1) * cos);
            double a2 = (a + 1) - (a - 1) * cos - sq;
            Set(b0, b1, b2, a0, a1, a2);
        }

        public void SetPeaking(double sampleRate, double freq, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * freq / sampleRate;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2 * q);
            Set(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
        }

        public void SetLowPass(double sampleRate, double freq, double q = 0.7071)
        {
            double w0 = 2 * Math.PI * freq / sampleRate;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2 * q);
            Set((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public void Reset() { _z1 = _z2 = 0; }

        /// <summary>Forme directe transposée II.</summary>
        public float Process(float x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            // anti-dénormaux
            if (Math.Abs(_z1) < 1e-20) _z1 = 0;
            if (Math.Abs(_z2) < 1e-20) _z2 = 0;
            return (float)y;
        }
    }
}
