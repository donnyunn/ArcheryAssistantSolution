using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace MultiWebcamApp
{
    public partial class WebcamForm : Form
    {
        private readonly int _cameraIndex;
        private VideoCapture? _capture;
        private PictureBox _pictureBox;
        private Label _statusLabel;
        private Label _statusLabel2;
        private CancellationTokenSource? _cancellationTokenSource;

        private const int MaxBufferSize = 30 * 200 + 1;
        private Queue<Mat> _frameBuffer = new Queue<Mat>(MaxBufferSize); // ��������� ������ ����
        private int _playPoint; // ���� ������ �ε���

        private int _delayPoint = 0; // ���� ������ ��

        // ���ο� ��� ���� ����
        private int _slowLevel = 1;

        // ��� ���� ����
        private enum _mode { Idle, Play, Replay, Stop }
        private _mode _state = _mode.Idle;
        private string _key = "";

        public WebcamForm(int cameraIndex)
        {
            InitializeComponent();

            _cameraIndex = cameraIndex;

            // PictureBox �ʱ�ȭ
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            Controls.Add(_pictureBox);

            // ���� ǥ�ÿ� label �ʱ�ȭ
            _statusLabel = new Label()
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Font = new Font("Arial", 24, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                //Location = new System.Drawing.Point(10, 10),
                Dock = DockStyle.Bottom,
                Height = 36,
                Visible = true
            };
            _pictureBox.Controls.Add( _statusLabel );

            _statusLabel2 = new Label()
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = Color.Red,
                Font = new Font("Arial", 24, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                //Location = new System.Drawing.Point(10, 10),
                Dock = DockStyle.Top,
                Height = 36,
                Visible = true
            };
            _pictureBox.Controls.Add(_statusLabel2);

            Load += WebcamForm_Load;
            FormClosing += WebcamForm_FormClosing;
        }

        private async void WebcamForm_Load(object? sender, EventArgs e)
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // ��ķ ĸó ����
            await Task.Run(() => StartCamera(_cancellationTokenSource.Token));
        }

        private async void StartCamera(CancellationToken token)
        {
            int countdown = 0;
            int slowcnt = 0;
            _capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                Console.WriteLine($"ī�޶� {_cameraIndex}�� �� �� �����ϴ�.");
                return;
            }

            _capture.Set(VideoCaptureProperties.FrameWidth, 1920);
            _capture.Set(VideoCaptureProperties.FrameHeight, 1080);
            _capture.Set(VideoCaptureProperties.Fps, 30.0f);

            var actualFps = _capture.Get(VideoCaptureProperties.Fps);

            for (int i = 0; i < 5; i++) // 5 ������ ������
            {
                _capture.Read(new Mat());
            }

            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();
            using var mat = new Mat();
            while (!token.IsCancellationRequested)
            {
                if (_capture.Read(mat) && !mat.Empty())
                {
                    if (_state == _mode.Play)
                    {
                        // ���� ���: �������� ���ۿ� ����
                        if (_frameBuffer.Count > MaxBufferSize)
                        {
                            var oldFrame = _frameBuffer.Dequeue();
                            oldFrame.Dispose();
                        }
                        _frameBuffer.Enqueue(mat.Flip(FlipMode.Y).Clone());

                        if (_delayPoint >= _frameBuffer.Count)
                        {
                            _playPoint = 0;
                            countdown = (int)((_delayPoint - _frameBuffer.Count - 1) / 30.0) + 1;
                        }
                        else
                        {
                            _playPoint = _frameBuffer.Count - _delayPoint - 1;
                            countdown = 0;
                        }

                        // ���� ���: ��� ���� ������ ǥ��
                        string msg = statusMessage(_playPoint / 30.0);
                        string msg2 = countdown != 0 ? countdown.ToString() : "��";
                        DisplayFrame(_frameBuffer.ToArray()[_playPoint], msg, msg2);
                    }
                    else if (_state == _mode.Replay)
                    {
                        if (slowcnt % _slowLevel == 0)
                        {
                            if (_playPoint < _frameBuffer.Count - 1)
                            {
                                _playPoint++;
                            }
                            else
                            {
                                _state = _mode.Stop;
                            }
                            string msg = statusMessage(_playPoint / 30.0, _slowLevel);
                            DisplayFrame(_frameBuffer.ToArray()[_playPoint], msg);
                        }
                        slowcnt++;
                    }
                    else if (_state == _mode.Stop)
                    {
                        string msg = statusMessage(_playPoint / 30.0, _slowLevel);
                        DisplayFrame(_frameBuffer.ToArray()[_playPoint], msg);
                    }
                    else if (_state == _mode.Idle)
                    {
                        // �ǽð� ���: ������ �ٷ� ǥ��
                        string msg = statusMessage(_playPoint / 30.0);
                        DisplayFrame(mat.Flip(FlipMode.Y), msg);
                    }
                }

                if (!string.IsNullOrEmpty(_key))
                {
                    HandleKeyInput();
                    _key = "";
                }

                // ��� �ð� ��� �� ��� �ð� ����
                var elapsed = stopwatch.ElapsedMilliseconds;
                stopwatch.Restart();
                var delay = Math.Max(0, (int)(1000 / actualFps) - (int)elapsed);

                await Task.Delay(delay); // ���� ���
            }
        }

        private void DisplayFrame(Mat mat, string msg = "", string msg2 = "")
        {
            var bitmap = BitmapConverter.ToBitmap(mat);

            try
            {
                _pictureBox.Invoke(new Action(() =>
                {
                        _pictureBox.Image?.Dispose();
                        _pictureBox.Image = bitmap;
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            UpdateStatusLabel(msg, msg2);
        }

        private void HandleKeyInput()
        {
            switch (_key)
            {
                case "r":
                    _state = _state == _mode.Idle ? _mode.Play : _mode.Idle;
                    ClearBuffer();

                    _playPoint = 0;
                    _slowLevel = 1;
                    break;
                case "p":
                    _state = _state == _mode.Play || _state == _mode.Replay ? _mode.Stop : _mode.Replay;
                    break;
                case "d":
                    if (_state != _mode.Idle)
                    {
                        _state = _mode.Stop;
                        _playPoint = Math.Clamp(_playPoint + 15, 0, _frameBuffer.Count - 1);
                    }
                    break;
                case "a":
                    if (_state != _mode.Idle)
                    {
                        _state = _mode.Stop;
                        _playPoint = Math.Clamp(_playPoint - 15, 0, _frameBuffer.Count - 1);
                    }
                    break;
                case "s":
                    if (_state == _mode.Stop || _state == _mode.Replay)
                    {
                        _slowLevel *= 2;
                        if (_slowLevel > 8)
                        {
                            _slowLevel = 1;
                        }
                    }
                    break;
            }
        }

        private string statusMessage(double seconds, int slowLevel = 1)
        {
            string message = "";

            //message += string.Format("{0,5:F1}s", seconds);
            message += $"{seconds, 5:F1}s\t ";
            message += $"x{1.0/slowLevel,1:F2}";
            return message;
        }

        private void UpdateStatusLabel(string str, string str2)
        {
            try
            {
                Invoke(new Action(() =>
                {
                    switch (_state)
                    {
                        case _mode.Idle:
                            _statusLabel.Text = str;
                            _statusLabel2.Text = str2;
                            break;
                        case _mode.Play:
                            _statusLabel.Text = str;
                            _statusLabel2.Text = str2;
                            break;
                        case _mode.Replay:
                            _statusLabel.Text = str;
                            _statusLabel2.Text = str2;
                            break;
                        case _mode.Stop:
                            _statusLabel.Text = str;
                            _statusLabel2.Text = str2;
                            break;
                    }
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private void ClearBuffer()
        {
            // ���� ����
            while (_frameBuffer.Count > 0)
            {
                var frame = _frameBuffer.Dequeue();
                frame.Dispose();
            }
            _frameBuffer.Clear();
        }

        private void WebcamForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            Task.Delay(100);
            _capture?.Release();
            _capture?.Dispose();

            ClearBuffer();
        }

        public void SetKey(string key)
        {
            _key = key;
        }

        public void SetDelay(int delaySeconds)
        {
            _delayPoint = delaySeconds * 30; // 30 FPS �������� ���� ������ �� ���

            ClearBuffer();
            _state = _mode.Idle;

            _playPoint = 0;
            _slowLevel = 1;
        }
    }
}
