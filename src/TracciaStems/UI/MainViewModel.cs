using System;
using System.Collections.ObjectModel;
using System.Globalization;
using TracciaStems.Audio;

namespace TracciaStems.UI
{
    public sealed class MainViewModel : ObservableObject
    {
        public ObservableCollection<TrackViewModel> Tracks { get; } = new();
        public MasterSettings Master { get; private set; } = new();

        public void ReplaceMaster(MasterSettings m)
        {
            Master = m;
            foreach (var p in new[] { nameof(Bpm), nameof(BpmText), nameof(MasterVolumeDb), nameof(MasterVolumeText), nameof(LimiterEnabled),
                                      nameof(ReverbRoom), nameof(ReverbDamp), nameof(ReverbReturnDb), nameof(SnapToGrid), nameof(DurationOverrideSec), nameof(GridOffsetSec), nameof(GridText),
                                      nameof(BeatsPerBar) })
                OnPropertyChanged(p);
            foreach (var t in Tracks) t.NotifyTempoChanged();
        }

        // ---------- tempo ----------
        public double Bpm
        {
            get => Master.Bpm;
            set
            {
                value = Math.Round(Math.Clamp(value, 20, 300), 2);
                if (Math.Abs(Master.Bpm - value) < 1e-9) return;
                Master.Bpm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BpmText));
                foreach (var t in Tracks) t.NotifyTempoChanged();
            }
        }

        /// <summary>Saisie texte du BPM (accepte virgule ou point).</summary>
        public string BpmText
        {
            get => Master.Bpm.ToString("0.##", CultureInfo.CurrentCulture);
            set
            {
                var s = (value ?? "").Trim().Replace(',', '.');
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0) Bpm = v;
                else OnPropertyChanged();
            }
        }

        public int BeatsPerBar
        {
            get => Master.BeatsPerBar;
            set
            {
                Master.BeatsPerBar = Math.Clamp(value, 1, 16);
                OnPropertyChanged();
                foreach (var t in Tracks) t.NotifyTempoChanged();
            }
        }

        public double BeatSeconds => Master.Bpm > 0 ? 60.0 / Master.Bpm : 0;
        public double BarSeconds => BeatSeconds * Master.BeatsPerBar;

        /// <summary>Longueur (mesures) utilisée par « Boucler ici » sur tous les stems. Réglage d'interface, non sauvegardé.</summary>
        private int _globalLoopBars = 4;
        public int GlobalLoopBars { get => _globalLoopBars; set => Set(ref _globalLoopBars, value); }
        public System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<int, string>> LoopBarOptions => TrackViewModel.BarOptions;

        /// <summary>Position d'un 1er temps de mesure (cale la grille sur la musique).</summary>
        public double GridOffsetSec
        {
            get => Master.GridOffsetSec;
            set
            {
                double bar = BarSeconds;
                Master.GridOffsetSec = bar > 0 ? ((value % bar) + bar) % bar : 0;
                if (bar > 0 && bar - Master.GridOffsetSec < 1e-6) Master.GridOffsetSec = 0;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GridText));
            }
        }
        public string GridText => $"1er temps à {Master.GridOffsetSec:0.000} s";

        public bool SnapToGrid { get => Master.SnapToGrid; set { Master.SnapToGrid = value; OnPropertyChanged(); } }

        /// <summary>Aimante un temps (s) au temps (beat) le plus proche si l'option est active.</summary>
        public double Snap(double sec)
        {
            double beat = BeatSeconds;
            if (!Master.SnapToGrid || beat <= 0) return Math.Max(0, sec);
            double off = Master.GridOffsetSec;
            double t = off + Math.Round((sec - off) / beat) * beat;
            while (t < 0) t += beat;
            return t;
        }

        // ---------- master ----------
        public double MasterVolumeDb
        {
            get => Master.VolumeDb;
            set { Master.VolumeDb = (float)Math.Round(value, 1); OnPropertyChanged(); OnPropertyChanged(nameof(MasterVolumeText)); }
        }
        public string MasterVolumeText => Master.VolumeDb <= -60 ? "-∞ dB" : $"{Master.VolumeDb:+0.0;-0.0;0.0} dB";

        public bool LimiterEnabled { get => Master.LimiterEnabled; set { Master.LimiterEnabled = value; OnPropertyChanged(); } }
        public double ReverbRoom { get => Master.ReverbRoom; set { Master.ReverbRoom = (float)Math.Round(value, 2); OnPropertyChanged(); } }
        public double ReverbDamp { get => Master.ReverbDamp; set { Master.ReverbDamp = (float)Math.Round(value, 2); OnPropertyChanged(); } }
        public double ReverbReturnDb { get => Master.ReverbReturnDb; set { Master.ReverbReturnDb = (float)Math.Round(value, 1); OnPropertyChanged(); } }

        public double DurationOverrideSec
        {
            get => Master.DurationOverrideSec;
            set { Master.DurationOverrideSec = Math.Max(0, Math.Round(value, 1)); OnPropertyChanged(); }
        }

        // ---------- état / affichage ----------
        private int _sampleRate;
        public int SampleRate { get => _sampleRate; set { Set(ref _sampleRate, value); OnPropertyChanged(nameof(FormatText)); } }
        public string FormatText => _sampleRate > 0 ? $"{_sampleRate / 1000.0:0.#} kHz" : "";

        private bool _isPlaying;
        public bool IsPlaying { get => _isPlaying; set { Set(ref _isPlaying, value); OnPropertyChanged(nameof(PlayGlyph)); } }
        public string PlayGlyph => _isPlaying ? "❚❚" : "▶";

        private bool _isRecording;
        public bool IsRecording { get => _isRecording; set => Set(ref _isRecording, value); }

        private string _timeText = "00:00.0 / 00:00.0";
        public string TimeText { get => _timeText; set => Set(ref _timeText, value); }

        private string _recordText = "";
        public string RecordText { get => _recordText; set => Set(ref _recordText, value); }

        private double _positionSec, _lengthSec;
        public double PositionSec { get => _positionSec; set => Set(ref _positionSec, value); }
        public double LengthSec { get => _lengthSec; set => Set(ref _lengthSec, value); }

        private double _meterL, _meterR;
        public double MeterL { get => _meterL; set => Set(ref _meterL, value); }
        public double MeterR { get => _meterR; set => Set(ref _meterR, value); }

        private bool _clip;
        public bool Clip { get => _clip; set => Set(ref _clip, value); }

        private string _status = "Ajoutez des stems (WAV ou MP3) pour commencer — ou glissez-déposez-les dans la fenêtre.";
        public string Status { get => _status; set => Set(ref _status, value); }

        private double _progress;
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); OnPropertyChanged(nameof(IsIdle)); } }
        public bool IsIdle => !_isBusy;

        public bool HasTracks => Tracks.Count > 0;
        public void NotifyTracksChanged() => OnPropertyChanged(nameof(HasTracks));
    }
}
