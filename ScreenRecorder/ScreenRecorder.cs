using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.DXGI;
using Vortice.Direct3D11;
using SkiaSharp;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.IO.Pipes;

namespace ScreenRecordingLib
{
    public class RecordingSettings
    {
        public bool UseDesktop { get; set; }
        public bool UseVerticalLayout { get; set; }
        public string SelectedDrive { get; set; }
        public int Width { get; set; } = 960;
        public int Height { get; set; } = 540;
        public int FrameRate { get; set; } = 60;
        public string GetOutputPath()
        {
            if (UseDesktop)
                return Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\LastReplay.mp4";
            else if (!string.IsNullOrEmpty(SelectedDrive))
                return SelectedDrive + "LastReplay.mp4";
            throw new ArgumentException("Invalid output path.");
        }
    }

    public class ScreenRecorder
    {
        // Win32 API declarations for window capture
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        // Private fields
        private RecordingSettings _settings;
        private bool _isRecording;
        private Thread _captureThread;
        private IntPtr[] _windowHandles;
        private Process _ffmpegProcess;
        private NamedPipeServerStream _pipeServer;
        private BinaryWriter _pipeWriter;
        private ID3D11Device _d3dDevice;
        private ID3D11DeviceContext _d3dContext;
        private IDXGIOutputDuplication _deskDupl;
        private Stopwatch _frameTimer;
        private readonly object _lockObject = new object();
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // �޽��� ������ ���� �̺�Ʈ �߰�
        public delegate void MessageEventHandler(string message);
        public event MessageEventHandler OnStatusMessage;

        // ���� ���� ���� ��� �� �ʵ�
        private const int FILE_SPLIT_INTERVAL_MINUTES = 1; // ���� ���� ����(��)
        private DateTime _recordingStartTime;
        private DateTime _lastFileSplitTime;
        private string _currentOutputPath;
        private int _segmentCounter = 1;

        /// <summary>
        /// �ʱ�ȭ �Լ�: ������ ĸó�� â �ڵ��� ����
        /// </summary>
        public void Initialize(RecordingSettings settings, IntPtr[] windowHandles)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _windowHandles = windowHandles ?? throw new ArgumentNullException(nameof(windowHandles));
            if (windowHandles.Length == 0) throw new ArgumentException("At least one window handle is required.");
            _isRecording = false;
            _frameTimer = new Stopwatch();
            Console.WriteLine($"Initialized with: Desktop={_settings.UseDesktop}, Vertical={_settings.UseVerticalLayout}, " +
                              $"Drive={_settings.SelectedDrive}, Resolution={_settings.Width}x{_settings.Height}");

            // DirectX ��ġ �ʱ�ȭ
            InitializeDirectX();
        }

        /// <summary>
        /// DirectX ��ġ �� ���ؽ�Ʈ �ʱ�ȭ
        /// </summary>
        private void InitializeDirectX()
        {
            try
            {
                // D3D11 ����̽� ����
                var featureLevels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 };
                D3D11.D3D11CreateDevice(
                    null,
                    Vortice.Direct3D.DriverType.Hardware,
                    Vortice.Direct3D11.DeviceCreationFlags.None,
                    featureLevels,
                    out _d3dDevice,
                    out _d3dContext);

                Console.WriteLine("DirectX initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize DirectX: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// ��ȭ ���� �޼���
        /// </summary>
        public void StartRecording()
        {
            if (!_isRecording)
            {
                lock (_lockObject)
                {
                    if (_isRecording) return;
                    _isRecording = true;

                    try
                    {
                        // ��ȭ ���� �ð� ���
                        _recordingStartTime = DateTime.Now;
                        _lastFileSplitTime = _recordingStartTime;
                        _segmentCounter = 1;

                        // �޽��� ����
                        SendStatusMessage("��ȭ�� ���۵Ǿ����ϴ�.");

                        // FFmpeg ���μ��� ����
                        StartFFmpegProcess();

                        // ĸó ������ ����
                        _captureThread = new Thread(CaptureLoop)
                        {
                            Name = "Screen Capture Thread",
                            IsBackground = true,
                            Priority = ThreadPriority.Highest
                        };
                        _captureThread.Start();

                        Console.WriteLine("Recording started successfully");
                    }
                    catch (Exception ex)
                    {
                        _isRecording = false;
                        CleanupResources();
                        SendStatusMessage($"��ȭ ���� ����: {ex.Message}");
                        Console.WriteLine($"Failed to start recording: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// ��ȭ ���� �޼���
        /// </summary>
        public void StopRecording()
        {
            if (_isRecording)
            {
                lock (_lockObject)
                {
                    if (!_isRecording) return;
                    _isRecording = false;

                    try
                    {
                        // ���� �� �޽��� ����
                        SendStatusMessage("���� ���Դϴ�...");

                        // ĸó �����尡 ����� ������ ���
                        if (_captureThread != null && _captureThread.IsAlive)
                        {
                            if (!_captureThread.Join(3000))
                            {
                                _captureThread.Interrupt();
                            }
                        }

                        // FFmpeg ���μ��� ����
                        CloseFFmpegProcess();

                        // ���� ���� ���� �� ����
                        SaveRecordingToArchive();

                        CleanupResources();

                        Console.WriteLine("Recording stopped successfully");
                    }
                    catch (Exception ex)
                    {
                        SendStatusMessage($"��ȭ ���� �� ����: {ex.Message}");
                        Console.WriteLine($"Error stopping recording: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// ���� ĸó ����
        /// </summary>
        private void CaptureLoop()
        {
            try
            {
                // ������ ���� ��� (�и���)
                double frameIntervalMs = 1000.0 / _settings.FrameRate;
                _frameTimer.Start();

                // ���� ��� �̹��� �غ�
                int outputWidth = _settings.Width;
                int outputHeight = _settings.Height;
                if (_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    outputHeight = _settings.Height * _windowHandles.Length;
                }
                else if (!_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    outputWidth = _settings.Width * 2; // ���η� 2�� â
                    outputHeight = _settings.Height * 2; // ���η� 2�� â
                }

                using var outputBitmap = new SKBitmap(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var outputCanvas = new SKCanvas(outputBitmap);

                long frameCount = 0;
                long lastFpsCheck = 0;
                int fps = 0;

                while (_isRecording)
                {
                    long frameStartTime = _frameTimer.ElapsedMilliseconds;

                    // �ð� Ȯ�� �� ���� ����
                    if ((DateTime.Now - _lastFileSplitTime).TotalMinutes >= FILE_SPLIT_INTERVAL_MINUTES)
                    {
                        SplitRecordingFile();
                    }

                    outputCanvas.Clear(SKColors.Black);

                    // �� â ĸó �� �ռ�
                    for (int i = 0; i < Math.Min(_windowHandles.Length, 3); i++)
                    {
                        int x, y;
                        if (_settings.UseVerticalLayout)
                        {
                            x = 0;
                            y = i * _settings.Height;
                        }
                        else
                        {
                            switch (i)
                            {
                                case 0: // ���� ���
                                    x = 0;
                                    y = 0;
                                    break;
                                case 1: // ���� ���
                                    x = _settings.Width;
                                    y = 0;
                                    break;
                                case 2: // ���� �ϴ�
                                    x = 0;
                                    y = _settings.Height;
                                    break;
                                default:
                                    continue; // 4��° â�� ���� (���� �ϴ� ���)
                            }
                        }

                        using var windowBitmap = CaptureWindow(_windowHandles[i]);
                        if (windowBitmap != null)
                        {
                            SKRect sourceRect = new SKRect(0, 0, windowBitmap.Width, windowBitmap.Height);
                            SKRect destRect = new SKRect(x, y, x + _settings.Width, y + _settings.Height);
                            outputCanvas.DrawBitmap(windowBitmap, sourceRect, destRect);
                        }
                    }

                    // FFmpeg�� ������ ����
                    SendFrameToFFmpeg(outputBitmap);

                    // FPS ����
                    frameCount++;
                    if (_frameTimer.ElapsedMilliseconds - lastFpsCheck >= 1000)
                    {
                        fps = (int)frameCount;
                        frameCount = 0;
                        lastFpsCheck = _frameTimer.ElapsedMilliseconds;
                        Console.WriteLine($"Current FPS: {fps}");
                    }

                    // ������ ���� ����
                    long frameTime = _frameTimer.ElapsedMilliseconds - frameStartTime;
                    int sleepTime = (int)Math.Max(0, frameIntervalMs - frameTime);
                    if (sleepTime > 0)
                    {
                        Thread.Sleep(sleepTime);
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                Console.WriteLine("Capture thread interrupted");
            }
            catch (Exception ex)
            {
                SendStatusMessage($"��ȭ �� ����: {ex.Message}");
                Console.WriteLine($"Error in capture loop: {ex.Message}");
            }
            finally
            {
                _frameTimer.Stop();
                CleanupResources();
            }
        }

        // ���� ���� ó�� �޼��� (���� �߰�)
        private void SplitRecordingFile()
        {
            try
            {
                // ���� ���� ����
                SendStatusMessage("��ȭ ���� ���� ��...");
                CloseFFmpegProcess();

                // �Ϸ�� ���� ����
                SaveRecordingToArchive();

                // �� ���� ����
                _segmentCounter++;
                _lastFileSplitTime = DateTime.Now;
                StartFFmpegProcess();

                SendStatusMessage($"��ȭ ��� ���� ��... (���׸�Ʈ {_segmentCounter})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error splitting recording file: {ex.Message}");
                SendStatusMessage($"���� ���� �� ����: {ex.Message}");
            }
        }

        // ��ȭ ���� ���� ó�� (���� �߰�)
        private void SaveRecordingToArchive()
        {
            try
            {
                if (File.Exists(_currentOutputPath))
                {
                    // Recordings ���� ����
                    string baseDir = Path.GetDirectoryName(_currentOutputPath);
                    string recordingsDir = Path.Combine(baseDir, "Recordings");
                    Directory.CreateDirectory(recordingsDir);

                    // ���ϸ� ���� (��¥_�ð�_���׸�Ʈ.mp4)
                    string timestamp = _lastFileSplitTime.ToString("yyyyMMdd_HHmmss");
                    string archiveFileName = $"{timestamp}_segment{_segmentCounter}.mp4";
                    string archivePath = Path.Combine(recordingsDir, archiveFileName);

                    // ���� ����
                    File.Copy(_currentOutputPath, archivePath, true);

                    // �޽��� ����
                    SendStatusMessage($"������ �Ϸ�Ǿ����ϴ�. ({archivePath})");
                    Console.WriteLine($"Recording saved to: {archivePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving recording to archive: {ex.Message}");
                SendStatusMessage($"���� ���� �� ����: {ex.Message}");
            }
        }

        // �޽��� ���� �޼��� (���� �߰�)
        private void SendStatusMessage(string message)
        {
            OnStatusMessage?.Invoke(message);
            Console.WriteLine($"Status: {message}");
        }

        /// <summary>
        /// â �ϳ��� ĸó�ϴ� �޼���
        /// </summary>
        private SKBitmap CaptureWindow(IntPtr hwnd)
        {
            try
            {
                // â ũ�� ���
                DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT windowRect, Marshal.SizeOf<RECT>());
                int width = Math.Max(1, windowRect.Width);
                int height = Math.Max(1, windowRect.Height);

                // SKBitmap ����
                var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

                // DXGI�� ����� ĸó
                IDXGIOutput dxgiOutput;
                using (var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>())
                using (var dxgiAdapter = dxgiDevice.GetAdapter())
                {
                    dxgiAdapter.EnumOutputs(0, out dxgiOutput);
                }
                using (dxgiOutput)
                using (var dxgiOutput1 = dxgiOutput.QueryInterface<IDXGIOutput1>())
                {
                    if (_deskDupl == null)
                    {
                        _deskDupl = dxgiOutput1.DuplicateOutput(_d3dDevice);
                    }

                    var result = _deskDupl.AcquireNextFrame(100, out var frameInfo, out var desktopResource);
                    if (result.Success)
                    {
                        using (var texture = desktopResource.QueryInterface<ID3D11Texture2D>())
                        {
                            var desc = texture.Description;
                            var stagingDesc = new Texture2DDescription
                            {
                                Width = desc.Width,
                                Height = desc.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = desc.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Staging,
                                BindFlags = 0,
                                CPUAccessFlags = CpuAccessFlags.Read,
                                MiscFlags = 0
                            };

                            using (var stagingTexture = _d3dDevice.CreateTexture2D(stagingDesc))
                            {
                                _d3dContext.CopyResource(stagingTexture, texture);
                                var mapped = _d3dContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                                IntPtr sourcePtr = (nint)(mapped.DataPointer + (windowRect.Top * mapped.RowPitch) + (windowRect.Left * 4));
                                IntPtr destPtr = bitmap.GetPixels();

                                for (int y = 0; y < height && y + windowRect.Top < desc.Height; y++)
                                {
                                    int srcOffset = (int)(y * mapped.RowPitch);
                                    int dstOffset = y * bitmap.RowBytes;
                                    if (windowRect.Left + width <= desc.Width)
                                    {
                                        unsafe
                                        {
                                            Buffer.MemoryCopy(
                                                (void*)(sourcePtr + srcOffset),
                                                (void*)(destPtr + dstOffset),
                                                bitmap.RowBytes,
                                                width * 4);
                                        }
                                    }
                                }

                                _d3dContext.Unmap(stagingTexture, 0);
                            }
                        }
                        _deskDupl.ReleaseFrame();
                    }
                    else
                    {
                        bitmap.Erase(SKColors.Black);
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing window: {ex.Message}");
                return null; // ���� �� null ��ȯ
            }
        }

        /// <summary>
        /// FFmpeg ���μ��� ����
        /// </summary>
        private void StartFFmpegProcess()
        {
            try
            {
                string outputPath = _settings.GetOutputPath();
                _currentOutputPath = outputPath; // ���� ��� ��� ����
                string pipeName = $"screencapture_{Guid.NewGuid().ToString("N")}";
                string pipeFullPath = $@"\\.\pipe\{pipeName}";

                // ������ ���� ����
                _pipeServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    1024 * 1024 * 10); // 10MB ����

                // FFmpeg ���μ��� ����
                int width = _settings.Width;
                int height = _settings.Height;

                if (_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    height = _settings.Height * _windowHandles.Length;
                }
                else if (!_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    //width = _settings.Width * _windowHandles.Length;
                    width = _settings.Width * 2;
                    height = _settings.Height * 2;
                }

                string ffmpegArgs = $"-f rawvideo -pixel_format bgra -video_size {width}x{height} " +
                                    $"-framerate {_settings.FrameRate} -i {pipeFullPath} " +
                                    $"-c:v libx264 -preset ultrafast -crf 20 -pix_fmt yuv420p " +
                                    $"-y \"{outputPath}\"";

                _ffmpegProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    },
                    EnableRaisingEvents = true
                };

                _ffmpegProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"FFmpeg: {e.Data}");
                };

                _ffmpegProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"FFmpeg: {e.Data}");
                };

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();
                _ffmpegProcess.BeginOutputReadLine();

                // FFmpeg�� �������� ����� ������ ���
                _pipeServer.WaitForConnection();
                _pipeWriter = new BinaryWriter(_pipeServer);

                Console.WriteLine($"FFmpeg started with output: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start FFmpeg: {ex.Message}");
                CleanupResources();
                throw;
            }
        }

        /// <summary>
        /// �������� FFmpeg�� ����
        /// </summary>
        private void SendFrameToFFmpeg(SKBitmap bitmap)
        {
            if (_pipeWriter != null && _pipeServer != null && _pipeServer.IsConnected)
            {
                try
                {
                    // ��Ʈ�� �����͸� ����Ʈ �迭�� ��������
                    IntPtr pixelsPtr = bitmap.GetPixels();
                    byte[] pixelData = new byte[bitmap.RowBytes * bitmap.Height];
                    Marshal.Copy(pixelsPtr, pixelData, 0, pixelData.Length);

                    // �������� ����
                    _pipeWriter.Write(pixelData);
                    _pipeWriter.Flush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending frame to FFmpeg: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// FFmpeg ���μ��� ����
        /// </summary>
        private void CloseFFmpegProcess()
        {
            try
            {
                // ������ �ݱ�
                if (_pipeWriter != null)
                {
                    _pipeWriter.Dispose();
                    _pipeWriter = null;
                }

                if (_pipeServer != null)
                {
                    if (_pipeServer.IsConnected)
                    {
                        _pipeServer.Disconnect();
                    }
                    _pipeServer.Dispose();
                    _pipeServer = null;
                }

                // FFmpeg ���μ��� ����
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    // ���� ���� �õ�
                    if (!_ffmpegProcess.WaitForExit(2000))
                    {
                        _ffmpegProcess.Kill(); // ������ ������ ���� ����
                    }
                    _ffmpegProcess.Dispose();
                    _ffmpegProcess = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing FFmpeg: {ex.Message}");
            }
        }

        /// <summary>
        /// ���ҽ� ����
        /// </summary>
        private void CleanupResources()
        {
            try
            {
                // DirectX ���ҽ� ����
                _deskDupl?.Dispose();
                _deskDupl = null;

                _d3dContext?.Dispose();
                _d3dContext = null;

                _d3dDevice?.Dispose();
                _d3dDevice = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up resources: {ex.Message}");
            }
        }

        /// <summary>
        /// ���� ��ȭ ������ ���� Ȯ��
        /// </summary>
        public bool IsRecording => _isRecording;
    }
}