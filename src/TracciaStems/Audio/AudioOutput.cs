using System;
using NAudio.Wave;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Sortie carte son. Le périphérique tourne en permanence ; lecture/pause se font via MixEngine.Paused
    /// (réaction immédiate, pas de redémarrage du flux).
    /// </summary>
    public sealed class AudioOutput : IDisposable
    {
        private WaveOutEvent? _out;
        public MixEngine? Engine { get; private set; }

        public void Attach(MixEngine engine, int latencyMs = 120)
        {
            Detach();
            Engine = engine;
            _out = new WaveOutEvent { DesiredLatency = latencyMs, NumberOfBuffers = 3 };
            _out.Init(engine);
            _out.Play();
        }

        public void Detach()
        {
            if (_out != null)
            {
                try { _out.Stop(); } catch { }
                _out.Dispose();
                _out = null;
            }
            Engine = null;
        }

        public void Dispose() => Detach();
    }
}
