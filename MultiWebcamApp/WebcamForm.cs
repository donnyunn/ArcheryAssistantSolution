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
        private CancellationTokenSource? _cancellationTokenSource;
        private ConcurrentQueue<Mat> _frameBuffer;
        private bool _isDelayed = false;
        private int _delayFrames = 0;

        public WebcamForm(int cameraIndex)
        {
            InitializeComponent();

            _cameraIndex = cameraIndex;
            _frameBuffer = new ConcurrentQueue<Mat>();

            // PictureBox 초기화
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            Controls.Add(_pictureBox);

            Load += WebcamForm_Load;
            FormClosing += WebcamForm_FormClosing;
        }

        private async void WebcamForm_Load(object? sender, EventArgs e)
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // 웹캠 캡처 시작
            await Task.Run(() => StartCamera(_cancellationTokenSource.Token));
        }

        private async void StartCamera(CancellationToken token)
        {
            _capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                MessageBox.Show($"카메라 {_cameraIndex}를 열 수 없습니다.");
                return;
            }

            _capture.Set(VideoCaptureProperties.FrameWidth, 1920);
            _capture.Set(VideoCaptureProperties.FrameHeight, 1080);
            _capture.Set(VideoCaptureProperties.Fps, 30.0f);

            var actualFps = _capture.Get(VideoCaptureProperties.Fps);

            for (int i = 0; i < 5; i++) // 5 프레임 버리기
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
                    if (_isDelayed)
                    {
                        // 지연 모드: 프레임을 버퍼에 저장
                        if (_frameBuffer.Count >= _delayFrames)
                        {
                            if (_frameBuffer.TryDequeue(out var delayedFrame))
                            {
                                DisplayFrame(delayedFrame);
                                delayedFrame.Dispose();
                            }
                        }
                        _frameBuffer.Enqueue(mat.Clone());
                    }
                    else
                    {
                        // 실시간 모드: 프레임 바로 표시
                        DisplayFrame(mat);
                    }
                }

                // 경과 시간 계산 후 대기 시간 조정
                var elapsed = stopwatch.ElapsedMilliseconds;
                stopwatch.Restart();
                var delay = Math.Max(0, (int)(1000 / actualFps) - (int)elapsed);

                if (token.IsCancellationRequested)
                    break;

                await Task.Delay(delay); // 직접 대기
            }
        }

        private void DisplayFrame(Mat mat)
        {
            var bitmap = BitmapConverter.ToBitmap(mat);
            _pictureBox.Invoke(new Action(() =>
            {
                _pictureBox.Image?.Dispose();
                _pictureBox.Image = bitmap;
            }));
        }

        public void StartDelay(int delaySeconds)
        {
            _delayFrames = delaySeconds * 30; // 30 FPS 기준으로 지연 프레임 수 계산
            _isDelayed = true;
        }

        public void StopDelay()
        {
            _isDelayed = false;

            // 버퍼 비우기
            while (_frameBuffer.TryDequeue(out var frame))
            {
                frame.Dispose();
            }
        }

        private void WebcamForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _capture?.Release();
            _capture?.Dispose();
        }
    }
}
