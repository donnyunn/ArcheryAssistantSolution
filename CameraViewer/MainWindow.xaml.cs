using System;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;

namespace CameraViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        private WriteableBitmap _writeableBitmap;
        private int _frameCount = 0;
        private DateTime _lastFpsCheck = DateTime.Now;

        // 드로잉 관련 변수
        private System.Windows.Point _startPoint;
        private bool _isDrawing = false;
        private List<Line> _lines = new List<Line>();
        private Line _currentLine = null;

        // 선 스타일 설정
        private readonly SolidColorBrush _lineColor = Brushes.Yellow;
        private readonly double _lineThickness = 3.0;
        private readonly double _lineOpacity = 0.8;

        public MainWindow()
        {
            InitializeComponent();

            // 터치/마우스 이벤트 핸들러 등록
            DrawingCanvas.TouchDown += Canvas_TouchDown;
            DrawingCanvas.TouchMove += Canvas_TouchMove;
            DrawingCanvas.TouchUp += Canvas_TouchUp;

            // 마우스 이벤트도 지원 (테스트용)
            DrawingCanvas.MouseDown += Canvas_MouseDown;
            DrawingCanvas.MouseMove += Canvas_MouseMove;
            DrawingCanvas.MouseUp += Canvas_MouseUp;
        }

        // 외부에서 호출되는 프레임 업데이트 메서드
        public void UpdateFrame(Mat frame, string statusText = "", string statusText2 = "")
        {
            if (frame == null || frame.IsDisposed) return;
#if (DEBUG)
            _frameCount++;
            TimeSpan elapsed = DateTime.Now - _lastFpsCheck;
            if (elapsed.TotalSeconds >= 1.0)
            {
                Console.WriteLine($"Camera FPS: {_frameCount}");
                _frameCount = 0;
                _lastFpsCheck = DateTime.Now;
            }
#endif

            // WriteableBitmap 초기화 (최초 1회)
            if (_writeableBitmap == null || _writeableBitmap.PixelWidth != frame.Width || _writeableBitmap.PixelHeight != frame.Height)
            {
                _writeableBitmap = new WriteableBitmap(
                    frame.Width, frame.Height,
                    96, 96,
                    System.Windows.Media.PixelFormats.Bgr24, null);
                DisplayImage.Source = _writeableBitmap;

                // 캔버스 크기 조정
                SizeChanged += MainWindow_SizeChanged;
                UpdateCanvasSize();
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

        // 창 크기 변경 시 캔버스 크기 조정
        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCanvasSize();
        }

        // 캔버스 크기를 이미지 크기에 맞게 업데이트
        private void UpdateCanvasSize()
        {
            if (DisplayImage.ActualWidth <= 0 || DisplayImage.ActualHeight <= 0)
                return;

            // 이미지와 캔버스 크기 맞추기
            DrawingCanvas.Width = DisplayImage.ActualWidth;
            DrawingCanvas.Height = DisplayImage.ActualHeight;

            // 위치 조정
            double left = (ActualWidth - DrawingCanvas.Width) / 2;
            double top = (ActualHeight - DrawingCanvas.Height) / 2;

            Canvas.SetLeft(DrawingCanvas, left);
            Canvas.SetTop(DrawingCanvas, top);
        }

        #region 터치 이벤트 핸들러

        private void Canvas_TouchDown(object sender, TouchEventArgs e)
        {
            _startPoint = e.GetTouchPoint(DrawingCanvas).Position;
            StartDrawing(_startPoint);
            e.TouchDevice.Capture(DrawingCanvas);
            e.Handled = true;
        }

        private void Canvas_TouchMove(object sender, TouchEventArgs e)
        {
            if (!_isDrawing || _currentLine == null) return;

            System.Windows.Point currentPoint = e.GetTouchPoint(DrawingCanvas).Position;
            UpdateLine(currentPoint);
            e.Handled = true;
        }

        private void Canvas_TouchUp(object sender, TouchEventArgs e)
        {
            if (_isDrawing)
            {
                System.Windows.Point endPoint = e.GetTouchPoint(DrawingCanvas).Position;
                FinishDrawing(endPoint);
                e.TouchDevice.Capture(null);
            }
            e.Handled = true;
        }

        #endregion

        #region 마우스 이벤트 핸들러 (터치 대체용)

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _startPoint = e.GetPosition(DrawingCanvas);
                StartDrawing(_startPoint);
                DrawingCanvas.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDrawing && e.LeftButton == MouseButtonState.Pressed && _currentLine != null)
            {
                System.Windows.Point currentPoint = e.GetPosition(DrawingCanvas);
                UpdateLine(currentPoint);
                e.Handled = true;
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawing)
            {
                System.Windows.Point endPoint = e.GetPosition(DrawingCanvas);
                FinishDrawing(endPoint);
                DrawingCanvas.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        #endregion

        // 선 그리기 시작
        private void StartDrawing(System.Windows.Point startPoint)
        {
            _isDrawing = true;

            // 새 선 생성
            _currentLine = new Line
            {
                X1 = startPoint.X,
                Y1 = startPoint.Y,
                X2 = startPoint.X, // 처음에는 시작점과 끝점이 같음
                Y2 = startPoint.Y,
                Stroke = _lineColor,
                StrokeThickness = _lineThickness,
                Opacity = _lineOpacity,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round
            };

            // 캔버스에 추가
            DrawingCanvas.Children.Add(_currentLine);
        }

        // 선 업데이트 (이동 중)
        private void UpdateLine(System.Windows.Point currentPoint)
        {
            if (_currentLine != null)
            {
                _currentLine.X2 = currentPoint.X;
                _currentLine.Y2 = currentPoint.Y;
            }
        }

        // 선 그리기 완료
        private void FinishDrawing(System.Windows.Point endPoint)
        {
            if (_currentLine != null)
            {
                // 최종 선 위치 설정
                _currentLine.X2 = endPoint.X;
                _currentLine.Y2 = endPoint.Y;

                // 최소 길이 확인 (우발적인 터치 방지)
                double length = Math.Sqrt(
                    Math.Pow(_currentLine.X2 - _currentLine.X1, 2) +
                    Math.Pow(_currentLine.Y2 - _currentLine.Y1, 2));

                if (length < 5)
                {
                    // 너무 짧은 선은 제거
                    DrawingCanvas.Children.Remove(_currentLine);
                }
                else
                {
                    // 선 목록에 추가
                    _lines.Add(_currentLine);
                }

                _currentLine = null;
            }

            _isDrawing = false;
        }

        // 지우기 버튼 클릭 이벤트 핸들러
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearDrawing();
        }

        // 그림 지우기
        public void ClearDrawing()
        {
            DrawingCanvas.Children.Clear();
            _lines.Clear();
            _currentLine = null;
            _isDrawing = false;
        }

        // 창이 닫힐 때 정리
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
        }
    }
}