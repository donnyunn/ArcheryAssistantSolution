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

namespace MultiWebcamApp
{
    public partial class MainForm : Form
    {
        private WebcamForm _webcamFormHead;// = new WebcamForm(0);
        private WebcamForm _webcamFormBody;// = new WebcamForm(1);
        private FootpadForm _footpadForm;// = new FootpadForm();
        private int _delaySeconds = 0; 
        private bool _isStarted = false;
        private bool _isPaused = false;
        private bool _isSlowMode = false;

        private System.Windows.Forms.Timer _backwardTimer;
        private System.Windows.Forms.Timer _forwardTimer;
        private System.Windows.Forms.Timer _backwardInitialTimer;
        private System.Windows.Forms.Timer _forwardInitialTimer;

        private CancellationTokenSource? _cancellationTokenSource;

        private readonly Stopwatch _mainStopwatch = new Stopwatch();

        public MainForm()
        {
            InitializeComponent();
            InitializeCustomControls();
            _mainStopwatch.Start();
        }

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
            _swapButton.Location = new Point(x, y);
            _swapButton.Size = new Size(width, height);
            _swapButton.OnColor = Color.Red;
            _swapButton.OnText = "녹화켜짐";
            _swapButton.OffText = "녹화꺼짐";
            _swapButton.TextFont = new Font("맑은 고딕", 24, FontStyle.Bold);
            _swapButton.CheckedChanged += new EventHandler(RecordButton_Checked);
            _swapButton.Enabled = false;

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

        private async void InitializeProcess()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            await Task.Run(() => Process(_cancellationTokenSource.Token));
        }

        private async Task Process(CancellationToken token)
        {
            const double targetFrameTime = 1000.0 / 60.0;
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            long frameCount = 0;
            long lastFpsCheck = 0;

            Task.Delay(1500).Wait();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    long frameStartTime = stopwatch.ElapsedMilliseconds;

                    if (frameCount % 2 == 0)
                    {
                        _ = Task.WhenAll(
                            Task.Run(() => _webcamFormHead.work(frameStartTime)),
                            Task.Run(() => _webcamFormBody.work(frameStartTime)),
                            Task.Run(() => _footpadForm.UpdateFrame())
                        );
                    } else
                    {
                        _ = Task.WhenAll(
                            Task.Run(() => _webcamFormHead.work(frameStartTime)),
                            Task.Run(() => _webcamFormBody.work(frameStartTime))
                        );
                    }

                    frameCount++;

                    if (frameStartTime - lastFpsCheck >= 1000)
                    {
                        Console.WriteLine($"Camera FPS: {frameCount}");
                        frameCount = 0;
                        lastFpsCheck = frameStartTime;
                    }

                    // 다음 프레임까지 남은 시간 계산
                    long elapsedTime = stopwatch.ElapsedMilliseconds - frameStartTime;
                    int delayTime = (int)Math.Max(0, targetFrameTime - elapsedTime);

                    if (delayTime > 0)
                    {
                        await Task.Delay(delayTime, token);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _webcamFormHead = new WebcamForm(0);
            _webcamFormBody = new WebcamForm(1);
            _footpadForm = new FootpadForm();

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
        }

        private void DelaySlider_ValueChanged(object sender, EventArgs e)
        {
            _delaySeconds = ((CustomTrackBar)sender).Value;
            _delayTextbox.Text = _delaySeconds.ToString();
            _webcamFormHead.SetDelay(_delaySeconds);
            _webcamFormBody.SetDelay(_delaySeconds);
        }

        private void StartButton_Click(object? sender, EventArgs e)
        {
            _mainStopwatch.Restart();
            _isStarted = !_isStarted;
            if (_isStarted)
            {
                _startButton.Text = "대기";
                _isPaused = false;
            }
            else
            {
                _startButton.Text = "시작";
                _isPaused = true;
            }
            UpdatePlayPauseButton();
            _webcamFormHead.SetKey("r");
            _webcamFormBody.SetKey("r");
        }

        private void BackwardButton_Click(Object? sender, EventArgs e)
        {
            if (_isStarted)
            {
                _isPaused = true;
                UpdatePlayPauseButton();
                _webcamFormHead.SetKey("a");
                _webcamFormBody.SetKey("a");
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
            }
        }

        private void RecordButton_Checked(Object? sender, EventArgs e)
        {
            Console.WriteLine($"{_swapButton.Checked}");
        }

        private void UpdatePlayPauseButton()
        {
            _pauseButton.IconChar = _isPaused ? IconChar.Play : IconChar.Pause;
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            Task.Delay(1000);
            _webcamFormHead.Close();
            _webcamFormBody.Close();
            this.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            _webcamFormHead.Close();
            _webcamFormBody.Close();
        }
    }
}
