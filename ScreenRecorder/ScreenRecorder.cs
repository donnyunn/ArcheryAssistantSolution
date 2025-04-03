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
        public int FrameRate { get; set; } = 30;
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

        public delegate void MessageEventHandler(string message);
        public event MessageEventHandler OnStatusMessage;

        private const int FILE_SPLIT_INTERVAL_MINUTES = 1;
        private DateTime _recordingStartTime;
        private DateTime _lastFileSplitTime;
        private string _currentOutputPath;
        private int _segmentCounter = 1;

        private SKBitmap[] _lastSuccessfulFrames;
        private Dictionary<int, IDXGIOutputDuplication> _outputDuplications = new Dictionary<int, IDXGIOutputDuplication>();
        private Dictionary<IntPtr, int> _windowToMonitorMap = new Dictionary<IntPtr, int>();
        private Dictionary<int, RECT> _monitorBounds = new Dictionary<int, RECT>();

        public void Initialize(RecordingSettings settings, IntPtr[] windowHandles)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _windowHandles = windowHandles ?? throw new ArgumentNullException(nameof(windowHandles));
            if (windowHandles.Length == 0) throw new ArgumentException("At least one window handle is required.");
            _isRecording = false;
            _frameTimer = new Stopwatch();
            _lastSuccessfulFrames = new SKBitmap[windowHandles.Length];
            // 배열 요소를 빈 SKBitmap으로 초기화
            for (int i = 0; i < _lastSuccessfulFrames.Length; i++)
            {
                _lastSuccessfulFrames[i] = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
                _lastSuccessfulFrames[i].Erase(SKColors.Black);
            }
            Console.WriteLine($"Initialized with: Desktop={_settings.UseDesktop}, Vertical={_settings.UseVerticalLayout}, " +
                              $"Drive={_settings.SelectedDrive}, Resolution={_settings.Width}x{_settings.Height}");

            InitializeDirectX();
            InitializeMonitorMapping();
        }

        private void InitializeMonitorMapping()
        {
            try
            {
                using (var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>())
                using (var dxgiAdapter = dxgiDevice.GetAdapter())
                {
                    for (int i = 0; i < 10; i++)
                    {
                        try
                        {
                            dxgiAdapter.EnumOutputs((uint)i, out var output);
                            if (output == null) break;

                            using (output)
                            {
                                var desc = output.Description;
                                _monitorBounds[i] = new RECT
                                {
                                    Left = desc.DesktopCoordinates.Left,
                                    Top = desc.DesktopCoordinates.Top,
                                    Right = desc.DesktopCoordinates.Right,
                                    Bottom = desc.DesktopCoordinates.Bottom
                                };
                                Console.WriteLine($"Monitor {i}: {desc.DeviceName}, " +
                                                  $"({desc.DesktopCoordinates.Left},{desc.DesktopCoordinates.Top})-" +
                                                  $"({desc.DesktopCoordinates.Right},{desc.DesktopCoordinates.Bottom})");
                            }
                        }
                        catch { break; }
                    }

                    foreach (var hwnd in _windowHandles)
                    {
                        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var windowRect, Marshal.SizeOf<RECT>());
                        int centerX = windowRect.Left + (windowRect.Width / 2);
                        int centerY = windowRect.Top + (windowRect.Height / 2);
                        _windowToMonitorMap[hwnd] = GetMonitorIndexForPoint(centerX, centerY);
                        Console.WriteLine($"Window {hwnd} mapped to monitor {_windowToMonitorMap[hwnd]}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing monitor mapping: {ex.Message}");
            }
        }

        private int GetMonitorIndexForPoint(int x, int y)
        {
            foreach (var monitor in _monitorBounds)
            {
                if (x >= monitor.Value.Left && x < monitor.Value.Right &&
                    y >= monitor.Value.Top && y < monitor.Value.Bottom)
                {
                    return monitor.Key;
                }
            }
            return 0;
        }

        private void InitializeDirectX()
        {
            try
            {
                var featureLevels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 };
                D3D11.D3D11CreateDevice(
                    null,
                    Vortice.Direct3D.DriverType.Hardware,
                    DeviceCreationFlags.None,
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
                        // DirectX 리소스가 해제된 경우 재초기화
                        if (_d3dDevice == null)
                        {
                            InitializeDirectX();
                            InitializeMonitorMapping();
                        }
                        
                        // _lastSuccessfulFrames 재초기화
                        if (_lastSuccessfulFrames == null || _lastSuccessfulFrames.Length != _windowHandles.Length)
                        {
                            _lastSuccessfulFrames = new SKBitmap[_windowHandles.Length];
                            for (int i = 0; i < _lastSuccessfulFrames.Length; i++)
                            {
                                _lastSuccessfulFrames[i] = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
                                _lastSuccessfulFrames[i].Erase(SKColors.Black);
                            }
                        }

                        _recordingStartTime = DateTime.Now;
                        _lastFileSplitTime = _recordingStartTime;
                        _segmentCounter = 1;

                        SendStatusMessage("녹화가 시작되었습니다.");
                        StartFFmpegProcess();

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
                        SendStatusMessage($"녹화 시작 실패: {ex.Message}");
                        Console.WriteLine($"Failed to start recording: {ex.Message}");
                        throw;
                    }
                }
            }
        }

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
                        SendStatusMessage("저장 중입니다...");

                        if (_captureThread != null && _captureThread.IsAlive)
                        {
                            if (!_captureThread.Join(3000))
                            {
                                _captureThread.Interrupt();
                            }
                        }

                        CloseFFmpegProcess();
                        SaveRecordingToArchive();
                        CleanupResources();

                        Console.WriteLine("Recording stopped successfully");
                    }
                    catch (Exception ex)
                    {
                        SendStatusMessage($"녹화 종료 중 오류: {ex.Message}");
                        Console.WriteLine($"Error stopping recording: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        private void CaptureLoop()
        {
            try
            {
                double frameIntervalMs = 1000.0 / _settings.FrameRate;
                _frameTimer.Start();

                int outputWidth = _settings.Width;
                int outputHeight = _settings.Height;
                if (_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    outputHeight = _settings.Height * _windowHandles.Length;
                }
                else if (!_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    outputWidth = _settings.Width * 2;
                    outputHeight = _settings.Height * 2;
                }

                using var outputBitmap = new SKBitmap(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var outputCanvas = new SKCanvas(outputBitmap);

                long frameCount = 0;
                long lastFpsCheck = 0;
                int fps = 0;

                while (_isRecording)
                {
                    long frameStartTime = _frameTimer.ElapsedMilliseconds;

                    if ((DateTime.Now - _lastFileSplitTime).TotalMinutes >= FILE_SPLIT_INTERVAL_MINUTES)
                    {
                        SplitRecordingFile();
                    }

                    outputCanvas.Clear(SKColors.Black);

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
                                case 0: x = 0; y = 0; break; // 좌측 상단
                                case 1: x = _settings.Width; y = 0; break; // 우측 상단
                                case 2: x = 0; y = _settings.Height; break; // 좌측 하단
                                default: continue;
                            }
                        }

                        var windowBitmap = CaptureWindow(_windowHandles[i]);
                        if (windowBitmap != null && !windowBitmap.IsEmpty && windowBitmap.GetPixels() != IntPtr.Zero)
                        {
                            try
                            {
                                using var resizedBitmap = windowBitmap.Resize(
                                    new SKImageInfo(_settings.Width, _settings.Height),
                                    new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                                if (resizedBitmap != null)
                                {
                                    SKRect sourceRect = new SKRect(0, 0, resizedBitmap.Width, resizedBitmap.Height);
                                    SKRect destRect = new SKRect(x, y, x + _settings.Width, y + _settings.Height);
                                    outputCanvas.DrawBitmap(resizedBitmap, sourceRect, destRect);
                                }
                                else
                                {
                                    Console.WriteLine($"Resize failed for window {i}.");
                                    outputCanvas.DrawRect(x, y, _settings.Width, _settings.Height, new SKPaint { Color = SKColors.Black });
                                }
                            }
                            finally
                            {
                                windowBitmap.Dispose();
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Window {i} capture invalid or empty.");
                            outputCanvas.DrawRect(x, y, _settings.Width, _settings.Height, new SKPaint { Color = SKColors.Black });
                        }
                    }

                    SendFrameToFFmpeg(outputBitmap);

                    frameCount++;
                    if (_frameTimer.ElapsedMilliseconds - lastFpsCheck >= 1000)
                    {
                        fps = (int)frameCount;
                        frameCount = 0;
                        lastFpsCheck = _frameTimer.ElapsedMilliseconds;
                        Console.WriteLine($"Current FPS: {fps}");
                    }

                    long totalProcessingTime = _frameTimer.ElapsedMilliseconds - frameStartTime;
                    if (totalProcessingTime > frameIntervalMs)
                    {
                        Console.WriteLine($"Warning: Frame processing time ({totalProcessingTime}ms) exceeds target ({frameIntervalMs}ms)");
                    }
                    else
                    {
                        int sleepTime = (int)(frameIntervalMs - totalProcessingTime);
                        if (sleepTime > 0) Thread.Sleep(sleepTime);
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                Console.WriteLine("Capture thread interrupted");
            }
            catch (Exception ex)
            {
                SendStatusMessage($"녹화 중 오류: {ex.Message}");
                Console.WriteLine($"Error in capture loop: {ex.Message}");
            }
            finally
            {
                _frameTimer.Stop();
                CleanupResources();
            }
        }

        private void SplitRecordingFile()
        {
            try
            {
                SendStatusMessage("녹화 파일 분할 중...");
                CloseFFmpegProcess();
                SaveRecordingToArchive();

                _segmentCounter++;
                _lastFileSplitTime = DateTime.Now;
                StartFFmpegProcess();

                SendStatusMessage($"녹화 계속 진행 중... (세그먼트 {_segmentCounter})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error splitting recording file: {ex.Message}");
                SendStatusMessage($"파일 분할 중 오류: {ex.Message}");
            }
        }

        private void SaveRecordingToArchive()
        {
            try
            {
                if (File.Exists(_currentOutputPath))
                {
                    string baseDir = Path.GetDirectoryName(_currentOutputPath);
                    string recordingsDir = Path.Combine(baseDir, "Recordings");
                    Directory.CreateDirectory(recordingsDir);

                    string timestamp = _lastFileSplitTime.ToString("yyyyMMdd_HHmmss");
                    string archiveFileName = $"{timestamp}_segment{_segmentCounter}.mp4";
                    string archivePath = Path.Combine(recordingsDir, archiveFileName);

                    File.Copy(_currentOutputPath, archivePath, true);

                    SendStatusMessage($"저장이 완료되었습니다. ({archivePath})");
                    Console.WriteLine($"Recording saved to: {archivePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving recording to archive: {ex.Message}");
                SendStatusMessage($"파일 저장 중 오류: {ex.Message}");
            }
        }

        private void SendStatusMessage(string message)
        {
            OnStatusMessage?.Invoke(message);
            Console.WriteLine($"Status: {message}");
        }

        private SKBitmap CaptureWindow(IntPtr hwnd)
        {
            try
            {
                if (_d3dDevice == null)
                {
                    Console.WriteLine("DirectX device is null. Cannot capture window.");
                    return new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul) {};
                }

                DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT windowRect, Marshal.SizeOf<RECT>());
                int width = Math.Max(1, windowRect.Width);
                int height = Math.Max(1, windowRect.Height);
                var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

                int monitorIndex = _windowToMonitorMap.TryGetValue(hwnd, out int idx) ? idx : 0;

                if (!_outputDuplications.TryGetValue(monitorIndex, out var deskDupl) || deskDupl == null)
                {
                    using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
                    using var dxgiAdapter = dxgiDevice.GetAdapter();
                    IDXGIOutput output = null;
                    try
                    {
                        dxgiAdapter.EnumOutputs((uint)monitorIndex, out output);
                    }
                    catch
                    {
                        dxgiAdapter.EnumOutputs(0, out output);
                    }

                    if (output == null)
                    {
                        bitmap.Erase(SKColors.Black);
                        return bitmap;
                    }

                    using (output)
                    using (var output1 = output.QueryInterface<IDXGIOutput1>())
                    {
                        try
                        {
                            deskDupl = output1.DuplicateOutput(_d3dDevice);
                            _outputDuplications[monitorIndex] = deskDupl;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error creating duplication for monitor {monitorIndex}: {ex.Message}");
                            bitmap.Erase(SKColors.Black);
                            return bitmap;
                        }
                    }
                }

                var result = deskDupl.AcquireNextFrame(100, out var frameInfo, out var desktopResource);
                if (result.Success && desktopResource != null)
                {
                    using (desktopResource)
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

                            int relativeLeft = windowRect.Left - _monitorBounds[monitorIndex].Left;
                            int relativeTop = windowRect.Top - _monitorBounds[monitorIndex].Top;
                            relativeLeft = (int)Math.Max(0, Math.Min(relativeLeft, desc.Width - width));
                            relativeTop = (int)Math.Max(0, Math.Min(relativeTop, desc.Height - height));
                            int copyWidth = (int)Math.Min(width, desc.Width - relativeLeft);
                            int copyHeight = (int)Math.Min(height, desc.Height - relativeTop);

                            if (copyWidth > 0 && copyHeight > 0)
                            {
                                IntPtr sourcePtr = (nint)(mapped.DataPointer + (relativeTop * mapped.RowPitch) + (relativeLeft * 4));
                                IntPtr destPtr = bitmap.GetPixels();

                                for (int y = 0; y < copyHeight; y++)
                                {
                                    unsafe
                                    {
                                        Buffer.MemoryCopy(
                                            (void*)(sourcePtr + y * mapped.RowPitch),
                                            (void*)(destPtr + y * bitmap.RowBytes),
                                            Math.Min(bitmap.RowBytes, copyWidth * 4),
                                            Math.Min(copyWidth * 4, mapped.RowPitch));
                                    }
                                }
                            }

                            _d3dContext.Unmap(stagingTexture, 0);

                            int handleIndex = Array.IndexOf(_windowHandles, hwnd);
                            if (_lastSuccessfulFrames != null && handleIndex >= 0 && handleIndex < _lastSuccessfulFrames.Length)
                            {
                                _lastSuccessfulFrames[handleIndex]?.Dispose();
                                _lastSuccessfulFrames[handleIndex] = bitmap.Copy();
                            }
                        }
                    }
                    deskDupl.ReleaseFrame();
                }
                else
                {
                    int handleIndex = Array.IndexOf(_windowHandles, hwnd);
                    if (_lastSuccessfulFrames != null && handleIndex >= 0 && handleIndex < _lastSuccessfulFrames.Length && _lastSuccessfulFrames[handleIndex] != null)
                    {
                        using var canvas = new SKCanvas(bitmap);
                        canvas.Clear(SKColors.Black);
                        canvas.DrawBitmap(_lastSuccessfulFrames[handleIndex], 0, 0);
                    }
                    else
                    {
                        bitmap.Erase(SKColors.Black);
                    }
                }

                return bitmap; // 원본 반환, 축소는 CaptureLoop에서 처리
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing window: {ex.Message}");
                int handleIndex = Array.IndexOf(_windowHandles, hwnd);
                if (_lastSuccessfulFrames != null && handleIndex >= 0 && handleIndex < _lastSuccessfulFrames.Length && _lastSuccessfulFrames[handleIndex] != null)
                {
                    return _lastSuccessfulFrames[handleIndex].Copy();
                }
                return new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul) {};
            }
        }

        private void StartFFmpegProcess()
        {
            try
            {
                string outputPath = _settings.GetOutputPath();
                _currentOutputPath = outputPath;
                string pipeName = $"screencapture_{Guid.NewGuid().ToString("N")}";
                string pipeFullPath = $@"\\.\pipe\{pipeName}";

                _pipeServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    1024 * 1024 * 20);

                int width = _settings.Width;
                int height = _settings.Height;
                if (_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    height = _settings.Height * _windowHandles.Length;
                }
                else if (!_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    width = _settings.Width * 2;
                    height = _settings.Height * 2;
                }

                // h264_amf 설정 수정
                string ffmpegArgs = $"-f rawvideo -pixel_format bgra -video_size {width}x{height} " +
                                    $"-framerate {_settings.FrameRate} -i {pipeFullPath} " +
                                    $"-c:v h264_amf -usage lowlatency -quality balanced -rc cbr -b:v 4M " +
                                    $"-pix_fmt yuv420p -y \"{_currentOutputPath}\"";

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

        private void SendFrameToFFmpeg(SKBitmap bitmap)
        {
            if (_pipeWriter != null && _pipeServer != null && _pipeServer.IsConnected)
            {
                try
                {
                    IntPtr pixelsPtr = bitmap.GetPixels();
                    if (pixelsPtr != IntPtr.Zero) // 유효성 검사
                    {
                        byte[] pixelData = new byte[bitmap.RowBytes * bitmap.Height];
                        Marshal.Copy(pixelsPtr, pixelData, 0, pixelData.Length);
                        _pipeWriter.Write(pixelData);
                        _pipeWriter.Flush();
                    }
                    else
                    {
                        Console.WriteLine("Invalid bitmap pixels pointer.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending frame to FFmpeg: {ex.Message}");
                    if (!_pipeServer.IsConnected)
                    {
                        try
                        {
                            _pipeServer.Close();
                            StartFFmpegProcess();
                        }
                        catch (Exception retryEx)
                        {
                            Console.WriteLine($"Failed to restart FFmpeg: {retryEx.Message}");
                        }
                    }
                }
            }
        }

        private void CloseFFmpegProcess()
        {
            try
            {
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

                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    if (!_ffmpegProcess.WaitForExit(2000))
                    {
                        _ffmpegProcess.Kill();
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

        private void CleanupResources()
        {
            try
            {
                // OutputDuplications 해제
                foreach (var dupl in _outputDuplications.Values)
                {
                    dupl?.Dispose();
                }
                _outputDuplications.Clear();

                // _lastSuccessfulFrames 해제
                if (_lastSuccessfulFrames != null)
                {
                    for (int i = 0; i < _lastSuccessfulFrames.Length; i++)
                    {
                        if (_lastSuccessfulFrames[i] != null)
                        {
                            try
                            {
                                _lastSuccessfulFrames[i].Dispose();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error disposing frame {i}: {ex.Message}");
                            }
                            _lastSuccessfulFrames[i] = null;
                        }
                    }
                    _lastSuccessfulFrames = null;
                }

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

        public bool IsRecording => _isRecording;
    }
}