using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetCat.UI.Views.Components
{
    public partial class LatencyBadge : UserControl
    {
        public static readonly DependencyProperty LatencyMsProperty =
            DependencyProperty.Register(nameof(LatencyMs), typeof(int), typeof(LatencyBadge),
                new PropertyMetadata(-1, OnLatencyChanged));

        public int LatencyMs
        {
            get => (int)GetValue(LatencyMsProperty);
            set => SetValue(LatencyMsProperty, value);
        }

        public LatencyBadge()
        {
            InitializeComponent();
            UpdateBadge(-1);
        }

        private static void OnLatencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LatencyBadge badge)
            {
                badge.UpdateBadge((int)e.NewValue);
            }
        }

        public void UpdateBadge(int latency)
        {
            if (latency < 0)
            {
                PingText.Text = "Нет связи";
                PingText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Gray
                DotIndicator.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 30, 41, 59));
                BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 51, 65, 85));
            }
            else if (latency <= 100)
            {
                PingText.Text = $"{latency} ms";
                PingText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Emerald
                DotIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 6, 78, 59));
                BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 16, 185, 129));
            }
            else if (latency <= 250)
            {
                PingText.Text = $"{latency} ms";
                PingText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow
                DotIndicator.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 120, 53, 15));
                BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 245, 158, 11));
            }
            else
            {
                PingText.Text = $"{latency} ms";
                PingText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)); // Red
                DotIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 127, 29, 29));
                BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 239, 68, 68));
            }
        }
    }
}
