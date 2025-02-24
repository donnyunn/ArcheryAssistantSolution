using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PressureMapViewer
{
    public partial class MainWindow : Window
    {
        private const int SENSOR_SIZE = 96;
        private const int FPS_2D = 30;
        private const int FPS_3D = 30;

        // 2D 렌더링 관련
        private WriteableBitmap heatmapBitmap;
        private int[] colorBuffer;
        private DateTime last2DUpdate = DateTime.MinValue;

        // 3D 렌더링 관련
        private Model3DGroup meshGroup;
        private ModelVisual3D modelVisual;
        private GeometryModel3D[,] meshGrid;
        private DateTime last3DUpdate = DateTime.MinValue;
        
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

        public MainWindow()
        {
            InitializeComponent();
            InitializeRendering();
            InitializeHeatmapPalette();
        }

        private void InitializeHeatmapPalette()
        {
            for (int i = 0; i < heatmapPalette.Length; i++)
            {
                float value = (float)i / (heatmapPalette.Length - 1);
                heatmapPalette[i] = GetHeatmapColor(value);
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

            // 렌더링 최적화 설정
            RenderOptions.SetBitmapScalingMode(viewport3D, BitmapScalingMode.LowQuality);
            RenderOptions.SetEdgeMode(viewport3D, EdgeMode.Aliased);

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

        public void UpdatePressureData(ushort[] data)
        {
            if (data == null || data.Length != SENSOR_SIZE * SENSOR_SIZE)
                return;

            var now = DateTime.Now;

            // 2D 업데이트 (60fps)
            if ((now - last2DUpdate).TotalMilliseconds > 1000.0 / FPS_2D)
            {
                Update2D(data);
                last2DUpdate = now;
            }

            // 3D 업데이트 (30fps)
            if ((now - last3DUpdate).TotalMilliseconds > 1000.0 / FPS_3D)
            {
                // 데이터 복사
                //var dataCopy = new ushort[data.Length];
                //Array.Copy(data, dataCopy, data.Length);

                // UI 스레드에서 실행
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Update3D(data);
                }), System.Windows.Threading.DispatcherPriority.Render);

                last3DUpdate = now;
            }
        }

        private void Update2D(ushort[] data)
        {
            try
            {
                // 색상 버퍼 업데이트
                Parallel.For(0, data.Length, i =>
                {
                    float normalizedValue = data[i] / 1024.0f;
                    // 룩업 테이블에서 색상 가져오기
                    int index = (int)(normalizedValue * (heatmapPalette.Length - 1));
                    //Color color = GetHeatmapColor(normalizedValue);
                    Color color = heatmapPalette[index];
                    colorBuffer[i] = (color.R << 16) | (color.G << 8) | color.B;
                });

                // 비트맵 업데이트
                heatmapBitmap.Lock();
                Marshal.Copy(colorBuffer, 0, heatmapBitmap.BackBuffer, colorBuffer.Length);
                heatmapBitmap.AddDirtyRect(new Int32Rect(0, 0, SENSOR_SIZE, SENSOR_SIZE));
            }
            finally
            {
                heatmapBitmap.Unlock();
            }
        }

        private void Update3D(ushort[] data)
        {
            //float maxValue = 0;
            //for (int i = 0; i < data.Length; i++)
            //{
            //    maxValue = Math.Max(maxValue, data[i]);
            //}
            //float scale = 1.0f / maxValue;
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
            float avgHeight = (h00 + h10 + h01 + h11) / 4.0f;
            Color color = avgHeight <= 0 ? Colors.Black : GetHeatmapColor(avgHeight);
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