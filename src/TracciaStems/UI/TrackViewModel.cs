using System;
using System.Collections.Generic;
using System.Globalization;
using TracciaStems.Audio;

namespace TracciaStems.UI
{
    public sealed class TrackViewModel : ObservableObject
    {
        public static readonly IReadOnlyList<KeyValuePair<int, string>> BarOptions = new[]
        {
            new KeyValuePair<int, string>(0, "Libre"),
            new KeyValuePair<int, string>(1, "1 mes."),
            new KeyValuePair<int, string>(2, "2 mes."),
            new KeyValuePair<int, string>(4, "4 mes."),
            new KeyValuePair<int, string>(8, "8 mes."),
            new KeyValuePair<int, string>(16, "16 mes."),
            new KeyValuePair<int, string>(32, "32 mes."),
        };

        /// <summary>Propriétés d'affichage temps réel (ne rendent pas le projet "modifié").</summary>
        public static readonly HashSet<string> TransientProperties = new()
        {
            nameof(PositionFrames), nameof(MeterL), nameof(MeterR), nameof(GainReduction), nameof(GainReductionText),
            nameof(TimelineFrames), nameof(IsDetecting), nameof(DetectedBpmText), nameof(VolumeText), nameof(PanText),
            nameof(ReverbText), nameof(LoopInfo), nameof(DisplayLoopEnabled), nameof(DisplayLoopStartSec), nameof(DisplayLoopEndSec), nameof(RepeatInfo), nameof(IsAligned), nameof(Peaks), nameof(StemFrames), nameof(FileInfo), nameof(Stem), nameof(ColorHex), nameof(IsDimmed), nameof(HasDetectedBpm),
        };

        private readonly MainViewModel _main;
        public TrackSettings Settings { get; }
        /// <summary>Audio joué (éventuellement étiré par le calage).</summary>
        public StemData Stem { get; private set; }
        /// <summary>Audio tel que chargé depuis le fichier.</summary>
        public StemData OriginalStem { get; }
        public TrackProcessor? Processor { get; set; }

        public TrackViewModel(MainViewModel main, StemData stem, TrackSettings settings)
        {
            OriginalStem = stem;
            _main = main;
            Stem = stem;
            Settings = settings;
            settings.FilePath = stem.FilePath;
            if (string.IsNullOrWhiteSpace(settings.Name))
                settings.Name = System.IO.Path.GetFileNameWithoutExtension(stem.FilePath);
            if (settings.LoopEndSec <= settings.LoopStartSec)
                RecalcLoopEnd();
        }

        public IReadOnlyList<KeyValuePair<int, string>> LoopBarOptions => BarOptions;
        public float[] Peaks => Stem.Peaks;
        public long StemFrames => Stem.Frames;
        public int SampleRate => Stem.SampleRate;
        public string FileInfo => $"{System.IO.Path.GetFileName(Stem.FilePath)} — {FormatTime(Stem.DurationSec)}";

        // ---------- réglages ----------
        public string Name { get => Settings.Name; set { Settings.Name = value; OnPropertyChanged(); } }

        public double VolumeDb
        {
            get => Settings.VolumeDb;
            set { Settings.VolumeDb = (float)Math.Round(value, 1); OnPropertyChanged(); OnPropertyChanged(nameof(VolumeText)); }
        }
        public string VolumeText => Settings.VolumeDb <= -60 ? "-∞ dB" : $"{Settings.VolumeDb:+0.0;-0.0;0.0} dB";

        public double Pan
        {
            get => Settings.Pan;
            set { Settings.Pan = (float)Math.Round(value, 2); OnPropertyChanged(); OnPropertyChanged(nameof(PanText)); }
        }
        public string PanText => Math.Abs(Settings.Pan) < 0.005 ? "C" : Settings.Pan < 0 ? $"G{-Settings.Pan * 100:0}" : $"D{Settings.Pan * 100:0}";

        public bool Mute { get => Settings.Mute; set { Settings.Mute = value; OnPropertyChanged(); } }
        public bool Solo { get => Settings.Solo; set { Settings.Solo = value; OnPropertyChanged(); } }

        public double EqLow { get => Settings.EqLowDb; set { Settings.EqLowDb = (float)Math.Round(value, 1); OnPropertyChanged(); } }
        public double EqMid { get => Settings.EqMidDb; set { Settings.EqMidDb = (float)Math.Round(value, 1); OnPropertyChanged(); } }
        public double EqHigh { get => Settings.EqHighDb; set { Settings.EqHighDb = (float)Math.Round(value, 1); OnPropertyChanged(); } }

        public bool CompEnabled { get => Settings.CompEnabled; set { Settings.CompEnabled = value; OnPropertyChanged(); } }
        public double CompThreshold { get => Settings.CompThresholdDb; set { Settings.CompThresholdDb = (float)Math.Round(value, 1); OnPropertyChanged(); } }
        public double CompRatio { get => Settings.CompRatio; set { Settings.CompRatio = (float)Math.Round(value, 1); OnPropertyChanged(); } }
        public double CompMakeup { get => Settings.CompMakeupDb; set { Settings.CompMakeupDb = (float)Math.Round(value, 1); OnPropertyChanged(); } }
        public double CompAttack { get => Settings.CompAttackMs; set { Settings.CompAttackMs = (float)Math.Round(value, 1); OnPropertyChanged(); } }
        public double CompRelease { get => Settings.CompReleaseMs; set { Settings.CompReleaseMs = (float)Math.Round(value); OnPropertyChanged(); } }

        public double ReverbSend
        {
            get => Settings.ReverbSend;
            set { Settings.ReverbSend = (float)Math.Round(value, 2); OnPropertyChanged(); OnPropertyChanged(nameof(ReverbText)); }
        }
        public string ReverbText => $"{Settings.ReverbSend * 100:0}%";

        // ---------- répétition du fichier entier ----------
        public bool RepeatWholeFile
        {
            get => Settings.RepeatWholeFile;
            set
            {
                Settings.RepeatWholeFile = value;
                if (value && Settings.LoopEnabled) { Settings.LoopEnabled = false; OnPropertyChanged(nameof(LoopEnabled)); }
                OnPropertyChanged();
                NotifyDisplayLoop();
            }
        }

        // boucle telle qu'affichée sur la forme d'onde (répétition du fichier ou boucle de zone)
        public bool DisplayLoopEnabled => !Settings.RepeatWholeFile && Settings.LoopEnabled;
        public double DisplayLoopStartSec => Settings.LoopStartSec;
        public double DisplayLoopEndSec => Settings.LoopEndSec;

        public double RepeatPhaseSec
        {
            get => Settings.RepeatPhaseSec;
            set
            {
                double len = Stem.DurationSec;
                Settings.RepeatPhaseSec = len > 0 ? ((value % len) + len) % len : 0;
                OnPropertyChanged();
            }
        }

        public bool IsAligned => Settings.AlignedLengthSec > 0;

        public string RepeatInfo
        {
            get
            {
                if (IsAligned)
                    return $"Calé : {Settings.AlignedBars} mes. ({Settings.AlignedFromBpm:0.#} → {_main.Bpm:0.##} BPM), départ à {Settings.RepeatPhaseSec:0.000} s";
                double bar = _main.BarSeconds;
                string bars = bar > 0 ? $" ≈ {OriginalStem.DurationSec / bar:0.##} mes. à {_main.Bpm:0.##} BPM" : "";
                return $"Fichier de {OriginalStem.DurationSec:0.000} s{bars} — non calé";
            }
        }

        /// <summary>Remplace l'audio joué (boucle étirée) ; l'original reste disponible pour un nouveau calage.</summary>
        public void ReplaceStem(StemData stem)
        {
            Stem = stem;
            if (Settings.LoopEndSec > stem.DurationSec) Settings.LoopEndSec = stem.DurationSec;
            if (Settings.LoopStartSec >= Settings.LoopEndSec) Settings.LoopStartSec = 0;
            OnPropertyChanged(nameof(Stem));
            OnPropertyChanged(nameof(Peaks));
            OnPropertyChanged(nameof(StemFrames));
            OnPropertyChanged(nameof(FileInfo));
            NotifyDisplayLoop();
        }

        /// <summary>Applique le résultat d'un calage.</summary>
        public void ApplyAlignment(LoopAligner.Result r)
        {
            Settings.AlignedLengthSec = r.LengthSec;
            Settings.AlignedBars = r.Bars;
            Settings.AlignedFromBpm = r.OriginalBpm;
            ReplaceStem(r.Stem);
            RepeatPhaseSec = r.PhaseSec;
            if (!Settings.RepeatWholeFile) RepeatWholeFile = true;
            OnPropertyChanged(nameof(IsAligned));
            OnPropertyChanged(nameof(RepeatInfo));
        }

        /// <summary>Annule le calage : retour au fichier d'origine, départ au début du morceau.</summary>
        public void ClearAlignment()
        {
            Settings.AlignedLengthSec = 0;
            Settings.AlignedBars = 0;
            Settings.AlignedFromBpm = 0;
            ReplaceStem(OriginalStem);
            RepeatPhaseSec = 0;
            OnPropertyChanged(nameof(IsAligned));
            OnPropertyChanged(nameof(RepeatInfo));
        }

        /// <summary>Décale la boucle répétée d'un nombre de temps (±).</summary>
        public void NudgeRepeat(int beats)
        {
            RepeatPhaseSec = Settings.RepeatPhaseSec + beats * _main.BeatSeconds;
            OnPropertyChanged(nameof(RepeatInfo));
        }

        private void NotifyDisplayLoop()
        {
            OnPropertyChanged(nameof(DisplayLoopEnabled));
            OnPropertyChanged(nameof(DisplayLoopStartSec));
            OnPropertyChanged(nameof(DisplayLoopEndSec));
        }

        protected override void OnAfterPropertyChanged(string? name)
        {
            if (name is nameof(LoopEnabled) or nameof(LoopStartSec) or nameof(LoopEndSec)) NotifyDisplayLoop();
        }

        // ---------- boucle ----------
        public bool LoopEnabled
        {
            get => Settings.LoopEnabled;
            set
            {
                Settings.LoopEnabled = value;
                if (value && Settings.RepeatWholeFile) { Settings.RepeatWholeFile = false; OnPropertyChanged(nameof(RepeatWholeFile)); }
                OnPropertyChanged(); OnPropertyChanged(nameof(LoopInfo));
            }
        }

        public double LoopStartSec
        {
            get => Settings.LoopStartSec;
            set
            {
                Settings.LoopStartSec = Math.Clamp(Math.Round(value, 3), 0, Stem.DurationSec);
                OnPropertyChanged();
                if (Settings.LoopBars > 0) RecalcLoopEnd();
                else if (Settings.LoopEndSec <= Settings.LoopStartSec) { Settings.LoopEndSec = Math.Min(Stem.DurationSec, Settings.LoopStartSec + 1); OnPropertyChanged(nameof(LoopEndSec)); }
                OnPropertyChanged(nameof(LoopInfo));
            }
        }

        public double LoopEndSec
        {
            get => Settings.LoopEndSec;
            set
            {
                double v = Math.Clamp(Math.Round(value, 3), 0, Stem.DurationSec);
                if (v <= Settings.LoopStartSec) v = Math.Min(Stem.DurationSec, Settings.LoopStartSec + 0.01);
                Settings.LoopEndSec = v;
                // une fin saisie à la main passe en longueur libre
                if (Settings.LoopBars != 0) { Settings.LoopBars = 0; OnPropertyChanged(nameof(LoopBars)); }
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoopInfo));
            }
        }

        public int LoopBars
        {
            get => Settings.LoopBars;
            set { Settings.LoopBars = value; OnPropertyChanged(); if (value > 0) RecalcLoopEnd(); }
        }

        /// <summary>Définit la boucle d'un coup (sélection à la souris), avec aimantation éventuelle à la grille.</summary>
        public void SetLoopRegion(double startSec, double endSec)
        {
            if (endSec < startSec) (startSec, endSec) = (endSec, startSec);
            startSec = _main.Snap(startSec);
            endSec = _main.Snap(endSec);
            if (endSec - startSec < 0.01)
            {
                double beat = _main.BeatSeconds;
                endSec = startSec + (beat > 0 ? beat : 0.5);
            }
            Settings.LoopBars = 0;
            Settings.LoopStartSec = Math.Clamp(startSec, 0, Stem.DurationSec);
            Settings.LoopEndSec = Math.Clamp(endSec, Settings.LoopStartSec + 0.01, Stem.DurationSec);
            Settings.LoopEnabled = true;
            if (Settings.RepeatWholeFile) { Settings.RepeatWholeFile = false; OnPropertyChanged(nameof(RepeatWholeFile)); }
            // durée multiple entier de mesures -> on l'affiche comme telle
            double barSec = _main.BarSeconds;
            if (barSec > 0)
            {
                double bars = (Settings.LoopEndSec - Settings.LoopStartSec) / barSec;
                int rb = (int)Math.Round(bars);
                foreach (var o in BarOptions)
                    if (o.Key == rb && o.Key > 0 && Math.Abs(bars - rb) < 0.01) Settings.LoopBars = rb;
            }
            OnPropertyChanged(nameof(LoopStartSec));
            OnPropertyChanged(nameof(LoopEndSec));
            OnPropertyChanged(nameof(LoopBars));
            OnPropertyChanged(nameof(LoopEnabled));
            OnPropertyChanged(nameof(LoopInfo));
        }

        /// <summary>
        /// Boucle « ici » : démarre la boucle à la position de lecture (aimantée au temps le plus proche si l'option est active),
        /// d'une longueur de <paramref name="bars"/> mesures (0 = garder la longueur actuelle), et l'active.
        /// </summary>
        public void StartLoopAt(double playheadSec, int bars)
        {
            double oldLen = Settings.LoopEndSec - Settings.LoopStartSec;
            double start = Math.Clamp(_main.Snap(playheadSec), 0, Math.Max(0, Stem.DurationSec - 0.01));
            Settings.LoopStartSec = start;
            if (bars > 0)
            {
                Settings.LoopBars = bars;
                RecalcLoopEnd();
            }
            else
            {
                double len = oldLen > 0.01 ? oldLen : (_main.BarSeconds > 0 ? 4 * _main.BarSeconds : 2.0);
                Settings.LoopBars = 0;
                Settings.LoopEndSec = Math.Min(Stem.DurationSec, start + len);
            }
            Settings.LoopEnabled = true;
            if (Settings.RepeatWholeFile) { Settings.RepeatWholeFile = false; OnPropertyChanged(nameof(RepeatWholeFile)); }
            OnPropertyChanged(nameof(LoopStartSec));
            OnPropertyChanged(nameof(LoopEndSec));
            OnPropertyChanged(nameof(LoopBars));
            OnPropertyChanged(nameof(LoopEnabled));
            OnPropertyChanged(nameof(LoopInfo));
        }

        /// <summary>Fin de boucle = début + N mesures au tempo du projet.</summary>
        public void RecalcLoopEnd()
        {
            double barSec = _main.BarSeconds;
            int bars = Settings.LoopBars > 0 ? Settings.LoopBars : 4;
            double len = barSec > 0 ? bars * barSec : 2.0;
            Settings.LoopEndSec = Math.Min(Stem.DurationSec, Settings.LoopStartSec + len);
            OnPropertyChanged(nameof(LoopEndSec));
            OnPropertyChanged(nameof(LoopInfo));
        }

        public string LoopInfo
        {
            get
            {
                double len = Settings.LoopEndSec - Settings.LoopStartSec;
                if (len <= 0) return "";
                double barSec = _main.BarSeconds;
                string bars = barSec > 0 ? $" ≈ {len / barSec:0.##} mes." : "";
                return $"{len:0.000} s{bars}";
            }
        }

        // ---------- BPM du stem ----------
        private bool _isDetecting;
        public bool IsDetecting { get => _isDetecting; set { Set(ref _isDetecting, value); OnPropertyChanged(nameof(DetectedBpmText)); } }

        public double? DetectedBpm
        {
            get => Settings.DetectedBpm;
            set { Settings.DetectedBpm = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetectedBpmText)); OnPropertyChanged(nameof(HasDetectedBpm)); }
        }
        public bool HasDetectedBpm => Settings.DetectedBpm.HasValue;
        public string DetectedBpmText => IsDetecting ? "analyse…" :
            Settings.DetectedBpm.HasValue ? Settings.DetectedBpm.Value.ToString("0.#", CultureInfo.CurrentCulture) + " BPM" : "";

        // ---------- affichage ----------
        private string _colorHex = "#4FB3FF";
        public string ColorHex { get => _colorHex; set => Set(ref _colorHex, value); }

        private bool _isDimmed;
        /// <summary>Piste non audible (mute, ou une autre piste est en solo).</summary>
        public bool IsDimmed { get => _isDimmed; set => Set(ref _isDimmed, value); }

        // ---------- affichage temps réel ----------
        private double _positionFrames;
        public double PositionFrames { get => _positionFrames; set => Set(ref _positionFrames, value); }

        private double _timelineFrames;
        public double TimelineFrames { get => _timelineFrames; set => Set(ref _timelineFrames, value); }

        private double _meterL, _meterR;
        public double MeterL { get => _meterL; set => Set(ref _meterL, value); }
        public double MeterR { get => _meterR; set => Set(ref _meterR, value); }

        private double _gr;
        public double GainReduction
        {
            get => _gr;
            set { if (Set(ref _gr, value)) OnPropertyChanged(nameof(GainReductionText)); }
        }
        public string GainReductionText => CompEnabled ? $"GR {_gr:0.0}" : "";

        /// <summary>Signale aux vues qu'une valeur dépendant du tempo du projet a changé.</summary>
        public void NotifyTempoChanged()
        {
            if (Settings.LoopBars > 0) RecalcLoopEnd();
            OnPropertyChanged(nameof(LoopInfo));
            OnPropertyChanged(nameof(RepeatInfo));
        }

        public static string FormatTime(double sec)
        {
            if (sec < 0) sec = 0;
            int m = (int)(sec / 60);
            double s = sec - m * 60;
            return $"{m:00}:{s:00.0}";
        }
    }
}
