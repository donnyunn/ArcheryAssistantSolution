using System;
using System.Drawing.Printing;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using FontAwesome.Sharp;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Multimedia;

namespace MultiWebcamApp
{
    public partial class MainForm : Form
    {
        private WebcamForm _webcamFormHead = new WebcamForm(0);
        private WebcamForm _webcamFormBody = new WebcamForm(1);
        private FootpadForm _footpadForm = new FootpadForm();
        private int _delaySeconds = 0; 
        private bool _isStarted = false;
        private bool _isPaused = false;
        private bool _isSlowMode = false;

        private System.Windows.Forms.Timer _backwardTimer;
        private System.Windows.Forms.Timer _forwardTimer;
        private System.Windows.Forms.Timer _backwardInitialTimer;
        private System.Windows.Forms.Timer _forwardInitialTimer;

        // 멀티미디어 타이머 추가
        private Multimedia.Timer _frameTimer;
        private bool _isProcessing = false;
        private long _frameCount = 0;
        private long _lastFpsCheck = 0;

        // 프레임 레이트 설정
        private const int TARGET_FPS = 60;
        private readonly int _frameTimeMs;

        private RecordingManager _recordingManager;
        private System.Windows.Forms.Timer _recordingStatusTimer;

        private readonly Stopwatch _mainStopwatch = new Stopwatch();

        public MainForm()
        {
            InitializeComponent();
            InitializeCustomControls();

            // 프레임 시간 계산 (밀리초)
            _frameTimeMs = (int)(1000.0 / TARGET_FPS);

            // 멀티미디어 타이머 초기화
            InitializeMultimediaTimer();

            _mainStopwatch.Start();
        }

        // 멀티미디어 타이머 초기화 메서드
        private void InitializeMultimediaTimer()
        {
            _frameTimer = new Multimedia.Timer();
            _frameTimer.Period = _frameTimeMs+1;   // 예: 60fps에 해당하는 16.67ms
            _frameTimer.Resolution = 1;          // 최고 해상도
            _frameTimer.Mode = TimerMode.Periodic;
            _frameTimer.SynchronizingObject = this;  // UI 스레드와 동기화

            // 타이머 이벤트 핸들러 등록
            _frameTimer.Tick += new EventHandler(FrameTimer_Tick);
        }

        // 타이머 이벤트 핸들러
        private void FrameTimer_Tick(object? sender, EventArgs e)
        {
            if (_isProcessing || _cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;

            _isProcessing = true;
            try
            {
                long frameStartTime = _mainStopwatch.ElapsedMilliseconds;

                // FPS 계산
                _frameCount++;
                if (frameStartTime - _lastFpsCheck >= 1000)
                {
                    Console.WriteLine($"Camera FPS: {_frameCount}");
                    _frameCount = 0;
                    _lastFpsCheck = frameStartTime;
                }

                // 프레임 처리
                if (_frameCount % 4 == 0)
                {
                    _ = Task.WhenAll(
                        Task.Run(() => _webcamFormHead.work(frameStartTime)),
                        Task.Run(() => _webcamFormBody.work(frameStartTime)),
                        Task.Run(() => _footpadForm.UpdateFrameAll())
                    );
                }
                else
                {
                    _ = Task.WhenAll(
                        Task.Run(() => _webcamFormHead.work(frameStartTime)),
                        Task.Run(() => _webcamFormBody.work(frameStartTime))
                    );
                }

                // 녹화 기능 추가: 녹화 중이면 현재 프레임 저장
                if ((int)_recordingManager.GetCurrentMode() == 1 && _recordingManager.IsRecording && _frameCount % 4 == 2)
                {
                    // 모든 프레임 소스에서 현재 프레임 획득
                    var frames = new List<(Bitmap frame, long timestamp)>
                        {
                            _webcamFormHead.GetCurrentFrame(),
                            _webcamFormBody.GetCurrentFrame(),
                            _footpadForm.GetCurrentFrame()
                        };
                    
                    Task.Run(() => _recordingManager.RecordFrame(frames));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private CancellationTokenSource? _cancellationTokenSource;

        private void InitializeCustomControls()
        {
            int margin = 90;
            int width = 300;
            int height = 300;
            int x = margin, y = margin + 90;

            // MainForm 속성
            this.Location = new Point(0, 1080);
            this.Size = new Size(2560, 720);
            this.BackColor = Color.GhostWhite;

            // 종료 버튼 속성
            _closeButton.Location = new Point(x, y);
            _closeButton.Size = new Size(width, height);
            _closeButton.Font = new Font("맑은 고딕", 50, FontStyle.Bold);
            _closeButton.BackColor = Color.LightGray;
            _closeButton.TextAlign = ContentAlignment.MiddleCenter;
            _closeButton.Text = "종료";
            _closeButton.Click += new EventHandler(CloseButton_Click);

            x += width + margin;

            // 지연 시간 트랙바 속성
            _delayTextbox.Location = new Point(x + margin / 2, y + margin / 2);
            _delayTextbox.Size = new Size(margin * 2, margin);
            _delayTextbox.Font = new Font("Calibri", margin, FontStyle.Bold);
            _delayTextbox.TextAlign = HorizontalAlignment.Center;
            _delayTextbox.BackColor = Color.Black;
            _delayTextbox.ForeColor = Color.Red;
            _delayTextbox.BorderStyle = BorderStyle.FixedSingle;
            _delayTextbox.Text = "0";

            _delaySlider.Location = new Point(x, y + height - margin);
            _delaySlider.Size = new Size(width, height);
            _delaySlider.Minimum = 0;
            _delaySlider.Maximum = 20;
            _delaySlider.TickFrequency = 1;
            _delaySlider.LargeChange = 1;
            _delaySlider.TickStyle = TickStyle.BottomRight;
            _delaySlider.ValueChanged += DelaySlider_ValueChanged;

            x += width + margin;

            // 시작버튼 속성
            _startButton.Location = new Point(x, y);
            _startButton.Size = new Size(width, height);
            _startButton.Font = new Font("맑은 고딕", 50, FontStyle.Bold);
            _startButton.BackColor = Color.LightGray;
            _startButton.TextAlign = ContentAlignment.MiddleCenter;
            _startButton.Text = "시작";
            _startButton.Click += new EventHandler(StartButton_Click);

            x += width + margin;

            margin = 90;
            width = 200;
            height = 200;
            y += 50;

            // 뒤로가기 버튼
            _backwardButton.Location = new Point(x, y);
            _backwardButton.Size = new Size(width, height);
            _backwardButton.FlatStyle = FlatStyle.Flat;
            _backwardButton.FlatAppearance.BorderSize = 2;
            _backwardButton.BackColor = Color.LightGray;
            _backwardButton.Text = "";
            _backwardButton.IconChar = IconChar.StepBackward;
            _backwardButton.IconSize = 100;
            //_backwardButton.Click += new EventHandler(BackwardButton_Click);
            _backwardButton.MouseDown += new MouseEventHandler(BackwardButton_MouseDown);
            _backwardButton.MouseUp += new MouseEventHandler(BackwardButton_MouseUp);

            x += width + margin;

            // 일시정지 버튼
            _pauseButton.Location = new Point(x, y);
            _pauseButton.Size = new Size(width, height);
            _pauseButton.FlatStyle = FlatStyle.Flat;
            _pauseButton.FlatAppearance.BorderSize = 2;
            _pauseButton.BackColor = Color.LightGray;
            _pauseButton.Text = "";
            _pauseButton.IconChar = IconChar.Play;
            _pauseButton.IconSize = 100;
            _pauseButton.Click += new EventHandler(PauseButton_Click);

            x += width + margin;

            // 앞으로가기 버튼튼
            _forwardButton.Location = new Point(x, y);
            _forwardButton.Size = new Size(width, height);
            _forwardButton.FlatStyle = FlatStyle.Flat;
            _forwardButton.FlatAppearance.BorderSize = 2;
            _forwardButton.BackColor = Color.LightGray;
            _forwardButton.Text = "";
            _forwardButton.IconChar = IconChar.StepForward;
            _forwardButton.IconSize = 100;
            //_forwardButton.Click += new EventHandler(ForwardButton_Click);
            _forwardButton.MouseDown += new MouseEventHandler(ForwardButton_MouseDown);
            _forwardButton.MouseUp += new MouseEventHandler(ForwardButton_MouseUp);

            margin = 100;
            x += width + margin;

            width = 300;
            height = 300;
            y -= 50;

            // 슬로우모드 버튼
            _slowButton.Location = new Point(x, y);
            _slowButton.Size = new Size(width, height);
            _slowButton.Font = new Font("Calibri", 50, FontStyle.Bold);
            _slowButton.BackColor = Color.LightGray;
            _slowButton.TextAlign= ContentAlignment.MiddleCenter;
            _slowButton.Text = "Slow";
            _slowButton.Click += new EventHandler(SlowButton_Click);

            // 스왑 버튼
            //x = margin + width + margin;
            y = y + height + margin;
            width = 120;
            height = 30;
            _recordButton.Location = new Point(x, y);
            _recordButton.Size = new Size(width, height);
            _recordButton.OnColor = Color.Red;
            _recordButton.OffColor = Color.DarkGray;
            _recordButton.OnText = "녹화중";
            _recordButton.OffText = "녹화";
            _recordButton.TextFont = new Font("맑은 고딕", 24, FontStyle.Bold);
            _recordButton.CheckedChanged += new EventHandler(RecordButton_Checked);
            //_recordButton.Enabled = false;

            // 녹화 상태 표시 타이머
            _recordingStatusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _recordingStatusTimer.Tick += (s, e) => UpdateRecordingStatus();
            _recordingStatusTimer.Start();

            // 버튼 타이머
            _backwardTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _backwardTimer.Tick += (s, e) => BackwardButton_Click(s, e);

            _forwardTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _forwardTimer.Tick += (s, e) => ForwardButton_Click(s, e);

            _backwardInitialTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _backwardInitialTimer.Tick += (s, e) => StartBackwardTimer();

            _forwardInitialTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _forwardInitialTimer.Tick += (s, e) => StartForwardTimer();
        }

        private void BackwardButton_MouseDown(object sender, MouseEventArgs e)
        {
            BackwardButton_Click(sender, e);
            _backwardInitialTimer.Start();
        }

        private void BackwardButton_MouseUp(object sender, MouseEventArgs e)
        {
            _backwardInitialTimer.Stop();
            _backwardTimer.Stop();
        }

        private void ForwardButton_MouseDown(object sender, MouseEventArgs e)
        {
            ForwardButton_Click(sender, e);
            _forwardInitialTimer.Start();
        }

        private void ForwardButton_MouseUp(object sender, MouseEventArgs e)
        {
            _forwardInitialTimer.Stop();
            _forwardTimer.Stop();
        }

        private void StartBackwardTimer()
        {
            _backwardInitialTimer.Stop();
            _backwardTimer.Start();
        }

        private void StartForwardTimer()
        {
            _forwardInitialTimer.Stop();
            _forwardTimer.Start();
        }

        private void InitializeProcess()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            Task.Delay(1500).Wait();
            // 멀티미디어 타이머 시작
            _frameTimer.Start();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            //_webcamFormHead = new WebcamForm(0);
            //_webcamFormBody = new WebcamForm(1);
            //_footpadForm = new FootpadForm();

            var screens = Screen.AllScreens;
            if (screens.Length > 3)
            {
                var orderedScreens = screens.OrderBy(s => s.Bounds.Y).ToList();

                var monitor1 = orderedScreens[0];
                _webcamFormHead.StartPosition = FormStartPosition.Manual;
                _webcamFormHead.Location = monitor1.WorkingArea.Location;
                _webcamFormHead.Size = monitor1.WorkingArea.Size;
                _webcamFormHead.FormBorderStyle = FormBorderStyle.None;
                _webcamFormHead.WindowState = FormWindowState.Minimized;

                var monitor2 = orderedScreens[1];
                this.StartPosition = FormStartPosition.Manual;
                this.Location = monitor2.WorkingArea.Location; 
                this.Size = monitor2.WorkingArea.Size;
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Maximized; 

                var monitor3 = orderedScreens[2];
                _webcamFormBody.StartPosition = FormStartPosition.Manual;
                _webcamFormBody.Location = monitor3.WorkingArea.Location;
                _webcamFormBody.Size = monitor3.WorkingArea.Size;
                _webcamFormBody.FormBorderStyle = FormBorderStyle.None;
                _webcamFormBody.WindowState = FormWindowState.Minimized;

                var monitor0 = orderedScreens[3];
                _footpadForm.StartPosition = FormStartPosition.Manual;
                _footpadForm.Location = monitor0.WorkingArea.Location;
                //_footpadForm.Size = monitor0.WorkingArea.Size;
                //_footpadForm.FormBorderStyle = FormBorderStyle.None;
                _footpadForm.WindowState = FormWindowState.Minimized;
            }

            this.Location = new Point(0, 720);
            _webcamFormHead.Text = "Head";
            _webcamFormHead.Show();
            _webcamFormBody.Text = "Body";
            _webcamFormBody.Show();

            _footpadForm.Show();

            InitializeProcess();
            InitializeRecordingManager();
        }

        private void InitializeRecordingManager()
        {
            try
            {
                // FootpadForm에 캡처 기능 초기화
                _footpadForm.InitializeForRecording();

                // 프레임 소스 목록 생성
                var frameSources = new List<IFrameProvider>
                {
                    _webcamFormHead,
                    _webcamFormBody,
                    _footpadForm
                };

                // 녹화 매니저 초기화
                _recordingManager = new RecordingManager(frameSources, @"C:\Users\dulab\Desktop");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"녹화 기능 초기화 오류: {ex.Message}");
            }
        }

        private void DelaySlider_ValueChanged(object sender, EventArgs e)
        {
            _delaySeconds = ((CustomTrackBar)sender).Value;
            _delayTextbox.Text = _delaySeconds.ToString();
            _webcamFormHead.SetDelay(_delaySeconds);
            _webcamFormBody.SetDelay(_delaySeconds);
            _footpadForm.SetDelay(_delaySeconds);
        }

        private void StartButton_Click(object? sender, EventArgs e)
        {
            _isStarted = !_isStarted;
            if (_isStarted)
            {
                _startButton.Text = "대기";
                _isPaused = false;
                _delaySlider.Enabled = false;
            }
            else
            {
                _startButton.Text = "시작";
                _isPaused = true;
                _delaySlider.Enabled = true;
            }
            UpdatePlayPauseButton();
            _webcamFormHead.SetKey("r");
            _webcamFormBody.SetKey("r");
            _footpadForm.SetKey("r");
            _recordingManager.SetKey("r");
        }

        private void BackwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isPaused = true;
                UpdatePlayPauseButton();
                _webcamFormHead.SetKey("a");
                _webcamFormBody.SetKey("a");
                _footpadForm.SetKey("a");
                _recordingManager.SetKey("a");
            }
        }

        private void PauseButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isPaused = !_isPaused;
                UpdatePlayPauseButton();
                _webcamFormHead.SetKey("p");
                _webcamFormBody.SetKey("p");
                _footpadForm.SetKey("p");
                _recordingManager.SetKey("p");
            }
        }

        private void ForwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isPaused = true;
                UpdatePlayPauseButton();
                _webcamFormHead.SetKey("d");
                _webcamFormBody.SetKey("d");
                _footpadForm.SetKey("d");
                _recordingManager.SetKey("d");
            }
        }

        private void SlowButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isSlowMode = !_isSlowMode;
                _slowButton.BackColor = _isSlowMode ? Color.DarkGray : Color.LightGray;
                _webcamFormHead.SetKey("s");
                _webcamFormBody.SetKey("s");
                _footpadForm.SetKey("s");
                _recordingManager.SetKey("s");
            }
        }

        private void RecordButton_Checked(Object? sender, EventArgs e)
        {
            Console.WriteLine($"{_recordButton.Checked}");
            if (_recordingManager == null) return;

            try
            {
                if (_recordButton.Checked)
                {
                    _recordingManager.StartRecording();
                }
                else
                {
                    _recordingManager.StopRecording();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"녹화 상태 변경 오류: {ex.Message}");
                _recordButton.Checked = false;
            }
        }

        private void UpdateRecordingStatus()
        {
            if (_recordingManager == null) return;

            // 녹화 중이면 버튼 깜빡임 효과
            if (_recordingManager.IsRecording && _isStarted)
            {
                _recordButton.OnColor = _recordButton.OnColor == System.Drawing.Color.Red
                    ? System.Drawing.Color.DarkRed
                    : System.Drawing.Color.Red;
            }
            else
            {
                _recordButton.OnColor = System.Drawing.Color.Red;
            }
        }

        private void CleanupRecordingResources()
        {
            _recordingStatusTimer?.Stop();
            _recordingStatusTimer?.Dispose();
            _recordingManager?.Dispose();
        }

        private void UpdatePlayPauseButton()
        {
            _pauseButton.IconChar = _isPaused ? IconChar.Play : IconChar.Pause;
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            _cancellationTokenSource?.Cancel();

            // 멀티미디어 타이머 중지 및 정리
            if (_frameTimer != null && _frameTimer.IsRunning)
            {
                _frameTimer.Stop();
                _frameTimer.Dispose();
            }

            Task.Delay(1000);
            _webcamFormHead.Close();
            _webcamFormBody.Close();
            this.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 멀티미디어 타이머 정리
            if (_frameTimer != null && _frameTimer.IsRunning)
            {
                _frameTimer.Stop();
                _frameTimer.Dispose();
            }

            CleanupRecordingResources();

            base.OnFormClosed(e);

            _webcamFormHead.Close();
            _webcamFormBody.Close();
        }
    }
}
