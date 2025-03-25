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
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Reflection.Metadata;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace MultiWebcamApp
{
    public partial class MainForm : Form
    {
        private readonly SyncManager _syncManager;
        private readonly FrameBuffer _buffer;
        private Task _renderingTask;
        private CancellationTokenSource _renderingCts;

        private OperationMode _mode = OperationMode.Idle;
        private readonly CameraViewer.MainWindow _headDisplay, _bodyDisplay;
        private readonly PressureMapViewer.MainWindow _pressureDisplay;

        private const double FPS = 30.0;
        private int _slowLevel = 1;

        private int _delaySeconds = 0;
        private bool _isStarted = false;
        private bool _isPaused = false;
        private bool _isSlowMode = false;

        private System.Windows.Forms.Timer _recordingStatusTimer;
        private System.Windows.Forms.Timer _backwardTimer;
        private System.Windows.Forms.Timer _forwardTimer;
        private System.Windows.Forms.Timer _backwardInitialTimer;
        private System.Windows.Forms.Timer _forwardInitialTimer;
        
        private volatile bool _isClosing; // 종료 중 플래그

        private PressurePadSource _pressurePadSource;
        private RecordingManager _recordingManager;

        public MainForm()
        {
            InitializeComponent();
            InitializeCustomControls();

            _buffer = new FrameBuffer(); 
            var webcamSource1 = new WebcamSource(0);
            var webcamSource2 = new WebcamSource(1);
            var pressurePadSource = new PressurePadSource();
            _pressurePadSource = pressurePadSource;
            var sources = new List<IFrameSource> { webcamSource1, webcamSource2 };
            // 압력패드 소스 추가 여부 확인
            try
            {
                pressurePadSource.Start(); // 초기화 테스트
                sources.Add(pressurePadSource);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pressure pad initialization failed: {ex.Message}. Proceeding with cameras only.");
            }

            _syncManager = new SyncManager(sources.ToArray(), _buffer);
            _headDisplay = new CameraViewer.MainWindow();
            _bodyDisplay = new CameraViewer.MainWindow();
            _pressureDisplay = new PressureMapViewer.MainWindow();

            _pressureDisplay.ResetPortsRequested += PressureDisplay_ResetPortsRequested;

            _recordingManager = new RecordingManager(_pressureDisplay);
            // 녹화 성능 최적화를 위한 설정
            _recordingManager.EnableFrameMixing(true);
            _recordingManager.SetFrameMixingRatio(0.8); // 80% 현재 프레임, 20% 이전 프레임

            // 프레임 시간 계산 (밀리초)
            //_frameTimeMs = (int)(1000.0 / TARGET_FPS);

            // 멀티미디어 타이머 초기화
            //InitializeMultimediaTimer();

            //_mainStopwatch.Start();
        }

        private void MainForm_Load(object sender, EventArgs e)
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
                this.StartPosition = FormStartPosition.Manual;
                this.Location = monitor2.WorkingArea.Location;
                this.Size = monitor2.WorkingArea.Size;
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Maximized;

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

            this.Location = new System.Drawing.Point(0, 720);
            _headDisplay.Show();
            _bodyDisplay.Show();
            _pressureDisplay.Show();

            _syncManager.Start();
            StartRendering();
        }

        private async void StartRendering()
        {
            _renderingCts = new CancellationTokenSource();
            _renderingTask = Task.Run(() => RenderLoop(_renderingCts.Token));
        }

        private async Task RenderLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_isClosing) break;

                FrameData frame = null;
                var msgLower = "";
                var msgUpper = "";
                switch (_mode)
                {
                    case OperationMode.Idle:
                        if (_syncManager.TryGetFrame(out frame))
                        {
                            msgLower = statusMessage(_buffer.PlayPosition / FPS);
                            break;
                        }
                        await Task.Delay(10);
                        break;
                    case OperationMode.Play:
                        if (_syncManager.TryGetFrame(out frame))
                        {
                            _buffer.Add(frame);

                            int delayFrames = _delaySeconds * (int)FPS;
                            int playPosition;
                            
                            if (delayFrames >= _buffer.Count)
                            {
                                playPosition = 0;
                                int remainingFrames = delayFrames - _buffer.Count + 1;
                                int countdownSeconds = (int)(remainingFrames / FPS) + 1;
                                msgLower = statusMessage(_buffer.Count / FPS);
                                msgUpper = countdownSeconds != 0 ? countdownSeconds.ToString() : "▶";
                            }
                            else
                            {
                                playPosition = _buffer.Count - delayFrames - 1;
                                msgLower = statusMessage(playPosition / FPS);
                                msgUpper = "▶";
                            }

                            frame = _buffer.GetFrame(playPosition);
                            _buffer.PlayPosition = playPosition;
                            break;
                        }
                        await Task.Delay(10);
                        break;
                    case OperationMode.Replay:
                        if (_syncManager.TryGetFrame(out _))
                        {
                            frame = _buffer.GetFrame(_buffer.PlayPosition);
                            if (frame != null)
                            {
                                _buffer.PlayPosition = (_buffer.PlayPosition + 1) % _buffer.Count;
                                if (_buffer.PlayPosition == (_buffer.Count - 1))
                                {
                                    PauseButton_Click(this, null);
                                }

                                msgLower = statusMessage(_buffer.PlayPosition / FPS);
                                msgUpper = " ";
                                await Task.Delay((int)(33.33 * _slowLevel), token);
                                break;
                            }
                        }
                        await Task.Delay(10, token);
                        break;
                    case OperationMode.Stop:
                        if (_syncManager.TryGetFrame(out _))
                        {
                            frame = _buffer.GetFrame(_buffer.PlayPosition);
                            if (frame != null)
                            {
                                msgLower = statusMessage(_buffer.PlayPosition / FPS);
                                msgUpper = " ";
                                break;
                            }
                        }
                        await Task.Delay(33, token);
                        break;
                }

                if (frame != null && !token.IsCancellationRequested && !_isClosing && IsHandleCreated)
                {
                    try
                    {
                        Invoke(new Action(() =>
                        {
                            if (!_isClosing && IsHandleCreated) // 중복 체크
                            {
                                if (_recordingManager != null && _recordingManager.IsRecording && _mode == OperationMode.Play)
                                {
                                    _recordingManager.AddFrame(frame);
                                }

                                UpdateUI(frame, msgLower, msgUpper);
                            }
                        }));
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }
                }
            }
        }

        private void UpdateUI(FrameData frame, string msg = "", string msg2 = "")
        {
            _headDisplay.UpdateFrame(frame.WebcamHead, msg, msg2);
            _bodyDisplay.UpdateFrame(frame.WebcamBody, msg, msg2);
            if (frame.PressureData != null)
            {
                _pressureDisplay.UpdatePressureData(frame.PressureData);
            }
        }

        private string statusMessage(double seconds, int slowLevel = 1)
        {
            string message = "";

            message += $"{seconds,5:F1}s\t ";
            message += $"x{1.0 / slowLevel,1:F2}";

            return message;
        }

        private void InitializeCustomControls()
        {
            int margin = 90;
            int width = 300;
            int height = 300;
            int x = margin, y = margin + 90;

            // MainForm 속성
            this.Location = new System.Drawing.Point(0, 1080);
            this.Size = new System.Drawing.Size(2560, 720);
            this.BackColor = System.Drawing.Color.GhostWhite;

            // 종료 버튼 속성
            _closeButton.Location = new System.Drawing.Point(x, y);
            _closeButton.Size = new System.Drawing.Size(width, height);
            _closeButton.Font = new Font("맑은 고딕", 50, FontStyle.Bold);
            _closeButton.BackColor = System.Drawing.Color.LightGray;
            _closeButton.TextAlign = ContentAlignment.MiddleCenter;
            _closeButton.Text = "종료";
            _closeButton.Click += new EventHandler(CloseButton_Click);

            x += width + margin;

            // 지연 시간 트랙바 속성
            _delayTextbox.Location = new System.Drawing.Point(x + margin / 2, y + margin / 2);
            _delayTextbox.Size = new System.Drawing.Size(margin * 2, margin);
            _delayTextbox.Font = new Font("Calibri", margin, FontStyle.Bold);
            _delayTextbox.TextAlign = HorizontalAlignment.Center;
            _delayTextbox.BackColor = System.Drawing.Color.Black;
            _delayTextbox.ForeColor = System.Drawing.Color.Red;
            _delayTextbox.BorderStyle = BorderStyle.FixedSingle;
            _delayTextbox.Text = "0";
            _delayTextbox.ReadOnly = true;

            _delaySlider.Location = new System.Drawing.Point(x, y + height - margin);
            _delaySlider.Size = new System.Drawing.Size(width, height);
            _delaySlider.Minimum = 0;
            _delaySlider.Maximum = 20;
            _delaySlider.TickFrequency = 1;
            _delaySlider.LargeChange = 1;
            _delaySlider.TickStyle = TickStyle.BottomRight;
            _delaySlider.ValueChanged += DelaySlider_ValueChanged;

            x += width + margin;

            // 시작버튼 속성
            _startButton.Location = new System.Drawing.Point(x, y);
            _startButton.Size = new System.Drawing.Size(width, height);
            _startButton.Font = new Font("맑은 고딕", 50, FontStyle.Bold);
            _startButton.BackColor = System.Drawing.Color.LightGray;
            _startButton.TextAlign = ContentAlignment.MiddleCenter;
            _startButton.Text = "시작";
            _startButton.Click += new EventHandler(StartButton_Click);

            x += width + margin;

            margin = 90;
            width = 200;
            height = 200;
            y += 50;

            // 뒤로가기 버튼
            _backwardButton.Location = new System.Drawing.Point(x, y);
            _backwardButton.Size = new System.Drawing.Size(width, height);
            _backwardButton.FlatStyle = FlatStyle.Flat;
            _backwardButton.FlatAppearance.BorderSize = 2;
            _backwardButton.BackColor = System.Drawing.Color.LightGray;
            _backwardButton.Text = "";
            _backwardButton.IconChar = IconChar.StepBackward;
            _backwardButton.IconSize = 100;
            //_backwardButton.Click += new EventHandler(BackwardButton_Click);
            _backwardButton.MouseDown += new MouseEventHandler(BackwardButton_MouseDown);
            _backwardButton.MouseUp += new MouseEventHandler(BackwardButton_MouseUp);

            x += width + margin;

            // 일시정지 버튼
            _pauseButton.Location = new System.Drawing.Point(x, y);
            _pauseButton.Size = new System.Drawing.Size(width, height);
            _pauseButton.FlatStyle = FlatStyle.Flat;
            _pauseButton.FlatAppearance.BorderSize = 2;
            _pauseButton.BackColor = System.Drawing.Color.LightGray;
            _pauseButton.Text = "";
            _pauseButton.IconChar = IconChar.Play;
            _pauseButton.IconSize = 100;
            _pauseButton.Click += new EventHandler(PauseButton_Click);

            x += width + margin;

            // 앞으로가기 버튼튼
            _forwardButton.Location = new System.Drawing.Point(x, y);
            _forwardButton.Size = new System.Drawing.Size(width, height);
            _forwardButton.FlatStyle = FlatStyle.Flat;
            _forwardButton.FlatAppearance.BorderSize = 2;
            _forwardButton.BackColor = System.Drawing.Color.LightGray;
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
            _slowButton.Location = new System.Drawing.Point(x, y);
            _slowButton.Size = new System.Drawing.Size(width, height);
            _slowButton.Font = new Font("Calibri", 50, FontStyle.Bold);
            _slowButton.BackColor = System.Drawing.Color.LightGray;
            _slowButton.TextAlign= ContentAlignment.MiddleCenter;
            _slowButton.Text = "Slow";
            _slowButton.Click += new EventHandler(SlowButton_Click);

            // 스왑 버튼
            //x = margin + width + margin;
            y = y + height + margin;
            width = 120;
            height = 30;
            _recordButton.Location = new System.Drawing.Point(x, y);
            _recordButton.Size = new System.Drawing.Size(width, height);
            _recordButton.OnColor = System.Drawing.Color.Red;
            _recordButton.OffColor = System.Drawing.Color.DarkGray;
            _recordButton.OnText = "녹화 On";
            _recordButton.OffText = "녹화 Off";
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

        private void DelaySlider_ValueChanged(object sender, EventArgs e)
        {
            _delaySeconds = ((CustomTrackBar)sender).Value;
            _delayTextbox.Text = _delaySeconds.ToString();
            //_webcamFormHead.SetDelay(_delaySeconds);
            //_webcamFormBody.SetDelay(_delaySeconds);
            //_footpadForm.SetDelay(_delaySeconds);
        }

        private void PressureDisplay_ResetPortsRequested(object sender, EventArgs e)
        {
            if (_pressurePadSource != null)
            {
                try
                {
                    _pressurePadSource.ResetAllPorts();
                    Console.WriteLine("Ports reset completed from MainForm.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error resetting ports from MainForm: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("No PressurePadSource available to reset.");
            }
        }

        private async void StartButton_Click(object? sender, EventArgs e)
        {
            _isStarted = !_isStarted;
            if (_isStarted)
            {
                _mode = OperationMode.Play;
                _startButton.Text = "대기";
                _isPaused = false;
                _delaySlider.Enabled = false;
                _recordButton.Enabled = false;
                _buffer.Clear();

                // 녹화 버튼이 체크되어 있으면 녹화 시작
                if (_recordButton.Checked && _recordingManager != null)
                {
                    _recordingManager.StartRecording();
                }
            }
            else
            {
                _mode = OperationMode.Idle;
                _startButton.Text = "시작";
                _isPaused = true;
                _delaySlider.Enabled = true;
                _recordButton.Enabled = true;
                _buffer.Clear();
                _pressurePadSource.ResetAllPorts();

                // 녹화 중이면 녹화 중지
                if (_recordingManager != null && _recordingManager.IsRecording)
                {
                    await _recordingManager.StopRecordingAsync();
                }
            }
            UpdatePlayPauseButton();
            //_webcamFormHead.SetKey("r");
            //_webcamFormBody.SetKey("r");
            //_footpadForm.SetKey("r");
            //_recordingManager.SetKey("r");
        }

        private async void PauseButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isPaused = !_isPaused;
                if (_isPaused)
                {
                    _mode = OperationMode.Stop;
                }
                else
                {
                    _mode = OperationMode.Replay;
                }
                UpdatePlayPauseButton();

                // 녹화 중이면 녹화 중지
                if (_recordingManager != null && _recordingManager.IsRecording)
                {
                    await _recordingManager.StopRecordingAsync();
                }
                //_webcamFormHead.SetKey("p");
                //_webcamFormBody.SetKey("p");
                //_footpadForm.SetKey("p");
                //_recordingManager.SetKey("p");
            }
        }

        private async void BackwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted && _mode != OperationMode.Idle)
            {
                _mode = OperationMode.Stop;
                _isPaused = true;
                _buffer.PlayPosition = Math.Max(0, _buffer.PlayPosition - 15);
                UpdatePlayPauseButton();

                // 녹화 중이면 녹화 중지
                if (_recordingManager != null && _recordingManager.IsRecording)
                {
                    await _recordingManager.StopRecordingAsync();
                }
                //_webcamFormHead.SetKey("a");
                //_webcamFormBody.SetKey("a");
                //_footpadForm.SetKey("a");
                //_recordingManager.SetKey("a");
            }
        }

        private async void ForwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted && _mode != OperationMode.Idle)
            {
                _mode = OperationMode.Stop;
                _isPaused = true;
                _buffer.PlayPosition = Math.Min(_buffer.Count - 1, _buffer.PlayPosition + 15);
                UpdatePlayPauseButton();

                // 녹화 중이면 녹화 중지
                if (_recordingManager != null && _recordingManager.IsRecording)
                {
                    await _recordingManager.StopRecordingAsync();
                }
                //_webcamFormHead.SetKey("d");
                //_webcamFormBody.SetKey("d");
                //_footpadForm.SetKey("d");
                //_recordingManager.SetKey("d");
            }
        }

        private void SlowButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted && (_mode == OperationMode.Stop || _mode == OperationMode.Replay))
            {
                _slowLevel = _slowLevel == 8 ? 1 : _slowLevel * 2;
                if (_slowLevel != 1) _isSlowMode = true;
                else _isSlowMode = false;

                _slowButton.BackColor = _isSlowMode ? System.Drawing.Color.DarkGray : System.Drawing.Color.LightGray;
                _slowButton.Text = _isSlowMode ? $" \nSlow\nx{1.0/_slowLevel,1:F2}" : "Slow";
                //_webcamFormHead.SetKey("s");
                //_webcamFormBody.SetKey("s");
                //_footpadForm.SetKey("s");
                //_recordingManager.SetKey("s");
            }
        }

        private void RecordButton_Checked(Object? sender, EventArgs e)
        {
            Console.WriteLine($"Record Status: {_recordButton.Checked}");

            if (_recordingManager == null) return;

            try
            {
                if (_recordButton.Checked)
                {
                    Console.WriteLine("Record Ready.");
                }
                else
                {
                    _recordingManager.StopRecordingAsync().Wait();
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
            _backwardTimer?.Stop();
            _backwardTimer?.Dispose();
            _forwardTimer?.Stop();
            _forwardTimer?.Dispose();
            _backwardInitialTimer?.Stop();
            _backwardInitialTimer?.Dispose();
            _forwardInitialTimer?.Stop();
            _forwardInitialTimer?.Dispose();

            _recordingManager?.Dispose();
            _recordingManager = null;
        }

        private void UpdatePlayPauseButton()
        {
            _pauseButton.IconChar = _isPaused ? IconChar.Play : IconChar.Pause;
        }

        private async void CloseButton_Click(object sender, EventArgs e)
        {
            if (_isClosing) return;
            await CloseApplication();
        }

        private async Task CloseApplication()
        {
            if (_isClosing) return;
            _isClosing = true;
            Console.WriteLine("Closing application...");
            _closeButton.Enabled = false;

            _renderingCts?.Cancel();
            if (_renderingTask != null)
            {
                try
                {
                    await _renderingTask.ConfigureAwait(false);
                }
                catch (TaskCanceledException) { }
            }
            await _syncManager.StopAsync();

            if (IsHandleCreated)
            {
                Invoke(new Action(() =>
                {
                    if (_headDisplay.IsLoaded) _headDisplay.Dispatcher.Invoke(() => _headDisplay.Close());
                    if (_bodyDisplay.IsLoaded) _bodyDisplay.Dispatcher.Invoke(() => _bodyDisplay.Close());
                    if (_pressureDisplay.IsLoaded) _pressureDisplay.Dispatcher.Invoke(() => _pressureDisplay.Close());
                }));
            }
            CleanupRecordingResources();

            if (IsHandleCreated)
            {
                Invoke(new Action(() => Close())); // UI 스레드에서 호출 보장
            }
            else
            {
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_isClosing)
            {
                e.Cancel = true;
                CloseButton_Click(null, null);
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _recordingManager?.Dispose();
            base.OnFormClosed(e);
        }
    }

    public class FrameData
    {
        public Mat WebcamHead { get; set; }
        public Mat WebcamBody { get; set; }
        public ushort[] PressureData { get; set; }
        public long Timestamp { get; set; }
    }

    public interface IFrameSource
    {
        FrameData CaptureFrame();
        void Start();
        void Stop();
    }

    public enum OperationMode
    {
        Idle,
        Play,
        Replay,
        Stop
    }
}
