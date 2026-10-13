using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TracciaStems.Audio;
using TracciaStems.Project;
using TracciaStems.UI;

namespace TracciaStems
{
    public partial class MainWindow : Window
    {
        private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".aif", ".aiff", ".flac", ".m4a", ".aac", ".wma" };
        private const string AudioFilter = "Fichiers audio|*.wav;*.mp3;*.aif;*.aiff;*.flac;*.m4a;*.aac;*.wma|WAV|*.wav|MP3|*.mp3|Tous les fichiers|*.*";
        private const string ProjectFilter = "Projet TracciaStems (*.traccia)|*.traccia";
        private const string OpenProjectFilter = "Projet TracciaStems (*.traccia, *.stemmix)|*.traccia;*.stemmix";
        private static readonly string[] Palette =
            { "#4FB3FF", "#FF9F1C", "#2ECC71", "#E056FD", "#F1C40F", "#FF6B6B", "#48DBFB", "#A3CB38", "#FDA7DF", "#C8D6E5" };

        private static readonly HashSet<string> MainDirtyProps = new()
        {
            nameof(MainViewModel.Bpm), nameof(MainViewModel.BeatsPerBar), nameof(MainViewModel.SnapToGrid),
            nameof(MainViewModel.MasterVolumeDb), nameof(MainViewModel.LimiterEnabled), nameof(MainViewModel.ReverbRoom),
            nameof(MainViewModel.ReverbDamp), nameof(MainViewModel.ReverbReturnDb), nameof(MainViewModel.DurationOverrideSec),
            nameof(MainViewModel.GridOffsetSec),
        };

        private readonly MainViewModel _vm = new();
        private readonly AudioOutput _output = new();
        private readonly DispatcherTimer _timer;
        private MixEngine? _engine;
        private LiveRecorder? _recorder;
        private string? _recordTarget;       // fichier final de la prise (wav ou mp3)
        private string? _projectPath;
        private bool _dirty;
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            BeatsCombo.ItemsSource = Enumerable.Range(1, 12).ToList();
            _vm.PropertyChanged += Main_PropertyChanged;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += Timer_Tick;
            _timer.Start();
            UpdateTitle();
        }

        // =====================================================================
        //  Moteur
        // =====================================================================

        private void EnsureEngine(int sampleRate)
        {
            if (_engine != null && _engine.SampleRate == sampleRate && ReferenceEquals(_engine.Master, _vm.Master)) return;
            _output.Detach();
            _engine = new MixEngine(sampleRate, _vm.Master);
            _vm.SampleRate = sampleRate;
            _vm.IsPlaying = false;
            try
            {
                _output.Attach(_engine);
            }
            catch (Exception ex)
            {
                _vm.Status = "Aucune sortie audio disponible (" + ex.Message + ") — l'export reste possible.";
            }
        }

        private void SyncEngineTracks()
        {
            if (_engine == null) return;
            _engine.SetTracks(_vm.Tracks.Select(t => (t.Stem, t.Settings)));
            foreach (var t in _vm.Tracks)
                t.Processor = _engine.Tracks.FirstOrDefault(p => ReferenceEquals(p.Settings, t.Settings));
            UpdateTimeline();
            UpdateDimmed();
            _vm.NotifyTracksChanged();
        }

        private void UpdateTimeline()
        {
            long len = _engine?.LengthFrames ?? 0;
            foreach (var t in _vm.Tracks) t.TimelineFrames = len;
            _vm.LengthSec = _engine != null && _engine.SampleRate > 0 ? (double)len / _engine.SampleRate : 0;
        }

        private void UpdateDimmed()
        {
            bool anySolo = _vm.Tracks.Any(t => t.Solo);
            foreach (var t in _vm.Tracks) t.IsDimmed = t.Mute || (anySolo && !t.Solo);
        }

        // =====================================================================
        //  Rafraîchissement (≈30 images/s)
        // =====================================================================

        private void Timer_Tick(object? sender, EventArgs e)
        {
            var eng = _engine;
            if (eng == null) return;
            long pos = eng.Position;
            long len = eng.LengthFrames;
            int sr = eng.SampleRate;

            foreach (var t in _vm.Tracks)
            {
                t.PositionFrames = pos;
                var p = t.Processor;
                if (p != null)
                {
                    t.MeterL = Decay(t.MeterL, p.PeakL); p.PeakL = 0;
                    t.MeterR = Decay(t.MeterR, p.PeakR); p.PeakR = 0;
                    t.GainReduction = t.CompEnabled && _vm.IsPlaying ? Math.Round(p.GainReductionDb, 1) : 0;
                }
            }
            _vm.MeterL = Decay(_vm.MeterL, eng.PeakL); eng.PeakL = 0;
            _vm.MeterR = Decay(_vm.MeterR, eng.PeakR); eng.PeakR = 0;
            if (eng.Clipped) _vm.Clip = true;

            _vm.PositionSec = (double)pos / sr;
            _vm.TimeText = $"{TrackViewModel.FormatTime((double)pos / sr)} / {TrackViewModel.FormatTime((double)len / sr)}";
            _vm.RecordText = _recorder != null ? "REC " + TrackViewModel.FormatTime(_recorder.RecordedSeconds) : "";

            if (_vm.IsPlaying && len > 0 && pos >= len)
            {
                PausePlayback();
                if (_recorder != null) _ = StopRecordingAsync();
                eng.Seek(0);
                _vm.Status = "Fin du morceau.";
            }
        }

        private static double Decay(double current, float peak)
        {
            double v = Math.Max(peak, current * 0.82);
            return v < 1e-4 ? 0 : v;
        }

        // =====================================================================
        //  Ajout / suppression de stems
        // =====================================================================

        private async void AddStems_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = AudioFilter, Multiselect = true, Title = "Ajouter des stems" };
            if (dlg.ShowDialog(this) == true) await AddFilesAsync(dlg.FileNames);
        }

        private async Task AddFilesAsync(IEnumerable<string> paths)
        {
            if (_vm.IsBusy) return;
            var files = paths.Where(p => AudioExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
            if (files.Count == 0) { _vm.Status = "Aucun fichier audio reconnu (WAV, MP3, AIFF, FLAC…)."; return; }

            _vm.IsBusy = true;
            var errors = new List<string>();
            var autoRepeat = new List<TrackViewModel>();
            try
            {
                if (_engine == null || _vm.Tracks.Count == 0)
                {
                    int sr = 44100;
                    try { sr = await Task.Run(() => StemData.ProbeSampleRate(files[0])); } catch { }
                    if (_engine == null || _vm.Tracks.Count == 0) EnsureEngine(sr);
                }
                int rate = _engine!.SampleRate;
                var added = new List<TrackViewModel>();

                for (int i = 0; i < files.Count; i++)
                {
                    string f = files[i];
                    _vm.Status = $"Chargement {i + 1}/{files.Count} : {Path.GetFileName(f)}…";
                    _vm.Progress = (double)i / files.Count;
                    try
                    {
                        var stem = await Task.Run(() => StemData.Load(f, rate));
                        added.Add(AddTrack(stem, new TrackSettings()));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(f)} : {ex.Message}");
                    }
                }
                SyncEngineTracks();
                MarkDirty();
                // stems nettement plus courts que le morceau : répétés automatiquement sur toute la longueur
                var auto = AutoRepeatShortStems(added);
                autoRepeat.AddRange(auto);
                SyncEngineTracks();
                _vm.Status = auto.Count > 0
                    ? $"{string.Join(", ", auto.Select(t => "« " + t.Name + " »"))} : stem court, répété sur tout le morceau (bouton ∞ Répéter pour désactiver)."
                    : $"{_vm.Tracks.Count} stem(s) — {rate / 1000.0:0.#} kHz. Espace = lecture, Maj+glisser sur une forme d'onde = boucle.";
            }
            finally
            {
                _vm.IsBusy = false;
                _vm.Progress = 0;
            }
            if (errors.Count > 0)
                MessageBox.Show(this, "Certains fichiers n'ont pas pu être chargés :\n\n" + string.Join("\n", errors),
                    "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Warning);

            // stems courts : calage automatique sur les mesures du morceau
            foreach (var t in autoRepeat) await AlignTrackAsync(t);
        }

        private TrackViewModel AddTrack(StemData stem, TrackSettings settings)
        {
            var tvm = new TrackViewModel(_vm, stem, settings) { ColorHex = Palette[_vm.Tracks.Count % Palette.Length] };
            tvm.PropertyChanged += Track_PropertyChanged;
            _vm.Tracks.Add(tvm);
            return tvm;
        }

        /// <summary>Active « Répéter » sur les stems ajoutés qui durent moins de la moitié du morceau (et moins d'une minute).</summary>
        private List<TrackViewModel> AutoRepeatShortStems(IEnumerable<TrackViewModel> candidates)
        {
            var result = new List<TrackViewModel>();
            var list = candidates.ToList();
            if (_vm.Tracks.Count < 2) return result;
            long longest = _vm.Tracks.Max(t => t.Stem.Frames);
            foreach (var t in list)
            {
                if (t.Stem.Frames < longest / 2 && t.Stem.DurationSec <= 60 && !t.LoopEnabled)
                {
                    t.RepeatWholeFile = true;
                    result.Add(t);
                }
            }
            return result;
        }

        private void RepeatToggle_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t) return;
            UpdateTimeline();
            _vm.Status = t.RepeatWholeFile
                ? $"« {t.Name} » est répété sur toute la longueur du morceau. {t.RepeatInfo}"
                : $"« {t.Name} » joue une seule fois.";
        }

        private void RemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t) return;
            t.PropertyChanged -= Track_PropertyChanged;
            _vm.Tracks.Remove(t);
            SyncEngineTracks();
            MarkDirty();
        }

        private void ClearTracks()
        {
            if (_recorder != null) StopRecordingSync();
            PausePlayback();
            foreach (var t in _vm.Tracks) t.PropertyChanged -= Track_PropertyChanged;
            _vm.Tracks.Clear();
            SyncEngineTracks();
            _engine?.Seek(0);
        }

        private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null || TrackViewModel.TransientProperties.Contains(e.PropertyName)) return;
            if (e.PropertyName is nameof(TrackViewModel.Mute) or nameof(TrackViewModel.Solo)) UpdateDimmed();
            if (e.PropertyName == nameof(TrackViewModel.RepeatWholeFile)) UpdateTimeline();
            MarkDirty();
        }

        private void Main_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.DurationOverrideSec)) UpdateTimeline();
            if (e.PropertyName == nameof(MainViewModel.Bpm) && !_vm.IsBusy && _vm.Tracks.Any(t => t.IsAligned))
                _vm.Status = "Tempo modifié : cliquez « 🎯 Caler » sur les boucles pour les recaler au nouveau tempo.";
            if (e.PropertyName != null && MainDirtyProps.Contains(e.PropertyName)) MarkDirty();
        }

        // =====================================================================
        //  Transport
        // =====================================================================

        private void StartPlayback()
        {
            if (_engine == null || _vm.Tracks.Count == 0) return;
            long len = _engine.LengthFrames;
            if (len > 0 && _engine.Position >= len) _engine.Seek(0);
            _engine.Paused = false;
            _vm.IsPlaying = true;
        }

        private void PausePlayback()
        {
            if (_engine != null) _engine.Paused = true;
            _vm.IsPlaying = false;
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (_vm.IsPlaying) PausePlayback(); else StartPlayback();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            if (_recorder != null) _ = StopRecordingAsync();
            _engine?.Seek(0);
        }

        private void Rewind_Click(object sender, RoutedEventArgs e) => _engine?.Seek(0);

        private void SeekSeconds(double sec)
        {
            if (_engine == null) return;
            _engine.Seek((long)(Math.Max(0, sec) * _engine.SampleRate));
        }

        private void Waveform_SeekRequested(object? sender, WaveformSeekEventArgs e) => SeekSeconds(e.Seconds);

        private void Waveform_LoopSelected(object? sender, WaveformLoopEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TrackViewModel t)
            {
                t.SetLoopRegion(e.StartSec, e.EndSec);
                _vm.Status = $"Boucle « {t.Name} » : {t.LoopStartSec:0.000} s → {t.LoopEndSec:0.000} s ({t.LoopInfo})";
            }
        }

        private void Clip_Click(object sender, MouseButtonEventArgs e)
        {
            if (_engine != null) _engine.Clipped = false;
            _vm.Clip = false;
        }

        private void Solo_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Ctrl + clic : solo exclusif
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t) return;
            foreach (var o in _vm.Tracks) o.Solo = ReferenceEquals(o, t);
            e.Handled = true;
        }

        // =====================================================================
        //  Enregistrement en direct
        // =====================================================================

        private async void Record_Click(object sender, RoutedEventArgs e)
        {
            if (_recorder != null) await StopRecordingAsync();
            else StartRecording();
            if (sender is ToggleButton tb) tb.SetCurrentValue(ToggleButton.IsCheckedProperty, _vm.IsRecording);
        }

        private void StartRecording()
        {
            if (_engine == null || _vm.Tracks.Count == 0)
            {
                _vm.Status = "Ajoutez des stems avant d'enregistrer.";
                return;
            }
            string baseName = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : "Mix";
            var dlg = new SaveFileDialog
            {
                Title = "Enregistrer la prise en direct",
                Filter = "WAV 24 bits (*.wav)|*.wav|MP3 320 kbps (*.mp3)|*.mp3",
                FileName = $"{baseName} - prise {DateTime.Now:yyyy-MM-dd HH'h'mm}",
            };
            if (_projectPath != null) dlg.InitialDirectory = Path.GetDirectoryName(_projectPath) ?? "";
            if (dlg.ShowDialog(this) != true) return;

            _recordTarget = dlg.FileName;
            bool mp3 = Path.GetExtension(_recordTarget).Equals(".mp3", StringComparison.OrdinalIgnoreCase);
            string wavPath = mp3 ? Path.Combine(Path.GetTempPath(), $"tracciastems_rec_{Guid.NewGuid():N}.wav") : _recordTarget;
            try
            {
                _recorder = new LiveRecorder(wavPath, _engine.SampleRate, 24);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible de créer le fichier :\n" + ex.Message, "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _engine.Recorder = _recorder;
            _vm.IsRecording = true;
            _vm.Status = "● Enregistrement en cours — tout ce que vous entendez est capturé (pause = pause de l'enregistrement).";
            if (!_vm.IsPlaying) StartPlayback();
        }

        private async Task StopRecordingAsync()
        {
            var rec = _recorder;
            string? target = _recordTarget;
            if (rec == null || _engine == null) return;
            _engine.Recorder = null;
            _recorder = null;
            _vm.IsRecording = false;
            _vm.RecordText = "";
            double secs = rec.RecordedSeconds;
            await Task.Run(rec.Stop);

            if (target != null && !string.Equals(rec.WavPath, target, StringComparison.OrdinalIgnoreCase))
            {
                _vm.IsBusy = true;
                _vm.Status = "Encodage MP3 de la prise…";
                try
                {
                    await Task.Run(() => Exporter.WavToMp3(rec.WavPath, target, 320));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Échec de l'encodage MP3 :\n" + ex.Message + "\n\nLa prise WAV est conservée ici :\n" + rec.WavPath,
                        "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Error);
                    _vm.IsBusy = false;
                    return;
                }
                finally { _vm.IsBusy = false; }
                try { File.Delete(rec.WavPath); } catch { }
            }
            _vm.Status = $"Prise enregistrée ({TrackViewModel.FormatTime(secs)}) : {target}";
        }

        /// <summary>Arrêt immédiat (fermeture de la fenêtre / nouveau projet) : garde le WAV sans encodage MP3.</summary>
        private void StopRecordingSync()
        {
            var rec = _recorder;
            if (rec == null) return;
            if (_engine != null) _engine.Recorder = null;
            _recorder = null;
            _vm.IsRecording = false;
            rec.Stop();
            if (_recordTarget != null && !string.Equals(rec.WavPath, _recordTarget, StringComparison.OrdinalIgnoreCase))
            {
                try { Exporter.WavToMp3(rec.WavPath, _recordTarget, 320); File.Delete(rec.WavPath); } catch { }
            }
        }

        // =====================================================================
        //  Export
        // =====================================================================

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null || _vm.Tracks.Count == 0) { _vm.Status = "Rien à exporter : ajoutez des stems."; return; }
            string baseName = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : "Mix";
            var dlg = new SaveFileDialog
            {
                Title = "Exporter le mix",
                Filter = "WAV 24 bits (*.wav)|*.wav|WAV 16 bits - qualité CD (*.wav)|*.wav|WAV 32 bits flottant (*.wav)|*.wav|MP3 320 kbps (*.mp3)|*.mp3|MP3 192 kbps (*.mp3)|*.mp3",
                FileName = baseName + " - mix",
            };
            if (_projectPath != null) dlg.InitialDirectory = Path.GetDirectoryName(_projectPath) ?? "";
            if (dlg.ShowDialog(this) != true) return;

            (ExportFormat fmt, int kbps) = dlg.FilterIndex switch
            {
                2 => (ExportFormat.Wav16, 0),
                3 => (ExportFormat.Wav32Float, 0),
                4 => (ExportFormat.Mp3, 320),
                5 => (ExportFormat.Mp3, 192),
                _ => (ExportFormat.Wav24, 0),
            };
            string path = dlg.FileName;
            string wantedExt = fmt == ExportFormat.Mp3 ? ".mp3" : ".wav";
            if (!Path.GetExtension(path).Equals(wantedExt, StringComparison.OrdinalIgnoreCase))
                path = Path.ChangeExtension(path, wantedExt);

            // réglages figés pendant le rendu
            var tracks = _vm.Tracks.Select(t => (t.Stem, ProjectFile.Clone(t.Settings))).ToList();
            var master = ProjectFile.Clone(_vm.Master);
            int sr = _engine.SampleRate;

            _cts = new CancellationTokenSource();
            _vm.IsBusy = true;
            _vm.Status = "Export en cours…";
            var progress = new Progress<double>(p => { _vm.Progress = p; _vm.Status = $"Export en cours… {p * 100:0} %"; });
            var sw = Stopwatch.StartNew();
            try
            {
                await Task.Run(() => Exporter.Export(tracks, master, sr, path, fmt, kbps, progress, _cts.Token));
                _vm.Status = $"Mix exporté en {sw.Elapsed.TotalSeconds:0.0} s : {path}";
                if (MessageBox.Show(this, $"Mix exporté :\n{path}\n\nOuvrir le dossier ?", "Export terminé",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (OperationCanceledException)
            {
                _vm.Status = "Export annulé.";
            }
            catch (Exception ex)
            {
                _vm.Status = "Échec de l'export.";
                MessageBox.Show(this, "Échec de l'export :\n" + ex.Message, "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsBusy = false;
                _vm.Progress = 0;
                _cts.Dispose();
                _cts = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        // =====================================================================
        //  Tempo
        // =====================================================================

        private async void DetectTrackBpm_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t || t.IsDetecting) return;
            t.IsDetecting = true;
            try
            {
                var r = await Task.Run(() => BpmDetector.Detect(t.Stem.Samples, t.Stem.SampleRate));
                t.DetectedBpm = r?.Bpm;
                _vm.Status = r == null
                    ? $"« {t.Name} » : pas de tempo détectable (stem trop calme ou trop court)."
                    : $"« {t.Name} » : {r.Bpm:0.#} BPM (fiabilité {r.Confidence * 100:0} %). Cliquez « → tempo projet » pour l'appliquer.";
            }
            catch (Exception ex) { _vm.Status = "Détection impossible : " + ex.Message; }
            finally { t.IsDetecting = false; }
        }

        private async void DetectMixBpm_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.IsBusy || !await DetectGridAsync()) return;
            // les boucles déjà calées suivent le nouveau tempo / la nouvelle grille
            foreach (var t in _vm.Tracks.Where(t => t.IsAligned).ToList()) await AlignTrackAsync(t);
        }

        /// <summary>
        /// Détecte tempo + grille (1er temps de mesure) sur les stems « normaux » (hors stems répétés).
        /// </summary>
        private async Task<bool> DetectGridAsync()
        {
            if (_vm.Tracks.Count == 0 || _engine == null) return false;
            var pool = _vm.Tracks.Where(t => !t.RepeatWholeFile).ToList();
            if (pool.Count == 0) pool = _vm.Tracks.ToList();
            var audible = pool.Where(t => !t.IsDimmed).ToList();
            var sources = (audible.Count > 0 ? audible : pool).Select(t => t.Stem).ToList();
            int sr = _engine.SampleRate;
            int bpb = _vm.BeatsPerBar;
            _vm.IsBusy = true;
            _vm.Status = "Détection du tempo et des mesures…";
            try
            {
                var (r, g) = await Task.Run(() =>
                {
                    long frames = Math.Min(sources.Max(s => s.Frames), (long)sr * 150);
                    var sum = new float[frames * 2];
                    foreach (var s in sources)
                    {
                        long n = Math.Min(frames * 2, s.Samples.LongLength);
                        for (long i = 0; i < n; i++) sum[i] += s.Samples[i];
                    }
                    var bpm = BpmDetector.Detect(sum, sr);
                    var grid = bpm == null ? null : BpmDetector.DetectGrid(sum, sr, bpm.Bpm, bpb, 150);
                    return (bpm, grid);
                });
                if (r == null) { _vm.Status = "Tempo non détecté."; return false; }
                _vm.Bpm = r.Bpm;
                if (g != null)
                {
                    _vm.GridOffsetSec = g.DownbeatOffsetSec;
                    _vm.Master.GridDetected = true;
                }
                _vm.Status = $"Tempo : {r.Bpm:0.#} BPM (fiabilité {r.Confidence * 100:0} %), 1er temps à {_vm.GridOffsetSec:0.000} s. ÷2 / ×2 et ◀ ▶ pour corriger.";
                return true;
            }
            finally { _vm.IsBusy = false; }
        }

        /// <summary>Cale un stem répété sur les mesures (détecte d'abord la grille du morceau si besoin).</summary>
        private async Task AlignTrackAsync(TrackViewModel t)
        {
            if (_engine == null || _vm.IsBusy) return;
            if (!_vm.Master.GridDetected && _vm.Tracks.Any(x => !x.RepeatWholeFile))
                if (!await DetectGridAsync()) return;
            double bpm = _vm.Bpm, grid = _vm.GridOffsetSec;
            int bpb = _vm.BeatsPerBar;
            var original = t.OriginalStem;
            _vm.IsBusy = true;
            _vm.Status = $"Calage de « {t.Name} » sur les mesures…";
            try
            {
                var r = await Task.Run(() => LoopAligner.Align(original, bpm, bpb, grid));
                t.ApplyAlignment(r);
                SyncEngineTracks();
                string stretch = Math.Abs(r.StretchPercent) < 0.05 ? "durée déjà exacte"
                    : $"tempo {r.OriginalBpm:0.#} → {bpm:0.##} BPM ({r.StretchPercent:+0.0;-0.0} %)";
                _vm.Status = $"« {t.Name} » calé : {r.Bars} mesure(s), {stretch}, 1er temps aligné. ◀ ▶ pour décaler d'un temps.";
                if (Math.Abs(r.StretchPercent) > 25)
                    _vm.Status += " Attention : écart de tempo important, vérifiez le BPM du projet (÷2 / ×2).";
            }
            catch (Exception ex) { _vm.Status = $"Calage impossible pour « {t.Name} » : {ex.Message}"; }
            finally { _vm.IsBusy = false; }
        }

        private async void AlignTrack_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is TrackViewModel t) await AlignTrackAsync(t);
        }

        private void NudgeRepeatLeft_Click(object sender, RoutedEventArgs e) => NudgeRepeat(sender, -1);
        private void NudgeRepeatRight_Click(object sender, RoutedEventArgs e) => NudgeRepeat(sender, +1);

        private void NudgeRepeat(object sender, int beats)
        {
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t) return;
            t.NudgeRepeat(beats);
            _vm.Status = $"« {t.Name} » : départ de la boucle à {t.RepeatPhaseSec:0.000} s.";
        }

        private void ClearAlign_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t) return;
            t.ClearAlignment();
            SyncEngineTracks();
            _vm.Status = $"« {t.Name} » : calage annulé (fichier d'origine).";
        }

        private void GridLeft_Click(object sender, RoutedEventArgs e) => ShiftGrid(-1);
        private void GridRight_Click(object sender, RoutedEventArgs e) => ShiftGrid(+1);

        /// <summary>Décale la grille d'un temps ; les boucles calées suivent.</summary>
        private void ShiftGrid(int beats)
        {
            double delta = beats * _vm.BeatSeconds;
            _vm.GridOffsetSec = _vm.GridOffsetSec + delta;
            foreach (var t in _vm.Tracks.Where(t => t.IsAligned)) t.RepeatPhaseSec = t.RepeatPhaseSec + delta;
            _vm.Status = _vm.GridText + " — les boucles calées ont suivi.";
        }

        private void UseTrackBpm_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is TrackViewModel t && t.DetectedBpm is double b)
            {
                _vm.Bpm = b;
                _vm.Status = $"Tempo du projet : {b:0.##} BPM";
            }
        }

        private void BpmHalf_Click(object sender, RoutedEventArgs e) => _vm.Bpm = _vm.Bpm / 2;
        private void BpmDouble_Click(object sender, RoutedEventArgs e) => _vm.Bpm = _vm.Bpm * 2;

        /// <summary>Position de lecture « audible » : on reste dans la boucle déjà active si on en a une.</summary>
        private double PlayheadSec => _engine?.PositionSec ?? 0;

        private void LoopToggle_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not TrackViewModel t) return;
            // le ToggleButton a déjà basculé LoopEnabled : si on vient de l'activer, la boucle démarre ici
            if (t.LoopEnabled)
            {
                t.StartLoopAt(PlayheadSec, t.LoopBars);
                _vm.Status = $"Boucle « {t.Name} » : {t.LoopStartSec:0.000} s → {t.LoopEndSec:0.000} s ({t.LoopInfo})";
            }
            else _vm.Status = $"Boucle « {t.Name} » désactivée : le stem reprend le morceau.";
        }

        private void LoopAllHere_Click(object sender, RoutedEventArgs e) => LoopAllHere();

        private void LoopAllHere()
        {
            if (_engine == null || _vm.Tracks.Count == 0) return;
            if (_vm.Tracks.Any(t => t.LoopEnabled))
            {
                foreach (var t in _vm.Tracks) t.LoopEnabled = false;
                _vm.Status = "Boucles désactivées : reprise du morceau.";
                return;
            }
            double pos = PlayheadSec;
            int bars = _vm.GlobalLoopBars;
            foreach (var t in _vm.Tracks)
                if (pos < t.Stem.DurationSec) t.StartLoopAt(pos, bars);
            var first = _vm.Tracks.FirstOrDefault(t => t.LoopEnabled);
            _vm.Status = first != null
                ? $"Tous les stems bouclent : {first.LoopStartSec:0.000} s → {first.LoopEndSec:0.000} s ({first.LoopInfo}). L ou « Tout boucler ici » pour reprendre."
                : "Position au-delà de la fin des stems.";
        }

        // =====================================================================
        //  Projet
        // =====================================================================

        private void MarkDirty()
        {
            if (_dirty) return;
            _dirty = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : "Nouveau projet";
            Title = $"TracciaStems — {name}{(_dirty && _vm.Tracks.Count > 0 ? " *" : "")}";
        }

        /// <returns>false si l'utilisateur annule.</returns>
        private bool ConfirmDiscard()
        {
            if (!_dirty || _vm.Tracks.Count == 0) return true;
            var r = MessageBox.Show(this, "Enregistrer les modifications du projet en cours ?", "TracciaStems",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return SaveProject(false);
            return true;
        }

        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            ClearTracks();
            _vm.ReplaceMaster(new MasterSettings());
            _output.Detach();
            _engine = null;
            _vm.SampleRate = 0;
            _vm.TimeText = "00:00.0 / 00:00.0";
            _projectPath = null;
            _dirty = false;
            UpdateTitle();
            _vm.Status = "Nouveau projet.";
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(false);
        private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProject(true);

        private bool SaveProject(bool saveAs)
        {
            if (_vm.Tracks.Count == 0) { _vm.Status = "Projet vide : rien à enregistrer."; return true; }
            string? path = _projectPath;
            if (saveAs || path == null)
            {
                var dlg = new SaveFileDialog { Filter = ProjectFilter, Title = "Enregistrer le projet", FileName = path != null ? Path.GetFileName(path) : "Mon mix" };
                if (path == null)
                {
                    // propose le dossier des stems : le projet y retrouvera ses fichiers même s'il est déplacé avec eux
                    string? dir = Path.GetDirectoryName(_vm.Tracks[0].Stem.FilePath);
                    if (dir != null) dlg.InitialDirectory = dir;
                }
                if (dlg.ShowDialog(this) != true) return false;
                path = dlg.FileName;
            }
            try
            {
                var p = new ProjectFile
                {
                    SampleRate = _vm.SampleRate,
                    Master = _vm.Master,
                    Tracks = _vm.Tracks.Select(t => t.Settings).ToList(),
                };
                p.Save(path);
                _projectPath = path;
                _dirty = false;
                UpdateTitle();
                _vm.Status = "Projet enregistré : " + path;
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible d'enregistrer le projet :\n" + ex.Message, "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            var dlg = new OpenFileDialog { Filter = OpenProjectFilter, Title = "Ouvrir un projet" };
            if (dlg.ShowDialog(this) == true) await LoadProjectAsync(dlg.FileName);
        }

        private async Task LoadProjectAsync(string path)
        {
            if (_vm.IsBusy) return;
            ProjectFile p;
            try { p = ProjectFile.Load(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Projet illisible :\n" + ex.Message, "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // fichiers introuvables : proposer de les localiser
            var tracks = new List<TrackSettings>();
            foreach (var t in p.Tracks)
            {
                if (!File.Exists(t.FilePath))
                {
                    var r = MessageBox.Show(this, $"Stem introuvable :\n{t.FilePath}\n\nVoulez-vous le localiser ? (Non = ignorer cette piste)",
                        "TracciaStems", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r != MessageBoxResult.Yes) continue;
                    var dlg = new OpenFileDialog { Filter = AudioFilter, Title = "Localiser " + t.Name, FileName = Path.GetFileName(t.FilePath) };
                    if (dlg.ShowDialog(this) != true) continue;
                    t.FilePath = dlg.FileName;
                }
                tracks.Add(t);
            }

            ClearTracks();
            _vm.ReplaceMaster(p.Master);
            int sr = p.SampleRate;
            if (sr <= 0 && tracks.Count > 0)
            {
                try { sr = StemData.ProbeSampleRate(tracks[0].FilePath); } catch { sr = 44100; }
            }
            if (sr <= 0) sr = 44100;
            EnsureEngine(sr);

            _vm.IsBusy = true;
            var errors = new List<string>();
            try
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    _vm.Status = $"Chargement {i + 1}/{tracks.Count} : {Path.GetFileName(t.FilePath)}…";
                    _vm.Progress = (double)i / Math.Max(1, tracks.Count);
                    try
                    {
                        var stem = await Task.Run(() => StemData.Load(t.FilePath, sr));
                        double alignedLen = t.AlignedLengthSec;
                        var tvm = AddTrack(stem, t);
                        if (alignedLen > 0)
                            tvm.ReplaceStem(await Task.Run(() => LoopAligner.StretchTo(stem, alignedLen)));
                    }
                    catch (Exception ex) { errors.Add($"{Path.GetFileName(t.FilePath)} : {ex.Message}"); }
                }
                SyncEngineTracks();
            }
            finally
            {
                _vm.IsBusy = false;
                _vm.Progress = 0;
            }
            _projectPath = path;
            _dirty = errors.Count > 0 || tracks.Count != p.Tracks.Count;
            UpdateTitle();
            _vm.Status = $"Projet ouvert : {Path.GetFileName(path)} — {_vm.Tracks.Count} stem(s).";
            if (errors.Count > 0)
                MessageBox.Show(this, "Certains stems n'ont pas pu être chargés :\n\n" + string.Join("\n", errors),
                    "TracciaStems", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // =====================================================================
        //  Divers interface
        // =====================================================================

        private void Slider_ResetOnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider s && s.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                s.Value = v;
                e.Handled = true;
            }
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (e.Key == Key.Enter)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                tb.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.S) { SaveProject(false); e.Handled = true; return; }
            if (ctrl && e.Key == Key.O) { AddStems_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox) return;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); e.Handled = true; break;
                case Key.Home: _engine?.Seek(0); e.Handled = true; break;
                case Key.L: LoopAllHere(); e.Handled = true; break;
                case Key.R:
                    Record_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            string? project = files.FirstOrDefault(f => Path.GetExtension(f).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)
                                                      || Path.GetExtension(f).Equals(ProjectFile.LegacyExtension, StringComparison.OrdinalIgnoreCase));
            if (project != null)
            {
                if (ConfirmDiscard()) await LoadProjectAsync(project);
                return;
            }
            // un dossier déposé : on prend les fichiers audio qu'il contient
            var all = new List<string>();
            foreach (var f in files)
            {
                if (Directory.Exists(f)) all.AddRange(Directory.GetFiles(f).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
                else all.Add(f);
            }
            await AddFilesAsync(all);
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_vm.IsBusy && _cts != null)
            {
                if (MessageBox.Show(this, "Un export est en cours. Quitter quand même ?", "TracciaStems", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            if (_recorder != null) StopRecordingSync();
            if (!ConfirmDiscard()) e.Cancel = true;
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _timer.Stop();
            _output.Dispose();
        }
    }
}
