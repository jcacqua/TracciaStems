using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TracciaStems.UI
{
    /// <summary>
    /// Logo TracciaStems animé : les éclairs (4 groupes indépendants) s'allument par salves
    /// de flashs rapides, avec un halo qui s'estompe, comme de vrais éclairs.
    /// Couches : logo sans les éclairs, halos bleutés, éclairs.
    /// </summary>
    public sealed class AnimatedLogo : Grid
    {
        private const int Groups = 4;
        private static BitmapImage? _base;
        private static readonly BitmapImage?[] _bolts = new BitmapImage?[Groups];
        private static readonly BitmapImage?[] _glows = new BitmapImage?[Groups];

        private readonly Image[] _boltImgs = new Image[Groups];
        private readonly Image[] _glowImgs = new Image[Groups];
        private readonly GroupState[] _state = new GroupState[Groups];
        private readonly DispatcherTimer _timer;
        private readonly Random _rnd = new();
        private readonly DateTime _t0 = DateTime.UtcNow;

        /// <summary>Intensité générale de l'animation (0 = éclairs fixes, 1 = normal).</summary>
        public double Intensity { get; set; } = 1.0;

        private sealed class GroupState
        {
            public double NextBurst;                       // instant (s) de la prochaine salve
            public readonly List<(double on, double off, double level)> Pulses = new();
            public double Glow;                            // halo courant (décroît)
        }

        private static BitmapImage Load(string name)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri($"pack://application:,,,/Assets/Logo/{name}.png", UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        public AnimatedLogo()
        {
            _base ??= Load("logo_base");
            for (int g = 0; g < Groups; g++)
            {
                _bolts[g] ??= Load($"bolt_{g}");
                _glows[g] ??= Load($"glow_{g}");
            }

            Children.Add(MakeImage(_base));
            for (int g = 0; g < Groups; g++)
            {
                _glowImgs[g] = MakeImage(_glows[g]!);
                _glowImgs[g].Opacity = 0;
                Children.Add(_glowImgs[g]);
            }
            for (int g = 0; g < Groups; g++)
            {
                _boltImgs[g] = MakeImage(_bolts[g]!);
                _boltImgs[g].Opacity = 0.3;
                Children.Add(_boltImgs[g]);
                _state[g] = new GroupState { NextBurst = 0.3 + _rnd.NextDouble() * 2.0 };
            }

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += (_, _) => Tick();
            IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
            Unloaded += (_, _) => _timer.Stop();
            SnapsToDevicePixels = true;
        }

        private static Image MakeImage(ImageSource src)
        {
            var img = new Image { Source = src, Stretch = Stretch.Uniform, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        private void Tick()
        {
            double t = (DateTime.UtcNow - _t0).TotalSeconds;
            for (int g = 0; g < Groups; g++)
            {
                var s = _state[g];

                // nouvelle salve : 2 à 4 flashs très courts
                if (t >= s.NextBurst && s.Pulses.Count == 0)
                {
                    double start = t;
                    int n = 2 + _rnd.Next(3);
                    for (int i = 0; i < n; i++)
                    {
                        double on = 0.04 + _rnd.NextDouble() * 0.07;
                        double level = i == 0 ? 1.0 : 0.6 + _rnd.NextDouble() * 0.4;
                        s.Pulses.Add((start, start + on, level));
                        start += on + 0.03 + _rnd.NextDouble() * 0.09;
                    }
                    // groupe « principal » (en haut à droite) plus actif
                    double pause = 0.8 + _rnd.NextDouble() * (g == 2 ? 1.8 : 3.2);
                    s.NextBurst = start + pause;
                }

                double level0 = 0;
                for (int i = s.Pulses.Count - 1; i >= 0; i--)
                {
                    var p = s.Pulses[i];
                    if (t >= p.on && t < p.off) level0 = Math.Max(level0, p.level);
                    if (t >= p.off) s.Pulses.RemoveAt(i);
                }

                // repos : éclairs faibles qui grésillent ; flash : pleine intensité + halo
                double idle = 0.10 + 0.10 * _rnd.NextDouble();
                double bolt = level0 > 0 ? level0 : idle;
                s.Glow = Math.Max(level0, s.Glow * 0.78);
                if (s.Glow < 0.01) s.Glow = 0;

                double k = Math.Clamp(Intensity, 0, 1);
                _boltImgs[g].Opacity = 1 - k + k * bolt;
                _glowImgs[g].Opacity = k * s.Glow * 0.9;
            }
        }
    }
}
