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
        private CameraViewer.MainWindow _viewer = new CameraViewer.MainWindow();

        private readonly Mat _frameMat;

        public WebcamForm(int cameraIndex)
        {
            InitializeComponent();

            _cameraIndex = cameraIndex;
            _frameMat = new Mat();

            Load += WebcamForm_Load;
            FormClosing += WebcamForm_FormClosing;
        }

        private void WebcamForm_Load(object? sender, EventArgs e)
        {
            Screen currentScreen = Screen.FromControl(this);
            Rectangle screenBounds = currentScreen.Bounds;
            _viewer.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            _viewer.WindowStyle = System.Windows.WindowStyle.None;
            _viewer.ResizeMode = System.Windows.ResizeMode.NoResize;

            _viewer.Left = screenBounds.Left;
            _viewer.Top = screenBounds.Top;
            _viewer.Width = screenBounds.Width;
            _viewer.Height = screenBounds.Height;
            
            _viewer.Show();

            // 웹캠 캡처 시작
            Task.Run(() => StartCamera());
        }

        private void StartCamera()
        {
            _capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                Console.WriteLine($"카메라 {_cameraIndex}를 열 수 없습니다.");
                return;
            }

            _capture.Set(VideoCaptureProperties.FrameWidth, 1920);
            _capture.Set(VideoCaptureProperties.FrameHeight, 1080);
            _capture.Set(VideoCaptureProperties.Fps, 60.0f);

            var actualFps = _capture.Get(VideoCaptureProperties.Fps);
            Console.WriteLine($"{actualFps}");
            //for (int i = 0; i < 5; i++) // 5 프레임 버리기
            //{
            //    _capture.Read(new Mat());
            //}
        }

        public void work()
        {
            try
            {
                if (_capture == null || !_capture.IsOpened())
                    return;

                if (_capture.Read(_frameMat) && !_frameMat.Empty())
                {
                    var flippedFrame = _frameMat.Flip(FlipMode.Y);
                    _viewer.UpdateFrame(flippedFrame);
                    flippedFrame.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Frame capture error: {ex.Message}");
            }
        }

        private void WebcamForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _viewer.Close();
            _capture?.Release();
            _capture?.Dispose();
        }
    }
}
