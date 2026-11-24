#nullable disable

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MmiConverter
{
    internal static class WindowChromeService
    {
        private const int GetMinMaxInfoMessage = 0x0024;
        private const uint MonitorDefaultToNearest = 0x00000002;

        public static void Attach(Window window, Border frame)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (frame == null) throw new ArgumentNullException("frame");
            new WindowTracker(window, frame).Attach();
        }

        private sealed class WindowTracker
        {
            private readonly Window window;
            private readonly Border frame;
            private readonly CornerRadius restoredCornerRadius;
            private readonly Thickness restoredBorderThickness;
            private HwndSource source;

            public WindowTracker(Window window, Border frame)
            {
                this.window = window;
                this.frame = frame;
                restoredCornerRadius = frame.CornerRadius;
                restoredBorderThickness = frame.BorderThickness;
            }

            public void Attach()
            {
                window.SourceInitialized += WindowSourceInitialized;
                frame.SizeChanged += WindowSizeOrStateChanged;
                window.StateChanged += WindowSizeOrStateChanged;
                window.Closed += WindowClosed;
                window.Dispatcher.BeginInvoke(new Action(UpdateFrame));
            }

            private void WindowSourceInitialized(object sender, EventArgs e)
            {
                if (window.ResizeMode == ResizeMode.NoResize || window.ResizeMode == ResizeMode.CanMinimize) return;
                source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                if (source != null) source.AddHook(WindowMessageHook);
            }

            private IntPtr WindowMessageHook(
                IntPtr windowHandle,
                int message,
                IntPtr wParam,
                IntPtr lParam,
                ref bool handled)
            {
                if (message == GetMinMaxInfoMessage)
                {
                    ApplyWindowConstraints(window, windowHandle, lParam);
                    handled = true;
                }

                return IntPtr.Zero;
            }

            private void WindowSizeOrStateChanged(object sender, EventArgs e)
            {
                UpdateFrame();
            }

            private void UpdateFrame()
            {
                if (frame.ActualWidth <= 0 || frame.ActualHeight <= 0) return;

                if (window.WindowState == WindowState.Maximized)
                {
                    frame.CornerRadius = new CornerRadius(0);
                    frame.BorderThickness = new Thickness(0);
                    frame.Clip = null;
                    return;
                }

                frame.CornerRadius = restoredCornerRadius;
                frame.BorderThickness = restoredBorderThickness;
                double radius = restoredCornerRadius.TopLeft;
                frame.Clip = new RectangleGeometry(
                    new Rect(0, 0, frame.ActualWidth, frame.ActualHeight),
                    radius,
                    radius);
            }

            private void WindowClosed(object sender, EventArgs e)
            {
                if (source != null)
                {
                    source.RemoveHook(WindowMessageHook);
                    source = null;
                }

                window.SourceInitialized -= WindowSourceInitialized;
                frame.SizeChanged -= WindowSizeOrStateChanged;
                window.StateChanged -= WindowSizeOrStateChanged;
                window.Closed -= WindowClosed;
            }
        }

        private static void ApplyWindowConstraints(
            Window window,
            IntPtr windowHandle,
            IntPtr minMaxInfoPointer)
        {
            IntPtr monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            if (monitorHandle == IntPtr.Zero) return;

            MonitorInfo monitorInfo = new MonitorInfo();
            monitorInfo.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(monitorHandle, ref monitorInfo)) return;

            MinMaxInfo minMaxInfo = (MinMaxInfo)Marshal.PtrToStructure(
                minMaxInfoPointer,
                typeof(MinMaxInfo));
            PresentationSource presentationSource = PresentationSource.FromVisual(window);
            double scaleX = presentationSource != null && presentationSource.CompositionTarget != null
                ? presentationSource.CompositionTarget.TransformToDevice.M11
                : 1.0;
            double scaleY = presentationSource != null && presentationSource.CompositionTarget != null
                ? presentationSource.CompositionTarget.TransformToDevice.M22
                : 1.0;

            minMaxInfo.MinimumTrackSize.X = (int)Math.Ceiling(window.MinWidth * scaleX);
            minMaxInfo.MinimumTrackSize.Y = (int)Math.Ceiling(window.MinHeight * scaleY);
            minMaxInfo.MaximumPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
            minMaxInfo.MaximumPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
            minMaxInfo.MaximumSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
            minMaxInfo.MaximumSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;

            if (!double.IsInfinity(window.MaxWidth))
            {
                minMaxInfo.MaximumTrackSize.X = (int)Math.Floor(window.MaxWidth * scaleX);
            }

            if (!double.IsInfinity(window.MaxHeight))
            {
                minMaxInfo.MaximumTrackSize.Y = (int)Math.Floor(window.MaxHeight * scaleY);
            }

            Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaximumSize;
            public NativePoint MaximumPosition;
            public NativePoint MinimumTrackSize;
            public NativePoint MaximumTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRectangle MonitorArea;
            public NativeRectangle WorkArea;
            public uint Flags;
        }
    }
}
