﻿using System;
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
using System.Timers;
using System.Windows;
using static UserInterface.MainWindow;
using System.Windows.Threading;

namespace MultiWebcamApp
{
    public partial class MainForm : Form
    {
        private readonly SyncManager _syncManager;
        private readonly FrameBuffer _buffer;
        private Task _renderingTask;
        private CancellationTokenSource _renderingCts;

        private OperationMode _mode = OperationMode.Idle;
        private DisplayManager _displayManager;

        private const double FPS = 60.0;
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

        private System.Timers.Timer _healthCheckTimer;

        private Multimedia.Timer _mainTimer;
        private int _isProcessing = 0;
        //private Task _mainTask;

        private ScreenRecordingLib.ScreenRecorder recorder;
        private bool _isRecording = false;

        // 슬로우 모드용 카운터
        private int _slowCounter = 0;

        public MainForm()
        {
            InitializeComponent();
            //InitializeCustomControls();
            InitializeHealthCheck();
            InitializeMainTimer();

            _buffer = new FrameBuffer();
            var webcamSource1 = new WebcamSource(0);
            var webcamSource2 = new WebcamSource(1);
            var pressurePadSource = new PressurePadSource();
            _pressurePadSource = pressurePadSource;

            var sources = new List<IFrameSource> { webcamSource1, webcamSource2 };
            // 압력패드 소스 추가 여부 확인
            try
            {
                //pressurePadSource.Start(); // 초기화 테스트
                sources.Add(pressurePadSource);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pressure pad initialization failed: {ex.Message}. Proceeding with cameras only.");
            }

            _syncManager = new SyncManager(sources.ToArray(), _buffer);

            _displayManager = new DisplayManager();
            _displayManager.RegisterPressureDisplayResetEvent(PressureDisplay_ResetPortsRequested);
            _displayManager.RegisterUiDisplayCloseButtonEvent(CloseButton_Click);
            _displayManager.RegisterUiDisplayDelaySliderEvent(DelaySlider_ValueChanged);
            _displayManager.RegisterUiDisplayStartButtonEvent(StartButton_Click);
            _displayManager.RegisterUiDisplayPlayButtonEvent(PauseButton_Click);
            _displayManager.RegisterUiDisplayBackwardButtonEvent(BackwardButton_Click);
            _displayManager.RegisterUiDisplayForwardButtonEvent(ForwardButton_Click);
            _displayManager.RegisterUiDisplaySlowButtonEvent(SlowButton_Click);
            _displayManager.RegisterUiDisplayRecordToggleEvent(RecordButton_Checked);

            // 녹화 상태 표시 타이머
            _recordingStatusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _recordingStatusTimer.Tick += (s, e) => _displayManager.CallUiDisplayUpdateRecordingStatus();
            _recordingStatusTimer.Start();

            recorder = new ScreenRecordingLib.ScreenRecorder();

            recorder.OnStatusMessage += (message) =>
            {
                _displayManager.UiDisplaySetStatusMessage(message);
            };
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _displayManager.ConfigureDisplayPositions();
            _displayManager.ShowDisplay();

            _pressurePadSource.Start();
            _syncManager.Start();

            _mainTimer.Start();

            this.WindowState = FormWindowState.Minimized;
        }

        private void InitializeMainTimer()
        {
            _mainTimer = new Multimedia.Timer();
            _mainTimer.Period = 17;
            _mainTimer.Resolution = 1;
            _mainTimer.Mode = TimerMode.Periodic;
            _mainTimer.Tick += MainWork;
        }

        private void MainWork(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _isProcessing, 1) == 1)
            {
                Console.WriteLine("이전 프레임 처리 중 - 건너뜀");
                return;
            }

            try
            {
                // 1. 캡처 단계: SyncManager를 통해 새 프레임 캡처
                FrameData frame = null;
                var msgLower = "";
                var msgUpper = "";
                var msgSlow = "";
                switch (_mode)
                {
                    case OperationMode.Idle:
                        if (_syncManager.TryGetFrame(out frame))
                        {
                            msgUpper = statusMessage(_buffer.PlayPosition / FPS);
                            msgSlow = slowStatusMessage();
                            msgLower = "Ready";
                        }
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
                                msgUpper = statusMessage(0.0f);
                                msgSlow = slowStatusMessage();
                                msgLower = countdownSeconds != 0 ? countdownSeconds.ToString() : "Play";
                            }
                            else
                            {
                                playPosition = _buffer.Count - delayFrames - 1;
                                msgUpper = statusMessage(playPosition / FPS);
                                msgSlow = slowStatusMessage();
                                msgLower = "Play";
                            }

                            frame = _buffer.GetFrame(playPosition);
                            _buffer.PlayPosition = playPosition;
                        }
                        break;
                    case OperationMode.Replay:
                        if (_syncManager.TryGetFrame(out _))
                        {
                            frame = _buffer.GetFrame(_buffer.PlayPosition);
                            if (frame != null)
                            {
                                if (_slowCounter++ % _slowLevel == 0)
                                {
                                    _buffer.PlayPosition = (_buffer.PlayPosition + 1) % _buffer.Count;
                                }
                                if (_buffer.PlayPosition == (_buffer.Count - 1))
                                {
                                    PauseButton_Click(this, null);
                                }

                                msgUpper = statusMessage(_buffer.PlayPosition / FPS, _slowLevel);
                                msgSlow = slowStatusMessage(_slowLevel);
                                msgLower = "Replay";
                            }
                        }
                        break;
                    case OperationMode.Stop:
                        if (_syncManager.TryGetFrame(out _))
                        {
                            frame = _buffer.GetFrame(_buffer.PlayPosition);
                            if (frame != null)
                            {
                                msgUpper = statusMessage(_buffer.PlayPosition / FPS, _slowLevel);
                                msgSlow = slowStatusMessage(_slowLevel);
                                msgLower = "Stop";
                            }
                        }
                        break;
                }

                if (frame != null && !_isClosing && IsHandleCreated)
                {
                    UpdateUI(frame, msgUpper, msgLower, msgSlow);
                }

                _syncManager.CaptureFrames();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainWork 처리 중 오류 발생: {ex.Message}");
            }
            finally
            {
                // 작업 완료 표시
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        private void UpdateUI(FrameData frame, string msg = "", string msg2 = "", string msg3 = "")
        {
            _displayManager.UpdateFrames(frame, msg, msg2, msg3);
        }

        private string statusMessage(double seconds, int slowLevel = 1)
        {
            string message = "";

            message += $" {seconds,5:F1}s ";
            //message += $" x{1.0 / slowLevel,1:F2} ";

            return message;
        }

        private string slowStatusMessage(int slowLevel = 1)
        {
            string message = "";

            message += $" x{1.0 / slowLevel,1:F2} ";

            return message;
        }

        private void InitializeHealthCheck()
        {
            // 1초마다 상태 확인 (필요에 따라 조정)
            _healthCheckTimer = new System.Timers.Timer(1000);
            _healthCheckTimer.Elapsed += HealthCheck_Elapsed;
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();
        }

        private void HealthCheck_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                // CaptureLoop에서 재설정이 필요하다고 플래그가 설정된 경우
                if (_pressurePadSource != null && _pressurePadSource.NeedsReset)
                {
                    Console.WriteLine("CaptureLoop에서 재설정 요청을 감지했습니다. 모든 포트 재설정 중...");

                    // 타이머 일시 중지
                    _healthCheckTimer.Stop();

                    // UI가 없는 환경에서는 직접 호출
                    _pressurePadSource.ResetAllPorts();
                    _pressurePadSource.ResetFlag(); // 재설정 플래그 초기화

                    // 타이머 재시작
                    _healthCheckTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"상태 확인 중 오류 발생: {ex.Message}");
            }
        }

        // 애플리케이션 종료 시 타이머 정리
        private void CleanupHealthCheck()
        {
            if (_healthCheckTimer != null)
            {
                _healthCheckTimer.Stop();
                _healthCheckTimer.Elapsed -= HealthCheck_Elapsed;
                _healthCheckTimer.Dispose();
            }
        }

        private void InitializeCustomControls()
        {
            int margin = 90/2;
            int width = 300/2;
            int height = 300 / 2;
            int x = margin, y = margin + (90/2);

            // MainForm 속성
            this.Location = new System.Drawing.Point(0, 1080);
            this.Size = new System.Drawing.Size(1280, 360);
            this.BackColor = System.Drawing.Color.GhostWhite;

            // 종료 버튼 속성
            _closeButton.Location = new System.Drawing.Point(x, y);
            _closeButton.Size = new System.Drawing.Size(width, height);
            _closeButton.Font = new System.Drawing.Font("맑은 고딕", 50/2, System.Drawing.FontStyle.Bold);
            _closeButton.BackColor = System.Drawing.Color.LightGray;
            _closeButton.TextAlign = ContentAlignment.MiddleCenter;
            _closeButton.Text = "종료";
            _closeButton.Click += new EventHandler(CloseButton_Click);

            x += width + margin;

            // 지연 시간 트랙바 속성
            _delayTextbox.Location = new System.Drawing.Point(x + margin / 2, y + margin / 2);
            _delayTextbox.Size = new System.Drawing.Size(margin * 2, margin);
            _delayTextbox.Font = new System.Drawing.Font("Calibri", margin, System.Drawing.FontStyle.Bold);
            _delayTextbox.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
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
            //_delaySlider.ValueChanged += DelaySlider_ValueChanged;

            x += width + margin;

            // 시작버튼 속성
            _startButton.Location = new System.Drawing.Point(x, y);
            _startButton.Size = new System.Drawing.Size(width, height);
            _startButton.Font = new System.Drawing.Font("맑은 고딕", 50/2, System.Drawing.FontStyle.Bold);
            _startButton.BackColor = System.Drawing.Color.LightGray;
            _startButton.TextAlign = ContentAlignment.MiddleCenter;
            _startButton.Text = "시작";
            _startButton.Click += new EventHandler(StartButton_Click);

            x += width + margin;

            margin = 90/2;
            width = 200/2;
            height = 200/2;
            y += 50/2;

            // 뒤로가기 버튼
            _backwardButton.Location = new System.Drawing.Point(x, y);
            _backwardButton.Size = new System.Drawing.Size(width, height);
            _backwardButton.FlatStyle = FlatStyle.Flat;
            _backwardButton.FlatAppearance.BorderSize = 2;
            _backwardButton.BackColor = System.Drawing.Color.LightGray;
            _backwardButton.Text = "";
            _backwardButton.IconChar = IconChar.StepBackward;
            _backwardButton.IconSize = 100/2;
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
            _pauseButton.IconSize = 100/2;
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
            _forwardButton.IconSize = 100/2;
            //_forwardButton.Click += new EventHandler(ForwardButton_Click);
            _forwardButton.MouseDown += new MouseEventHandler(ForwardButton_MouseDown);
            _forwardButton.MouseUp += new MouseEventHandler(ForwardButton_MouseUp);

            margin = 100/2;
            x += width + margin;

            width = 300/2;
            height = 300/2;
            y -= 50/2;

            // 슬로우모드 버튼
            _slowButton.Location = new System.Drawing.Point(x, y);
            _slowButton.Size = new System.Drawing.Size(width, height);
            _slowButton.Font = new System.Drawing.Font("Calibri", 50/2, System.Drawing.FontStyle.Bold);
            _slowButton.BackColor = System.Drawing.Color.LightGray;
            _slowButton.TextAlign= ContentAlignment.MiddleCenter;
            _slowButton.Text = "Slow";
            _slowButton.Click += new EventHandler(SlowButton_Click);

            // 스왑 버튼
            //x = margin + width + margin;
            y = y + height + margin;
            width = 120/2;
            height = 30/2;
            _recordButton.Location = new System.Drawing.Point(x, y);
            _recordButton.Size = new System.Drawing.Size(width, height);
            _recordButton.OnColor = System.Drawing.Color.Red;
            _recordButton.OffColor = System.Drawing.Color.DarkGray;
            _recordButton.OnText = "녹화 On";
            _recordButton.OffText = "녹화 Off";
            _recordButton.TextFont = new System.Drawing.Font("맑은 고딕", 24/2, System.Drawing.FontStyle.Bold);
            //_recordButton.CheckedChanged += new EventHandler(RecordButton_Checked);
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

        private void DelaySlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            //_delaySeconds = ((CustomTrackBar)sender).Value;
            _delaySeconds = (int)e.NewValue;
            _delayTextbox.Text = _delaySeconds.ToString();
        }

        private void PressureDisplay_ResetPortsRequested(object? sender, EventArgs e)
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

            _displayManager.UiDisplaySetStartButtonText(_isStarted);

            if (_isStarted)
            {
                _mode = OperationMode.Play;
                _startButton.Text = "중단";
                _isPaused = false;
                _delaySlider.Enabled = false;
                _recordButton.Enabled = false;
                _buffer.Clear();

                // 녹화 버튼이 체크되어 있으면 녹화 시작
                if (_isRecording)
                {
                    recorder.StartRecording();
                }
                _displayManager.UiDisplaySetStatusMessage("관찰을 시작합니다.");
            }
            else
            {
                _displayManager.UiDisplaySetStatusMessage("대기 상태로 돌아갑니다.");
                // 녹화 중이면 녹화 중지
                if (_isRecording)
                {
                    recorder.StopRecording();
                }

                _mode = OperationMode.Idle;
                _startButton.Text = "시작";
                _isPaused = true;
                _delaySlider.Enabled = true;
                _recordButton.Enabled = true;
                _buffer.Clear();
                _pressurePadSource.ResetAllPorts();

                _slowLevel = 1;
                _slowCounter = 0;
                _isSlowMode = false;
                _slowButton.Text = "Slow";
                _displayManager.UiDisplaySetSlowButtonText(_slowLevel);
            }
            _displayManager.UiDisplaySetDelaySliderEnabled(!_isStarted);
            _displayManager.UiDisplaySetRecordToggleEnabled(!_isStarted);
            _buffer.Clear();
            UpdatePlayPauseButton();
        }

        private async void PauseButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isPaused = !_isPaused;
                if (_isPaused)
                {
                    _mode = OperationMode.Stop;
                    _displayManager.UiDisplaySetStatusMessage("재생을 멈춥니다.");
                }
                else
                {
                    _mode = OperationMode.Replay;
                    _displayManager.UiDisplaySetStatusMessage("다시보기를 시작합니다.");
                }
                UpdatePlayPauseButton();

                // 녹화 중이면 녹화 중지
                if (_isRecording)
                {
                    recorder.StopRecording();
                    _isRecording = false;
                    _recordButton.Checked = false;
                    _displayManager.UiDisplaySetRecordingState(false);
                }
            }
        }

        private async void BackwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted && _mode != OperationMode.Idle)
            {
                _isPaused = true;
                _buffer.PlayPosition = Math.Max(0, _buffer.PlayPosition - 30);
                _mode = OperationMode.Stop;
                UpdatePlayPauseButton();

                // 녹화 중이면 녹화 중지
                if (_isRecording)
                {
                    recorder.StopRecording();
                    _isRecording = false;
                    _recordButton.Checked = false;
                    _displayManager.UiDisplaySetRecordingState(false);
                }
            }
        }

        private async void ForwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted && _mode != OperationMode.Idle)
            {
                _isPaused = true;
                _buffer.PlayPosition = Math.Min(_buffer.Count - 1, _buffer.PlayPosition + 30);
                _mode = OperationMode.Stop;
                UpdatePlayPauseButton();

                // 녹화 중이면 녹화 중지
                if (_isRecording)
                {
                    recorder.StopRecording();
                    _isRecording = false;
                    _recordButton.Checked = false;
                    _displayManager.UiDisplaySetRecordingState(false);
                }
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

                _displayManager.UiDisplaySetSlowButtonText(_slowLevel);
            }
        }

        private void RecordButton_Checked(Object? sender, RecordingSettingsEventArgs e)
        {
            try
            {
                if (recorder == null)
                {
                    recorder = new ScreenRecordingLib.ScreenRecorder();
                }

                if (!_isRecording)
                {
                    // 설정 전달
                    var settings = new ScreenRecordingLib.RecordingSettings
                    {
                        UseDesktop = e.UseDesktop,
                        UseVerticalLayout = e.UseVerticalLayout,
                        SelectedDrive = e.DrivePath,
                    };

                    IntPtr[] windowHandles = _displayManager.GetWindowHandles();
                    recorder.Initialize(settings, windowHandles);
                    _isRecording = true;
                    _recordButton.Checked = true;
                    _displayManager.UiDisplaySetRecordingState(true);
                }
                else
                {
                    _isRecording = false; 
                    _recordButton.Checked = false;
                    _displayManager.UiDisplaySetRecordingState(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"녹화 상태 변경 오류: {ex.Message}");

                // 오류 발생 시 녹화 중지로 초기화
                _isRecording = false;
                _recordButton.Checked = false;
                _displayManager.UiDisplaySetRecordingState(false);
            }
        }

        private void UpdateRecordingStatus()
        {
            //if (_recordingManager == null) return;

            //// 녹화 중이면 버튼 깜빡임 효과
            //if (_recordingManager.IsRecording && _isStarted)
            //{
            //    _recordButton.OnColor = _recordButton.OnColor == System.Drawing.Color.Red
            //        ? System.Drawing.Color.DarkRed
            //        : System.Drawing.Color.Red;
            //}
            //else
            //{
            //    _recordButton.OnColor = System.Drawing.Color.Red;
            //}
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
        }

        private void UpdatePlayPauseButton()
        {
            _pauseButton.IconChar = _isPaused ? IconChar.Play : IconChar.Pause;
            _displayManager.UiDisplaySetPlayPauseIcon(_isPaused);
        }

        private async void CloseButton_Click(object? sender, EventArgs e)
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

            _mainTimer.Stop();
            _mainTimer.Dispose();
            //_mainTask.Wait();

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
                    _displayManager.CloseDisplay();
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

            CleanupHealthCheck();
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
