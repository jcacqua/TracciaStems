namespace TracciaStems.Audio.Dsp
{
    /// <summary>EQ 3 bandes stéréo : shelf grave 200 Hz, cloche médium 1 kHz, shelf aigu 5 kHz.</summary>
    public sealed class ThreeBandEq
    {
        public const double LowFreq = 200, MidFreq = 1000, MidQ = 0.8, HighFreq = 5000;

        private readonly int _sampleRate;
        private readonly Biquad[] _low = { new Biquad(), new Biquad() };
        private readonly Biquad[] _mid = { new Biquad(), new Biquad() };
        private readonly Biquad[] _high = { new Biquad(), new Biquad() };
        private float _lowDb = float.NaN, _midDb = float.NaN, _highDb = float.NaN;

        public ThreeBandEq(int sampleRate) { _sampleRate = sampleRate; Update(0, 0, 0); }

        public bool IsFlat => _lowDb == 0 && _midDb == 0 && _highDb == 0;

        public void Update(float lowDb, float midDb, float highDb)
        {
            if (lowDb != _lowDb)
            {
                _lowDb = lowDb;
                foreach (var f in _low) f.SetLowShelf(_sampleRate, LowFreq, lowDb);
            }
            if (midDb != _midDb)
            {
                _midDb = midDb;
                foreach (var f in _mid) f.SetPeaking(_sampleRate, MidFreq, MidQ, midDb);
            }
            if (highDb != _highDb)
            {
                _highDb = highDb;
                foreach (var f in _high) f.SetHighShelf(_sampleRate, HighFreq, highDb);
            }
        }

        public void Reset()
        {
            foreach (var f in _low) f.Reset();
            foreach (var f in _mid) f.Reset();
            foreach (var f in _high) f.Reset();
        }

        public void Process(float[] interleaved, int frames)
        {
            if (IsFlat) return;
            for (int i = 0; i < frames; i++)
            {
                int j = i * 2;
                interleaved[j] = _high[0].Process(_mid[0].Process(_low[0].Process(interleaved[j])));
                interleaved[j + 1] = _high[1].Process(_mid[1].Process(_low[1].Process(interleaved[j + 1])));
            }
        }
    }
}
