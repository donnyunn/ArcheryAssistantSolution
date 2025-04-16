using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows.Shapes;
using System.Windows.Controls;

namespace PressureMapViewer
{
    // Color 확장 메서드 (밝기 조절용)
    public static class ColorExtensions
    {
        public static Color ChangeBrightness(this Color color, double factor)
        {
            return Color.FromArgb(
                color.A,
                (byte)Math.Clamp(color.R * factor, 0, 255),
                (byte)Math.Clamp(color.G * factor, 0, 255),
                (byte)Math.Clamp(color.B * factor, 0, 255));
        }
    }

    public partial class MainWindow : Window
    {
        private const int SENSOR_SIZE = 96;
        private const int FPS = 60;

        // 2D 렌더링 관련
        private WriteableBitmap heatmapBitmap;
        private Color[] heatmapPalette = new Color[1024];
        private int[] precomputedColors;

        // 무게중심
        private Point centerOfPressure;
        private Ellipse copIndicator;
        private Queue<Point> copHistory = new Queue<Point>(100);
        private Polyline copTrajectory;
        private const double TRAJECTORY_OPACITY = 0.6;
        private double copSensitivity = 1.0;

        // 밸런스 게이지
        private const double WEIGHT_THRESHOLD = 1.0;
        private double leftPercent = 50, rightPercent = 50;
        private double leftForefootPercent = 0, leftHeelPercent = 0;
        private double rightForefootPercent = 0, rightHeelPercent = 0;

        // 가이드라인
        private Line leftGuideLine;
        private Line rightGuideLine;
        // 보조원
        private Ellipse AuxiliaryCircle;
        private double ACX, ACY;
        private double ACScale;

        // 차트
        private const int MAX_CHART_POINTS = 200;
        private Queue<Point> forefootHeelData = new Queue<Point>(MAX_CHART_POINTS);
        private Queue<Point> leftPressureData = new Queue<Point>(MAX_CHART_POINTS);
        private Queue<Point> rightPressureData = new Queue<Point>(MAX_CHART_POINTS);
        private int _chartUpdateCounter = 0;
        private const int CHART_UPDATE_FREQUENCY = 4;
        private int _weightUpdateCounter = 0;
        private const int WEIGHT_UPDATE_FREQUENCY = 30;

        // 가로선 추가
        private Line _forefootHeelLastValueLine;
        private Line _leftPressureLastValueLine;
        private Line _rightPressureLastValueLine;
        private Line _forefootHeelcenterLine;
        private Line _leftfootHeelcenterLine;
        private Line _rightfootHeelcenterLine;

        // FPS 모니터링
        private int _frameCount = 0;
        private DateTime _lastFpsCheck = DateTime.Now;

        public event EventHandler ResetPortsRequested;

        public MainWindow()
        {
            InitializeComponent();
            InitializeRendering();
            InitializeHeatmapPalette(256);
            InitializeCOPIndicator();
            InitializeCharts();
            InitializeFootOutlineGuides();
        }
        
        public void UpdatePressureData(ushort[] data, string statusText = "")
        {
            if (data == null || data.Length != SENSOR_SIZE * SENSOR_SIZE)
                return;

            _frameCount++;
            TimeSpan elapsed = DateTime.Now - _lastFpsCheck;
            if (elapsed.TotalSeconds >= 1.0)
            {
#if (DEBUG)
                Console.WriteLine($"Footpad FPS: {_frameCount}");
#endif
                FPSText.Text = $" {_frameCount,2}Hz ";
                _frameCount = 0;
                _lastFpsCheck = DateTime.Now;
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] < 5) data[i] = 0;
            }

            Update2D(data);
            UpdateCenterOfPressure(data);
            UpdateBalanceGauges(data);
            UpdateFootOutlineGuides(data);

            _chartUpdateCounter++;
            if (_chartUpdateCounter >= CHART_UPDATE_FREQUENCY)
            {
                UpdateCharts();
                _chartUpdateCounter = 0;
            }
            _weightUpdateCounter++;
            if (_weightUpdateCounter >= WEIGHT_UPDATE_FREQUENCY)
            {
                EstimateWeight(data);
                _weightUpdateCounter = 0;
            }

            StatusText.Text = statusText;
        }

        private void InitializeRendering()
        {
            heatmapBitmap = new WriteableBitmap(
                SENSOR_SIZE, SENSOR_SIZE,
                96, 96,
                PixelFormats.Bgr32,
                null);
            HeatmapImage.Source = heatmapBitmap;
        }

        private void InitializeHeatmapPalette(int maxValue)
        {
            maxValue = Math.Clamp(maxValue, 1, heatmapPalette.Length);

            Color maxColor = GetHeatmapColor(1f);

            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                if (i < maxValue)
                {
                    float value = (float)i / (maxValue - 1);
                    heatmapPalette[i] = GetHeatmapColor(value);
                }
                else
                {
                    heatmapPalette[i] = maxColor;
                }
            }

            precomputedColors = new int[heatmapPalette.Length];
            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                Color color = heatmapPalette[i];
                precomputedColors[i] = (255 << 24) | (color.R << 16) | (color.G << 8) | color.B;
            }
        }

        private void InitializeHeatmapPalette()
        {
            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                float value = (float)i / (heatmapPalette.Length - 1);
                heatmapPalette[i] = GetHeatmapColor(value);
            }

            precomputedColors = new int[heatmapPalette.Length];
            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                Color color = heatmapPalette[i];
                precomputedColors[i] = (255 << 24) | (color.R << 16) | (color.G << 8) | color.B;
            }
        }

        private void InitializeCOPIndicator()
        {
            copIndicator = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Red,
                Stroke = Brushes.Red,
                StrokeThickness = 2
            };
            copTrajectory = new Polyline
            {
                Stroke = new SolidColorBrush(Colors.Yellow) { Opacity = TRAJECTORY_OPACITY },
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection()
            };
            ACX = 48;
            ACY = 48;
            ACScale = 0.1;
            AuxiliaryCircle = new Ellipse
            {
                Width = 10,
                Height = 10,
                Stroke = Brushes.Blue,
                StrokeThickness = 1,
                Fill = Brushes.Yellow,
                Opacity = 0.3,
                Visibility = Visibility.Hidden,
            };

            Canvas copCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            (HeatmapImage.Parent as Grid).Children.Add(copCanvas);
            copCanvas.Children.Add(copTrajectory);
            copCanvas.Children.Add(copIndicator);
            copCanvas.Children.Add(AuxiliaryCircle);

            HeatmapImage.SizeChanged += (s, e) =>
            {
                copCanvas.Width = HeatmapImage.ActualWidth;
                copCanvas.Height = HeatmapImage.ActualHeight;
                UpdateTrajectoryPoints();
            };
            copCanvas.Width = HeatmapImage.ActualWidth;
            copCanvas.Height = HeatmapImage.ActualHeight;
        }

        private void InitializeFootOutlineGuides()
        {
            leftGuideLine = new Line
            {
                Stroke = Brushes.Blue,
                StrokeThickness = 2,
                Tag = "LeftGuide",
                Visibility = Visibility.Hidden,
            };
            leftGuideLine.X1 = leftGuideLine.X2 = 0;
            leftGuideLine.Y1 = leftGuideLine.Y2 = 0;
            rightGuideLine = new Line
            {
                Stroke = Brushes.Blue,
                StrokeThickness = 2,
                Tag = "RightGuide",
                Visibility = Visibility.Hidden,
            };
            
            // 기존 copCanvas 재사용
            Canvas copCanvas = (HeatmapImage.Parent as Grid).Children.OfType<Canvas>().FirstOrDefault();
            if (copCanvas != null)
            {
                copCanvas.Children.Add(leftGuideLine);
                copCanvas.Children.Add(rightGuideLine);
            }
        }

        private void InitializeCharts()
        {
            ForefootHeelLine.Points = new PointCollection();
            LeftPressureLine.Points = new PointCollection();
            RightPressureLine.Points = new PointCollection();

            _forefootHeelLastValueLine = new Line
            {
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                X1 = 0,
                X2 = ForefootHeelChart.ActualWidth,
                Visibility = Visibility.Visible
            };
            _leftPressureLastValueLine = new Line
            {
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                X1 = 0,
                X2 = LeftPressureChart.ActualWidth,
                Visibility = Visibility.Visible
            };
            _rightPressureLastValueLine = new Line
            {
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                X1 = 0,
                X2 = RightPressureChart.ActualWidth,
                Visibility = Visibility.Visible
            };

            ForefootHeelChart.Children.Add(_forefootHeelLastValueLine);
            LeftPressureChart.Children.Add(_leftPressureLastValueLine);
            RightPressureChart.Children.Add(_rightPressureLastValueLine);

            _forefootHeelcenterLine = new Line
            {
                Stroke = Brushes.White,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                X1 = 0,
                X2 = ForefootHeelChart.ActualWidth,
                Y1 = 0,
                Y2 = ForefootHeelChart.ActualHeight / 2,
                Visibility = Visibility.Visible
            };
            _leftfootHeelcenterLine = new Line
            {
                Stroke = Brushes.White,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                X1 = 0,
                X2 = ForefootHeelChart.ActualWidth,
                Y1 = 0,
                Y2 = ForefootHeelChart.ActualHeight / 2,
                Visibility = Visibility.Visible
            };
            _rightfootHeelcenterLine = new Line
            {
                Stroke = Brushes.White,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                X1 = 0,
                X2 = ForefootHeelChart.ActualWidth,
                Y1 = 0,
                Y2 = ForefootHeelChart.ActualHeight / 2,
                Visibility = Visibility.Visible
            };
            ForefootHeelChart.Children.Add(_forefootHeelcenterLine);
            LeftPressureChart.Children.Add(_leftfootHeelcenterLine);
            RightPressureChart.Children.Add(_rightfootHeelcenterLine);

            SizeChanged += (s, e) => { if (IsLoaded) UpdateChartSizes(); };
            ForefootHeelChart.SizeChanged += (s, e) => UpdateChartSizes();
            LeftPressureChart.SizeChanged += (s, e) => UpdateChartSizes();
            RightPressureChart.SizeChanged += (s, e) => UpdateChartSizes();

            double width1 = ForefootHeelChart.ActualWidth;
            double width2 = LeftPressureChart.ActualWidth;
            double width3 = RightPressureChart.ActualWidth;
            for (int i = 0; i < MAX_CHART_POINTS; i++)
            {
                forefootHeelData.Enqueue(new Point(width1, leftPercent));
                leftPressureData.Enqueue(new Point(width2, leftHeelPercent));
                rightPressureData.Enqueue(new Point(width3, rightHeelPercent));
            }
        }

        private unsafe void Update2D(ushort[] data)
        {
            heatmapBitmap.Lock();
            IntPtr pBackBuffer = heatmapBitmap.BackBuffer;
            int stride = heatmapBitmap.BackBufferStride;

            for (int y = 0; y < SENSOR_SIZE; y++)
            {
                int* pDest = (int*)(pBackBuffer + y * stride);
                for (int x = 0; x < SENSOR_SIZE; x++)
                {
                    float normalizedValue = data[y * SENSOR_SIZE + x] / 1024.0f;
                    int colorIndex = Math.Min((int)(normalizedValue * (heatmapPalette.Length - 1)), heatmapPalette.Length - 1);
                    *pDest++ = precomputedColors[colorIndex];
                }
            }

            heatmapBitmap.AddDirtyRect(new Int32Rect(0, 0, SENSOR_SIZE, SENSOR_SIZE));
            heatmapBitmap.Unlock();
        }

        private void UpdateCenterOfPressure(ushort[] data)
        {
            double totalPressure = 0, weightedX = 0, weightedY = 0;
            for (int y = 0; y < SENSOR_SIZE; y++)
            {
                for (int x = 0; x < SENSOR_SIZE; x++)
                {
                    double pressure = data[y * SENSOR_SIZE + x];
                    totalPressure += pressure;
                    weightedX += x * pressure;
                    weightedY += y * pressure;
                }
            }

            if (totalPressure > 500)
            {
                centerOfPressure = new Point(weightedX / totalPressure, weightedY / totalPressure);

                // 중심점 (48, 48)을 기준으로 상대적 이동 거리 계산
                double deltaX = centerOfPressure.X - 48; // 중심점으로부터의 X 이동 거리
                double deltaY = centerOfPressure.Y - 48; // 중심점으로부터의 Y 이동 거리
                // 이동 거리에 copSensitivity 적용
                double sensitiveDeltaX = deltaX * copSensitivity;
                double sensitiveDeltaY = deltaY * copSensitivity;

                double scaleX = HeatmapImage.ActualWidth / SENSOR_SIZE;
                double scaleY = HeatmapImage.ActualHeight / SENSOR_SIZE;
                double screenX = (48 + sensitiveDeltaX) * scaleX;
                double screenY = (48 + sensitiveDeltaY) * scaleY;

                Canvas.SetLeft(copIndicator, screenX - copIndicator.Width / 2);
                Canvas.SetTop(copIndicator, screenY - copIndicator.Height / 2);

                copHistory.Enqueue(new Point(screenX, screenY));
                if (copHistory.Count > 100) copHistory.Dequeue();
                copTrajectory.Points = new PointCollection(copHistory);

                if (copHistory.Count > 1)
                {
                    var gradientStops = new GradientStopCollection();
                    int i = 0;
                    foreach (var point in copHistory)
                    {
                        double opacity = (double)i++ / copHistory.Count * TRAJECTORY_OPACITY;
                        gradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * opacity), 255, 255, 0), (double)(i - 1) / (copHistory.Count - 1)));
                    }
                    copTrajectory.Stroke = new LinearGradientBrush { GradientStops = gradientStops, StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                }
            }
            else
            {
                Canvas.SetLeft(copIndicator, HeatmapImage.ActualWidth / 2 - copIndicator.Width / 2);
                Canvas.SetTop(copIndicator, HeatmapImage.ActualHeight / 2 - copIndicator.Height / 2);
            }
        }

        private void UpdateAuxiliaryCircle(bool visible)
        {
            if (visible)
            {
                if (leftGuideLine.IsVisible && rightGuideLine.IsVisible)
                {
                    double diameter = (leftGuideLine.X1 + rightGuideLine.X2) / 2 * ACScale;
                    AuxiliaryCircle.Width = AuxiliaryCircle.Height = diameter;

                    ACX = Canvas.GetLeft(copIndicator) + (copIndicator.Width / 2);
                    ACY = Canvas.GetTop(copIndicator) + (copIndicator.Height / 2);

                    Canvas.SetLeft(AuxiliaryCircle, ACX - (AuxiliaryCircle.Width / 2));
                    Canvas.SetTop(AuxiliaryCircle, ACY - (AuxiliaryCircle.Height / 2));

                    AuxiliaryCircle.Visibility = Visibility.Visible;
                }
            }
            else
            {
                AuxiliaryCircle.Visibility = Visibility.Hidden;
            }
        }

        private void UpdateBalanceGauges(ushort[] data)
        {
            double totalPressure = 0, leftPressure = 0, rightPressure = 0;
            double leftForefootPressure = 0, leftHeelPressure = 0, rightForefootPressure = 0, rightHeelPressure = 0;
            double leftWeightedY = 0, rightWeightedY = 0;
            int leftMinY = SENSOR_SIZE, leftMaxY = 0, rightMinY = SENSOR_SIZE, rightMaxY = 0;
            int validCellCount = 0, leftValidCells = 0, rightValidCells = 0;
            int centerX = SENSOR_SIZE / 2;

            for (int y = 0; y < SENSOR_SIZE; y++)
            {
                for (int x = 0; x < SENSOR_SIZE; x++)
                {
                    double pressure = data[y * SENSOR_SIZE + x];
                    if (pressure < WEIGHT_THRESHOLD) continue;

                    totalPressure += pressure;
                    validCellCount++;

                    if (x < centerX)
                    {
                        leftPressure += pressure;
                        leftWeightedY += y * pressure;
                        leftValidCells++;
                        if (y < SENSOR_SIZE / 2) leftForefootPressure += pressure;
                        else leftHeelPressure += pressure;
                        leftMinY = Math.Min(leftMinY, y);
                        leftMaxY = Math.Max(leftMaxY, y);
                    }
                    else
                    {
                        rightPressure += pressure;
                        rightWeightedY += y * pressure;
                        rightValidCells++;
                        if (y < SENSOR_SIZE / 2) rightForefootPressure += pressure;
                        else rightHeelPressure += pressure;
                        rightMinY = Math.Min(rightMinY, y);
                        rightMaxY = Math.Max(rightMaxY, y);
                    }
                }
            }

            if (totalPressure < 500 || validCellCount < 10) return;

            leftPercent = (leftPressure / totalPressure) * 100;
            rightPercent = 100 - leftPercent;

            if (leftValidCells > 0 && leftPressure > 0)
            {
                double leftCenterY = leftWeightedY / leftPressure;
                int leftFootLength = Math.Max(leftMaxY - leftMinY + 1, 10);
                leftForefootPercent = ((leftCenterY - leftMinY) / leftFootLength) * 100;
                leftHeelPercent = 100 - leftForefootPercent;
            }
            else
            {
                leftForefootPercent = 50;
                leftHeelPercent = 50;
            }

            if (rightValidCells > 0 && rightPressure > 0)
            {
                double rightCenterY = rightWeightedY / rightPressure;
                int rightFootLength = Math.Max(rightMaxY - rightMinY + 1, 10);
                rightForefootPercent = ((rightCenterY - rightMinY) / rightFootLength) * 100;
                rightHeelPercent = 100 - rightForefootPercent;
            }
            else
            {
                rightForefootPercent = 50;
                rightHeelPercent = 50;
            }

            double maxHeight = LeftGauge.Parent is FrameworkElement ? (LeftGauge.Parent as FrameworkElement).ActualHeight : 400;
            double maxWidth = ForefootGauge.Parent is FrameworkElement ? (ForefootGauge.Parent as FrameworkElement).ActualWidth / 2 : 400;

            LeftGauge.Height = maxHeight * (100 - leftForefootPercent) / 100;
            RightGauge.Height = maxHeight * (100 - rightForefootPercent) / 100;
            ForefootGauge.Width = maxWidth * leftPercent / 100;
            HeelGauge.Width = maxWidth * rightPercent / 100;

            LeftPercentText.Text = $"{Math.Round(leftHeelPercent)}%";
            RightPercentText.Text = $"{Math.Round(rightHeelPercent)}%";
            ForefootPercentText.Text = $"{Math.Round(leftPercent)}%";
            HeelPercentText.Text = $"{Math.Round(rightPercent)}%";
            LeftBalanceText.Text = $"{Math.Round(leftPercent)}";
            RightBalanceText.Text = $"{Math.Round(rightPercent)}";

            UpdateGaugeColors(leftForefootPercent, rightForefootPercent, leftPercent, rightPercent);
        }

        private void UpdateGaugeColors(double leftForefootPercent, double rightForefootPercent, double leftPercent, double rightPercent)
        {
            Color leftColor = leftForefootPercent >= 30 && leftForefootPercent <= 60 ? Colors.LimeGreen : (leftForefootPercent > 60 ? Colors.Yellow : Colors.Orange);
            Color rightColor = rightForefootPercent >= 30 && rightForefootPercent <= 60 ? Colors.LimeGreen : (rightForefootPercent > 60 ? Colors.Yellow : Colors.Orange);
            LeftGauge.Fill = new SolidColorBrush(leftColor);
            RightGauge.Fill = new SolidColorBrush(rightColor);

            Color leftFootColor = Math.Abs(leftPercent - 50) <= 5 ? Colors.DodgerBlue : (leftPercent > 55 ? Colors.DodgerBlue : Colors.RoyalBlue.ChangeBrightness(0.8));
            Color rightFootColor = Math.Abs(rightPercent - 50) <= 5 ? Colors.DodgerBlue : (rightPercent > 55 ? Colors.DodgerBlue : Colors.RoyalBlue.ChangeBrightness(0.8));
            ForefootGauge.Fill = new SolidColorBrush(leftFootColor);
            HeelGauge.Fill = new SolidColorBrush(rightFootColor);
        }

        private void UpdateFootOutlineGuides(ushort[] data)
        {
            // 발의 경계를 저장할 변수
            int leftMinX = SENSOR_SIZE, leftMinY = SENSOR_SIZE, leftMaxY = 0;
            int rightMaxX = 0, rightMinY = SENSOR_SIZE, rightMaxY = 0;
            int centerX = SENSOR_SIZE / 2;
            double totalPressure = 0; 
            bool hasValidData = false;
            // 총 압력 및 경계 탐지
            for (int y = 0; y < SENSOR_SIZE; y++)
            {
                for (int x = 0; x < SENSOR_SIZE; x++)
                {
                    double pressure = data[y * SENSOR_SIZE + x];
                    totalPressure += pressure;

                    if (pressure >= 5) // UpdateCenterOfPressure와 동일한 임계값 사용
                    {
                        hasValidData = true;
                        if (x < centerX) // 왼발
                        {
                            leftMinX = Math.Min(leftMinX, x);
                            leftMinY = Math.Min(leftMinY, y);
                            leftMaxY = Math.Max(leftMaxY, y);
                        }
                        else // 오른발
                        {
                            rightMaxX = Math.Max(rightMaxX, x);
                            rightMinY = Math.Min(rightMinY, y);
                            rightMaxY = Math.Max(rightMaxY, y);
                        }
                    }
                }
            }

            // 압력이 충분하지 않으면 라인 숨김
            if (totalPressure < 500 || !hasValidData)
            {
                leftGuideLine.Visibility = Visibility.Hidden;
                rightGuideLine.Visibility = Visibility.Hidden;
                return;
            }

            // 스케일링 계산
            double scaleX = HeatmapImage.ActualWidth / SENSOR_SIZE;
            double scaleY = HeatmapImage.ActualHeight / SENSOR_SIZE;

            // 왼발 가이드 선 업데이트
            leftGuideLine.X1 = leftGuideLine.X2 = leftMinX * scaleX;
            leftGuideLine.Y1 = leftMinY * scaleY;
            leftGuideLine.Y2 = leftMaxY * scaleY;
            leftGuideLine.Visibility = Visibility.Visible;

            // 오른발 가이드 선 업데이트
            rightGuideLine.X1 = rightGuideLine.X2 = rightMaxX * scaleX + scaleX;
            rightGuideLine.Y1 = rightMinY * scaleY;
            rightGuideLine.Y2 = rightMaxY * scaleY;
            rightGuideLine.Visibility = Visibility.Visible;
        }

        private void UpdateCharts()
        {
            if (ForefootHeelChart.ActualWidth <= 0 || LeftPressureChart.ActualWidth <= 0 || RightPressureChart.ActualWidth <= 0) return;

            double width1 = ForefootHeelChart.ActualWidth, height1 = ForefootHeelChart.ActualHeight;
            double width2 = LeftPressureChart.ActualWidth, height2 = LeftPressureChart.ActualHeight;
            double width3 = RightPressureChart.ActualWidth, height3 = RightPressureChart.ActualHeight;

            if (forefootHeelData.Count >= MAX_CHART_POINTS) forefootHeelData.Dequeue();
            if (leftPressureData.Count >= MAX_CHART_POINTS) leftPressureData.Dequeue();
            if (rightPressureData.Count >= MAX_CHART_POINTS) rightPressureData.Dequeue();

            forefootHeelData.Enqueue(new Point(width1, leftPercent));
            leftPressureData.Enqueue(new Point(width2, leftHeelPercent));
            rightPressureData.Enqueue(new Point(width3, rightHeelPercent));

            UpdateChartLine(forefootHeelData, ForefootHeelChart, ForefootHeelLine, _forefootHeelLastValueLine, height1);
            UpdateChartLine(leftPressureData, LeftPressureChart,  LeftPressureLine, _leftPressureLastValueLine, height2);
            UpdateChartLine(rightPressureData, RightPressureChart, RightPressureLine, _rightPressureLastValueLine, height3);

            _forefootHeelcenterLine.X1 = 0;
            _forefootHeelcenterLine.X2 = width1;
            _forefootHeelcenterLine.Y1 = height1 / 2;
            _forefootHeelcenterLine.Y2 = height1 / 2;
            _leftfootHeelcenterLine.X1 = 0;
            _leftfootHeelcenterLine.X2 = width2;
            _leftfootHeelcenterLine.Y1 = height2 / 2;
            _leftfootHeelcenterLine.Y2 = height2 / 2;
            _rightfootHeelcenterLine.X1 = 0;
            _rightfootHeelcenterLine.X2 = width3;
            _rightfootHeelcenterLine.Y1 = height3 / 2;
            _rightfootHeelcenterLine.Y2 = height3 / 2;

            ForefootHeelValueText.Text = $"L: {Math.Round(leftPercent)}% / R: {Math.Round(rightPercent)}%";
            LeftPressureValueText.Text = $"F: {Math.Round(leftForefootPercent)}% / H: {Math.Round(leftHeelPercent)}%";
            RightPressureValueText.Text = $"F: {Math.Round(rightForefootPercent)}% / H: {Math.Round(rightHeelPercent)}%";
        }

        private void UpdateChartLine(Queue<Point> dataQueue, Canvas chartCanvas, Polyline chartLine, Line lastValueLine, double height)
        {
            double width = chartCanvas.ActualWidth;
            Point[] points = dataQueue.ToArray();
            PointCollection newPoints = new PointCollection();

            for (int i = 0; i < points.Length; i++)
            {
                double x = (double)i / (points.Length - 1) * width;
                double y = height - (points[i].Y * height / 100);
                newPoints.Add(new Point(x, y));
            }
            chartLine.Points = newPoints;

            // 마지막 값의 Y 위치로 가로선 업데이트
            if (points.Length > 0)
            {
                double lastY = height - (points[points.Length - 1].Y * height / 100);
                lastValueLine.X1 = 0;
                lastValueLine.X2 = width;
                lastValueLine.Y1 = lastY;
                lastValueLine.Y2 = lastY;
            }
        }

        private void UpdateChartSizes()
        {
            if (!IsLoaded || ForefootHeelChart.ActualWidth <= 0 || LeftPressureChart.ActualWidth <= 0 || RightPressureChart.ActualWidth <= 0) return;
            UpdateChartLine(forefootHeelData, ForefootHeelChart, ForefootHeelLine, _forefootHeelLastValueLine, ForefootHeelChart.ActualHeight);
            UpdateChartLine(leftPressureData, LeftPressureChart, LeftPressureLine, _leftPressureLastValueLine, LeftPressureChart.ActualHeight);
            UpdateChartLine(rightPressureData, RightPressureChart, RightPressureLine, _rightPressureLastValueLine, RightPressureChart.ActualHeight);
        }

        private void EstimateWeight(ushort[] data)
        {
            //double totalForce = 0, totalPressure = 0, totalActiveCells = 0;
            double totalPressure = 0, leftPressure = 0, rightPressure = 0, totalActiveCells = 0;
            //const double CELL_SIZE = 0.0055, CELL_AREA = CELL_SIZE * CELL_SIZE, GRAVITY = 9.81, PRESSURE_SCALING_FACTOR = 1000.0;

            for (int y = 0; y < SENSOR_SIZE; y++)
            {
                for (int x = 0; x < SENSOR_SIZE; x++)
                {
                    double pressure = data[y * SENSOR_SIZE + x];
                    if (pressure > WEIGHT_THRESHOLD)
                    {
                        totalActiveCells++;
                        totalPressure += pressure;
                        if (x < 48)
                            leftPressure += pressure;
                        else
                            rightPressure += pressure;
                    }
                }
            }

            TotalPressureText.Text = totalActiveCells < 1 ? "- kpa" : $"{Math.Round(totalPressure)} kpa";
            LeftPressureText.Text = totalActiveCells < 1 ? "- kpa" : $"{Math.Round(leftPressure)} kpa";
            RightPressureText.Text = totalActiveCells < 1 ? "- kpa" : $"{Math.Round(rightPressure)} kpa";


            //for (int i = 0; i < data.Length; i++)
            //{
            //    double pressure = data[i];
            //    if (pressure > WEIGHT_THRESHOLD)
            //    {
            //        double force = pressure * CELL_AREA * PRESSURE_SCALING_FACTOR;
            //        totalForce += force;
            //        totalActiveCells++;
            //        totalPressure += pressure;
            //    }
            //}

            //double estimatedWeight = totalForce / GRAVITY;
            //WeightValueText.Text = totalActiveCells < 1 ? "- kg" : $"{Math.Round(estimatedWeight, 1)} kg";
            //ActiveCellsText.Text = totalActiveCells < 1 ? "- kpa" : $"{Math.Round(totalPressure)} kpa";
        }

        private Color GetHeatmapColor(float value)
        {
            //value = Math.Clamp(value, 0, 1);
            //if (value < 0.1f) return Color.FromRgb(0, 0, (byte)(255 * (value / 0.1f)));
            //if (value < 0.2f) return Color.FromRgb(0, (byte)(255 * ((value - 0.1f) / 0.1f)), 255);
            //if (value < 0.4f) return Color.FromRgb((byte)(255 * ((value - 0.2f) / 0.2f)), 255, (byte)(255 * (1 - (value - 0.2f) / 0.2f)));
            //if (value < 0.8f) return Color.FromRgb(255, (byte)(255 * (1 - (value - 0.4f) / 0.4f)), 0);
            //return Color.FromRgb(255, 0, 0);

            value = Math.Clamp(value, 0, 1); // 값이 0~1 사이로 제한됨

            byte r, g, b;

            //if (value == 0)
            //{
            //    r = 17; g = 0; b = 21;
            //}
            //else if (value <= 0.25f)
            //{
            //    // 0 (68, 1, 84) -> 0.25 (72, 40, 120)
            //    float t = value / 0.25f;
            //    r = (byte)(68 + (72 - 68) * t);
            //    g = (byte)(1 + (40 - 1) * t);
            //    b = (byte)(84 + (120 - 84) * t);
            //}
            //else if (value <= 0.5f)
            //{
            //    // 0.25 (72, 40, 120) -> 0.5 (62, 74, 137)
            //    float t = (value - 0.25f) / 0.25f;
            //    r = (byte)(72 + (62 - 72) * t);
            //    g = (byte)(40 + (74 - 40) * t);
            //    b = (byte)(120 + (137 - 120) * t);
            //}
            //else if (value <= 0.75f)
            //{
            //    // 0.5 (62, 74, 137) -> 0.75 (49, 104, 142)
            //    float t = (value - 0.5f) / 0.25f;
            //    r = (byte)(62 + (49 - 62) * t);
            //    g = (byte)(74 + (104 - 74) * t);
            //    b = (byte)(137 + (142 - 137) * t);
            //}
            //else
            //{
            //    // 0.75 (49, 104, 142) -> 1.0 (253, 231, 37)
            //    float t = (value - 0.75f) / 0.25f;
            //    r = (byte)(49 + (253 - 49) * t);
            //    g = (byte)(104 + (231 - 104) * t);
            //    b = (byte)(142 + (37 - 142) * t);
            //}

            if (value == 0)
            {
                r = 0; g = 0; b = 0;
            }
            else if (value <= 0.18f)
            {
                // 0 (130, 0, 255) -> 0.18 (0, 0, 255)
                float t = (value - 0) / 0.18f;
                r = (byte)(130 + (0 - 130) * t);
                g = 0;
                b = 255;
            }
            else if (value <= 0.38f)
            {
                // 0.18 (0, 0, 255) -> 0.38 (0, 165, 255)
                float t = (value - 0.18f) / 0.2f;
                r = 0;
                g = (byte)(0 + (165 - 0) * t);
                b = 255;
            }
            else if (value <= 0.65f)
            {
                // 0.38 (0, 165, 255) -> 0.65 (0, 255, 0)
                float t = (value - 0.38f) / 0.27f;
                r = 0;
                g = (byte)(165 + (255 - 165) * t);
                b = (byte)(255 + (0 - 255) * t);
            }
            else if (value <= 0.8f)
            {
                // 0.65 (0, 255, 0) -> 0.8 (255, 255, 0)
                float t = (value - 0.65f) / 0.15f;
                r = (byte)(0 + (255 - 0) * t);
                g = 255;
                b = 0;
            }
            else if (value <= 0.88f)
            {
                // 0.8 (255, 255, 0) -> 0.88 (255, 165, 0)
                float t = (value - 0.8f) / 0.08f;
                r = 255;
                g = (byte)(255 + (165 - 255) * t);
                b = 0;
            }
            else
            {
                // 0.88(255, 165, 0) -> 1.0 (255, 0, 0)
                float t = (value - 0.88f) / 0.12f;
                r = 255;
                g = (byte)(165 + (0 - 165) * t);
                b = 0;
            }

            return Color.FromRgb(r, g, b);
        }

        private void SmallCopButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SmallCopButton.IsChecked = true;
            MediumCopButton.IsChecked = false;
            LargeCopButton.IsChecked = false;
            copIndicator.Width = 10;
            copIndicator.Height = 10;
        }

        private void SmallCopButton_Click(object sender, RoutedEventArgs e)
        {
            SmallCopButton.IsChecked = true;
            MediumCopButton.IsChecked = false;
            LargeCopButton.IsChecked = false;
            copIndicator.Width = 10;
            copIndicator.Height = 10;
        }

        private void MediumCopButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SmallCopButton.IsChecked = false;
            MediumCopButton.IsChecked = true;
            LargeCopButton.IsChecked = false;
            copIndicator.Width = 20;
            copIndicator.Height = 20;
        }

        private void MediumCopButton_Click(object sender, RoutedEventArgs e)
        {
            SmallCopButton.IsChecked = false;
            MediumCopButton.IsChecked = true;
            LargeCopButton.IsChecked = false;
            copIndicator.Width = 20;
            copIndicator.Height = 20;
        }

        private void LargeCopButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SmallCopButton.IsChecked = false;
            MediumCopButton.IsChecked = false;
            LargeCopButton.IsChecked = true;
            copIndicator.Width = 30;
            copIndicator.Height = 30;
        }

        private void LargeCopButton_Click(object sender, RoutedEventArgs e)
        {
            SmallCopButton.IsChecked = false;
            MediumCopButton.IsChecked = false;
            LargeCopButton.IsChecked = true;
            copIndicator.Width = 30;
            copIndicator.Height = 30;
        }

        private void LowMovingSensitivityButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            LowMovingSensitivityButton.IsChecked = true;
            MiddleMovingSensitivityButton.IsChecked = false;
            HighMovingSensitivityButton.IsChecked = false;
            copSensitivity = 1.0;
        }

        private void LowMovingSensitivityButton_Click(object sender, RoutedEventArgs e)
        {
            LowMovingSensitivityButton.IsChecked = true;
            MiddleMovingSensitivityButton.IsChecked = false;
            HighMovingSensitivityButton.IsChecked = false;
            copSensitivity = 1.0;
        }

        private void MiddleMovingSensitivityButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            LowMovingSensitivityButton.IsChecked = false;
            MiddleMovingSensitivityButton.IsChecked = true;
            HighMovingSensitivityButton.IsChecked = false;
            copSensitivity = 1.5;
        }

        private void MiddleMovingSensitivityButton_Click(object sender, RoutedEventArgs e)
        {
            LowMovingSensitivityButton.IsChecked = false;
            MiddleMovingSensitivityButton.IsChecked = true;
            HighMovingSensitivityButton.IsChecked = false;
            copSensitivity = 1.5;
        }

        private void HighMovingSensitivityButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            LowMovingSensitivityButton.IsChecked = false;
            MiddleMovingSensitivityButton.IsChecked = false;
            HighMovingSensitivityButton.IsChecked = true;
            copSensitivity = 2.0;
        }

        private void HighMovingSensitivityButton_Click(object sender, RoutedEventArgs e)
        {
            LowMovingSensitivityButton.IsChecked = false;
            MiddleMovingSensitivityButton.IsChecked = false;
            HighMovingSensitivityButton.IsChecked = true;
            copSensitivity = 2.0;
        }

        private void AuxiliaryCircleOnButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateAuxiliaryCircle(true);
        }

        private void AuxiliaryCircleOffButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateAuxiliaryCircle(false);
        }

        private void AuxiliaryCircle10perButton_Click(object sender, RoutedEventArgs e)
        {
            AuxiliaryCircle10perButton.Background = Brushes.Gray;
            AuxiliaryCircle20perButton.Background = Brushes.LightGray;
            AuxiliaryCircle30perButton.Background = Brushes.LightGray;
            ACScale = 0.1;
            if (AuxiliaryCircle.IsVisible) UpdateAuxiliaryCircle(true);
        }

        private void AuxiliaryCircle20perButton_Click(object sender, RoutedEventArgs e)
        {
            AuxiliaryCircle10perButton.Background = Brushes.LightGray;
            AuxiliaryCircle20perButton.Background = Brushes.Gray;
            AuxiliaryCircle30perButton.Background = Brushes.LightGray;
            ACScale = 0.2;
            if (AuxiliaryCircle.IsVisible) UpdateAuxiliaryCircle(true);
        }

        private void MaxColorCombobox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            int maxValue = 1024;
            switch (MaxColorCombobox.SelectedIndex)
            {
                case 0:
                    maxValue = 64;
                    break;
                case 1:
                    maxValue = 128;
                    break;
                case 2:
                    maxValue = 256;
                    break;
                case 3:
                    maxValue = 512;
                    break;
                case 4:
                    maxValue = 1024;
                    break;
            }
            InitializeHeatmapPalette(maxValue);
        }

        private void AuxiliaryCircle30perButton_Click(object sender, RoutedEventArgs e)
        {
            AuxiliaryCircle10perButton.Background = Brushes.LightGray;
            AuxiliaryCircle20perButton.Background = Brushes.LightGray;
            AuxiliaryCircle30perButton.Background = Brushes.Gray;
            ACScale = 0.3;
            if (AuxiliaryCircle.IsVisible) UpdateAuxiliaryCircle(true);
        }

        private void UpdateTrajectoryPoints()
        {
            if (copHistory.Count <= 0) return;
            double scaleX = HeatmapImage.ActualWidth / SENSOR_SIZE;
            double scaleY = HeatmapImage.ActualHeight / SENSOR_SIZE;
            var points = new PointCollection();
            foreach (var point in copHistory)
            {
                points.Add(new Point(point.X * scaleX, point.Y * scaleY));
            }
            copTrajectory.Points = points;
        }

        private void ResetPortsButton_Click(object sender, EventArgs e)
        {
            ResetPortsButton.IsEnabled = false;
            try
            {
                ResetPortsRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error requesting port reset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ResetPortsButton.IsEnabled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}