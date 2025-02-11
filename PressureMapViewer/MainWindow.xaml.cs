using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Linq;

namespace PressureMapViewer
{
    public partial class MainWindow : Window
    {
        private const int SensorSize = 96;
        private const double INITIAL_POSITION_Y = 3.0;
        private const double INITIAL_POSITION_Z = 3.0;
        private const double INITIAL_LOOK_Y = -1.0;
        private const double INITIAL_LOOK_Z = -1.0;

        private WriteableBitmap heatmapBitmap;
        private double rotationAngle = 0;
        private double cameraDistance = Math.Sqrt(INITIAL_POSITION_Y * INITIAL_POSITION_Y + INITIAL_POSITION_Z * INITIAL_POSITION_Z);
        private double cameraHeight = INITIAL_POSITION_Z;
        private Point lastMousePosition;
        private bool isMouseDragging = false;

        private Point3D initialPosition;
        private Vector3D initialLookDirection;
        private Vector3D initialUpDirection;

        public MainWindow()
        {
            InitializeComponent();
            InitializeHeatmap();
            Initialize3DView();

            // 초기 카메라 위치 설정
            initialPosition = new Point3D(0, INITIAL_POSITION_Y, INITIAL_POSITION_Z);
            initialLookDirection = new Vector3D(0, INITIAL_LOOK_Y, INITIAL_LOOK_Z);
            initialUpDirection = new Vector3D(0, 1, 0);

            // 초기 카메라 위치 설정
            camera.Position = initialPosition;
            camera.LookDirection = initialLookDirection;
            camera.UpDirection = initialUpDirection;
        }

        private void InitializeHeatmap()
        {
            heatmapBitmap = new WriteableBitmap(
                SensorSize, SensorSize,
                96, 96,
                PixelFormats.Bgr32,
                null);

            HeatmapImage.Source = heatmapBitmap;
        }

        private void Initialize3DView()
        {
            // 마우스 이벤트 핸들러
            viewport3D.MouseWheel += Viewport3D_MouseWheel;
            viewport3D.MouseLeftButtonDown += Viewport3D_MouseLeftButtonDown;
            viewport3D.MouseLeftButtonUp += Viewport3D_MouseLeftButtonUp;
            viewport3D.MouseMove += Viewport3D_MouseMove;
        }

        public void UpdatePressureData(ushort[] data)
        {
            if (data == null || data.Length != SensorSize * SensorSize)
                return;

            // 2D 히트맵 업데이트
            Update2DHeatmap(data);

            // 3D 메시 업데이트
            Update3DMesh(data);
        }

        private void Update2DHeatmap(ushort[] data)
        {
            try
            {
                heatmapBitmap.Lock();

                unsafe
                {
                    IntPtr pBackBuffer = heatmapBitmap.BackBuffer;
                    int stride = heatmapBitmap.BackBufferStride;

                    Parallel.For(0, SensorSize, y =>
                    {
                        for (int x = 0; x < SensorSize; x++)
                        {
                            int index = y * SensorSize + x;
                            double value = data[index] / 1024.0;
                            Color color = GetHeatmapColor(value);
                            int colorData = (color.R << 16) | (color.G << 8) | (color.B);
                            *((int*)pBackBuffer + y * stride / 4 + x) = colorData;
                        }
                    });
                }

                heatmapBitmap.AddDirtyRect(new Int32Rect(0, 0, SensorSize, SensorSize));
            }
            finally
            {
                heatmapBitmap.Unlock();
            }
        }

        private void Update3DMesh(ushort[] data)
        {
            var geometryGroup = new Model3DGroup();
            double maxPressure = data.Max();
            double scale = 1.0 / maxPressure;

            // 포인트 배열 생성 및 스무딩을 위한 데이터 처리
            double[,] smoothedHeights = new double[SensorSize, SensorSize];
            for (int z = 0; z < SensorSize; z++)
            {
                for (int x = 0; x < SensorSize; x++)
                {
                    // 주변 포인트들의 평균으로 스무딩
                    double sum = 0;
                    int count = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int newX = x + dx;
                            int newZ = z + dz;
                            if (newX >= 0 && newX < SensorSize && newZ >= 0 && newZ < SensorSize)
                            {
                                sum += data[newZ * SensorSize + newX] * scale;
                                count++;
                            }
                        }
                    }
                    smoothedHeights[x, z] = sum / count;
                }
            }

            // 각 격자별로 메시 생성
            for (int z = 0; z < SensorSize - 1; z++)
            {
                for (int x = 0; x < SensorSize - 1; x++)
                {
                    var cellMesh = new MeshGeometry3D();

                    double normalizedX = x / (double)(SensorSize - 1) * 2 - 1;
                    double normalizedZ = z / (double)(SensorSize - 1) * 2 - 1;
                    double nextX = (x + 1) / (double)(SensorSize - 1) * 2 - 1;
                    double nextZ = (z + 1) / (double)(SensorSize - 1) * 2 - 1;

                    // 현재 격자의 4개 정점에 대한 높이
                    double h00 = smoothedHeights[x, z];
                    double h10 = smoothedHeights[x + 1, z];
                    double h01 = smoothedHeights[x, z + 1];
                    double h11 = smoothedHeights[x + 1, z + 1];

                    // 격자의 4개 정점 추가
                    cellMesh.Positions.Add(new Point3D(normalizedX, h00, normalizedZ));
                    cellMesh.Positions.Add(new Point3D(nextX, h10, normalizedZ));
                    cellMesh.Positions.Add(new Point3D(normalizedX, h01, nextZ));
                    cellMesh.Positions.Add(new Point3D(nextX, h11, nextZ));

                    // 법선 벡터 추가
                    for (int i = 0; i < 4; i++)
                    {
                        cellMesh.Normals.Add(new Vector3D(0, 1, 0));
                    }

                    // 삼각형 인덱스 추가
                    cellMesh.TriangleIndices.Add(0);
                    cellMesh.TriangleIndices.Add(1);
                    cellMesh.TriangleIndices.Add(2);
                    cellMesh.TriangleIndices.Add(1);
                    cellMesh.TriangleIndices.Add(3);
                    cellMesh.TriangleIndices.Add(2);

                    // 격자의 평균 높이로 색상 결정
                    double avgHeight = (h00 + h10 + h01 + h11) / 4.0;
                    Color cellColor = GetHeatmapColor(avgHeight);

                    var material = new MaterialGroup();
                    material.Children.Add(new DiffuseMaterial(new SolidColorBrush(cellColor)));
                    material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), 50));

                    var cellGeometry = new GeometryModel3D(cellMesh, material);
                    geometryGroup.Children.Add(cellGeometry);
                }
            }

            // 모델 업데이트
            var modelVisual = new ModelVisual3D();
            modelVisual.Content = geometryGroup;

            // 뷰포트 업데이트
            viewport3D.Children.Clear();
            viewport3D.Children.Add(modelVisual);

            // 조명 추가
            var lightsVisual = new ModelVisual3D();
            lightsVisual.Content = new Model3DGroup
            {
                Children =
        {
            new AmbientLight(Color.FromRgb(100, 100, 100)),
            new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1)),
            new DirectionalLight(Colors.White, new Vector3D(1, -1, 1))
        }
            };
            viewport3D.Children.Add(lightsVisual);
        }

        private Color GetHeatmapColor(double value)
        {
            value = Math.Clamp(value, 0, 1);

            if (value < 0.2)
                return Color.FromRgb(0, 0, (byte)(255 * (value / 0.2)));
            else if (value < 0.4)
                return Color.FromRgb(0, (byte)(255 * ((value - 0.2) / 0.2)), 255);
            else if (value < 0.6)
                return Color.FromRgb((byte)(255 * ((value - 0.4) / 0.2)), 255, (byte)(255 * (1 - (value - 0.4) / 0.2)));
            else if (value < 0.8)
                return Color.FromRgb(255, (byte)(255 * (1 - (value - 0.6) / 0.2)), 0);
            else
                return Color.FromRgb(255, 0, 0);
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
        private void Viewport3D_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 0.9 : 1.1;
            cameraDistance = Math.Max(2.0, Math.Min(10.0, cameraDistance * zoomFactor));
            UpdateCameraPosition();
        }

        private void Viewport3D_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            viewport3D.CaptureMouse();
            lastMousePosition = e.GetPosition(viewport3D);
            isMouseDragging = true;
        }

        private void Viewport3D_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            viewport3D.ReleaseMouseCapture();
            isMouseDragging = false;
        }

        private void Viewport3D_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!isMouseDragging) return;

            Point currentPosition = e.GetPosition(viewport3D);
            double deltaX = currentPosition.X - lastMousePosition.X;
            double deltaY = currentPosition.Y - lastMousePosition.Y;

            rotationAngle += deltaX;
            cameraHeight = Math.Max(1.0, Math.Min(8.0, cameraHeight - deltaY * 0.05));

            UpdateCameraPosition();
            lastMousePosition = currentPosition;
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            // 저장된 초기값으로 정확히 복원
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
        private void CalculateNormals(MeshGeometry3D mesh)
        {
            // 법선 벡터 초기화
            Vector3D[] normals = new Vector3D[mesh.Positions.Count];

            // 각 삼각형에 대해 법선 벡터 계산
            for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
            {
                int index1 = mesh.TriangleIndices[i];
                int index2 = mesh.TriangleIndices[i + 1];
                int index3 = mesh.TriangleIndices[i + 2];

                Point3D position1 = mesh.Positions[index1];
                Point3D position2 = mesh.Positions[index2];
                Point3D position3 = mesh.Positions[index3];

                Vector3D v1 = position2 - position1;
                Vector3D v2 = position3 - position1;
                Vector3D normal = Vector3D.CrossProduct(v1, v2);

                normals[index1] += normal;
                normals[index2] += normal;
                normals[index3] += normal;
            }

            // 법선 벡터 정규화
            for (int i = 0; i < normals.Length; i++)
            {
                if (normals[i].Length > 0)
                {
                    normals[i].Normalize();
                    mesh.Normals.Add(normals[i]);
                }
                else
                {
                    // 법선 벡터가 0인 경우 기본값 설정
                    mesh.Normals.Add(new Vector3D(0, 1, 0));
                }
            }
        }
    }
}