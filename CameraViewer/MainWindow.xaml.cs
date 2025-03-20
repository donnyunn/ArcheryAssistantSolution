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
using System.Runtime.InteropServices;

namespace CameraViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        private WriteableBitmap _writeableBitmap;

        private string _currentStatusText = "";
        private string _currentStatusText2 = "";
        private System.Threading.Timer _statusUpdateTimer;

        private int _frameCount = 0;
        private DateTime _lastFpsCheck = DateTime.Now;
        private int _lastWidth = 0;
        private int _lastHeight = 0;
        private bool _isUpdatingFrame = false;
        private readonly object _lockObject = new object();
        private int _droppedFrames = 0;
        private int _timerCounter = 0;

        public MainWindow()
        {
            InitializeComponent();

            // 100ms 간격으로 상태 텍스트 업데이트
            _statusUpdateTimer = new System.Threading.Timer(
                StatusUpdateTimer_Tick,
                null,
                0,
                100);
        }

        private void StatusUpdateTimer_Tick(object? state)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = _currentStatusText;
                StatusText2.Text = _currentStatusText2;
            }));

            if (++_timerCounter >= 10)
            {
                Console.WriteLine($"Camera FPS: {_frameCount} | Dropped: {_droppedFrames}");
                _frameCount = 0;
                _droppedFrames = 0;
                _timerCounter = 0;
            }
        }

        public void UpdateFrame(Mat frame, string statusText = "", string statusText2 = "")
        {
            if (frame == null) return;

            Interlocked.Increment(ref _frameCount);

            // 상태 텍스트 업데이트
            _currentStatusText = statusText;
            _currentStatusText2 = statusText2;

            // 이미 프레임 업데이트 중이면 이 프레임은 건너뜀 (프레임 스킵)
            if (_isUpdatingFrame)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }

            // 백그라운드에서 프레임 처리
            Task.Run(() => ProcessFrameAsync(frame.Clone()));
        }

        private async Task ProcessFrameAsync(Mat frame)
        {
            try
            {
                _isUpdatingFrame = true;

                // 프레임 데이터 준비 (백그라운드 스레드에서)
                int width = frame.Width;
                int height = frame.Height;
                int step = (int)frame.Step();
                IntPtr dataPtr = frame.Data;

                // 백그라운드에서 데이터 복사 준비
                byte[] frameData = null;

                // 프레임 크기가 크다면 데이터 복사
                if (width * height > 1000 * 1000) // 큰 이미지인 경우
                {
                    frameData = new byte[step * height];
                    Marshal.Copy(dataPtr, frameData, 0, frameData.Length);
                }

                // UI 스레드에서 비동기적으로 화면 업데이트
                //await Dispatcher.BeginInvoke(new Action(() =>
                //{
                //    try
                //    {
                //        // WriteableBitmap 초기화 또는 크기 변경 감지
                //        if (_writeableBitmap == null || _lastWidth != width || _lastHeight != height)
                //        {
                //            _writeableBitmap = new WriteableBitmap(
                //                width, height,
                //                96, 96,
                //                PixelFormats.Bgr24, null);
                //            DisplayImage.Source = _writeableBitmap;
                //            _lastWidth = width;
                //            _lastHeight = height;
                //        }

                //        _writeableBitmap.Lock();

                //        unsafe
                //        {
                //            var backBuffer = (byte*)_writeableBitmap.BackBuffer;
                //            var stride = _writeableBitmap.BackBufferStride;

                //            if (frameData != null)
                //            {
                //                // 미리 복사한 데이터 사용
                //                for (int y = 0; y < height; y++)
                //                {
                //                    IntPtr dstRow = new IntPtr(backBuffer + (y * stride));
                //                    Marshal.Copy(frameData, y * step, dstRow, Math.Min(step, stride));
                //                }
                //            }
                //            else
                //            {
                //                // 직접 복사 (작은 이미지)
                //                for (int y = 0; y < height; y++)
                //                {
                //                    IntPtr srcRow = new IntPtr(dataPtr.ToInt64() + (y * step));
                //                    IntPtr dstRow = new IntPtr(backBuffer + (y * stride));

                //                    Buffer.MemoryCopy(
                //                        srcRow.ToPointer(),
                //                        dstRow.ToPointer(),
                //                        stride,
                //                        Math.Min(step, stride));
                //                }
                //            }
                //        }

                //        _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                //        _writeableBitmap.Unlock();
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine($"UI 업데이트 오류: {ex.Message}");
                //    }
                //}));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 오류: {ex.Message}");
            }
            finally
            {
                // 리소스 정리
                frame.Dispose();
                _isUpdatingFrame = false;
            }
        }

        // 창이 닫힐 때 타이머 정리
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            //_statusUpdateTimer.Stop();
            base.OnClosing(e);
        }
    }
}