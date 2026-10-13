using System;
using System.Threading;
using TracciaStems.Audio.Dsp;

namespace TracciaStems.Audio
{
    /// <summary>
    /// Calage d'un stem court (boucle) sur la grille du morceau :
    /// 1) nombre de mesures de la boucle au tempo du projet (1, 2, 4, 8…) ;
    /// 2) étirement sans changement de hauteur pour qu'elle dure exactement ce nombre de mesures ;
    /// 3) phase : le 1er temps de la boucle tombe sur les 1ers temps du morceau.
    /// </summary>
    public static class LoopAligner
    {
        private static readonly int[] BarChoices = { 1, 2, 4, 8, 16, 32 };

        /// <param name="Stem">Boucle étirée (ou l'originale si aucun étirement n'est nécessaire).</param>
        /// <param name="PhaseSec">Instant du morceau où démarre le début du fichier (modulo la longueur de la boucle).</param>
        public sealed record Result(StemData Stem, int Bars, double OriginalBpm, double LengthSec, double PhaseSec, double StretchPercent);

        /// <summary>Nombre de mesures le plus plausible pour une durée donnée.</summary>
        public static int GuessBars(double durationSec, double barSec)
        {
            int best = 1; double bestErr = double.MaxValue;
            foreach (int b in BarChoices)
            {
                double err = Math.Abs(Math.Log(b * barSec / durationSec));
                if (err < bestErr) { bestErr = err; best = b; }
            }
            return best;
        }

        /// <summary>Étire la boucle à la longueur voulue (appelé aussi au rechargement d'un projet).</summary>
        public static StemData StretchTo(StemData original, double lengthSec, CancellationToken ct = default)
        {
            long target = (long)Math.Round(lengthSec * original.SampleRate);
            if (Math.Abs(target - original.Frames) <= 2) return original;
            var data = TimeStretch.StretchLoop(original.Samples, target, ct);
            return new StemData(original.FilePath, data, original.SampleRate);
        }

        public static Result Align(StemData original, double projectBpm, int beatsPerBar, double songDownbeatSec, int forcedBars = 0, CancellationToken ct = default)
        {
            if (projectBpm <= 0) throw new InvalidOperationException("Tempo du projet inconnu.");
            int bpb = Math.Max(1, beatsPerBar);
            double beat = 60.0 / projectBpm, bar = beat * bpb;
            double dur = original.DurationSec;
            int bars = forcedBars > 0 ? forcedBars : GuessBars(dur, bar);
            double length = bars * bar;
            double origBpm = bars * bpb * 60.0 / dur;

            var stem = StretchTo(original, length, ct);
            ct.ThrowIfCancellationRequested();

            // phase des temps à l'intérieur de la boucle (boucle répétée pour avoir assez de matière)
            int reps = (int)Math.Ceiling(24.0 / length) + 1;
            var tiled = new float[stem.Samples.LongLength * reps];
            for (int r = 0; r < reps; r++) Array.Copy(stem.Samples, 0, tiled, r * stem.Samples.LongLength, stem.Samples.LongLength);
            var g = BpmDetector.DetectGrid(tiled, stem.SampleRate, projectBpm, bpb, 60, ct);

            // on suppose que le 1er temps présent dans le fichier est un 1er temps de mesure
            // (cas des boucles) ; seul le décalage fin à l'intérieur d'un temps est corrigé.
            double inner = g?.BeatOffsetSec ?? 0;
            if (inner > beat / 2) inner -= beat;

            double phase = songDownbeatSec - inner;
            phase = ((phase % length) + length) % length;
            return new Result(stem, bars, origBpm, length, phase, (length / dur - 1) * 100);
        }
    }
}
