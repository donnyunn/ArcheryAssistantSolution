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
        private ushort[]? _pendingData;
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
        private const double INITIAL_POSITION_Y = 2.0;
        private const double INITIAL_POSITION_Z = 2.0;
        private double rotationAngle = 45;
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

        // 1. 스로틀링 타이머 추가 (클래스 멤버 변수로)
        private System.Windows.Threading.DispatcherTimer _updateThrottleTimer;
        private ushort[] _latestData;
        private bool _dataUpdatedSinceLastRender = false;
        private const int THROTTLE_INTERVAL_MS = 33; // 약 30fps로 제한

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

        private int[] precomputedColors; // BGR32 포맷으로 미리 계산된 색상
        private void InitializeHeatmapPalette()
        {
            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                float value = (float)i / (heatmapPalette.Length - 1);
                heatmapPalette[i] = GetHeatmapColor(value);
            }

            // 추가: BGR32 형식으로 미리 계산
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
                if (totalPressure > 4608) // 임계값은 적절히 조정 필요
                {
                    // 궤적 히스토리 업데이트
                    copHistory.Enqueue(new Point(screenX, screenY));
                    while (copHistory.Count > MAX_TRAJECTORY_POINTS)
                    {
                        copHistory.Dequeue();
                    }

                    // 궤적 라인 업데이트
                    var points = new PointCollection(copHistory);
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

        public void UpdatePressureData(ushort[] data)
        {
            if (data == null || data.Length != SENSOR_SIZE * SENSOR_SIZE)
                return;

            lock (_dataLock)
            {
                //_pendingData = data;
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
                        Color color = heatmapPalette[colorIndex];

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