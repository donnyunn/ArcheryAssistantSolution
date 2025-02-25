﻿using System;
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
    public partial class MainWindow : Window
    {
        private const int SENSOR_SIZE = 96;
        private const int FPS_2D = 30;
        private const int FPS_3D = 30;

        // 렌더링 관련
        private DateTime _lastRenderTime = DateTime.MinValue;
        private readonly object _dataLock = new object();

        // 2D 렌더링 관련
        private WriteableBitmap heatmapBitmap;
        private int[] colorBuffer;

        // 3D 렌더링 관련
        private Model3DGroup meshGroup;
        private ModelVisual3D modelVisual;
        private GeometryModel3D[,] meshGrid;
        
        // 카메라 관련
        private const double INITIAL_POSITION_Y = 2.5;
        private const double INITIAL_POSITION_Z = 2.5;
        private double rotationAngle = 0;
        private double cameraDistance = Math.Sqrt(INITIAL_POSITION_Y * INITIAL_POSITION_Y + INITIAL_POSITION_Z * INITIAL_POSITION_Z);
        private double cameraHeight = INITIAL_POSITION_Z;
        private Point3D initialPosition;
        private Vector3D initialLookDirection;
        private Vector3D initialUpDirection;

        // 미리 계산된 색상 팔레트 (예: 1024개의 색상)
        private Color[] heatmapPalette = new Color[1024];

        // 무게중심 관련
        private Point centerOfPressure;
        private Ellipse copIndicator;
        private Queue<Point> copHistory;
        private Polyline copTrajectory;
        private const int MAX_TRAJECTORY_POINTS = 100;  // 궤적 표시 최대 포인트 수
        private const double TRAJECTORY_OPACITY = 0.6;  // 궤적 선 투명도

        // 격자 관련
        private ModelVisual3D gridVisual;
        private const double GRID_SPACING = 0.2; // 격자 간격

        // 스로틀링 타이머 추가
        private System.Windows.Threading.DispatcherTimer _updateThrottleTimer;
        private ushort[] _latestData;
        private bool _dataUpdatedSinceLastRender = false;
        private const int THROTTLE_INTERVAL_MS = 33; // 약 30fps로 제한

        // 밸런스 게이지 관련
        private const double WEIGHT_THRESHOLD = 16.0; // 유효 압력 임계값
        private Point footCenter = new Point(SENSOR_SIZE / 2, SENSOR_SIZE / 2);

        // 시계열 그래프 관련
        private const int MAX_CHART_POINTS = 200; // 최대 그래프 포인트 수
        private const double CHART_UPDATE_INTERVAL_MS = 100; // 그래프 업데이트 간격 (ms)
        private DateTime lastChartUpdateTime = DateTime.MinValue;

        // 그래프 데이터 큐
        private Queue<Point> forefootHeelData = new Queue<Point>(MAX_CHART_POINTS);
        private Queue<Point> leftPressureData = new Queue<Point>(MAX_CHART_POINTS);
        private Queue<Point> rightPressureData = new Queue<Point>(MAX_CHART_POINTS);

        // 밸런스 값 저장 변수
        private double leftPercent = 50;
        private double rightPercent = 50;
        private double forefootPercent = 50;
        private double heelPercent = 50;

        public MainWindow()
        {
            InitializeComponent();
            InitializeRendering();
            InitializeHeatmapPalette();

            // 무게중심 표시기 초기화
            InitializeCOPIndicator();

            // 3D 격자 초기화
            InitializeGrid();

            InitializeUpdateThrottling();

            InitializeCharts();
        }

        private void InitializeUpdateThrottling()
        {
            _updateThrottleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(THROTTLE_INTERVAL_MS)
            };
            _updateThrottleTimer.Tick += OnUpdateTimerTick;
            _updateThrottleTimer.Start();

            _latestData = new ushort[SENSOR_SIZE * SENSOR_SIZE];
        }

        // 차트 초기화 메서드
        private void InitializeCharts()
        {
            // 각 그래프 라인 초기화
            ForefootHeelLine.Points = new PointCollection();
            LeftPressureLine.Points = new PointCollection();
            RightPressureLine.Points = new PointCollection();

            // 초기 데이터 포인트 추가 (0값으로)
            for (int i = 0; i < MAX_CHART_POINTS; i++)
            {
                double x = i * (ForefootHeelChart.ActualWidth / MAX_CHART_POINTS);

                forefootHeelData.Enqueue(new Point(x, 0));
                leftPressureData.Enqueue(new Point(x, 0));
                rightPressureData.Enqueue(new Point(x, 0));
            }

            // 창 크기 변경 시 차트 업데이트 이벤트 추가
            SizeChanged += (s, e) => {
                if (IsLoaded)
                {
                    UpdateChartSizes();
                }
            };

            // 차트 캔버스 크기 변경 이벤트 추가
            ForefootHeelChart.SizeChanged += (s, e) => UpdateChartSizes();
            LeftPressureChart.SizeChanged += (s, e) => UpdateChartSizes();
            RightPressureChart.SizeChanged += (s, e) => UpdateChartSizes();
        }

        // 차트 크기 업데이트 (차트 컨테이너 크기 변경 시 호출)
        private void UpdateChartSizes()
        {
            // 차트가 로드되었는지 확인
            if (!IsLoaded ||
                ForefootHeelChart.ActualWidth <= 0 ||
                LeftPressureChart.ActualWidth <= 0 ||
                RightPressureChart.ActualWidth <= 0)
                return;

            // 모든 그래프 데이터 포인트 재계산
            RecalculateChartPoints(forefootHeelData, ForefootHeelChart, ForefootHeelLine);
            RecalculateChartPoints(leftPressureData, LeftPressureChart, LeftPressureLine);
            RecalculateChartPoints(rightPressureData, RightPressureChart, RightPressureLine);
        }

        // 차트 포인트 재계산 (크기 변경 시)
        private void RecalculateChartPoints(Queue<Point> dataQueue, Canvas chartCanvas, Polyline chartLine)
        {
            if (dataQueue.Count == 0 || chartCanvas.ActualWidth <= 0 || chartCanvas.ActualHeight <= 0)
                return;

            double width = chartCanvas.ActualWidth;
            double height = chartCanvas.ActualHeight;

            // 기존 데이터 값을 유지하면서 X 좌표만 업데이트
            Point[] points = dataQueue.ToArray();
            PointCollection newPoints = new PointCollection();

            for (int i = 0; i < points.Length; i++)
            {
                double x = (double)i / (MAX_CHART_POINTS - 1) * width;
                double y = height - (points[i].Y * height / 100); // 퍼센트 값을 Y 좌표로 변환

                newPoints.Add(new Point(x, y));
            }

            chartLine.Points = newPoints;
        }

        // 타이머 틱 이벤트 처리기
        int frameCount = 0;
        private DateTime lastFpsCheck = DateTime.Now;
        private void OnUpdateTimerTick(object? sender, EventArgs e)
        {
            if (_dataUpdatedSinceLastRender)
            {
                frameCount++;
                TimeSpan elapsed = DateTime.Now - lastFpsCheck;
                if (elapsed.TotalMilliseconds >= 1000)
                {
                    Console.WriteLine($"Footpad FPS: {frameCount}");
                    frameCount = 0;
                    lastFpsCheck = DateTime.Now;
                }

                // 직접 렌더링 메서드를 호출
                _lastRenderTime = DateTime.Now;
                _dataUpdatedSinceLastRender = false;

                try
                {
                    Update2D(_latestData);
                    UpdateCenterOfPressure(_latestData);
                    UpdateBalanceGauges(_latestData );

                    // 일정 시간마다 차트 업데이트 (성능 최적화)
                    TimeSpan chartUpdateElapsed = DateTime.Now - lastChartUpdateTime;
                    if (chartUpdateElapsed.TotalMilliseconds >= CHART_UPDATE_INTERVAL_MS)
                    {
                        UpdateCharts();
                        lastChartUpdateTime = DateTime.Now;
                    }

                    // 3D 업데이트는 선택적으로 더 낮은 빈도로 수행 가능
                    if (viewport3D.IsVisible) // 실제로 보이는 경우만 업데이트
                    {
                        Update3D(_latestData);
                        UpdateGrid(_latestData);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Rendering error: {ex.Message}");
                }
            }
        }

        // 차트 업데이트 메서드
        private void UpdateCharts()
        {
            // 캔버스 크기 확인
            if (ForefootHeelChart.ActualWidth <= 0 || LeftPressureChart.ActualWidth <= 0 || RightPressureChart.ActualWidth <= 0)
                return;

            double width1 = ForefootHeelChart.ActualWidth;
            double height1 = ForefootHeelChart.ActualHeight;
            double width2 = LeftPressureChart.ActualWidth;
            double height2 = LeftPressureChart.ActualHeight;
            double width3 = RightPressureChart.ActualWidth;
            double height3 = RightPressureChart.ActualHeight;

            // 이전 데이터 큐에서 첫 번째 항목 제거
            if (forefootHeelData.Count >= MAX_CHART_POINTS)
                forefootHeelData.Dequeue();
            if (leftPressureData.Count >= MAX_CHART_POINTS)
                leftPressureData.Dequeue();
            if (rightPressureData.Count >= MAX_CHART_POINTS)
                rightPressureData.Dequeue();

            // 새 데이터 포인트 추가 (현재 균형값)
            forefootHeelData.Enqueue(new Point(width1, forefootPercent));
            leftPressureData.Enqueue(new Point(width2, leftPercent));
            rightPressureData.Enqueue(new Point(width3, rightPercent));

            // 차트 포인트 갱신
            UpdateChartLine(forefootHeelData, ForefootHeelChart, ForefootHeelLine);
            UpdateChartLine(leftPressureData, LeftPressureChart, LeftPressureLine);
            UpdateChartLine(rightPressureData, RightPressureChart, RightPressureLine);

            // 현재 값 텍스트 업데이트
            ForefootHeelValueText.Text = $"F: {Math.Round(forefootPercent)}% / H: {Math.Round(heelPercent)}%";
            LeftPressureValueText.Text = $"{Math.Round(leftPercent)}%";
            RightPressureValueText.Text = $"{Math.Round(rightPercent)}%";
        }

        // 차트 라인 업데이트 메서드
        private void UpdateChartLine(Queue<Point> dataQueue, Canvas chartCanvas, Polyline chartLine)
        {
            double width = chartCanvas.ActualWidth;
            double height = chartCanvas.ActualHeight;

            Point[] points = dataQueue.ToArray();
            PointCollection newPoints = new PointCollection();

            for (int i = 0; i < points.Length; i++)
            {
                double x = (double)i / (points.Length - 1) * width;
                double y = height - (points[i].Y * height / 100); // 퍼센트 값을 Y 좌표로 변환
                newPoints.Add(new Point(x, y));
            }

            chartLine.Points = newPoints;
        }

        private int[] precomputedColors; // BGR32 포맷으로 미리 계산된 색상
        private void InitializeHeatmapPalette()
        {
            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                float value = (float)i / (heatmapPalette.Length - 1);
                heatmapPalette[i] = GetHeatmapColor(value);
            }

            // BGR32 형식으로 미리 계산
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
                Fill = Brushes.White,
                Stroke = Brushes.Wheat,
                StrokeThickness = 2
            };

            copHistory = new Queue<Point>(MAX_TRAJECTORY_POINTS);

            copTrajectory = new Polyline
            {
                Stroke = new SolidColorBrush(Colors.Yellow) { Opacity = TRAJECTORY_OPACITY },
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection()
            };

            Canvas copCanvas = new Canvas();
            Grid.SetColumn(copCanvas, 0);
            ((Grid)Content).Children.Add(copCanvas);

            // 궤적을 먼저 추가하고 그 위에 현재 포인트 표시
            copCanvas.Children.Add(copTrajectory);
            copCanvas.Children.Add(copIndicator);

            // 캔버스를 이미지와 동일한 크기로 설정
            copCanvas.Width = HeatmapImage.ActualWidth;
            copCanvas.Height = HeatmapImage.ActualHeight;

            // 이미지 크기 변경 이벤트 처리
            HeatmapImage.SizeChanged += (s, e) =>
            {
                copCanvas.Width = HeatmapImage.ActualWidth;
                copCanvas.Height = HeatmapImage.ActualHeight;
                UpdateTrajectoryPoints();
            };
        }

        private void UpdateTrajectoryPoints()
        {
            if (copHistory.Count > 0)
            {
                double scaleX = HeatmapImage.ActualWidth / SENSOR_SIZE;
                double scaleY = HeatmapImage.ActualHeight / SENSOR_SIZE;

                var points = new PointCollection();
                foreach (var point in copHistory)
                {
                    points.Add(new Point(
                        point.X * scaleX,
                        point.Y * scaleY
                    ));
                }
                copTrajectory.Points = points;
            }
        }

        private void InitializeRendering()
        {
            // 2D 초기화
            heatmapBitmap = new WriteableBitmap(
                SENSOR_SIZE, SENSOR_SIZE,
                96, 96,
                PixelFormats.Bgr32,
                null);
            colorBuffer = new int[SENSOR_SIZE * SENSOR_SIZE];
            HeatmapImage.Source = heatmapBitmap;

            // 3D 초기화
            meshGroup = new Model3DGroup();
            modelVisual = new ModelVisual3D { Content = meshGroup };
            viewport3D.Children.Add(modelVisual);

            // 3D 메시 그리드 초기화
            meshGrid = new GeometryModel3D[SENSOR_SIZE - 1, SENSOR_SIZE - 1];
            InitializeMeshGrid();

            // 조명 설정
            var lightsVisual = new ModelVisual3D
            {
                Content = new Model3DGroup
                {
                    Children =
                    {
                        new AmbientLight(Color.FromRgb(100, 100, 100)),
                        new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1))
                    }
                }
            };
            viewport3D.Children.Add(lightsVisual);

            // 카메라 초기화
            initialPosition = new Point3D(0, INITIAL_POSITION_Y, INITIAL_POSITION_Z);
            initialLookDirection = new Vector3D(0, -1.0, -1.0);
            initialUpDirection = new Vector3D(0, 1, 0);

            camera = new PerspectiveCamera
            {
                Position = initialPosition,
                LookDirection = initialLookDirection,
                UpDirection = initialUpDirection
            };
            viewport3D.Camera = camera;

            UpdateCameraPosition();
        }

        private void InitializeGrid()
        {
            gridVisual = new ModelVisual3D();
            meshGroup.Children.Add(new GeometryModel3D());
        }

        private GeometryModel3D CreateGridLine(Point3D start, Point3D end)
        {
            var mesh = new MeshGeometry3D();
            const double thickness = 0.002;

            // 선을 표현하는 얇은 사각형 생성
            Vector3D direction = end - start;
            Vector3D perpendicular = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
            perpendicular.Normalize();
            perpendicular *= thickness;

            mesh.Positions.Add(start - perpendicular);
            mesh.Positions.Add(start + perpendicular);
            mesh.Positions.Add(end - perpendicular);
            mesh.Positions.Add(end + perpendicular);

            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(1);
            mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(1);
            mesh.TriangleIndices.Add(3);
            mesh.TriangleIndices.Add(2);

            return new GeometryModel3D
            {
                Geometry = mesh,
                Material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(255, 255, 255)))
            };
        }

        private void InitializeMeshGrid()
        {
            // 데이터 메시 초기화
            meshGrid = new GeometryModel3D[SENSOR_SIZE - 1, SENSOR_SIZE - 1];
            for (int z = 0; z < SENSOR_SIZE - 1; z++)
            {
                for (int x = 0; x < SENSOR_SIZE - 1; x++)
                {
                    var mesh = new MeshGeometry3D();
                    var material = new DiffuseMaterial(new SolidColorBrush(Colors.Blue));
                    var model = new GeometryModel3D(mesh, material)
                    {
                        BackMaterial = material
                    };

                    mesh.TriangleIndices.Add(0);
                    mesh.TriangleIndices.Add(1);
                    mesh.TriangleIndices.Add(2);
                    mesh.TriangleIndices.Add(1);
                    mesh.TriangleIndices.Add(3);
                    mesh.TriangleIndices.Add(2);

                    meshGrid[z, x] = model;
                    meshGroup.Children.Add(model);
                }
            }
        }

        private int _gridUpdateCounter = 0;
        private const int UPDATE_GRID_FREQUENCY = 6; // 6프레임마다 한 번씩 격자 업데이트
        private void UpdateGrid(ushort[] data)
        {
            _gridUpdateCounter++;
            if (_gridUpdateCounter < UPDATE_GRID_FREQUENCY)
                return;

            _gridUpdateCounter = 0;

            var gridGeometry = new Model3DGroup();
            float scale = 1.0f / 1024;

            // 가로선
            for (double z = 0; z < SENSOR_SIZE - 1; z += GRID_SPACING * (SENSOR_SIZE - 1))
            {
                for (double x = 0; x < SENSOR_SIZE - 1; x += 0.5)
                {
                    float h1 = data[(int)z * SENSOR_SIZE + (int)x] * scale;
                    float h2 = data[(int)z * SENSOR_SIZE + (int)(x + 0.5)] * scale;

                    var p1 = new Point3D(
                        x / (SENSOR_SIZE - 1) * 2 - 1,
                        h1,
                        z / (SENSOR_SIZE - 1) * 2 - 1
                    );
                    var p2 = new Point3D(
                        (x + 0.5) / (SENSOR_SIZE - 1) * 2 - 1,
                        h2,
                        z / (SENSOR_SIZE - 1) * 2 - 1
                    );

                    var line = CreateGridLine(p1, p2);
                    gridGeometry.Children.Add(line);
                }
            }

            // 세로선
            for (double x = 0; x < SENSOR_SIZE - 1; x += GRID_SPACING * (SENSOR_SIZE - 1))
            {
                for (double z = 0; z < SENSOR_SIZE - 1; z += 0.5)
                {
                    float h1 = data[(int)z * SENSOR_SIZE + (int)x] * scale;
                    float h2 = data[(int)(z + 0.5) * SENSOR_SIZE + (int)x] * scale;

                    var p1 = new Point3D(
                        x / (SENSOR_SIZE - 1) * 2 - 1,
                        h1,
                        z / (SENSOR_SIZE - 1) * 2 - 1
                    );
                    var p2 = new Point3D(
                        x / (SENSOR_SIZE - 1) * 2 - 1,
                        h2,
                        (z + 0.5) / (SENSOR_SIZE - 1) * 2 - 1
                    );

                    var line = CreateGridLine(p1, p2);
                    gridGeometry.Children.Add(line);
                }
            }

            if (gridVisual.Content != null)
            {
                meshGroup.Children.Remove((Model3D)gridVisual.Content);
            }
            gridVisual.Content = gridGeometry;
            meshGroup.Children.Add(gridGeometry);
        }

        private void UpdateCenterOfPressure(ushort[] data)
        {
            double totalPressure = 0;
            double weightedX = 0;
            double weightedY = 0;

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

            if (totalPressure > 0)
            {
                centerOfPressure = new Point(
                    weightedX / totalPressure,
                    weightedY / totalPressure
                );

                double scaleX = HeatmapImage.ActualWidth / SENSOR_SIZE;
                double scaleY = HeatmapImage.ActualHeight / SENSOR_SIZE;

                // 현재 포인트의 화면 좌표 계산
                double screenX = centerOfPressure.X * scaleX;
                double screenY = centerOfPressure.Y * scaleY;

                // 현재 포인트 위치 업데이트
                Canvas.SetLeft(copIndicator, screenX - copIndicator.Width / 2);
                Canvas.SetTop(copIndicator, screenY - copIndicator.Height / 2);

                // 현재 압력이 임계값을 넘을 때만 궤적에 추가
                //if (totalPressure > 4608) // 임계값은 적절히 조정 필요
                if (totalPressure > 1000)
                {
                    // 궤적 히스토리 업데이트
                    copHistory.Enqueue(new Point(screenX, screenY));
                    while (copHistory.Count > MAX_TRAJECTORY_POINTS)
                    {
                        copHistory.Dequeue();
                    }

                    // 궤적 라인 업데이트
                    PointCollection points = new PointCollection(copHistory);
                    copTrajectory.Points = points;

                    // 궤적 색상 그라데이션 업데이트
                    if (copHistory.Count > 1)
                    {
                        var gradientStops = new GradientStopCollection();
                        for (int i = 0; i < copHistory.Count; i++)
                        {
                            // 최근 포인트일수록 더 선명하게
                            double opacity = (double)i / copHistory.Count * TRAJECTORY_OPACITY;
                            gradientStops.Add(new GradientStop(
                                Color.FromArgb(
                                    (byte)(255 * opacity),
                                    255, // Red
                                    255, // Green
                                    0   // Blue - Yellow color
                                ),
                                (double)i / (copHistory.Count - 1)
                            ));
                        }

                        copTrajectory.Stroke = new LinearGradientBrush
                        {
                            GradientStops = gradientStops,
                            StartPoint = new Point(0, 0),
                            EndPoint = new Point(1, 0)
                        };
                    }
                }
            }
        }

        private void UpdateBalanceGauges(ushort[] data)
        {
            double totalPressure = 0;
            double leftPressure = 0;
            double rightPressure = 0;
            double forefootPressure = 0;
            double heelPressure = 0;

            // 발 중앙선 (왼쪽/오른쪽 구분)
            int centerX = SENSOR_SIZE / 2;
            // 발 중앙선 (앞쪽/뒤쪽 구분) - 일반적으로 전체 길이의 40~60% 지점
            int centerY = SENSOR_SIZE / 2;

            // 무게중심이 계산되었다면 사용
            if (centerOfPressure.X > 0 && centerOfPressure.Y > 0)
            {
                centerX = (int)footCenter.X; // 발의 가로 중심선
                centerY = (int)footCenter.Y; // 발의 세로 중심선
            }

            // 좌우, 전후 압력 계산
            for (int y = 0; y < SENSOR_SIZE; y++)
            {
                for (int x = 0; x < SENSOR_SIZE; x++)
                {
                    double pressure = data[y * SENSOR_SIZE + x];

                    if (pressure < WEIGHT_THRESHOLD) // 너무 작은 값은 무시
                        continue;

                    totalPressure += pressure;

                    // 좌우 분리
                    if (x < centerX)
                    {
                        leftPressure += pressure;
                    }
                    else
                    {
                        rightPressure += pressure;
                    }

                    // 전후 분리
                    if (y < centerY)
                    {
                        forefootPressure += pressure; // 앞쪽 (forefoot)
                    }
                    else
                    {
                        heelPressure += pressure; // 뒤쪽 (heel)
                    }
                }
            }

            if (totalPressure > 0)
            {
                // 백분율 계산
                leftPercent = (leftPressure / totalPressure) * 100;
                rightPercent = (rightPressure / totalPressure) * 100;
                forefootPercent = (forefootPressure / totalPressure) * 100;
                heelPercent = (heelPressure / totalPressure) * 100;

                // 애니메이션 없이 게이지 높이/너비 직접 설정
                double maxHeight = LeftGauge.Parent is FrameworkElement leftParent ? leftParent.ActualHeight : 400;
                double maxWidth = ForefootGauge.Parent is FrameworkElement frontParent ? frontParent.ActualWidth / 2 : 400;

                // 왼쪽 게이지 업데이트
                LeftGauge.Height = maxHeight * leftPercent / 100;
                LeftPercentText.Text = $"{Math.Round(leftPercent)}%";

                // 오른쪽 게이지 업데이트
                RightGauge.Height = maxHeight * rightPercent / 100;
                RightPercentText.Text = $"{Math.Round(rightPercent)}%";

                // 앞쪽 게이지 업데이트
                ForefootGauge.Width = maxWidth * forefootPercent / 100;
                ForefootPercentText.Text = $"{Math.Round(forefootPercent)}%";

                // 뒤쪽 게이지 업데이트
                HeelGauge.Width = maxWidth * heelPercent / 100;
                HeelPercentText.Text = $"{Math.Round(heelPercent)}%";

                // 하단 좌우 균형 텍스트 업데이트
                LeftBalanceText.Text = $"{Math.Round(leftPercent)}";
                RightBalanceText.Text = $"{Math.Round(rightPercent)}";

                // 게이지 색상 업데이트 (편향성에 따라)
                UpdateGaugeColors(leftPercent, rightPercent, forefootPercent, heelPercent);
            }
        }

        private void UpdateGaugeColors(double leftPercent, double rightPercent,
                                      double forefootPercent, double heelPercent)
        {
            // 좌우 균형 색상 업데이트 (45-55% 범위는 녹색, 그 외는 경고색)
            Color leftColor, rightColor;

            if (Math.Abs(leftPercent - 50) <= 5)
            {
                leftColor = Colors.LimeGreen; // 밸런스가 좋음
            }
            else if (Math.Abs(leftPercent - 50) <= 15)
            {
                leftColor = Colors.Yellow; // 약간 불균형
            }
            else
            {
                leftColor = Colors.Red; // 심한 불균형
            }

            if (Math.Abs(rightPercent - 50) <= 5)
            {
                rightColor = Colors.LimeGreen;
            }
            else if (Math.Abs(rightPercent - 50) <= 15)
            {
                rightColor = Colors.Yellow;
            }
            else
            {
                rightColor = Colors.Red;
            }

            LeftGauge.Fill = new SolidColorBrush(leftColor);
            RightGauge.Fill = new SolidColorBrush(rightColor);

            // 전후 균형 색상 업데이트 (일반적으로 앞/뒤 40-60% 비율이 좋음)
            Color forefootColor, heelColor;

            if (Math.Abs(forefootPercent - 45) <= 15) // 30-60% 범위는 정상
            {
                forefootColor = Colors.DodgerBlue; // 정상 범위
            }
            else
            {
                forefootColor = Colors.Orange; // 불균형
            }

            if (Math.Abs(heelPercent - 55) <= 15) // 40-70% 범위는 정상
            {
                heelColor = Colors.DodgerBlue; // 정상 범위
            }
            else
            {
                heelColor = Colors.Orange; // 불균형
            }

            ForefootGauge.Fill = new SolidColorBrush(forefootColor);
            HeelGauge.Fill = new SolidColorBrush(heelColor);
        }

        public void UpdatePressureData(ushort[] data)
        {
            if (data == null || data.Length != SENSOR_SIZE * SENSOR_SIZE)
                return;

            lock (_dataLock)
            {
                Buffer.BlockCopy(data, 0, _latestData, 0, data.Length * sizeof(ushort));
                _dataUpdatedSinceLastRender = true;
            }
        }

        // 2D 렌더링 최적화 
        private unsafe void Update2D(ushort[] data)
        {
            try
            {
                heatmapBitmap.Lock();

                // 직접 메모리 조작으로 성능 향상
                IntPtr pBackBuffer = heatmapBitmap.BackBuffer;
                int stride = heatmapBitmap.BackBufferStride;

                // 이미 계산된, 자주 사용하는 색상 값의 룩업 테이블을 사용
                for (int y = 0; y < SENSOR_SIZE; y++)
                {
                    int* pDest = (int*)(pBackBuffer + y * stride);

                    for (int x = 0; x < SENSOR_SIZE; x++)
                    {
                        float normalizedValue = data[y * SENSOR_SIZE + x] / 1024.0f;
                        int colorIndex = Math.Min((int)(normalizedValue * (heatmapPalette.Length - 1)), heatmapPalette.Length - 1);
                        
                        //Color color = heatmapPalette[colorIndex];
                        // ARGB 값 계산 (알파는 255로 고정)
                        //*pDest++ = (255 << 24) | (color.R << 16) | (color.G << 8) | color.B;
                        *pDest++ = precomputedColors[colorIndex];
                    }
                }

                heatmapBitmap.AddDirtyRect(new Int32Rect(0, 0, SENSOR_SIZE, SENSOR_SIZE));
            }
            finally
            {
                heatmapBitmap.Unlock();
            }
        }

        // 3D 업데이트 최적화 - 성능을 위해 매 프레임마다 모든 셀을 업데이트하지 않음
        private int _3dUpdateCounter = 0;
        private const int UPDATE_3D_FREQUENCY = 3; // 3프레임마다 한 번씩 3D 업데이트
        private void Update3D(ushort[] data)
        {
            _3dUpdateCounter++;
            if (_3dUpdateCounter < UPDATE_3D_FREQUENCY)
                return;

            _3dUpdateCounter = 0;

            float scale = 1.0f / 1024;
            for (int z = 0; z < SENSOR_SIZE - 1; z++)
            {
                for (int x = 0; x < SENSOR_SIZE - 1; x++)
                {
                    UpdateMeshCell(x, z, data, scale);
                }
            }
        }

        private void UpdateMeshCell(int x, int z, ushort[] data, float scale)
        {
            var mesh = (MeshGeometry3D)meshGrid[z, x].Geometry;
            var material = (DiffuseMaterial)meshGrid[z, x].Material;

            // 높이값 계산
            float h00 = data[z * SENSOR_SIZE + x] * scale;
            float h10 = data[z * SENSOR_SIZE + (x + 1)] * scale;
            float h01 = data[(z + 1) * SENSOR_SIZE + x] * scale;
            float h11 = data[(z + 1) * SENSOR_SIZE + (x + 1)] * scale;

            // 평균 높이 계산
            float avgHeight = (h00 + h10 + h01 + h11) / 4.0f;

            // 정규화된 좌표
            float nx = x / (float)(SENSOR_SIZE - 1) * 2 - 1;
            float nz = z / (float)(SENSOR_SIZE - 1) * 2 - 1;
            float nx1 = (x + 1) / (float)(SENSOR_SIZE - 1) * 2 - 1;
            float nz1 = (z + 1) / (float)(SENSOR_SIZE - 1) * 2 - 1;

            // 메시 업데이트
            mesh.Positions.Clear();
            mesh.Positions.Add(new Point3D(nx, h00, nz));
            mesh.Positions.Add(new Point3D(nx1, h10, nz));
            mesh.Positions.Add(new Point3D(nx, h01, nz1));
            mesh.Positions.Add(new Point3D(nx1, h11, nz1));

            // 색상 업데이트
            int index = (int)(avgHeight * (heatmapPalette.Length - 1));
            Color color = avgHeight <= 0 ? Colors.Black : heatmapPalette[index];
            material.Brush = new SolidColorBrush(color);
        }

        private Color GetHeatmapColor(float value)
        {
            value = Math.Clamp(value, 0, 1);

            if (value < 0.2f)
                return Color.FromRgb(0, 0, (byte)(255 * (value / 0.2f)));
            else if (value < 0.4f)
                return Color.FromRgb(0, (byte)(255 * ((value - 0.2f) / 0.2f)), 255);
            else if (value < 0.6f)
                return Color.FromRgb((byte)(255 * ((value - 0.4f) / 0.2f)), 255, (byte)(255 * (1 - (value - 0.4f) / 0.2f)));
            else if (value < 0.8f)
                return Color.FromRgb(255, (byte)(255 * (1 - (value - 0.6f) / 0.2f)), 0);
            else
                return Color.FromRgb(255, 0, 0);
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            camera.Position = initialPosition;
            camera.LookDirection = initialLookDirection;
            camera.UpDirection = initialUpDirection;
            rotationAngle = 0;
            cameraDistance = Math.Sqrt(INITIAL_POSITION_Y * INITIAL_POSITION_Y + INITIAL_POSITION_Z * INITIAL_POSITION_Z);
            cameraHeight = INITIAL_POSITION_Z;
            UpdateCameraPosition();
        }

        private void RotateLeft_Click(object sender, RoutedEventArgs e)
        {
            rotationAngle -= 15;
            UpdateCameraPosition();
        }

        private void RotateRight_Click(object sender, RoutedEventArgs e)
        {
            rotationAngle += 15;
            UpdateCameraPosition();
        }

        private void UpdateCameraPosition()
        {
            double angleRad = rotationAngle * Math.PI / 180.0;
            double x = cameraDistance * Math.Sin(angleRad);
            double y = cameraDistance * Math.Cos(angleRad);

            camera.Position = new Point3D(x, y, cameraHeight);
            camera.LookDirection = new Vector3D(-x, -y, -cameraHeight);
            camera.UpDirection = initialUpDirection;
        }
    }
}