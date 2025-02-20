using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using SharpDX.Direct3D9;
using Surface = SharpDX.Direct3D9.Surface;
using System.Windows.Threading;

namespace CameraViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        private WriteableBitmap _writeableBitmap;

        private string _currentStatusText = "";
        private string _currentStatusText2 = "";
        private readonly DispatcherTimer _statusUpdateTimer;
        public MainWindow()
        {
            InitializeComponent();

            // 100ms 간격으로 상태 텍스트 업데이트
            _statusUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _statusUpdateTimer.Tick += StatusUpdateTimer_Tick;
            _statusUpdateTimer.Start();
        }

        private void StatusUpdateTimer_Tick(object sender, EventArgs e)
        {
            StatusText.Text = _currentStatusText;
            StatusText2.Text = _currentStatusText2;
        }

        public void UpdateFrame(Mat frame, string statusText = "", string statusText2 = "")
        {
            if (frame == null) return;

            // 상태 텍스트 값 저장 (실제 UI 업데이트는 타이머에서 수행)
            _currentStatusText = statusText;
            _currentStatusText2 = statusText2;

            // WriteableBitmap 초기화 (최초 1회)
            if (_writeableBitmap == null)
            {
                Dispatcher.Invoke(() =>
                {
                    _writeableBitmap = new WriteableBitmap(
                        frame.Width, frame.Height,
                        96, 96,
                        PixelFormats.Bgr24, null);
                    DisplayImage.Source = _writeableBitmap;
                });
            }

            // 프레임 데이터 업데이트
            Dispatcher.Invoke(() =>
            {
                _writeableBitmap.Lock();

                unsafe
                {
                    var backBuffer = (byte*)_writeableBitmap.BackBuffer;
                    var stride = _writeableBitmap.BackBufferStride;
                    var dataPointer = (byte*)frame.DataStart;
                    var dataStride = frame.Step();

                    for (int y = 0; y < frame.Height; y++)
                    {
                        Buffer.MemoryCopy(
                            dataPointer + (y * dataStride),
                            backBuffer + (y * stride),
                            stride,
                            dataStride);
                    }
                }

                _writeableBitmap.AddDirtyRect(
                    new Int32Rect(0, 0, frame.Width, frame.Height));
                _writeableBitmap.Unlock();
            });
        }

        // 창이 닫힐 때 타이머 정리
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _statusUpdateTimer.Stop();
            base.OnClosing(e);
        }
    }
}