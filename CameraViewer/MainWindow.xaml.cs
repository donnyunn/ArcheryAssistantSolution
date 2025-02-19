using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using SharpDX.Direct3D9;
using Surface = SharpDX.Direct3D9.Surface;

namespace CameraViewer
{
    public class D3DImageSource : D3DImage, IDisposable
    {
        private Direct3DEx _d3dContext;
        private DeviceEx _device;
        private Surface _surface;
        private readonly int _width;
        private readonly int _height;

        public D3DImageSource(int width, int height)
        {
            _width = width;
            _height = height;
            InitializeD3D();
        }

        private void InitializeD3D()
        {
            try
            {
                _d3dContext = new Direct3DEx();

                // 프레젠테이션 파라미터 설정
                var presentParams = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    DeviceWindowHandle = new IntPtr(0),
                    BackBufferFormat = Format.A8R8G8B8,
                    BackBufferWidth = _width,
                    BackBufferHeight = _height,
                    BackBufferCount = 1,
                    PresentationInterval = PresentInterval.Default
                };

                _device = new DeviceEx(_d3dContext,
                                     0,
                                     DeviceType.Hardware,
                                     IntPtr.Zero,
                                     CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                                     presentParams);

                // 렌더링 서피스 생성 시 디버그 정보 추가
                Console.WriteLine($"서피스 생성 시도: {_width}x{_height}");
                _surface = Surface.CreateRenderTarget(_device,
                                                    _width,
                                                    _height,
                                                    Format.A8R8G8B8,  // BGRA32 포맷
                                                    MultisampleType.None,
                                                    0,
                                                    false);
                Console.WriteLine("서피스 생성 성공");

                // D3DImage에 백버퍼 설정
                Lock();
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.NativePointer);
                Unlock();

                Console.WriteLine("D3D9 초기화 성공");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"D3D9 초기화 실패: {ex}");
                throw;
            }
        }

        public void UpdateFrame(Mat frame)
        {
            if (frame == null || _surface == null) return;

            try
            {
                // OpenCV의 BGR24 포맷을 BGRA32로 변환
                using (var bgra = new Mat())
                {
                    Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);

                    var rect = _surface.LockRectangle(LockFlags.None);

                    unsafe
                    {
                        byte* srcPtr = (byte*)bgra.DataStart;
                        byte* dstPtr = (byte*)rect.DataPointer;
                        int srcStride = (int)bgra.Step();
                        int dstStride = rect.Pitch;

                        for (int y = 0; y < bgra.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                srcPtr + (y * srcStride),
                                dstPtr + (y * dstStride),
                                dstStride,
                                srcStride);
                        }
                    }

                    _surface.UnlockRectangle();
                }

                // 화면 갱신
                Lock();
                AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
                Unlock();

                System.Diagnostics.Debug.WriteLine("프레임 업데이트 성공");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"프레임 업데이트 실패: {ex}");
            }
        }
        public void Dispose()
        {
            _surface?.Dispose();
            _device?.Dispose();
            _d3dContext?.Dispose();
        }
    }

    public partial class MainWindow : System.Windows.Window
    {
        private WriteableBitmap _writeableBitmap;
        public MainWindow()
        {
            InitializeComponent();
        }

        public void UpdateFrame(Mat frame)
        {
            if (frame == null) return;

            // WriteableBitmap 초기화 (최초 1회)
            if (_writeableBitmap == null)
            {
                Dispatcher.Invoke(() =>
                {
                    _writeableBitmap = new WriteableBitmap(
                        frame.Width, frame.Height,
                        96, 96,
                        PixelFormats.Bgr24, null);
                    DisplayImage.Source = _writeableBitmap;
                });
            }

            // 프레임 데이터 업데이트
            Dispatcher.Invoke(() =>
            {
                _writeableBitmap.Lock();

                unsafe
                {
                    var backBuffer = (byte*)_writeableBitmap.BackBuffer;
                    var stride = _writeableBitmap.BackBufferStride;
                    var dataPointer = (byte*)frame.DataStart;
                    var dataStride = frame.Step();

                    for (int y = 0; y < frame.Height; y++)
                    {
                        Buffer.MemoryCopy(
                            dataPointer + (y * dataStride),
                            backBuffer + (y * stride),
                            stride,
                            dataStride);
                    }
                }

                _writeableBitmap.AddDirtyRect(
                    new Int32Rect(0, 0, frame.Width, frame.Height));
                _writeableBitmap.Unlock();
            });
        }
    }
}