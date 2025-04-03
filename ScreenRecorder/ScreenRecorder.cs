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

        // 메시지 전달을 위한 이벤트 추가
        public delegate void MessageEventHandler(string message);
        public event MessageEventHandler OnStatusMessage;

        // 파일 분할 관련 상수 및 필드
        private const int FILE_SPLIT_INTERVAL_MINUTES = 1; // 파일 분할 간격(분)
        private DateTime _recordingStartTime;
        private DateTime _lastFileSplitTime;
        private string _currentOutputPath;
        private int _segmentCounter = 1;

        /// <summary>
        /// 초기화 함수: 설정과 캡처할 창 핸들을 설정
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

            // DirectX 장치 초기화
            InitializeDirectX();
        }

        /// <summary>
        /// DirectX 장치 및 컨텍스트 초기화
        /// </summary>
        private void InitializeDirectX()
        {
            try
            {
                // D3D11 디바이스 생성
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
        /// 녹화 시작 메서드
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
                        // 녹화 시작 시간 기록
                        _recordingStartTime = DateTime.Now;
                        _lastFileSplitTime = _recordingStartTime;
                        _segmentCounter = 1;

                        // 메시지 전송
                        SendStatusMessage("녹화가 시작되었습니다.");

                        // FFmpeg 프로세스 시작
                        StartFFmpegProcess();

                        // 캡처 스레드 시작
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

        /// <summary>
        /// 녹화 중지 메서드
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
                        // 저장 중 메시지 전송
                        SendStatusMessage("저장 중입니다...");

                        // 캡처 스레드가 종료될 때까지 대기
                        if (_captureThread != null && _captureThread.IsAlive)
                        {
                            if (!_captureThread.Join(3000))
                            {
                                _captureThread.Interrupt();
                            }
                        }

                        // FFmpeg 프로세스 종료
                        CloseFFmpegProcess();

                        // 최종 파일 저장 및 복사
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

        /// <summary>
        /// 메인 캡처 루프
        /// </summary>
        private void CaptureLoop()
        {
            try
            {
                // 프레임 간격 계산 (밀리초)
                double frameIntervalMs = 1000.0 / _settings.FrameRate;
                _frameTimer.Start();

                // 최종 출력 이미지 준비
                int outputWidth = _settings.Width;
                int outputHeight = _settings.Height;
                if (_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    outputHeight = _settings.Height * _windowHandles.Length;
                }
                else if (!_settings.UseVerticalLayout && _windowHandles.Length > 1)
                {
                    outputWidth = _settings.Width * 2; // 가로로 2개 창
                    outputHeight = _settings.Height * 2; // 세로로 2개 창
                }

                using var outputBitmap = new SKBitmap(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var outputCanvas = new SKCanvas(outputBitmap);

                long frameCount = 0;
                long lastFpsCheck = 0;
                int fps = 0;

                while (_isRecording)
                {
                    long frameStartTime = _frameTimer.ElapsedMilliseconds;

                    // 시간 확인 및 파일 분할
                    if ((DateTime.Now - _lastFileSplitTime).TotalMinutes >= FILE_SPLIT_INTERVAL_MINUTES)
                    {
                        SplitRecordingFile();
                    }

                    outputCanvas.Clear(SKColors.Black);

                    // 각 창 캡처 및 합성
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
                                case 0: // 좌측 상단
                                    x = 0;
                                    y = 0;
                                    break;
                                case 1: // 우측 상단
                                    x = _settings.Width;
                                    y = 0;
                                    break;
                                case 2: // 좌측 하단
                                    x = 0;
                                    y = _settings.Height;
                                    break;
                                default:
                                    continue; // 4번째 창은 무시 (우측 하단 비움)
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

                    // FFmpeg로 프레임 전송
                    SendFrameToFFmpeg(outputBitmap);

                    // FPS 측정
                    frameCount++;
                    if (_frameTimer.ElapsedMilliseconds - lastFpsCheck >= 1000)
                    {
                        fps = (int)frameCount;
                        frameCount = 0;
                        lastFpsCheck = _frameTimer.ElapsedMilliseconds;
                        Console.WriteLine($"Current FPS: {fps}");
                    }

                    // 프레임 간격 조정
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
                SendStatusMessage($"녹화 중 오류: {ex.Message}");
                Console.WriteLine($"Error in capture loop: {ex.Message}");
            }
            finally
            {
                _frameTimer.Stop();
                CleanupResources();
            }
        }

        // 파일 분할 처리 메서드 (새로 추가)
        private void SplitRecordingFile()
        {
            try
            {
                // 현재 파일 종료
                SendStatusMessage("녹화 파일 분할 중...");
                CloseFFmpegProcess();

                // 완료된 파일 저장
                SaveRecordingToArchive();

                // 새 파일 시작
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

        // 녹화 파일 보관 처리 (새로 추가)
        private void SaveRecordingToArchive()
        {
            try
            {
                if (File.Exists(_currentOutputPath))
                {
                    // Recordings 폴더 생성
                    string baseDir = Path.GetDirectoryName(_currentOutputPath);
                    string recordingsDir = Path.Combine(baseDir, "Recordings");
                    Directory.CreateDirectory(recordingsDir);

                    // 파일명 생성 (날짜_시간_세그먼트.mp4)
                    string timestamp = _lastFileSplitTime.ToString("yyyyMMdd_HHmmss");
                    string archiveFileName = $"{timestamp}_segment{_segmentCounter}.mp4";
                    string archivePath = Path.Combine(recordingsDir, archiveFileName);

                    // 파일 복사
                    File.Copy(_currentOutputPath, archivePath, true);

                    // 메시지 전송
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

        // 메시지 전달 메서드 (새로 추가)
        private void SendStatusMessage(string message)
        {
            OnStatusMessage?.Invoke(message);
            Console.WriteLine($"Status: {message}");
        }

        /// <summary>
        /// 창 하나를 캡처하는 메서드
        /// </summary>
        private SKBitmap CaptureWindow(IntPtr hwnd)
        {
            try
            {
                // 창 크기 얻기
                DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT windowRect, Marshal.SizeOf<RECT>());
                int width = Math.Max(1, windowRect.Width);
                int height = Math.Max(1, windowRect.Height);

                // SKBitmap 생성
                var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

                // DXGI를 사용한 캡처
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
                return null; // 실패 시 null 반환
            }
        }

        /// <summary>
        /// FFmpeg 프로세스 시작
        /// </summary>
        private void StartFFmpegProcess()
        {
            try
            {
                string outputPath = _settings.GetOutputPath();
                _currentOutputPath = outputPath; // 현재 출력 경로 저장
                string pipeName = $"screencapture_{Guid.NewGuid().ToString("N")}";
                string pipeFullPath = $@"\\.\pipe\{pipeName}";

                // 파이프 서버 생성
                _pipeServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    1024 * 1024 * 10); // 10MB 버퍼

                // FFmpeg 프로세스 시작
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

                // FFmpeg가 파이프에 연결될 때까지 대기
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
        /// 프레임을 FFmpeg로 전송
        /// </summary>
        private void SendFrameToFFmpeg(SKBitmap bitmap)
        {
            if (_pipeWriter != null && _pipeServer != null && _pipeServer.IsConnected)
            {
                try
                {
                    // 비트맵 데이터를 바이트 배열로 가져오기
                    IntPtr pixelsPtr = bitmap.GetPixels();
                    byte[] pixelData = new byte[bitmap.RowBytes * bitmap.Height];
                    Marshal.Copy(pixelsPtr, pixelData, 0, pixelData.Length);

                    // 파이프에 쓰기
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
        /// FFmpeg 프로세스 종료
        /// </summary>
        private void CloseFFmpegProcess()
        {
            try
            {
                // 파이프 닫기
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

                // FFmpeg 프로세스 종료
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    // 정상 종료 시도
                    if (!_ffmpegProcess.WaitForExit(2000))
                    {
                        _ffmpegProcess.Kill(); // 응답이 없으면 강제 종료
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
        /// 리소스 정리
        /// </summary>
        private void CleanupResources()
        {
            try
            {
                // DirectX 리소스 해제
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
        /// 현재 녹화 중인지 여부 확인
        /// </summary>
        public bool IsRecording => _isRecording;
    }
}