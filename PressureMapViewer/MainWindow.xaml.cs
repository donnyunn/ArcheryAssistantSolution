using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PressureMapViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private WriteableBitmap heatmapBitmap;
        private const int SensorSize = 48;
        private const int DisplaySize = 480;

        public MainWindow()
        {
            InitializeComponent();
            InitializeHeatmap();
        }

        private void InitializeHeatmap()
        {
            heatmapBitmap = new WriteableBitmap(
                DisplaySize, DisplaySize,
                96, 96,
                PixelFormats.Bgr32,
                null);

            var image = new Image
            {
                Source = heatmapBitmap,
                Stretch = Stretch.None
            };
            HeatmapCanvas.Children.Add(image);
        }

        public void UpdatePressureData(ushort[] data)
        {
            if (data == null || data.Length != SensorSize * SensorSize)
                return;

            try
            {
                heatmapBitmap.Lock();

                unsafe
                {
                    IntPtr pBackBuffer = heatmapBitmap.BackBuffer;
                    int stride = heatmapBitmap.BackBufferStride;
                    int scale = DisplaySize / SensorSize;

                    Parallel.For(0, SensorSize, y =>
                    {
                        for (int x = 0; x < SensorSize; x++)
                        {
                            int index = y * SensorSize + x;
                            double value = data[index] / 1024.0;
                            Color color = GetHeatmapColor(value);
                            int colorData = (color.R << 16) | (color.G << 8) | (color.B);

                            int baseY = y * scale;
                            int baseX = x * scale;
                            for (int dy = 0; dy < scale; dy++)
                            {
                                int rowOffset = (baseY + dy) * stride / 4;
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    *((int*)pBackBuffer + rowOffset + (baseX + dx)) = colorData;
                                }
                            }
                        }
                    });

                    //for (int y = 0; y < SensorSize; y++)
                    //{
                    //    for (int x = 0; x < SensorSize; x++)
                    //    {
                    //        int index = y * SensorSize + x;
                    //        double value = data[index] / 1024.0;
                    //        Color color = GetHeatmapColor(value);
                    //        int colorData = (color.R << 16) | (color.G << 8) | color.B;

                    //        //*((int*)pBackBuffer + y * stride / 4 + x) = colorData;
                    //        // 확대하여 적용
                    //        for (int dy = 0; dy < scale; dy++)
                    //        {
                    //            for (int dx = 0; dx < scale; dx++)
                    //            {
                    //                int newX = x * scale + dx;
                    //                int newY = y * scale + dy;
                    //                *((int*)pBackBuffer + newY * stride / 4 + newX) = colorData;
                    //            }
                    //        }
                    //    }
                    //}
                }

                heatmapBitmap.AddDirtyRect(new Int32Rect(0, 0, DisplaySize, DisplaySize));
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
    }
}