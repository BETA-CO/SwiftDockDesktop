using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace SwiftDock
{
    public class PresentationOverlayWindow : Window
    {
        public enum OverlayMode { Off, Laser, Spotlight }

        private OverlayMode _currentMode = OverlayMode.Off;
        private double _posX;
        private double _posY;
        private const double Sensitivity = 22.0;

        public PresentationOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            WindowState = WindowState.Maximized;

            _posX = SystemParameters.PrimaryScreenWidth / 2;
            _posY = SystemParameters.PrimaryScreenHeight / 2;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // Make window click-through so user can interact with software underneath
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        }

        public void SetMode(OverlayMode mode)
        {
            _currentMode = mode;
            if (mode == OverlayMode.Off)
            {
                Hide();
            }
            else
            {
                Show();
                InvalidateVisual();
            }
        }

        public OverlayMode GetMode()
        {
            return _currentMode;
        }

        public void ApplyGyroDelta(double dx, double dy)
        {
            _posX = Math.Clamp(_posX + (dx * Sensitivity), 0, SystemParameters.PrimaryScreenWidth);
            _posY = Math.Clamp(_posY + (dy * Sensitivity), 0, SystemParameters.PrimaryScreenHeight);

            Dispatcher.InvokeAsync(InvalidateVisual);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (_currentMode == OverlayMode.Laser)
            {
                // Draw glowing red laser dot
                var fillBrush = new SolidColorBrush(Color.FromRgb(255, 35, 35));
                var strokePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 2);
                drawingContext.DrawEllipse(fillBrush, strokePen, new Point(_posX, _posY), 12, 12);
                drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 0, 0)), null, new Point(_posX, _posY), 22, 22);
            }
            else if (_currentMode == OverlayMode.Spotlight)
            {
                // Draw dimmed screen mask with clear spotlight cutout
                var fullRect = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
                var spotlightCircle = new EllipseGeometry(new Point(_posX, _posY), 110, 110);

                var combinedGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, spotlightCircle);

                var maskBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)); // 63% dark dimming
                drawingContext.DrawGeometry(maskBrush, null, combinedGeometry);

                // Glowing border ring around spotlight
                var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 2);
                drawingContext.DrawEllipse(null, ringPen, new Point(_posX, _posY), 110, 110);
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
