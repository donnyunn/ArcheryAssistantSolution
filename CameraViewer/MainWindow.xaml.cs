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
        private List<Shape> _shapes = new List<Shape>();
        private Shape _currentShape = null;

        // 버튼 이벤트
        public event EventHandler SwapButtonEvent;

        // 드로잉 모드
        public enum DrawingMode
        {
            FreeLine,
            StraightLine,
            Circle
        }
        private DrawingMode _currentDrawingMode = DrawingMode.FreeLine;

        // 선 스타일 설정
        private SolidColorBrush _currentColor = Brushes.Yellow;
        private readonly double _lineThickness = 3.0;
        private readonly double _lineOpacity = 0.8;

        // 카메라 모드
        private volatile bool _mirrorMode = true;

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
        public void UpdateFrame(Mat frame, string statusText = "", string statusText2 = "", string slowStatusText="")
        {
            if (frame == null || frame.IsDisposed) return;

            _frameCount++;
            TimeSpan elapsed = DateTime.Now - _lastFpsCheck;
            if (elapsed.TotalSeconds >= 1.0)
            {
#if (DEBUG)
                Console.WriteLine($"Camera FPS: {_frameCount}");
#endif
                //FPSText.Text = $" {_frameCount,2}fps ";
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

                // 캔버스 크기 조정
                SizeChanged += MainWindow_SizeChanged;
                UpdateCanvasSize();
            }

            if (_mirrorMode)
            {
                frame = frame.Flip(FlipMode.Y);
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
                SlowStatusText.Text = slowStatusText;
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
            if (!_isDrawing || _currentShape == null) return;

            System.Windows.Point currentPoint = e.GetTouchPoint(DrawingCanvas).Position;
            UpdateShape(currentPoint);
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
            if (_isDrawing && e.LeftButton == MouseButtonState.Pressed && _currentShape != null)
            {
                System.Windows.Point currentPoint = e.GetPosition(DrawingCanvas);
                UpdateShape(currentPoint);
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

        #region 드로잉 기능

        // 그리기 시작
        private void StartDrawing(System.Windows.Point startPoint)
        {
            _isDrawing = true;

            switch (_currentDrawingMode)
            {
                case DrawingMode.FreeLine:
                    // 자유 직선 그리기 시작
                    Line line = new Line
                    {
                        X1 = startPoint.X,
                        Y1 = startPoint.Y,
                        X2 = startPoint.X,
                        Y2 = startPoint.Y,
                        Stroke = _currentColor,
                        StrokeThickness = _lineThickness,
                        Opacity = _lineOpacity,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeStartLineCap = PenLineCap.Round
                    };
                    _currentShape = line;
                    break;

                case DrawingMode.StraightLine:
                    // 수직/수평 직선 그리기 시작
                    line = new Line
                    {
                        X1 = startPoint.X,
                        Y1 = startPoint.Y,
                        X2 = startPoint.X,
                        Y2 = startPoint.Y,
                        Stroke = _currentColor,
                        StrokeThickness = _lineThickness,
                        Opacity = _lineOpacity,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeStartLineCap = PenLineCap.Round
                    };
                    _currentShape = line;
                    break;

                case DrawingMode.Circle:
                    // 원 그리기 시작
                    Ellipse ellipse = new Ellipse
                    {
                        Stroke = _currentColor,
                        StrokeThickness = _lineThickness,
                        Opacity = _lineOpacity,
                        Width = 0,
                        Height = 0
                    };
                    Canvas.SetLeft(ellipse, startPoint.X);
                    Canvas.SetTop(ellipse, startPoint.Y);
                    _currentShape = ellipse;
                    break;
            }

            // 캔버스에 추가
            DrawingCanvas.Children.Add(_currentShape);
        }

        // 도형 업데이트 (이동 중)
        private void UpdateShape(System.Windows.Point currentPoint)
        {
            if (_currentShape == null) return;

            switch (_currentDrawingMode)
            {
                case DrawingMode.FreeLine:
                    // 자유 직선 업데이트
                    if (_currentShape is Line line)
                    {
                        line.X2 = currentPoint.X;
                        line.Y2 = currentPoint.Y;
                    }
                    break;

                case DrawingMode.StraightLine:
                    // 수직/수평 직선 업데이트
                    if (_currentShape is Line straightLine)
                    {
                        // 각도 계산
                        double deltaX = currentPoint.X - _startPoint.X;
                        double deltaY = currentPoint.Y - _startPoint.Y;
                        double angle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;

                        // 각도에 따라 수평/수직 결정
                        if ((angle >= -45 && angle <= 45) || (angle >= 135 || angle <= -135))
                        {
                            // 수평선
                            straightLine.X1 = _startPoint.X;
                            straightLine.Y1 = currentPoint.Y;
                            straightLine.X2 = currentPoint.X;
                            straightLine.Y2 = currentPoint.Y;
                        }
                        else
                        {
                            // 수직선
                            straightLine.X1 = currentPoint.X;
                            straightLine.Y1 = _startPoint.Y;
                            straightLine.X2 = currentPoint.X;
                            straightLine.Y2 = currentPoint.Y;
                        }
                    }
                    break;

                case DrawingMode.Circle:
                    // 원 업데이트
                    if (_currentShape is Ellipse ellipse)
                    {
                        // 반지름 계산
                        double radius = Math.Sqrt(
                            Math.Pow(currentPoint.X - _startPoint.X, 2) +
                            Math.Pow(currentPoint.Y - _startPoint.Y, 2));

                        // 원 크기 및 위치 업데이트
                        ellipse.Width = radius * 2;
                        ellipse.Height = radius * 2;
                        Canvas.SetLeft(ellipse, _startPoint.X - radius);
                        Canvas.SetTop(ellipse, _startPoint.Y - radius);
                    }
                    break;
            }
        }

        // 그리기 완료
        private void FinishDrawing(System.Windows.Point endPoint)
        {
            if (_currentShape == null) return;

            // 도형 타입에 따라 최종 처리
            switch (_currentDrawingMode)
            {
                case DrawingMode.FreeLine:
                case DrawingMode.StraightLine:
                    if (_currentShape is Line finishedLine)
                    {
                        // 최소 길이 확인 (우발적인 터치 방지)
                        double length = Math.Sqrt(
                            Math.Pow(finishedLine.X2 - finishedLine.X1, 2) +
                            Math.Pow(finishedLine.Y2 - finishedLine.Y1, 2));

                        if (length < 5)
                        {
                            // 너무 짧은 선은 제거
                            DrawingCanvas.Children.Remove(_currentShape);
                        }
                        else
                        {
                            // 도형 목록에 추가
                            _shapes.Add(_currentShape);
                        }
                    }
                    break;

                case DrawingMode.Circle:
                    if (_currentShape is Ellipse finishedEllipse)
                    {
                        // 최소 크기 확인
                        if (finishedEllipse.Width < 10)
                        {
                            // 너무 작은 원은 제거
                            DrawingCanvas.Children.Remove(_currentShape);
                        }
                        else
                        {
                            // 도형 목록에 추가
                            _shapes.Add(_currentShape);
                        }
                    }
                    break;
            }

            _currentShape = null;
            _isDrawing = false;
        }

        #endregion

        #region UI 이벤트 핸들러

        // 드로잉 모드 변경 이벤트
        private void DrawingModeButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender == FreeLineButton)
            {
                _currentDrawingMode = DrawingMode.FreeLine;
                FreeLineButton.Background = Brushes.Gray;
                StraightLineButton.Background = Brushes.LightGray;
                CircleButton.Background = Brushes.LightGray;
            }
            else if (sender == StraightLineButton)
            {
                _currentDrawingMode = DrawingMode.StraightLine;
                FreeLineButton.Background = Brushes.LightGray;
                StraightLineButton.Background = Brushes.Gray;
                CircleButton.Background = Brushes.LightGray;
            }
            else if (sender == CircleButton)
            {
                _currentDrawingMode = DrawingMode.Circle;
                FreeLineButton.Background = Brushes.LightGray;
                StraightLineButton.Background = Brushes.LightGray;
                CircleButton.Background = Brushes.Gray;
            }
        }

        // 색상 변경 이벤트
        private void ColorButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender == YellowColorButton)
                _currentColor = Brushes.Yellow;
            else if (sender == RedColorButton)
                _currentColor = Brushes.Red;
            else if (sender == BlueColorButton)
                _currentColor = Brushes.Blue;
            else if (sender == GreenColorButton)
                _currentColor = Brushes.Green;
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
            _shapes.Clear();
            _currentShape = null;
            _isDrawing = false;
        }

        private void ViewModeButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender == NormalViewButton)
            {
                _mirrorMode = false;
            }
            else if (sender == MirrorViewButton)
            {
                _mirrorMode = true;
            }
        }

        private void SwapButton_Click(object sender, RoutedEventArgs e)
        {
            SwapButtonEvent?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        // 창이 닫힐 때 정리
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
        }
    }
}