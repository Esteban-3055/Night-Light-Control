using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NightLightControl.Core;

namespace NightLightControl.UI;

public sealed class CurveEditor : FrameworkElement
{
    public List<CurvePoint> Points { get; set; } = Profile.Defaults();
    public CurvePoint? Selected { get; private set; }
    public event Action? Changed;
    public event Action? SelectionChanged;
    private bool dragging;
    private Rect Plot => new(48, 28, Math.Max(1, ActualWidth - 70), Math.Max(1, ActualHeight - 72));
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private static readonly Brush TextBrush = Brush("#B4B4BD"), Accent = Brushes.White;
    public double CurrentMinute { get; set; }
    public CurveEditor() { MinHeight = 260; Focusable = true; Cursor = Cursors.Cross; ClipToBounds = true; }
    public void Select(CurvePoint point) { Selected = point; SelectionChanged?.Invoke(); InvalidateVisual(); }
    public void ResetSelection() { Selected = Points.FirstOrDefault(); SelectionChanged?.Invoke(); InvalidateVisual(); }
    private Point Position(double minute, double strength) => new(Plot.Left + minute / 1440 * Plot.Width, Plot.Bottom - strength / 100 * Plot.Height);
    private void Label(DrawingContext dc, string text, double x, double y, Brush? brush = null, double size = 11)
    {
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush ?? TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var plot = Plot;
        dc.DrawRectangle(Brush("#070708"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (int percent = 0; percent <= 100; percent += 25)
        {
            double y = Position(0, percent).Y;
            dc.DrawLine(new Pen(Brush("#37373C"), 1), new(plot.Left, y), new(plot.Right, y)); Label(dc, percent.ToString(), 14, y - 8);
        }
        for (int hour = 0; hour <= 24; hour += 3)
        {
            double x = Position(hour * 60, 0).X;
            dc.DrawLine(new Pen(Brush("#303035"), 1), new(x, plot.Top), new(x, plot.Bottom)); Label(dc, $"{hour:00}:00", x - 15, plot.Bottom + 12);
        }
        if (Points.Count < 2) return;
        var sorted = Points.OrderBy(p => p.Minute).ToArray();
        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(Position(0, Curve.Evaluate(Points, 0)), false, false);
            foreach (var p in sorted) g.LineTo(Position(p.Minute, p.Strength), true, false);
            g.LineTo(Position(1440, Curve.Evaluate(Points, 0)), true, false);
        }
        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(Position(0, 0), true, true); g.LineTo(Position(0, Curve.Evaluate(Points, 0)), true, false);
            foreach (var p in sorted) g.LineTo(Position(p.Minute, p.Strength), true, false);
            g.LineTo(Position(1440, Curve.Evaluate(Points, 0)), true, false); g.LineTo(Position(1440, 0), true, false);
        }
        dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90), null, fill);
        dc.DrawGeometry(null, new Pen(Brush("#14FFFFFF"), 9), path);
        dc.DrawGeometry(null, new Pen(Brush("#25FFFFFF"), 5), path);
        dc.DrawGeometry(null, new Pen(Accent, 2.5), path);
        double nowX = Position(CurrentMinute, 0).X;
        dc.DrawLine(new Pen(Brush("#9999A1"), 1) { DashStyle = DashStyles.Dash }, new(nowX, plot.Top), new(nowX, plot.Bottom));
        Label(dc, "AHORA", Math.Clamp(nowX - 17, plot.Left, plot.Right - 42), 7, Brush("#E5E5E9"), 10);
        foreach (var p in sorted)
        {
            bool selected = ReferenceEquals(p, Selected);
            dc.DrawEllipse(Brush("#070708"), new Pen(Accent, selected ? 3 : 2), Position(p.Minute, p.Strength), selected ? 7 : 5, selected ? 7 : 5);
            if (selected) dc.DrawEllipse(Accent, null, Position(p.Minute, p.Strength), 2.5, 2.5);
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var position = e.GetPosition(this);
        var hit = Points.OrderBy(p => (Position(p.Minute, p.Strength) - position).Length).FirstOrDefault();
        if (hit != null && (Position(hit.Minute, hit.Strength) - position).Length < 16)
        {
            Select(hit); dragging = true; CaptureMouse();
        }
        else if (e.ClickCount == 2 && Plot.Contains(position) && Points.Count < 96)
        {
            int minute = MinuteAt(position.X);
            if (Points.All(p => p.Minute != minute)) { var point = new CurvePoint(minute, StrengthAt(position.Y)); Points.Add(point); Select(point); Changed?.Invoke(); }
        }
        e.Handled = true;
    }
    private int MinuteAt(double x) => Math.Clamp((int)Math.Round((x - Plot.Left) / Plot.Width * 1440 / 5) * 5, 0, 1439);
    private double StrengthAt(double y) => Math.Clamp(Math.Round((Plot.Bottom - y) / Plot.Height * 100), 0, 100);
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging || Selected == null) return;
        var pos = e.GetPosition(this); int minute = MinuteAt(pos.X);
        // Keep the dragged point between its neighbors so points never collide or swap.
        var ordered = Points.OrderBy(p => p.Minute).ToList(); int i = ordered.IndexOf(Selected);
        minute = Math.Clamp(minute, i > 0 ? ordered[i - 1].Minute + 1 : 0, i < ordered.Count - 1 ? ordered[i + 1].Minute - 1 : 1439);
        Selected.Minute = minute; Selected.Strength = StrengthAt(pos.Y); InvalidateVisual(); SelectionChanged?.Invoke(); Changed?.Invoke();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { dragging = false; ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { dragging = false; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected != null && Points.Count > 2) { Points.Remove(Selected); ResetSelection(); Changed?.Invoke(); e.Handled = true; }
    }
}
