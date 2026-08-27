using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SwiftDock
{
    public partial class SplashScreen : Window
    {
        public SplashScreen()
        {
            InitializeComponent();
            StartAnimations();
        }

        private void StartAnimations()
        {
            // Subtle scale pulse animation
            var scaleAnimation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });
            scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });

            LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);

            // Glow pulse animation
            var glowAnimation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            glowAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            glowAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });
            glowAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });

            LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glowAnimation);
        }

        public void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (StatusText != null)
                {
                    StatusText.Text = status;
                }
            });
        }

        public void CloseSplash()
        {
            Dispatcher.Invoke(() =>
            {
                var fadeOut = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(400),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                fadeOut.Completed += (s, e) =>
                {
                    Close();
                };

                this.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            });
        }
    }
}
