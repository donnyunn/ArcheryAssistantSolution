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

        private string _currentStatusText = "";
        private string _currentStatusText2 = "";
        private System.Threading.Timer _statusUpdateTimer;

        private int _frameCount = 0;
        private int _droppedFrames = 0;
        private int _timerCounter = 0;
        private DateTime _lastRenderTime = DateTime.MinValue;

        // 프레임 큐와 렌더링 플래그
        private readonly ConcurrentQueue<Mat> _frameQueue = new ConcurrentQueue<Mat>();
        private bool _isRendering = false;

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

            if (++_timerCounter >= 10) // 1초마다 FPS 출력
            {
                Console.WriteLine($"Camera FPS: {_frameCount} | Dropped: {_droppedFrames}");
                _frameCount = 0;
                _droppedFrames = 0;
                _timerCounter = 0;
            }
        }

        // 외부에서 호출되는 프레임 업데이트 메서드
        public void UpdateFrame(Mat frame, string statusText = "", string statusText2 = "")
        {
            if (frame == null || frame.IsDisposed) return;

            Interlocked.Increment(ref _frameCount);

            // 상태 텍스트 업데이트
            _currentStatusText = statusText;
            _currentStatusText2 = statusText2;

            // 프레임 큐에 추가
            _frameQueue.Enqueue(frame.Clone()); // Clone으로 원본 프레임 보존

            // 렌더링 루프가 실행 중이 아니면 시작
            if (!_isRendering)
            {
                _isRendering = true;
                Task.Run(RenderLoop);
            }
        }

        // 프레임 큐를 처리하는 비동기 렌더링 루프
        private async Task RenderLoop()
        {
            while (_frameQueue.TryDequeue(out Mat frame))
            {
                await ProcessFrameAsync(frame);
                _lastRenderTime = DateTime.Now;
            }
            _isRendering = false; // 큐가 비면 렌더링 종료
        }

        // 최적화된 프레임 처리 메서드
        private async Task ProcessFrameAsync(Mat frame)
        {
            try
            {
                // UI 스레드에서 비동기적으로 화면 업데이트
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // OpenCVSharp.WpfExtensions를 사용해 Mat을 WriteableBitmap으로 변환
                        _writeableBitmap = frame.ToWriteableBitmap();
                        DisplayImage.Source = _writeableBitmap;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UI 업데이트 오류: {ex.Message}");
                    }
                }, DispatcherPriority.Render); // Render 우선순위로 빠른 UI 업데이트
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 오류: {ex.Message}");
            }
            finally
            {
                frame.Dispose(); // 리소스 정리
            }
        }

        // 창이 닫힐 때 타이머 정리
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _statusUpdateTimer?.Dispose();
            while (_frameQueue.TryDequeue(out Mat frame))
            {
                frame.Dispose(); // 남은 프레임 정리
            }
            base.OnClosing(e);
        }
    }
}