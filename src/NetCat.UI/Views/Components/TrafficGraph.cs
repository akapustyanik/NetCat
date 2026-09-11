using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace NetCat.UI.Views.Components
{
    public class TrafficGraph : FrameworkElement
    {
        private const int MaxPoints = 60;
        private readonly Queue<double> _downloadHistory = new(MaxPoints);
        private readonly Queue<double> _uploadHistory = new(MaxPoints);

        public static readonly DependencyProperty DownloadSpeedProperty =
            DependencyProperty.Register(nameof(DownloadSpeed), typeof(double), typeof(TrafficGraph),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSpeedChanged));

        public static readonly DependencyProperty UploadSpeedProperty =
            DependencyProperty.Register(nameof(UploadSpeed), typeof(double), typeof(TrafficGraph),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSpeedChanged));

        public static readonly DependencyProperty DownloadBrushProperty =
            DependencyProperty.Register(nameof(DownloadBrush), typeof(Brush), typeof(TrafficGraph),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(6, 182, 212)))); // Cyan

        public static readonly DependencyProperty UploadBrushProperty =
            DependencyProperty.Register(nameof(UploadBrush), typeof(Brush), typeof(TrafficGraph),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(168, 85, 247)))); // Purple

        public double DownloadSpeed
        {
            get => (double)GetValue(DownloadSpeedProperty);
            set => SetValue(DownloadSpeedProperty, value);
        }

        public double UploadSpeed
        {
            get => (double)GetValue(UploadSpeedProperty);
            set => SetValue(UploadSpeedProperty, value);
        }

        public Brush DownloadBrush
        {
            get => (Brush)GetValue(DownloadBrushProperty);
            set => SetValue(DownloadBrushProperty, value);
        }

        public Brush UploadBrush
        {
            get => (Brush)GetValue(UploadBrushProperty);
            set => SetValue(UploadBrushProperty, value);
        }

        public TrafficGraph()
        {
            ClipToBounds = true;
            for (int i = 0; i < MaxPoints; i++)
            {
                _downloadHistory.Enqueue(0);
                _uploadHistory.Enqueue(0);
            }
        }

        private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TrafficGraph graph)
            {
                graph.PushNewSample();
            }
        }

        private void PushNewSample()
        {
            if (_downloadHistory.Count >= MaxPoints) _downloadHistory.Dequeue();
            _downloadHistory.Enqueue(DownloadSpeed);

            if (_uploadHistory.Count >= MaxPoints) _uploadHistory.Dequeue();
            _uploadHistory.Enqueue(UploadSpeed);

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            // Draw subtle background grid lines
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1.0);
            gridPen.Freeze();

            for (int i = 1; i <= 3; i++)
            {
                double y = height * i / 4.0;
                dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            }

            // Determine max value for auto-scaling
            double maxSpeed = 1024.0 * 1024.0; // minimum 1 MB/s scale
            foreach (var d in _downloadHistory) if (d > maxSpeed) maxSpeed = d;
            foreach (var u in _uploadHistory) if (u > maxSpeed) maxSpeed = u;

            // Render Download wave
            DrawWave(dc, _downloadHistory, maxSpeed, width, height, DownloadBrush, Color.FromArgb(40, 6, 182, 212));

            // Render Upload wave
            DrawWave(dc, _uploadHistory, maxSpeed, width, height, UploadBrush, Color.FromArgb(40, 168, 85, 247));
        }

        private static void DrawWave(DrawingContext dc, Queue<double> history, double maxSpeed, double width, double height, Brush strokeBrush, Color fillColor)
        {
            if (history.Count < 2) return;

            var points = new List<Point>(history.Count);
            double stepX = width / (MaxPoints - 1);
            int idx = 0;

            foreach (var val in history)
            {
                double x = idx * stepX;
                double normalized = Math.Clamp(val / maxSpeed, 0.0, 1.0);
                double y = height - (normalized * (height - 10)) - 5;
                points.Add(new Point(x, y));
                idx++;
            }

            // Fill geometry
            var fillGeom = new StreamGeometry();
            using (var ctx = fillGeom.Open())
            {
                ctx.BeginFigure(new Point(0, height), true, true);
                foreach (var pt in points)
                {
                    ctx.LineTo(pt, true, true);
                }
                ctx.LineTo(new Point(width, height), true, true);
            }
            fillGeom.Freeze();

            var fillBrush = new SolidColorBrush(fillColor);
            fillBrush.Freeze();
            dc.DrawGeometry(fillBrush, null, fillGeom);

            // Line geometry
            var lineGeom = new StreamGeometry();
            using (var ctx = lineGeom.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++)
                {
                    ctx.LineTo(points[i], true, true);
                }
            }
            lineGeom.Freeze();

            var strokePen = new Pen(strokeBrush, 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            strokePen.Freeze();
            dc.DrawGeometry(null, strokePen, lineGeom);
        }
    }
}
