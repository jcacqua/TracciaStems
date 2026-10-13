using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TracciaStems.Audio;

namespace TracciaStems.Project
{
    /// <summary>Fichier projet (.traccia, JSON ; les anciens .stemmix restent lisibles) : chemins des stems + tous les réglages du mix.</summary>
    public sealed class ProjectFile
    {
        public const string Extension = ".traccia";
        public const string LegacyExtension = ".stemmix";
        public int Version { get; set; } = 1;
        public int SampleRate { get; set; }
        public MasterSettings Master { get; set; } = new();
        public List<TrackSettings> Tracks { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            foreach (var t in Tracks)
            {
                try { t.RelativePath = Path.GetRelativePath(dir, t.FilePath); }
                catch { t.RelativePath = null; }
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, path, overwrite: true);
        }

        public static ProjectFile Load(string path)
        {
            var p = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options)
                    ?? throw new InvalidDataException("Fichier projet invalide.");
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            foreach (var t in p.Tracks)
            {
                // priorité au chemin relatif (projet + stems déplacés ensemble)
                if (!string.IsNullOrEmpty(t.RelativePath))
                {
                    string candidate = Path.GetFullPath(Path.Combine(dir, t.RelativePath));
                    if (File.Exists(candidate)) { t.FilePath = candidate; continue; }
                }
            }
            return p;
        }

        /// <summary>Copie profonde (pour figer les réglages pendant un export).</summary>
        public static T Clone<T>(T obj) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj))!;
    }
}
