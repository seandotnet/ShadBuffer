using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ShadBuffer
{
    // windows forms has no video player, so this one comes from wpf, which ships with the framework
    public sealed class VideoHost : ElementHost
    {
        private const double BadgeSize = 72;
        private const string PlayShape = "M 0,0 L 0,28 L 24,14 Z";

        private readonly MediaElement media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            Volume = 1,
        };

        private readonly Grid badge = new Grid { Width = BadgeSize, Height = BadgeSize, IsHitTestVisible = false };
        private readonly TextBlock problem = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)),
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(32),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        public event Action Clicked;
        public event Action DoubleClicked;
        public event Action Finished;
        public event Action Opened;
        public event Action Failed;
        public event Action<System.Windows.Input.Key> KeyPressed;

        public bool Playing { get; private set; }
        public bool HasFailed { get; private set; }

        public bool IsMuted
        {
            get => media.IsMuted;
            set => media.IsMuted = value;
        }

        public void ToggleMute()
        {
            IsMuted = !IsMuted;
        }

        public TimeSpan Length => media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan : TimeSpan.Zero;

        public TimeSpan Position
        {
            get => media.Position;
            set => media.Position = value;
        }

        public VideoHost()
        {
            badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27)) });
            badge.Children.Add(new Path
            {
                Data = Geometry.Parse(PlayShape),
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 244, 245)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
            });

            var root = new Grid { Background = Brushes.Black, Focusable = true };
            root.Children.Add(media);
            root.Children.Add(badge);
            root.Children.Add(problem);
            root.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    DoubleClicked?.Invoke();
                }
            };
            root.MouseLeftButtonUp += (_, __) => Clicked?.Invoke();
            root.PreviewKeyDown += (_, e) => KeyPressed?.Invoke(e.Key);
            Child = root;

            media.MediaOpened += (_, __) => Opened?.Invoke();
            media.MediaEnded += (_, __) =>
            {
                Pause();
                media.Position = TimeSpan.Zero;
                Finished?.Invoke();
            };
            media.MediaFailed += (_, __) =>
            {
                HasFailed = true;
                Playing = false;
                badge.Visibility = Visibility.Collapsed;
                problem.Text = "This video can't play here, Windows may be missing its codec.\nClick to open it in your default player.";
                problem.Visibility = Visibility.Visible;
                Failed?.Invoke();
            };
        }

        // play then pause straight away so the first frame shows instead of black
        public void Load(string path)
        {
            HasFailed = false;
            Playing = false;
            problem.Visibility = Visibility.Collapsed;
            badge.Visibility = Visibility.Visible;
            media.Source = new Uri(path);
            media.Play();
            media.Pause();
        }

        public void Toggle()
        {
            if (Playing)
            {
                Pause();
            }
            else if (!HasFailed)
            {
                media.Play();
                Playing = true;
                badge.Visibility = Visibility.Collapsed;
            }
        }

        public void Pause()
        {
            media.Pause();
            Playing = false;
            if (!HasFailed)
            {
                badge.Visibility = Visibility.Visible;
            }
        }

        // closes the file too, otherwise windows can't move or delete it afterwards
        public void Unload()
        {
            media.Stop();
            media.Close();
            media.Source = null;
            Playing = false;
        }
    }
}
