using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SEOAuditStudio.Controls
{
    /// <summary>Circular 0-100 gauge (green ≥ 80, orange ≥ 50, red below) with the value in the middle.</summary>
    public class ScoreRing : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(int), typeof(ScoreRing), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
            nameof(Suffix), typeof(string), typeof(ScoreRing), new FrameworkPropertyMetadata("%", FrameworkPropertyMetadataOptions.AffectsRender));

        public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
        public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

        private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3F)));

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            double t = Thickness;
            double r = (size - t) / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            int v = Math.Clamp(Value, 0, 100);
            var color = v >= 80 ? Color.FromRgb(0x2B, 0xB6, 0x73) : v >= 50 ? Color.FromRgb(0xF2, 0x99, 0x4A) : Color.FromRgb(0xE5, 0x48, 0x4D);
            var arcPen = new Pen(new SolidColorBrush(color), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            dc.DrawEllipse(null, new Pen(Track, t), center, r, r);
            if (v >= 100)
                dc.DrawEllipse(null, arcPen, center, r, r);
            else if (v > 0)
            {
                double angle = 360.0 * v / 100.0;
                var start = PointOn(center, r, 0);
                var end = PointOn(center, r, angle);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, arcPen, geo);
            }

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var text = new FormattedText($"{v}{Suffix}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size * 0.24,
                new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), dpi);
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }

        // 0° = 12 o'clock, clockwise
        private static Point PointOn(Point c, double r, double degrees)
        {
            double rad = (degrees - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
