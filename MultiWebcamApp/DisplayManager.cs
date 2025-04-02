using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    public class DisplayManager : IDisposable
    {
        private UserInterface.MainWindow _uiDisplay;
        private CameraViewer.MainWindow _headDisplay;
        private CameraViewer.MainWindow _bodyDisplay;
        private PressureMapViewer.MainWindow _pressureDisplay;
        private bool _isDisposed;

        // PressureMapViewer 접근 속성 (RecordingManager가 필요로 함)
        public PressureMapViewer.MainWindow PressureDisplay => _pressureDisplay;

        public DisplayManager()
        {
            try
            {
                _uiDisplay = new UserInterface.MainWindow();
                _headDisplay = new CameraViewer.MainWindow();
                _bodyDisplay = new CameraViewer.MainWindow();
                _pressureDisplay = new PressureMapViewer.MainWindow();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DisplayManager 초기화 중 오류: {ex.Message}");
                throw;
            }
        }

        public void ShowDisplay()
        {
            try
            {
                _uiDisplay.Show();
                _headDisplay.Show();
                _bodyDisplay.Show();
                _pressureDisplay.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"디스플레이 표시 중 오류: {ex.Message}");
            }
        }

        public void ConfigureDisplayPositions()
        {
            try
            {
                var screens = Screen.AllScreens;

                if (screens.Length > 3)
                {
                    var orderedScreens = screens.OrderBy(s => s.Bounds.Y).ToList();

                    var monitor1 = orderedScreens[0];
                    _headDisplay.WindowStartupLocation = WindowStartupLocation.Manual;
                    _headDisplay.WindowStyle = WindowStyle.None;
                    _headDisplay.ResizeMode = ResizeMode.NoResize;
                    _headDisplay.Left = monitor1.Bounds.Left;
                    _headDisplay.Top = monitor1.Bounds.Top;
                    _headDisplay.Width = monitor1.Bounds.Width;
                    _headDisplay.Height = monitor1.Bounds.Height;

                    var monitor2 = orderedScreens[1];
                    _uiDisplay.WindowStartupLocation = WindowStartupLocation.Manual;
                    _uiDisplay.WindowStyle = WindowStyle.None;
                    _uiDisplay.ResizeMode = ResizeMode.NoResize;
                    _uiDisplay.Left = monitor2.Bounds.Left;
                    _uiDisplay.Top = monitor2.Bounds.Top;
                    _uiDisplay.Width = monitor2.Bounds.Width;
                    _uiDisplay.Height = monitor2.Bounds.Height;

                    var monitor3 = orderedScreens[2];
                    _bodyDisplay.WindowStartupLocation = WindowStartupLocation.Manual;
                    _bodyDisplay.WindowStyle = WindowStyle.None;
                    _bodyDisplay.ResizeMode = ResizeMode.NoResize;
                    _bodyDisplay.Left = monitor3.Bounds.Left;
                    _bodyDisplay.Top = monitor3.Bounds.Top;
                    _bodyDisplay.Width = monitor3.Bounds.Width;
                    _bodyDisplay.Height = monitor3.Bounds.Height;

                    var monitor0 = orderedScreens[3];
                    _pressureDisplay.WindowStartupLocation = WindowStartupLocation.Manual;
                    _pressureDisplay.WindowStyle = WindowStyle.None;
                    _pressureDisplay.ResizeMode = ResizeMode.NoResize;
                    _pressureDisplay.Left = monitor0.Bounds.Left;
                    _pressureDisplay.Top = monitor0.Bounds.Top;
                    _pressureDisplay.Width = monitor0.Bounds.Width;
                    _pressureDisplay.Height = monitor0.Bounds.Height;
                }
                else
                {
                    // 모니터가 부족한 경우의 대체 배치
                    _headDisplay.WindowState = WindowState.Normal;
                    _headDisplay.WindowStyle = WindowStyle.None;
                    _headDisplay.Width = 1280;
                    _headDisplay.Height = 720;
                    _headDisplay.Left = 0;
                    _headDisplay.Top = 0;

                    _bodyDisplay.WindowState = WindowState.Normal;
                    _bodyDisplay.WindowStyle = WindowStyle.None;
                    _bodyDisplay.Width = 1280;
                    _bodyDisplay.Height = 720;
                    _bodyDisplay.Left = 1280;
                    _bodyDisplay.Top = 0;

                    _pressureDisplay.WindowState = WindowState.Normal;
                    _pressureDisplay.WindowStyle = WindowStyle.None;
                    _pressureDisplay.Width = 1280;
                    _pressureDisplay.Height = 720;
                    _pressureDisplay.Left = 0;
                    _pressureDisplay.Top = 720;

                    _uiDisplay.WindowState = WindowState.Normal;
                    _uiDisplay.WindowStyle = WindowStyle.None;
                    _uiDisplay.Width = 1280;
                    _uiDisplay.Height = 360;
                    _uiDisplay.Left = 1280;
                    _uiDisplay.Top = 720;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"디스플레이 위치 설정 중 오류: {ex.Message}");
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
                if (frame.WebcamHead != null)
                {
                    _headDisplay.Dispatcher.Invoke(() =>
                    {
                        _headDisplay.UpdateFrame(frame.WebcamHead, lowerMessage, upperMessage);
                    });
                }
                if (frame.WebcamBody != null)
                {
                    _bodyDisplay.Dispatcher.Invoke(() =>
                    {
                        _bodyDisplay.UpdateFrame(frame.WebcamBody, lowerMessage, upperMessage);
                    });
                }
                if (frame.PressureData != null)
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
                if (_uiDisplay != null && _uiDisplay.IsLoaded)
                    _uiDisplay.Dispatcher.Invoke(() => _uiDisplay.Close());

                if (_headDisplay != null && _headDisplay.IsLoaded)
                    _headDisplay.Dispatcher.Invoke(() => _headDisplay.Close());

                if (_bodyDisplay != null && _bodyDisplay.IsLoaded)
                    _bodyDisplay.Dispatcher.Invoke(() => _bodyDisplay.Close());

                if (_pressureDisplay != null && _pressureDisplay.IsLoaded)
                    _pressureDisplay.Dispatcher.Invoke(() => _pressureDisplay.Close());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"디스플레이 종료 중 오류: {ex.Message}");
            }
        }

        public void CallUiDisplayUpdateRecordingStatus()
        {
            _uiDisplay?.UpdateRecordingStatus();
        }

        public void UiDisplaySetStartButtonText(bool isStarted)
        {
            _uiDisplay.SetStartButtonText(isStarted);
        }

        public void UiDisplaySetDelaySliderEnabled(bool isStarted)
        {
            _uiDisplay.SetDelaySliderEnabled(isStarted);
        }

        public void UiDisplaySetRecordToggleEnabled(bool isStarted)
        {
            _uiDisplay.SetRecordToggleEnabled(isStarted);
        }

        public void UiDisplaySetSlowButtonText(int slowLevel)
        {
            _uiDisplay.SetSlowButtonText(slowLevel);
        }

        public void UiDisplaySetRecordingState(bool recordButtonChecked)
        {
            _uiDisplay.SetRecordingState(recordButtonChecked);
        }

        public void UiDisplaySetPlayPauseIcon(bool isPaused)
        {
            _uiDisplay.SetPlayPauseIcon(isPaused);
        }

        #region 이벤트 등록 메서드
        public void RegisterPressureDisplayResetEvent(EventHandler handler)
        {
            if (_pressureDisplay != null)
            {
                // 중복 등록 방지
                _pressureDisplay.ResetPortsRequested -= handler;
                _pressureDisplay.ResetPortsRequested += handler;
            }
        }

        public void RegisterUiDisplayCloseButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.CloseButton -= handler;
                _uiDisplay.CloseButton += handler;
            }
        }

        public void RegisterUiDisplayDelaySliderEvent(EventHandler<RoutedPropertyChangedEventArgs<double>> handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.DelaySlider -= handler;
                _uiDisplay.DelaySlider += handler;
            }
        }

        public void RegisterUiDisplayStartButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.StartButton -= handler;
                _uiDisplay.StartButton += handler;
            }
        }

        public void RegisterUiDisplayPlayButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.PlayButton -= handler;
                _uiDisplay.PlayButton += handler;
            }
        }

        public void RegisterUiDisplayBackwardButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.BackwardButton -= handler;
                _uiDisplay.BackwardButton += handler;
            }
        }

        public void RegisterUiDisplayForwardButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.ForwardButton -= handler;
                _uiDisplay.ForwardButton += handler;
            }
        }

        public void RegisterUiDisplaySlowButtonEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.SlowButton -= handler;
                _uiDisplay.SlowButton += handler;
            }
        }

        public void RegisterUiDisplayRecordToggleEvent(EventHandler handler)
        {
            if (_uiDisplay != null)
            {
                _uiDisplay.RecordToggle -= handler;
                _uiDisplay.RecordToggle += handler;
            }
        }
        #endregion

        #region IDisposable 구현
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // 관리되는 리소스 해제
                    CloseDisplay();

                    // 윈도우 참조 정리
                    _uiDisplay = null;
                    _headDisplay = null;
                    _bodyDisplay = null;
                    _pressureDisplay = null;
                }

                _isDisposed = true;
            }
        }

        ~DisplayManager()
        {
            Dispose(false);
        }
        #endregion
    }
}
