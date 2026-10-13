using System;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Réglages d'une piste. Objet simple sérialisé tel quel dans le fichier projet,
    /// lu en continu par le moteur audio (les écritures de float/bool sont atomiques).
    /// </summary>
    public sealed class TrackSettings
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string? RelativePath { get; set; }

        public float VolumeDb { get; set; } = 0f;      // -60 (= -inf) .. +12
        public float Pan { get; set; } = 0f;           // -1 .. +1
        public bool Mute { get; set; }
        public bool Solo { get; set; }

        public float EqLowDb { get; set; }             // -15 .. +15
        public float EqMidDb { get; set; }
        public float EqHighDb { get; set; }

        public bool CompEnabled { get; set; }
        public float CompThresholdDb { get; set; } = -18f;
        public float CompRatio { get; set; } = 4f;
        public float CompAttackMs { get; set; } = 10f;
        public float CompReleaseMs { get; set; } = 120f;
        public float CompMakeupDb { get; set; } = 0f;

        public float ReverbSend { get; set; }          // 0 .. 1

        /// <summary>Répète le fichier entier en boucle sur toute la durée du morceau (stem court, boucle de batterie…).</summary>
        public bool RepeatWholeFile { get; set; }
        /// <summary>Instant du morceau (s) où démarre le début du fichier répété (modulo sa longueur).</summary>
        public double RepeatPhaseSec { get; set; }
        /// <summary>Si &gt; 0 : la boucle a été calée (étirée) à cette durée exacte en secondes.</summary>
        public double AlignedLengthSec { get; set; }
        /// <summary>Nombre de mesures de la boucle calée (information).</summary>
        public int AlignedBars { get; set; }
        /// <summary>Tempo d'origine estimé de la boucle calée (information).</summary>
        public double AlignedFromBpm { get; set; }

        public bool LoopEnabled { get; set; }
        public double LoopStartSec { get; set; }
        public double LoopEndSec { get; set; }
        public int LoopBars { get; set; } = 4;         // 0 = longueur libre

        public double? DetectedBpm { get; set; }

        public static float DbToGain(float db) => db <= -60f ? 0f : (float)Math.Pow(10, db / 20.0);
    }

    public sealed class MasterSettings
    {
        public float VolumeDb { get; set; } = 0f;
        public bool LimiterEnabled { get; set; } = true;
        public float ReverbRoom { get; set; } = 0.7f;
        public float ReverbDamp { get; set; } = 0.45f;
        public float ReverbReturnDb { get; set; } = 0f;
        public double Bpm { get; set; } = 120;
        public int BeatsPerBar { get; set; } = 4;
        public bool SnapToGrid { get; set; } = true;
        /// <summary>Position (s) d'un 1er temps de mesure, dans [0, durée d'une mesure[ : cale la grille sur la musique.</summary>
        public double GridOffsetSec { get; set; }
        public bool GridDetected { get; set; }
        /// <summary>Durée forcée du morceau en secondes (0 = automatique : stem le plus long).</summary>
        public double DurationOverrideSec { get; set; }
    }
}
