using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NightLightControl.UI;

/// <summary>Resizable vector frame with cut corners and restrained white highlights.</summary>
public sealed class AngularPanel : ContentControl
{
    public static readonly DependencyProperty AccentCornersProperty = DependencyProperty.Register(nameof(AccentCorners), typeof(bool), typeof(AngularPanel), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool AccentCorners { get => (bool)GetValue(AccentCornersProperty); set => SetValue(AccentCornersProperty, value); }
    public AngularPanel() { SnapsToDevicePixels = true; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth - 1, h = ActualHeight - 1;
        if (w <= 0 || h <= 0) return;
        double cut = Math.Min(12, Math.Min(w, h) / 4);
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(new(cut, .5), true, true);
            g.PolyLineTo(new Point[] { new(w - cut, .5), new(w, cut), new(w, h - cut), new(w - cut, h), new(cut, h), new(.5, h - cut), new(.5, cut) }, true, false);
        }
        dc.DrawGeometry(Background, BorderBrush == null ? null : new Pen(BorderBrush, Math.Max(1, BorderThickness.Left)), outline);
        if (!AccentCorners) return;
        var accent = new StreamGeometry();
        using (var g = accent.Open())
        {
            g.BeginFigure(new(5, 31), true, true); g.PolyLineTo(new Point[] { new(5, 17), new(17, 5), new(39, 5), new(32, 10), new(21, 10) }, false, false);
            g.BeginFigure(new(w - 5, h - 31), true, true); g.PolyLineTo(new Point[] { new(w - 5, h - 17), new(w - 17, h - 5), new(w - 39, h - 5), new(w - 32, h - 10), new(w - 21, h - 10) }, false, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 6), accent);
        dc.DrawGeometry(Brushes.White, null, accent);
    }
}

public sealed class HeaderPattern : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(39, 39, 42)), 1);
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 16; column++)
        {
            double x = column * 22 + row * 11, y = row * 19 - 22;
            dc.DrawLine(pen, new(x, y), new(x + 22, y));
            dc.DrawLine(pen, new(x, y), new(x + 11, y + 19));
            dc.DrawLine(pen, new(x + 22, y), new(x + 11, y + 19));
        }
    }
}
