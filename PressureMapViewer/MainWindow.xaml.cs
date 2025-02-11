using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PressureMapViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int SensorSize = 96;
        private WriteableBitmap heatmapBitmap;
        private MeshGeometry3D meshGeometry;
        private DiffuseMaterial meshMaterial;
        private GeometryModel3D geometryModel;
        private double rotationAngle = 0;

        private Point lastMousePosition;
        private bool isMouseDragging = false;
        private double cameraDistance = 5.0;
        private double cameraHeight = 4.0;

        public MainWindow()
        {
            InitializeComponent();
            InitializeHeatmap();
            Initialize3DView();
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
            meshGeometry = new MeshGeometry3D();
            meshMaterial = new DiffuseMaterial(new SolidColorBrush(Colors.Blue));
            geometryModel = new GeometryModel3D(meshGeometry, meshMaterial);

            var modelVisual = new ModelVisual3D();
            modelVisual.Content = geometryModel;
            viewport3D.Children.Add(modelVisual);

            // 마우스 조작 이벤트 추가
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

        private Color GetHeatmapColor(double value)
        {
            return Color.FromRgb(
                (byte)(value * 255),  // R
                0,                    // G
                (byte)((1 - value) * 255) // B
            );
        }

        private void Update3DMesh(ushort[] data)
        {
            var positions = new Point3DCollection();
            var triangleIndices = new Int32Collection();
            var colors = new List<Color>();

            // Nomalized factor
            double maxPressure = data.Max();
            double scale = 1.0 / maxPressure;

            // vertical axises
            for (int z = 0; z < SensorSize; z++)
            {
                for (int x = 0; x < SensorSize; x++)
                {
                    double normalizedX = x / (double)(SensorSize - 1) * 2 - 1;
                    double normalizedZ = z / (double)(SensorSize - 1) * 2 - 1;
                    double height = data[z * SensorSize + x] * scale;

                    positions.Add(new Point3D(normalizedX, height, normalizedZ));

                    Color color = GetHeightColor(height);
                    colors.Add(color);
                }
            }

            // triangle index
            for (int z = 0; z < SensorSize - 1; z++)
            {
                for (int x = 0; x < SensorSize - 1; x++)
                {
                    int baseIndex = z * SensorSize + x;

                    triangleIndices.Add(baseIndex);
                    triangleIndices.Add(baseIndex + SensorSize);
                    triangleIndices.Add(baseIndex + 1);

                    triangleIndices.Add(baseIndex + 1);
                    triangleIndices.Add(baseIndex + SensorSize);
                    triangleIndices.Add(baseIndex + SensorSize + 1);
                }
            }

            // update mesh
            meshGeometry.Positions = positions;
            meshGeometry.TriangleIndices = triangleIndices;

            // update material
            var gradientBrush = new LinearGradientBrush();
            gradientBrush.StartPoint = new Point(0, 0);
            gradientBrush.EndPoint = new Point(1, 1);
            foreach (var color in colors.Distinct())
            {
                gradientBrush.GradientStops.Add(new GradientStop(color, colors.IndexOf(color) / (double)colors.Count));
            }
            meshMaterial.Brush = gradientBrush;
        }

        private Color GetHeightColor(double height)
        {
            // color mapping by height
            if (height < 0.2) return Colors.Blue;
            if (height < 0.4) return Colors.Green;
            if (height < 0.6) return Colors.Yellow;
            if (height < 0.8) return Colors.Orange;
            return Colors.Red;
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

        private void Viewport3D_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 줌 인/아웃 - 카메라 거리 조절
            double zoomFactor = e.Delta > 0 ? 0.9 : 1.1;
            cameraDistance = Math.Max(2.0, Math.Min(10.0, cameraDistance * zoomFactor));
            UpdateCameraPosition();
        }

        private void Viewport3D_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            viewport3D.CaptureMouse();
            lastMousePosition = e.GetPosition(viewport3D);
            isMouseDragging = true;
        }

        private void Viewport3D_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            viewport3D.ReleaseMouseCapture();
            isMouseDragging = false;
        }

        private void Viewport3D_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isMouseDragging) return;

            Point currentPosition = e.GetPosition(viewport3D);
            double deltaX = currentPosition.X - lastMousePosition.X;
            double deltaY = currentPosition.Y - lastMousePosition.Y;

            // 회전 각도 업데이트
            rotationAngle += deltaX;
            cameraHeight = Math.Max(1.0, Math.Min(8.0, cameraHeight - deltaY * 0.05));

            UpdateCameraPosition();
            lastMousePosition = currentPosition;
        }

        private void UpdateCameraPosition()
        {
            // 구면 좌표계를 사용하여 카메라 위치 계산
            double angleRad = rotationAngle * Math.PI / 180.0;
            double x = cameraDistance * Math.Cos(angleRad);
            double y = cameraDistance * Math.Sin(angleRad);

            camera.Position = new Point3D(x, y, cameraHeight);
            camera.LookDirection = new Vector3D(-x, -y, -cameraHeight);
            camera.UpDirection = new Vector3D(0, 0, 1);
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            // 카메라 위치 초기화
            rotationAngle = 0;
            cameraDistance = 5.0;
            cameraHeight = 4.0;
            UpdateCameraPosition();
        }
    }
}