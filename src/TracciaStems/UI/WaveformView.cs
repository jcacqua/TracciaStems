using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TracciaStems.Audio;

namespace TracciaStems.UI
{
    public sealed class WaveformSeekEventArgs : EventArgs
    {
        public double Seconds { get; }
        public WaveformSeekEventArgs(double s) { Seconds = s; }
    }

    public sealed class WaveformLoopEventArgs : EventArgs
    {
        public double StartSec { get; }
        public double EndSec { get; }
        public WaveformLoopEventArgs(double a, double b) { StartSec = a; EndSec = b; }
    }

    /// <summary>
    /// Forme d'onde d'un stem sur la ligne de temps du morceau, avec boucle (répétitions affichées),
    /// grille de tempo et tête de lecture.
    /// Clic = déplacer la lecture ; Maj + glisser = définir la boucle.
    /// </summary>
    public sealed class WaveformView : FrameworkElement
    {
        // --------- propriétés ---------
        public static readonly DependencyProperty PeaksProperty = Reg(nameof(Peaks), typeof(object), null);
        public static readonly DependencyProperty StemFramesProperty = Reg(nameof(StemFrames), typeof(long), 0L);
        public static readonly DependencyProperty TimelineFramesProperty = Reg(nameof(TimelineFrames), typeof(double), 0.0);
        public static readonly DependencyProperty SampleRateProperty = Reg(nameof(SampleRate), typeof(int), 44100);
        public static readonly DependencyProperty PositionFramesProperty = Reg(nameof(PositionFrames), typeof(double), 0.0);
        public static readonly DependencyProperty LoopEnabledProperty = Reg(nameof(LoopEnabled), typeof(bool), false);
        public static readonly DependencyProperty LoopStartSecProperty = Reg(nameof(LoopStartSec), typeof(double), 0.0);
        public static readonly DependencyProperty LoopEndSecProperty = Reg(nameof(LoopEndSec), typeof(double), 0.0);
        public static readonly DependencyProperty BpmProperty = Reg(nameof(Bpm), typeof(double), 0.0);
        public static readonly DependencyProperty BeatsPerBarProperty = Reg(nameof(BeatsPerBar), typeof(int), 4);
        public static readonly DependencyProperty RepeatModeProperty = Reg(nameof(RepeatMode), typeof(bool), false);
        public static readonly DependencyProperty RepeatPhaseSecProperty = Reg(nameof(RepeatPhaseSec), typeof(double), 0.0);
        public static readonly DependencyProperty GridOffsetSecProperty = Reg(nameof(GridOffsetSec), typeof(double), 0.0);
        public static readonly DependencyProperty IsDimmedProperty = Reg(nameof(IsDimmed), typeof(bool), false);
        public static readonly DependencyProperty WaveColorProperty = Reg(nameof(WaveColor), typeof(Color), Color.FromRgb(0x4F, 0xB3, 0xFF));

        private static DependencyProperty Reg(string name, Type t, object? def) =>
            DependencyProperty.Register(name, t, typeof(WaveformView),
                new FrameworkPropertyMetadata(def!, FrameworkPropertyMetadataOptions.AffectsRender, OnAnyChanged));

        // typé object : le compilateur XAML refuse les propriétés de type tableau dans un DataTemplate (MC4102)
        public object? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value!); }
        public long StemFrames { get => (long)GetValue(StemFramesProperty); set => SetValue(StemFramesProperty, value); }
        public double TimelineFrames { get => (double)GetValue(TimelineFramesProperty); set => SetValue(TimelineFramesProperty, value); }
        public int SampleRate { get => (int)GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }
        public double PositionFrames { get => (double)GetValue(PositionFramesProperty); set => SetValue(PositionFramesProperty, value); }
        public bool LoopEnabled { get => (bool)GetValue(LoopEnabledProperty); set => SetValue(LoopEnabledProperty, value); }
        public double LoopStartSec { get => (double)GetValue(LoopStartSecProperty); set => SetValue(LoopStartSecProperty, value); }
        public double LoopEndSec { get => (double)GetValue(LoopEndSecProperty); set => SetValue(LoopEndSecProperty, value); }
        public double Bpm { get => (double)GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
        public int BeatsPerBar { get => (int)GetValue(BeatsPerBarProperty); set => SetValue(BeatsPerBarProperty, value); }
        public bool RepeatMode { get => (bool)GetValue(RepeatModeProperty); set => SetValue(RepeatModeProperty, value); }
        public double RepeatPhaseSec { get => (double)GetValue(RepeatPhaseSecProperty); set => SetValue(RepeatPhaseSecProperty, value); }
        public double GridOffsetSec { get => (double)GetValue(GridOffsetSecProperty); set => SetValue(GridOffsetSecProperty, value); }
        public bool IsDimmed { get => (bool)GetValue(IsDimmedProperty); set => SetValue(IsDimmedProperty, value); }
        public Color WaveColor { get => (Color)GetValue(WaveColorProperty); set => SetValue(WaveColorProperty, value); }

        public event EventHandler<WaveformSeekEventArgs>? SeekRequested;
        public event EventHandler<WaveformLoopEventArgs>? LoopSelected;

        // --------- cache de rendu ---------
        private DrawingGroup? _cache;
        private Size _cacheSize;

        private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.Property != PositionFramesProperty) ((WaveformView)d)._cache = null;
        }

        public WaveformView()
        {
            ClipToBounds = true;
            Cursor = Cursors.IBeam;
            Focusable = false;
            ToolTip = "Clic : déplacer la lecture  •  Maj + glisser : définir la boucle de ce stem";
        }

        private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1F)));
        private static readonly Pen BeatPen = FrozenPen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 1);
        private static readonly Pen BarPen = FrozenPen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1);
        private static readonly Brush LoopBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x9F, 0x1C)));
        private static readonly Brush LoopRepeatBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0x9F, 0x1C)));
        private static readonly Pen LoopEdgePen = FrozenPen(Color.FromRgb(0xFF, 0x9F, 0x1C), 1.5);
        private static readonly Pen LoopRepeatPen = FrozenPen(Color.FromArgb(0x90, 0xFF, 0x9F, 0x1C), 1);
        private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xFF, 0xFF, 0xFF), 1.5);
        private static readonly Brush SelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        private static readonly Pen CenterPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1);

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }
        private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

        private double FramesPerPixel => ActualWidth > 0 ? Math.Max(1e-9, TimelineFrames) / ActualWidth : 1;
        private double SecToX(double sec) => TimelineFrames > 0 ? sec * SampleRate / TimelineFrames * ActualWidth : 0;
        private double XToSec(double x) => TimelineFrames > 0 ? Math.Clamp(x, 0, ActualWidth) / ActualWidth * TimelineFrames / SampleRate : 0;

        protected override void OnRender(DrawingContext dc)
        {
            var size = new Size(ActualWidth, ActualHeight);
            dc.DrawRectangle(BgBrush, null, new Rect(size));
            if (size.Width < 2 || size.Height < 2) return;

            if (_cache == null || _cacheSize != size)
            {
                _cache = BuildCache(size);
                _cacheSize = size;
            }
            dc.DrawDrawing(_cache);

            if (_selecting)
            {
                double a = Math.Min(_selStartX, _selEndX), b = Math.Max(_selStartX, _selEndX);
                dc.DrawRectangle(SelBrush, null, new Rect(a, 0, Math.Max(1, b - a), size.Height));
            }

            if (TimelineFrames > 0)
            {
                double x = Math.Round(PositionFrames / TimelineFrames * size.Width) + 0.5;
                dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, size.Height));
            }
        }

        private DrawingGroup BuildCache(Size size)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                double w = size.Width, h = size.Height, mid = h / 2;
                int sr = Math.Max(1, SampleRate);
                double timeline = TimelineFrames;
                if (timeline <= 0) goto Done; // ne pas figer le groupe tant que le DrawingContext est ouvert

                // grille de tempo
                double bpm = Bpm;
                if (bpm > 0)
                {
                    double beatPx = 60.0 / bpm * sr / timeline * w;
                    int bpb = Math.Max(1, BeatsPerBar);
                    bool drawBeats = beatPx >= 6;
                    bool drawBars = beatPx * bpb >= 4;
                    if (drawBars)
                    {
                        // la grille part du 1er temps détecté (GridOffsetSec) et s'étend de part et d'autre
                        double offPx = GridOffsetSec * sr / timeline * w;
                        int iStart = -(int)Math.Ceiling(offPx / beatPx);
                        int count = (int)((w - offPx) / beatPx) + 1;
                        for (int i = iStart; i <= count; i++)
                        {
                            bool isBar = ((i % bpb) + bpb) % bpb == 0;
                            if (!isBar && !drawBeats) continue;
                            double x = Math.Round(offPx + i * beatPx) + 0.5;
                            if (x < 0 || x > w) continue;
                            dc.DrawLine(isBar ? BarPen : BeatPen, new Point(x, 0), new Point(x, h));
                        }
                    }
                }
                dc.DrawLine(CenterPen, new Point(0, mid), new Point(w, mid));

                // zone de boucle et répétitions
                long stemFrames = StemFrames;
                bool loop = LoopEnabled;
                long ls = (long)Math.Round(LoopStartSec * sr), le = Math.Min(stemFrames, (long)Math.Round(LoopEndSec * sr));
                if (loop && le - ls < 64) loop = false;
                if (loop)
                {
                    double x0 = (double)ls / timeline * w, x1 = (double)le / timeline * w;
                    dc.DrawRectangle(LoopBrush, null, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
                    dc.DrawLine(LoopEdgePen, new Point(x0, 0), new Point(x0, h));
                    dc.DrawLine(LoopEdgePen, new Point(x1, 0), new Point(x1, h));
                    double lenPx = (double)(le - ls) / timeline * w;
                    if (x1 < w)
                        dc.DrawRectangle(LoopRepeatBrush, null, new Rect(x1, 0, w - x1, h));
                    if (lenPx >= 3)
                        for (double x = x1 + lenPx; x < w; x += lenPx)
                            dc.DrawLine(LoopRepeatPen, new Point(x, 0), new Point(x, h));
                }

                // stem répété : repères à chaque début de fichier
                bool repeat = RepeatMode && stemFrames >= 64;
                long phase = (long)Math.Round(RepeatPhaseSec * sr);
                if (repeat)
                {
                    loop = false;
                    dc.DrawRectangle(LoopRepeatBrush, null, new Rect(0, 0, w, h));
                    double lenPx = (double)stemFrames / timeline * w;
                    double firstPx = (double)TrackProcessor.MapRepeat(0, -phase, stemFrames) / timeline * w; // = phase modulo longueur
                    if (lenPx >= 3)
                        for (double x = firstPx; x < w; x += lenPx)
                            dc.DrawLine(LoopRepeatPen, new Point(x, 0), new Point(x, h));
                }

                // forme d'onde (ce qui est réellement joué, boucle comprise)
                var peaks = Peaks as float[];
                if (peaks != null && peaks.Length > 0)
                {
                    int cols = (int)Math.Ceiling(w);
                    var top = new Point[cols];
                    double fpp = timeline / w;
                    int last = -1;
                    for (int x = 0; x < cols; x++)
                    {
                        long f0 = (long)(x * fpp);
                        long span = Math.Max(1, (long)fpp);
                        long s0 = repeat ? TrackProcessor.MapRepeat(f0, phase, stemFrames) : TrackProcessor.MapPosition(f0, loop, ls, le);
                        double v = 0;
                        if (s0 < stemFrames)
                        {
                            long s1 = s0 + span;
                            if (loop && s0 < le) s1 = Math.Min(s1, le);
                            if (repeat) s1 = Math.Min(s1, stemFrames);
                            s1 = Math.Min(s1, stemFrames);
                            int b0 = (int)(s0 / StemData.PeakBlock);
                            int b1 = (int)Math.Max(b0, (s1 - 1) / StemData.PeakBlock);
                            b1 = Math.Min(b1, peaks.Length - 1);
                            float m = 0;
                            for (int b = b0; b <= b1; b++) if (peaks[b] > m) m = peaks[b];
                            v = Math.Min(1.0, m);
                            last = x;
                        }
                        top[x] = new Point(x, mid - v * (mid - 2));
                    }
                    if (last >= 0)
                    {
                        var geo = new StreamGeometry();
                        using (var g = geo.Open())
                        {
                            g.BeginFigure(new Point(0, mid), true, true);
                            for (int x = 0; x <= last; x++) g.LineTo(top[x], false, false);
                            for (int x = last; x >= 0; x--) g.LineTo(new Point(x, 2 * mid - top[x].Y), false, false);
                        }
                        geo.Freeze();
                        var c = WaveColor;
                        if (IsDimmed) c = Color.FromArgb(0x60, c.R, c.G, c.B);
                        var fill = new SolidColorBrush(c);
                        fill.Freeze();
                        dc.DrawGeometry(fill, null, geo);
                    }
                }
            }
        Done:
            group.Freeze();
            return group;
        }

        // --------- souris ---------
        private bool _seeking, _selecting;
        private double _selStartX, _selEndX;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (TimelineFrames <= 0) return;
            double x = e.GetPosition(this).X;
            CaptureMouse();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _selecting = true;
                _selStartX = _selEndX = x;
                InvalidateVisual();
            }
            else
            {
                _seeking = true;
                SeekRequested?.Invoke(this, new WaveformSeekEventArgs(XToSec(x)));
            }
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!IsMouseCaptured) return;
            double x = e.GetPosition(this).X;
            if (_selecting) { _selEndX = Math.Clamp(x, 0, ActualWidth); InvalidateVisual(); }
            else if (_seeking) SeekRequested?.Invoke(this, new WaveformSeekEventArgs(XToSec(x)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_selecting)
            {
                _selecting = false;
                double a = XToSec(Math.Min(_selStartX, _selEndX)), b = XToSec(Math.Max(_selStartX, _selEndX));
                LoopSelected?.Invoke(this, new WaveformLoopEventArgs(a, b));
                InvalidateVisual();
            }
            _seeking = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _seeking = false;
            if (_selecting) { _selecting = false; InvalidateVisual(); }
        }
    }
}
