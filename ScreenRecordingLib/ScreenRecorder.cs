using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.DXGI;
using Vortice.Direct3D11;
using SkiaSharp;
using System.Diagnostics;

namespace ScreenRecordingLib
{
    public class RecordingSettings
    {
        public bool UseDesktop { get; set; }
        public bool UseVerticalLayout { get; set; }
        public string SelectedDrive { get; set; }
        public int Width { get; set; } = 960;
        public int Height { get; set; } = 540;

        public string GetOutputPath()
        {
            if (UseDesktop)
                return Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\recording.mp4";
            else if (!string.IsNullOrEmpty(SelectedDrive))
                return SelectedDrive + "recording.mp4";
            throw new ArgumentException("Invalid output path.");
        }
    }

    public class ScreenRecorder
    {
        private RecordingSettings _settings;
        private bool _isRecording; 
        private Thread _captureThread; 
        private IntPtr[] _windowHandles; // 창 핸들 배열
        private Process _ffmpegProcess;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        public void Initialize(RecordingSettings settings, IntPtr[] windowHandles)
        {
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this._windowHandles = windowHandles ?? throw new ArgumentNullException(nameof(windowHandles));
            if (_windowHandles.Length == 0) throw new ArgumentException("At least one window handle is required.");
            _isRecording = false;
            Console.WriteLine($"Initialized with: Desktop={settings.UseDesktop}, Vertical={settings.UseVerticalLayout}, Drive={settings.SelectedDrive}");
        }

        public void StartRecording()
        {
            if (!_isRecording)
            {
                _isRecording = true;
                Console.WriteLine("Recording started.");
                // 이후 DXGI 캡처 로직 추가 예정
                _captureThread = new Thread(CaptureLoop);
                _captureThread.Start();
            }
        }

        public void StopRecording()
        {
            if (_isRecording)
            {
                _isRecording = false;
                _captureThread?.Join();
                if (_ffmpegProcess != null)
                {
                    _ffmpegProcess.StandardInput.WriteLine("q");
                    _ffmpegProcess.WaitForExit();
                    _ffmpegProcess.Close();
                }
                Console.WriteLine("Recording stopped.");
            }
        }

        private void CaptureLoop()
        {
            try
            {
                using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
                {
                    var adapter = factory.GetAdapter1(0);
                    IntPtr monitorHandle = MonitorFromWindow(_windowHandles[0], MONITOR_DEFAULTTONEAREST);
                    IDXGIOutput targetOutput = null;
                    for (int i = 0; i < adapter.GetOutputCount(); i++)
                    {
                        var output = adapter.GetOutput(i);
                        if (output.Description.Monitor == monitorHandle)
                        {
                            targetOutput = output;
                            break;
                        }
                    }

                    if (targetOutput == null)
                    {
                        Console.WriteLine("No matching output found.");
                        return;
                    }

                    var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
                    var d3dDevice = Direct3D11.D3D11CreateDevice(adapter, Vortice.Direct3D.DriverType.Hardware);
                    using (var duplication = output1.DuplicateOutput(d3dDevice))
                    {
                        _ffmpegProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "ffmpeg",
                                Arguments = $"-f rawvideo -pix_fmt rgba -s {_settings.Width}x{_settings.Height} -r 60 -i pipe: -c:v libx264 -preset ultrafast {_settings.GetOutputPath()}",
                                UseShellExecute = false,
                                RedirectStandardInput = true,
                                CreateNoWindow = true
                            }
                        };
                        _ffmpegProcess.Start();

                        while (_isRecording)
                        {
                            duplication.AcquireNextFrame(500, out DXGIOutduplFrameInfo frameInfo, out IDXGIResource resource);
                            using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                            {
                                ProcessFrame(texture);
                            }
                            duplication.ReleaseFrame();
                            Thread.Sleep(16);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Capture error: {ex.Message}");
            }
        }

        private void ProcessFrame(ID3D11Texture2D texture)
        {
            var desc = texture.Description;
            var device = texture.Device;

            var stagingDesc = desc;
            stagingDesc.BindFlags = 0;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            using (var stagingTexture = device.CreateTexture2D(stagingDesc))
            {
                device.ImmediateContext.CopyResource(texture, stagingTexture);
                var data = device.ImmediateContext.Map(stagingTexture, 0, MapMode.Read);

                GetWindowRect(_windowHandles[0], out RECT rect);
                int windowWidth = rect.Right - rect.Left;
                int windowHeight = rect.Bottom - rect.Top;
                int windowX = rect.Left;
                int windowY = rect.Top;

                using (var bitmap = new SKBitmap(_settings.Width, _settings.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                {
                    unsafe
                    {
                        byte* src = (byte*)data.DataPointer;
                        byte* dst = (byte*)bitmap.GetPixels();
                        for (int y = 0; y < _settings.Height && (y + windowY) < desc.Height; y++)
                        {
                            int srcOffset = (y + windowY) * data.RowPitch + windowX * 4;
                            int dstOffset = y * _settings.Width * 4;
                            if (srcOffset + _settings.Width * 4 <= data.RowPitch * desc.Height)
                            {
                                Buffer.MemoryCopy(src + srcOffset, dst + dstOffset, _settings.Width * 4, _settings.Width * 4);
                            }
                        }
                    }

                    var dataSpan = bitmap.GetPixelSpan();
                    _ffmpegProcess.StandardInput.BaseStream.Write(dataSpan);
                    _ffmpegProcess.StandardInput.BaseStream.Flush();
                }
                device.ImmediateContext.Unmap(stagingTexture, 0);
            }
        }

        public bool IsRecording => _isRecording;
    }
}
