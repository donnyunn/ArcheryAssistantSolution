using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace UserInterface
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public class RecordingSettingsEventArgs : EventArgs
        {
            public bool UseDesktop { get; set; }
            public bool UseVerticalLayout { get; set; }
            public string DrivePath { get; set; }

            public RecordingSettingsEventArgs(bool useDesktop, bool useVerticalLayout, string drivePath)
            {
                UseDesktop = useDesktop;
                UseVerticalLayout = useVerticalLayout;
                DrivePath = drivePath;
            }
        }

        public event EventHandler CloseButton;
        public event EventHandler<RoutedPropertyChangedEventArgs<double>> DelaySlider;
        public event EventHandler StartButton;
        public event EventHandler PlayButton;
        public event EventHandler BackwardButton;
        public event EventHandler ForwardButton;
        public event EventHandler SlowButton;
        // 이벤트 정의 변경
        public event EventHandler<RecordingSettingsEventArgs> RecordToggle;

        private bool _isStarted = false;
        // 녹화 상태에 따른 토글 UI 
        private bool _isRecording = false;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

        }

        #region 이벤트 핸들러

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            CloseButton?.Invoke(this, EventArgs.Empty);
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            StartButton?.Invoke(this, EventArgs.Empty);
        }

        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            PlayButton?.Invoke(this, EventArgs.Empty);
        }

        private void btnBackward_Click(object sender, RoutedEventArgs e)
        {
            BackwardButton?.Invoke(this, EventArgs.Empty);
        }

        private void btnForward_Click(object sender, RoutedEventArgs e)
        {
            ForwardButton?.Invoke(this, EventArgs.Empty);
        }

        private void btnSlow_Click(object sender, RoutedEventArgs e)
        {
            SlowButton?.Invoke(this, EventArgs.Empty);
        }

        private void sliderDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int delaySeconds = (int)e.NewValue;
            txtDelay.Text = delaySeconds.ToString();
            DelaySlider?.Invoke(this, e);
        }

        private void recordToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
            {
                // 녹화 시작 시 설정 창 표시
                var settingsWindow = new RecordingSettingsWindow();
                settingsWindow.Owner = this;
                bool? result = settingsWindow.ShowDialog();

                if (result == true)
                {
                    // 선택된 설정으로 이벤트 발생
                    var args = new RecordingSettingsEventArgs(
                        settingsWindow.UseDesktop,
                        settingsWindow.UseVerticalLayout,
                        settingsWindow.SelectedDrive
                    );

                    RecordToggle?.Invoke(this, args);
                }
            }
            else
            {
                // 녹화 중지는 추가 설정 없이 기존 이벤트 발생
                RecordToggle?.Invoke(this, new RecordingSettingsEventArgs(true, true, string.Empty));
            }
        }

        // 마우스 다운/업 이벤트 - 이벤트 버블링으로 클릭 이벤트 발생
        private void btnBackward_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 핸들러 구현 필요 시 구현 (버튼 누르고 있을 때)
        }

        private void btnBackward_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // 핸들러 구현 필요 시 구현 (버튼 뗄 때)
        }

        private void btnForward_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 핸들러 구현 필요 시 구현 (버튼 누르고 있을 때)
        }

        private void btnForward_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // 핸들러 구현 필요 시 구현 (버튼 뗄 때)
        }

        #endregion

        #region 공개 메서드 - 외부에서 UI 상태 변경용
        /// <summary>
        /// 시작/중단 버튼 텍스트 설정
        /// </summary>
        public void SetStartButtonText(bool isStarted)
        {
            _isStarted = isStarted;
            btnStart.Content = isStarted ? "중단" : "시작";
        }

        /// <summary>
        /// 딜레이 슬라이더 활성화/비활성화
        /// </summary>
        public void SetDelaySliderEnabled(bool enabled)
        {
            sliderDelay.IsEnabled = enabled;
        }

        public void SetSlowButtonText(int slowLevel)
        {
            btnSlow.Content = slowLevel == 1 ? "Slow" : $"\nSlow\nx{1.0 / slowLevel,1:F2}";
        }

        public void SetRecordToggleEnabled(bool enabled)
        {
            recordToggleBorder.IsEnabled = enabled;
        }

        /// <summary>
        /// 재생/일시정지 아이콘 변경
        /// </summary>
        public void SetPlayPauseIcon(bool isPaused)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                playIcon.Visibility = isPaused ? Visibility.Visible : Visibility.Collapsed;
                playPauseIcon.Visibility = isPaused ? Visibility.Collapsed : Visibility.Visible;
            }));
        }

        /// <summary>
        /// Slow 버튼 상태 업데이트
        /// </summary>
        public void UpdateSlowButton(bool isSlowMode, int slowLevel)
        {
            if (isSlowMode)
            {
                btnSlow.Background = new SolidColorBrush(Colors.DarkGray);
                btnSlow.Content = $"Slow\nx{1.0 / slowLevel:F2}";
            }
            else
            {
                btnSlow.Background = new SolidColorBrush(Colors.LightGray);
                btnSlow.Content = "Slow";
            }
        }

        /// <summary>
        /// 녹화 토글 상태 변경
        /// </summary>
        public void SetRecordingState(bool isRecording)
        {
            _isRecording = isRecording;
            UpdateRecordingToggle();
        }

        /// <summary>
        /// 녹화 상태 깜박임 효과 업데이트 (녹화 중일 때 호출)
        /// </summary>
        public void UpdateRecordingStatus()
        {
            if (_isRecording && _isStarted)
            {
                var brush = recordToggleBorder.Background as SolidColorBrush;
                if (brush != null && brush.Color == Colors.Red)
                {
                    recordToggleBorder.Background = new SolidColorBrush(Colors.DarkRed);
                }
                else
                {
                    recordToggleBorder.Background = new SolidColorBrush(Colors.Red);
                }
            }
        }
        #endregion

        #region 헬퍼 메서드
        public void UpdateRecordingToggle()
        {
            if (_isRecording)
            {
                recordToggleBorder.Background = new SolidColorBrush(Colors.Red);
                recordToggleText.Text = "  On";
                recordToggleText.HorizontalAlignment = HorizontalAlignment.Left;
                recordToggleKnob.Margin = new Thickness(37, 0, 0, 0); // 위치 조정
            }
            else
            {
                recordToggleBorder.Background = new SolidColorBrush(Colors.DarkGray);
                recordToggleText.Text = "Off  ";
                recordToggleText.HorizontalAlignment = HorizontalAlignment.Right;
                recordToggleKnob.Margin = new Thickness(2, 0, 0, 0);
            }
        }
        #endregion
    }
}
