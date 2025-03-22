using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Threading;
using System.Windows.Threading;

namespace CameraViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        private WriteableBitmap _writeableBitmap;

        private int _frameCount = 0;
        private DateTime _lastFpsCheck = DateTime.Now;

        public MainWindow()
        {
            InitializeComponent();
        }

        // 외부에서 호출되는 프레임 업데이트 메서드
        public void UpdateFrame(Mat frame, string statusText = "", string statusText2 = "")
        {
            if (frame == null || frame.IsDisposed) return; 
            
            _frameCount++;
            TimeSpan elapsed = DateTime.Now - _lastFpsCheck;
            if (elapsed.TotalSeconds >= 1.0)
            {
                Console.WriteLine($"Camera FPS: {_frameCount}");
                _frameCount = 0;
                _lastFpsCheck = DateTime.Now;
            }

            // WriteableBitmap 초기화 (최초 1회)
            if (_writeableBitmap == null || _writeableBitmap.PixelWidth != frame.Width || _writeableBitmap.PixelHeight != frame.Height)
            {
                _writeableBitmap = new WriteableBitmap(
                    frame.Width, frame.Height,
                    96, 96,
                    System.Windows.Media.PixelFormats.Bgr24, null);
                DisplayImage.Source = _writeableBitmap;
            }

            // 프레임 데이터 직접 업데이트
            try
            {
                _writeableBitmap.Lock();
                unsafe
                {
                    byte* dst = (byte*)_writeableBitmap.BackBuffer;
                    byte* src = (byte*)frame.Data;
                    int stride = _writeableBitmap.BackBufferStride;
                    int srcStride = (int)frame.Step();

                    int bytesPerRow = frame.Width * 3; // BGR24는 3바이트/픽셀
                    for (int y = 0; y < frame.Height; y++)
                    {
                        Buffer.MemoryCopy(src + y * srcStride, dst + y * stride, bytesPerRow, bytesPerRow);
                    }
                }
                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
                _writeableBitmap.Unlock();

                // 상태 텍스트 즉시 업데이트
                StatusText.Text = statusText;
                StatusText2.Text = statusText2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 렌더링 오류: {ex.Message}");
            }
        }

        // 창이 닫힐 때 타이머 정리
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
        }
    }
}