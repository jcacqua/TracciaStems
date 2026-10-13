using System;

namespace TracciaStems.Audio.Dsp
{
    /// <summary>
    /// Réverbération de type Freeverb (Jezar, domaine public) : 8 filtres en peigne + 4 passe-tout par canal.
    /// Utilisée comme bus d'envoi : la sortie est 100 % "wet".
    /// </summary>
    public sealed class Freeverb
    {
        private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllpassTunings = { 556, 441, 341, 225 };
        private const int StereoSpread = 23;
        private const float FixedGain = 0.015f;
        private const float ScaleRoom = 0.28f, OffsetRoom = 0.7f, ScaleDamp = 0.4f;

        private readonly Comb[] _combL, _combR;
        private readonly Allpass[] _apL, _apR;
        private float _room = -1, _damp = -1;

        public Freeverb(int sampleRate)
        {
            double k = sampleRate / 44100.0;
            _combL = new Comb[8]; _combR = new Comb[8];
            for (int i = 0; i < 8; i++)
            {
                _combL[i] = new Comb((int)(CombTunings[i] * k));
                _combR[i] = new Comb((int)((CombTunings[i] + StereoSpread) * k));
            }
            _apL = new Allpass[4]; _apR = new Allpass[4];
            for (int i = 0; i < 4; i++)
            {
                _apL[i] = new Allpass((int)(AllpassTunings[i] * k));
                _apR[i] = new Allpass((int)((AllpassTunings[i] + StereoSpread) * k));
            }
            SetParameters(0.75f, 0.4f);
        }

        /// <param name="roomSize">0..1</param>
        /// <param name="damping">0..1</param>
        public void SetParameters(float roomSize, float damping)
        {
            if (roomSize == _room && damping == _damp) return;
            _room = roomSize; _damp = damping;
            float fb = roomSize * ScaleRoom + OffsetRoom;
            float d = damping * ScaleDamp;
            for (int i = 0; i < 8; i++)
            {
                _combL[i].Feedback = fb; _combR[i].Feedback = fb;
                _combL[i].Damp = d; _combR[i].Damp = d;
            }
        }

        public void Reset()
        {
            foreach (var c in _combL) c.Clear();
            foreach (var c in _combR) c.Clear();
            foreach (var a in _apL) a.Clear();
            foreach (var a in _apR) a.Clear();
        }

        /// <summary>Traite input (stéréo entrelacé) et écrit le signal réverbéré dans output.</summary>
        public void Process(float[] input, float[] output, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                int j = i * 2;
                float inp = (input[j] + input[j + 1]) * FixedGain;
                float outL = 0, outR = 0;
                for (int c = 0; c < 8; c++)
                {
                    outL += _combL[c].Process(inp);
                    outR += _combR[c].Process(inp);
                }
                for (int a = 0; a < 4; a++)
                {
                    outL = _apL[a].Process(outL);
                    outR = _apR[a].Process(outR);
                }
                output[j] = outL;
                output[j + 1] = outR;
            }
        }

        private sealed class Comb
        {
            private readonly float[] _buf;
            private int _idx;
            private float _store;
            public float Feedback, Damp;
            public Comb(int size) { _buf = new float[Math.Max(1, size)]; }
            public void Clear() { Array.Clear(_buf, 0, _buf.Length); _store = 0; }
            public float Process(float input)
            {
                float output = _buf[_idx];
                _store = output * (1 - Damp) + _store * Damp;
                if (Math.Abs(_store) < 1e-20f) _store = 0;
                _buf[_idx] = input + _store * Feedback;
                if (++_idx >= _buf.Length) _idx = 0;
                return output;
            }
        }

        private sealed class Allpass
        {
            private readonly float[] _buf;
            private int _idx;
            private const float Feedback = 0.5f;
            public Allpass(int size) { _buf = new float[Math.Max(1, size)]; }
            public void Clear() { Array.Clear(_buf, 0, _buf.Length); }
            public float Process(float input)
            {
                float bufout = _buf[_idx];
                if (Math.Abs(bufout) < 1e-20f) bufout = 0;
                float output = -input + bufout;
                _buf[_idx] = input + bufout * Feedback;
                if (++_idx >= _buf.Length) _idx = 0;
                return output;
            }
        }
    }
}
