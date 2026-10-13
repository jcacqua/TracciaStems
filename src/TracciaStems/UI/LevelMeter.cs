using System;
using System.Windows;
using System.Windows.Media;

namespace TracciaStems.UI
{
    /// <summary>Vu-mètre stéréo vertical (échelle en dB de -60 à +3).</summary>
    public sealed class LevelMeter : FrameworkElement
    {
        public static readonly DependencyProperty LevelLProperty = DependencyProperty.Register(nameof(LevelL), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty LevelRProperty = DependencyProperty.Register(nameof(LevelR), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double LevelL { get => (double)GetValue(LevelLProperty); set => SetValue(LevelLProperty, value); }
        public double LevelR { get => (double)GetValue(LevelRProperty); set => SetValue(LevelRProperty, value); }

        private const double MinDb = -60, MaxDb = 3;
        private static readonly Brush Bg = Make(Color.FromRgb(0x10, 0x12, 0x16));
        private static readonly Brush Fill;

        static LevelMeter()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            double y6 = Frac(-6), y0 = Frac(0);
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0xCC, 0x71), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0xCC, 0x71), y6 - 0.15));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xF1, 0xC4, 0x0F), y6));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xF3, 0x9C, 0x12), y0 - 0.01));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE7, 0x4C, 0x3C), y0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE7, 0x4C, 0x3C), 1));
            g.Freeze();
            Fill = g;
        }

        private static Brush Make(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static double Frac(double db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);
        private static double LinToFrac(double lin) => lin <= 1e-6 ? 0 : Frac(20 * Math.Log10(lin));

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
            double gap = 1, bw = (w - gap) / 2;
            DrawBar(dc, 0, bw, h, LinToFrac(LevelL));
            DrawBar(dc, bw + gap, bw, h, LinToFrac(LevelR));
        }

        private static void DrawBar(DrawingContext dc, double x, double bw, double h, double frac)
        {
            if (frac <= 0) return;
            double top = h * (1 - frac);
            dc.PushClip(new RectangleGeometry(new Rect(x, top, bw, h - top)));
            dc.DrawRectangle(Fill, null, new Rect(x, 0, bw, h));
            dc.Pop();
        }
    }
}
