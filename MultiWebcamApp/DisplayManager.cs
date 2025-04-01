using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace MultiWebcamApp
{
    public class DisplayManager
    {
        public UserInterface.MainWindow _uiDisplay;
        public CameraViewer.MainWindow _headDisplay;
        public CameraViewer.MainWindow _bodyDisplay;
        public PressureMapViewer.MainWindow _pressureDisplay;

        public DisplayManager()
        {
            _uiDisplay = new UserInterface.MainWindow();
            _headDisplay = new CameraViewer.MainWindow();
            _bodyDisplay = new CameraViewer.MainWindow();
            _pressureDisplay = new PressureMapViewer.MainWindow();
        }

        public void ShowDisplay()
        {
            _uiDisplay.Show();
            _headDisplay.Show();
            _bodyDisplay.Show();
            _pressureDisplay.Show();
        }

        public void ConfigureDisplayPositions()
        {
            var screens = Screen.AllScreens;

            if (screens.Length > 3)
            {
                var orderedScreens = screens.OrderBy(s => s.Bounds.Y).ToList();

                var monitor1 = orderedScreens[0];
                _headDisplay.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                _headDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _headDisplay.ResizeMode = System.Windows.ResizeMode.NoResize;
                _headDisplay.Left = monitor1.Bounds.Left;
                _headDisplay.Top = monitor1.Bounds.Top;
                _headDisplay.Width = monitor1.Bounds.Width;
                _headDisplay.Height = monitor1.Bounds.Height;

                var monitor2 = orderedScreens[1];
                _uiDisplay.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                _uiDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _uiDisplay.ResizeMode = System.Windows.ResizeMode.NoResize;
                _uiDisplay.Left = monitor2.Bounds.Left;
                _uiDisplay.Top = monitor2.Bounds.Top;
                _uiDisplay.Width = monitor2.Bounds.Width;
                _uiDisplay.Height = monitor2.Bounds.Height;

                var monitor3 = orderedScreens[2];
                _bodyDisplay.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                _bodyDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _bodyDisplay.ResizeMode = System.Windows.ResizeMode.NoResize;
                _bodyDisplay.Left = monitor3.Bounds.Left;
                _bodyDisplay.Top = monitor3.Bounds.Top;
                _bodyDisplay.Width = monitor3.Bounds.Width;
                _bodyDisplay.Height = monitor3.Bounds.Height;

                var monitor0 = orderedScreens[3];
                _pressureDisplay.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                _pressureDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _pressureDisplay.ResizeMode = System.Windows.ResizeMode.NoResize;
                _pressureDisplay.Left = monitor0.Bounds.Left;
                _pressureDisplay.Top = monitor0.Bounds.Top;
                _pressureDisplay.Width = monitor0.Bounds.Width;
                _pressureDisplay.Height = monitor0.Bounds.Height;
            }
            else
            {
                // 모니터가 부족한 경우의 대체 배치
                _headDisplay.WindowState = WindowState.Normal;
                _headDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _headDisplay.Width = 960;
                _headDisplay.Height = 540;
                _headDisplay.Left = 0;
                _headDisplay.Top = 0;

                _bodyDisplay.WindowState = WindowState.Normal;
                _bodyDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _bodyDisplay.Width = 960;
                _bodyDisplay.Height = 540;
                _bodyDisplay.Left = 960;
                _bodyDisplay.Top = 0;

                _pressureDisplay.WindowState = WindowState.Normal;
                _pressureDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _pressureDisplay.Width = 960;
                _pressureDisplay.Height = 540;
                _pressureDisplay.Left = 0;
                _pressureDisplay.Top = 540;

                _uiDisplay.WindowState = WindowState.Normal;
                _uiDisplay.WindowStyle = System.Windows.WindowStyle.None;
                _uiDisplay.Width = 1280;
                _uiDisplay.Height = 360;
                _uiDisplay.Left = 640;
                _uiDisplay.Top = 540;
            }
        }

        public void UpdateFrames(FrameData frame, string lowerMessage = "", string upperMessage = "")
        {
            if (frame == null)
            {
                return;
            }

            try
            {
                if (_headDisplay.IsLoaded && frame.WebcamHead != null)
                {
                    _headDisplay.Dispatcher.Invoke(() =>
                    {
                        _headDisplay.UpdateFrame(frame.WebcamHead, lowerMessage, upperMessage);
                    });
                }
                if (_bodyDisplay.IsLoaded && frame.WebcamBody!= null)
                {
                    _bodyDisplay.Dispatcher.Invoke(() =>
                    {
                        _bodyDisplay.UpdateFrame(frame.WebcamBody, lowerMessage, upperMessage);
                    });
                }
                if (_pressureDisplay.IsLoaded && frame.PressureData != null)
                {
                    _pressureDisplay.Dispatcher.Invoke(() =>
                    {
                        _pressureDisplay.UpdatePressureData(frame.PressureData, lowerMessage);
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"디스플레이 업데이트 중 오류: {ex.Message}");
            }
        }

        public void CloseDisplay()
        {
            try
            {
                if (_uiDisplay.IsLoaded)
                    _uiDisplay.Dispatcher.Invoke(() => _uiDisplay.Close());
                if (_headDisplay.IsLoaded)
                    _headDisplay.Dispatcher.Invoke(() => _headDisplay.Close());
                if (_bodyDisplay.IsLoaded)
                    _bodyDisplay.Dispatcher.Invoke(() => _bodyDisplay.Close());
                if (_pressureDisplay.IsLoaded)
                    _pressureDisplay.Dispatcher.Invoke(() => _pressureDisplay.Close());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"디스플레이 종료 중 오류: {ex.Message}");
            }
        }

        public void RegisterPressureDisplayResetEvent(EventHandler handler)
        {
            if (_pressureDisplay != null)
            {
                _pressureDisplay.ResetPortsRequested += handler;
            }
        }

        public void RegisterUiDisplayCloseButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.CloseButton += handler;
            }
        }

        public void RegisterUiDisplayDelaySliderEvent(EventHandler<RoutedPropertyChangedEventArgs<double>> handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.DelaySlider += handler;
            }
        }

        public void RegisterUiDisplayStartButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.StartButton += handler;
            }
        }

        public void RegisterUiDisplayPlayButtonEvent(EventHandler handler)
        {
            if ( _uiDisplay != null)
            {
                _uiDisplay.PlayButton += handler;
            }
        }

        public void RegisterUiDisplayBackwardButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.BackwardButton += handler;
            }
        }

        public void RegisterUiDisplayForwardButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.ForwardButton += handler;
            }
        }

        public void RegisterUiDisplaySlowButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.SlowButton += handler;
            }
        }

        public void RegisterUiDisplayRecordToggleEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.RecordToggle += handler;
            }
        }
    }
}
