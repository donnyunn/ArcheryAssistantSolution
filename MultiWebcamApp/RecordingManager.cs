using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Size = OpenCvSharp.Size;

namespace MultiWebcamApp
{
    /// <summary>
    /// 멀티 카메라 및 압력 맵 데이터를 녹화하는 매니저 클래스
    /// 주요 개선사항:
    /// - 메모리 누수 방지를 위한 적절한 자원 관리
    /// - UI 스레드 블로킹 방지
    /// - 효율적인 스크린샷 캡처 및 처리
    /// - 스레드 안전성 강화
    /// - 화면 배치 모드 선택 기능 (세로 스택 또는 PC 모니터 최적화)
    /// - 커스텀 저장 경로 지원
    /// - 60FPS 고화질 녹화 지원
    /// </summary>
    public class RecordingManager : IDisposable
    {
        #region 필드 및 속성

        /// <summary>
        /// 화면 배치 모드 열거형
        /// </summary>
        public enum LayoutMode
        {
            /// <summary>
            /// 세로 스택 모드 (3개 화면을 세로로 배치, 960x1620)
            /// </summary>
            VerticalStack = 1,

            /// <summary>
            /// PC 모니터 최적화 모드 (웹캠 가로 배치 + 압력맵 아래, 1920x1080)
            /// </summary>
            MonitorOptimized = 2
        }

        // 녹화 설정
        private readonly int _frameWidth;
        private readonly int _frameHeight;
        private readonly double _fps;
        private readonly int _recordingIntervalMinutes;
        private readonly string _tempFilePath;
        private string _targetDirectory;
        private readonly FourCC _codec;
        private LayoutMode _layoutMode;

        // 레이아웃 관련 계산 필드
        private readonly int _outputWidth;
        private readonly int _outputHeight;

        // 녹화 상태 관리
        private ConcurrentQueue<Mat> _frameQueue;
        private VideoWriter _videoWriter;
        private Task _recordingTask;
        private CancellationTokenSource _cts;
        private readonly object _lockObject = new object();
        private volatile bool _isRecording;
        private DateTime _recordingStartTime;
        private readonly System.Threading.Timer _saveTimer;

        // 프레임 혼합 관련
        private Mat _lastHeadFrame;
        private Mat _lastBodyFrame;
        private Mat _lastPressureFrame;
        private bool _isMixingFrames;
        private double _frameAlpha;

        // 스크린샷 캡처 관련
        private readonly SemaphoreSlim _captureSemaphore = new SemaphoreSlim(1, 1);
        private readonly PressureMapViewer.MainWindow _pressureMapWindow;
        private Task _captureTask;
        private CancellationTokenSource _captureCts;
        private readonly BlockingCollection<Bitmap> _captureQueue = new BlockingCollection<Bitmap>(new ConcurrentQueue<Bitmap>(), 3); // 큐 크기 증가
        private readonly int _captureWidth;
        private readonly int _captureHeight;
        private readonly TimeSpan _captureInterval = TimeSpan.FromMilliseconds(16); // ~60fps에 맞게 조정
        private bool _isCaptureActive;

        // 성능 최적화를 위한 필드
        private readonly ConcurrentQueue<FrameData> _inputFrameQueue = new ConcurrentQueue<FrameData>();
        private Task _frameProcessingTask;
        private CancellationTokenSource _frameProcessingCts;
        private bool _isFrameProcessingActive;

        // 상태 모니터링
        private readonly Stopwatch _perfStopwatch = new Stopwatch();
        private long _frameCount;
        private long _droppedFrames;

        /// <summary>
        /// 현재 녹화 상태
        /// </summary>
        public bool IsRecording => _isRecording;

        /// <summary>
        /// 녹화 경과 시간 (초)
        /// </summary>
        public double ElapsedRecordingTime => _isRecording ? (DateTime.Now - _recordingStartTime).TotalSeconds : 0;

        /// <summary>
        /// 녹화 중인 파일 경로
        /// </summary>
        public string CurrentVideoPath => _tempFilePath;

        /// <summary>
        /// 녹화 저장할 폴더 이름
        /// </summary>
        private string _usbDirectoryName = "Recordings";

        #endregion

        #region 생성자 및 초기화

        /// <summary>
        /// RecordingManager 생성자
        /// </summary>
        /// <param name="pressureMapWindow">압력 맵 윈도우 인스턴스</param>
        /// <param name="savePath">녹화 파일 저장 경로 (null이면 기본 경로 사용)</param>
        /// <param name="layoutMode">화면 배치 모드</param>
        /// <param name="frameWidth">입력 프레임 가로 크기</param>
        /// <param name="frameHeight">입력 프레임 세로 크기</param>
        /// <param name="fps">초당 프레임 수</param>
        /// <param name="saveIntervalMinutes">자동 저장 간격(분)</param>
        public RecordingManager(
            PressureMapViewer.MainWindow pressureMapWindow,
            string savePath = null,
            LayoutMode layoutMode = LayoutMode.VerticalStack,
            int frameWidth = 960,
            int frameHeight = 540,
            double fps = 60.0, // 기본값 60FPS로 변경
            int saveIntervalMinutes = 1)
        {
            // 기본 설정
            _pressureMapWindow = pressureMapWindow ?? throw new ArgumentNullException(nameof(pressureMapWindow));
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _fps = fps;
            _recordingIntervalMinutes = saveIntervalMinutes;
            _frameAlpha = 0.7;  // 기본 프레임 혼합 비율 (70% 현재, 30% 이전)
            _isMixingFrames = true;
            _captureWidth = frameWidth;
            _captureHeight = frameHeight;
            _layoutMode = layoutMode;

            // 레이아웃 모드에 따른 출력 해상도 설정
            switch (layoutMode)
            {
                case LayoutMode.MonitorOptimized:
                    _outputWidth = frameWidth * 2; // 1920 (웹캠 2개 가로 배치)
                    _outputHeight = frameHeight * 2; // 1080 (웹캠 위, 압력맵 아래)
                    break;
                case LayoutMode.VerticalStack:
                default:
                    _outputWidth = frameWidth; // 960
                    _outputHeight = frameHeight * 3; // 1620 (세로로 3개 쌓기)
                    break;
            }

            // 코덱 설정 (MP4V가 가장 호환성이 좋음)
            _codec = FourCC.MP4V;

            // 저장 경로 설정
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _tempFilePath = Path.Combine(desktopPath, "LastReplay.mp4");

            // 상위 클래스에서 전달받은 저장 경로 또는 기본 경로 사용
            _targetDirectory = !string.IsNullOrEmpty(savePath) ?
                savePath : Path.Combine(desktopPath, _usbDirectoryName);

            // 저장 디렉토리 생성
            if (!Directory.Exists(_targetDirectory))
            {
                Directory.CreateDirectory(_targetDirectory);
            }

            // 프레임 큐 초기화
            _frameQueue = new ConcurrentQueue<Mat>();

            // 자동 저장 타이머 초기화
            _saveTimer = new System.Threading.Timer(SaveVideoTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            // 성능 측정 시작
            _perfStopwatch.Start();
        }

        #endregion

        #region 공개 메서드

        /// <summary>
        /// 녹화 시작
        /// </summary>
        public void StartRecording()
        {
            lock (_lockObject)
            {
                if (_isRecording)
                    return;

                try
                {
                    // 상태 초기화
                    _isRecording = true;
                    _recordingStartTime = DateTime.Now;
                    _frameCount = 0;
                    _droppedFrames = 0;
                    _frameQueue = new ConcurrentQueue<Mat>();
                    _cts = new CancellationTokenSource();

                    // 화면 캡처 시작
                    StartCaptureProcess();

                    // 프레임 처리 작업 시작
                    StartFrameProcessingTask();

                    // 기존 임시 파일 삭제
                    CleanupTempFile();

                    // 비디오 작성기 초기화
                    InitializeVideoWriter();

                    // 프레임 처리 작업 시작
                    _recordingTask = Task.Run(() => ProcessFramesAsync(_cts.Token), _cts.Token);

                    // 저장 타이머 시작
                    _saveTimer.Change(TimeSpan.FromMinutes(_recordingIntervalMinutes), TimeSpan.FromMilliseconds(-1));

                    Console.WriteLine("녹화가 시작되었습니다.");
                }
                catch (Exception ex)
                {
                    _isRecording = false;
                    Console.WriteLine($"녹화 시작 중 오류 발생: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 녹화 중지
        /// </summary>
        public async Task StopRecordingAsync()
        {
            if (!_isRecording)
                return;

            lock (_lockObject)
            {
                if (!_isRecording)
                    return;

                _isRecording = false;
                _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _cts?.Cancel();
            }

            try
            {
                // 프레임 처리 중지
                await StopFrameProcessingTaskAsync();

                // 캡처 중지
                await StopCaptureProcessAsync();

                // 남은 프레임 처리 대기
                await FinishProcessingAsync();

                // 비디오 파일 저장
                await Task.Run(() =>
                {
                    SaveCurrentFrames();
                    CopyToFinalDestination();
                });

                // 자원 정리
                ReleaseLastFrames();

                Console.WriteLine($"녹화가 중지되었습니다. 총 {_frameCount}개 프레임 기록, {_droppedFrames}개 프레임 드롭됨");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"녹화 중지 중 오류 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// 프레임 추가 - 시간 지연 없이 빠르게 처리
        /// </summary>
        /// <param name="frame">추가할 프레임 데이터</param>
        public void AddFrame(FrameData frame)
        {
            // 녹화 중이 아니면 무시
            if (!_isRecording || frame == null)
                return;

            // 입력 큐에 프레임 추가 (깊은 복사를 위해 복제 필요)
            var frameCopy = frame.Clone();
            _inputFrameQueue.Enqueue(frameCopy);

            // 큐가 너무 커지면 오래된 프레임 드롭
            if (_inputFrameQueue.Count > 120) // 60fps 기준 2초 분량
            {
                if (_inputFrameQueue.TryDequeue(out var oldFrame))
                {
                    oldFrame.Dispose();
                    Interlocked.Increment(ref _droppedFrames);
                }
            }
        }

        /// <summary>
        /// 프레임 혼합 설정
        /// </summary>
        /// <param name="enable">활성화 여부</param>
        public void EnableFrameMixing(bool enable)
        {
            _isMixingFrames = enable;
        }

        /// <summary>
        /// 프레임 혼합 비율 설정
        /// </summary>
        /// <param name="alpha">현재 프레임 비율 (0.0 ~ 1.0)</param>
        public void SetFrameMixingRatio(double alpha)
        {
            _frameAlpha = Math.Clamp(alpha, 0.0, 1.0);
        }

        /// <summary>
        /// 저장 경로 설정
        /// </summary>
        /// <param name="path">저장할 디렉토리 경로</param>
        public void SetSavePath(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                if (!Directory.Exists(path))
                {
                    try
                    {
                        Directory.CreateDirectory(path);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"저장 경로 생성 중 오류: {ex.Message}");
                        return;
                    }
                }

                // 경로 업데이트
                _targetDirectory = path;
                Console.WriteLine($"저장 경로가 '{path}'(으)로 변경되었습니다.");
            }
        }

        public void SetLayoutMode(LayoutMode mode)
        {
            _layoutMode = mode;
        }

        /// <summary>
        /// USB 저장 폴더명 설정
        /// </summary>
        /// <param name="directoryName">USB에 생성할 폴더명</param>
        public void SetUsbDirectoryName(string directoryName)
        {
            if (!string.IsNullOrWhiteSpace(directoryName))
            {
                _usbDirectoryName = directoryName;
            }
        }

        /// <summary>
        /// 자원 해제
        /// </summary>
        public void Dispose()
        {
            try
            {
                // 녹화 중지
                StopRecordingAsync().Wait();

                // 타이머 정리
                _saveTimer?.Dispose();

                // 비디오 작성기 정리
                CleanupVideoWriter();

                // 프레임 정리
                ReleaseLastFrames();

                // 캔슬레이션 토큰 정리
                _cts?.Dispose();
                _captureCts?.Dispose();
                _frameProcessingCts?.Dispose();

                // 세마포어 정리
                _captureSemaphore?.Dispose();

                // 캡처 큐 정리
                ClearCaptureQueue();
                _captureQueue?.Dispose();

                // 입력 프레임 큐 정리
                ClearInputFrameQueue();

                Console.WriteLine("RecordingManager 자원이 정리되었습니다.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"자원 정리 중 오류: {ex.Message}");
            }
        }

        #endregion

        #region 내부 메서드 - 프레임 처리 최적화

        /// <summary>
        /// 입력 프레임 처리 작업 시작
        /// </summary>
        private void StartFrameProcessingTask()
        {
            if (_isFrameProcessingActive)
                return;

            _frameProcessingCts = new CancellationTokenSource();
            _isFrameProcessingActive = true;

            _frameProcessingTask = Task.Run(async () =>
            {
                try
                {
                    while (!_frameProcessingCts.Token.IsCancellationRequested)
                    {
                        // 큐에서 프레임 가져오기
                        if (_inputFrameQueue.TryDequeue(out var frameData))
                        {
                            try
                            {
                                // 배치 모드에 따라 프레임 처리
                                using (var combinedFrame = CombineFramesWithLayout(frameData))
                                {
                                    if (combinedFrame != null)
                                    {
                                        _frameQueue.Enqueue(combinedFrame.Clone());
                                        Interlocked.Increment(ref _frameCount);
                                    }
                                }

                                // 프레임 데이터 해제
                                frameData.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"프레임 처리 중 오류: {ex.Message}");
                                frameData.Dispose();
                            }
                        }
                        else
                        {
                            // 큐가 비어있으면 짧게 대기
                            await Task.Delay(1, _frameProcessingCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 정상적인 작업 취소
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"프레임 처리 작업 중 오류: {ex.Message}");
                }
                finally
                {
                    _isFrameProcessingActive = false;
                }
            }, _frameProcessingCts.Token);
        }

        /// <summary>
        /// 입력 프레임 처리 작업 중지
        /// </summary>
        private async Task StopFrameProcessingTaskAsync()
        {
            if (!_isFrameProcessingActive)
                return;

            _frameProcessingCts?.Cancel();

            try
            {
                // 최대 2초 대기
                if (_frameProcessingTask != null)
                {
                    var timeoutTask = Task.Delay(2000);
                    await Task.WhenAny(_frameProcessingTask, timeoutTask);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 작업 중지 중 오류: {ex.Message}");
            }
            finally
            {
                _isFrameProcessingActive = false;

                // 남은 프레임 정리
                ClearInputFrameQueue();
            }
        }

        /// <summary>
        /// 입력 프레임 큐 정리
        /// </summary>
        private void ClearInputFrameQueue()
        {
            while (_inputFrameQueue.TryDequeue(out var frame))
            {
                try
                {
                    frame?.Dispose();
                }
                catch { }
            }
        }

        /// <summary>
        /// 레이아웃 모드에 따라 프레임 결합
        /// </summary>
        private Mat CombineFramesWithLayout(FrameData frameData)
        {
            switch (_layoutMode)
            {
                case LayoutMode.MonitorOptimized:
                    return CombineFramesMonitorOptimized(frameData);
                case LayoutMode.VerticalStack:
                default:
                    return CombineFramesVertical(frameData);
            }
        }

        /// <summary>
        /// PC 모니터 최적화 레이아웃으로 프레임 결합 (1920x1080)
        /// </summary>
        private Mat CombineFramesMonitorOptimized(FrameData frameData)
        {
            try
            {
                // 출력 이미지 생성 (1920x1080)
                Mat combinedFrame = new Mat(_outputHeight, _outputWidth, MatType.CV_8UC3, Scalar.Black);

                // 웹캠 이미지 영역 계산
                Rect headRect = new Rect(0, 0, _frameWidth, _frameHeight);  // 좌측 상단
                Rect bodyRect = new Rect(_frameWidth, 0, _frameWidth, _frameHeight);  // 우측 상단
                Rect pressureRect = new Rect(0, _frameHeight, _frameWidth, _frameHeight);  // 하단 전체

                // 헤드 카메라 프레임 처리 (좌측 상단)
                ProcessHeadFrameWithRect(frameData, combinedFrame, headRect);

                // 바디 카메라 프레임 처리 (우측 상단)
                ProcessBodyFrameWithRect(frameData, combinedFrame, bodyRect);

                // 압력 맵 프레임 처리 (하단 전체)
                ProcessPressureViewWithRect(frameData, combinedFrame, pressureRect, true); // 가로로 넓게 표시

                return combinedFrame;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"모니터 최적화 프레임 결합 중 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 기존 세로 스택 레이아웃으로 프레임 결합 (960x1620)
        /// </summary>
        private Mat CombineFramesVertical(FrameData frameData)
        {
            try
            {
                // 출력할 결합된 이미지 생성 (세로로 3개 적층)
                Mat combinedFrame = new Mat(_outputHeight, _outputWidth, MatType.CV_8UC3, Scalar.Black);

                // 영역 계산
                Rect headRect = new Rect(0, 0, _frameWidth, _frameHeight);  // 상단
                Rect bodyRect = new Rect(0, _frameHeight, _frameWidth, _frameHeight);  // 중간
                Rect pressureRect = new Rect(0, _frameHeight * 2, _frameWidth, _frameHeight);  // 하단

                // Head 프레임 처리 (상단)
                ProcessHeadFrameWithRect(frameData, combinedFrame, headRect);

                // Body 프레임 처리 (중간)
                ProcessBodyFrameWithRect(frameData, combinedFrame, bodyRect);

                // Pressure 화면 처리 (하단)
                ProcessPressureViewWithRect(frameData, combinedFrame, pressureRect, false);

                return combinedFrame;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"세로 스택 프레임 결합 중 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 헤드 카메라 프레임 처리 (지정 영역)
        /// </summary>
        private void ProcessHeadFrameWithRect(FrameData frameData, Mat combinedFrame, Rect targetRect)
        {
            if (frameData.WebcamHead == null || frameData.WebcamHead.Empty())
            {
                // 현재 프레임이 없고 이전 프레임이 있으면 이전 프레임 사용
                if (_lastHeadFrame != null && !_lastHeadFrame.Empty())
                {
                    Mat resized = new Mat();
                    Cv2.Resize(_lastHeadFrame, resized, new Size(targetRect.Width, targetRect.Height));

                    Mat headRegion = new Mat(combinedFrame, targetRect);
                    resized.CopyTo(headRegion);

                    resized.Dispose();
                }
                return;
            }

            // 헤드 프레임 리사이징
            Mat currentResized = new Mat();
            try
            {
                Cv2.Resize(frameData.WebcamHead, currentResized, new Size(targetRect.Width, targetRect.Height));

                // 깜빡임 방지를 위한 프레임 혼합
                if (_isMixingFrames && _lastHeadFrame != null && !_lastHeadFrame.Empty())
                {
                    try
                    {
                        Mat lastResized = new Mat();
                        Cv2.Resize(_lastHeadFrame, lastResized, new Size(targetRect.Width, targetRect.Height));

                        Mat blendedHead = new Mat();
                        Cv2.AddWeighted(currentResized, _frameAlpha, lastResized, 1.0 - _frameAlpha, 0, blendedHead);

                        // 혼합된 프레임 복사
                        Mat headRegion = new Mat(combinedFrame, targetRect);
                        blendedHead.CopyTo(headRegion);

                        blendedHead.Dispose();
                        lastResized.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"헤드 프레임 혼합 중 오류: {ex.Message}");

                        // 오류 시 현재 프레임만 사용
                        Mat headRegion = new Mat(combinedFrame, targetRect);
                        currentResized.CopyTo(headRegion);
                    }
                }
                else
                {
                    // 혼합 없이 현재 프레임 사용
                    Mat headRegion = new Mat(combinedFrame, targetRect);
                    currentResized.CopyTo(headRegion);
                }

                // 이전 프레임 업데이트 (원본 크기로 저장)
                SafeDisposeAndUpdate(ref _lastHeadFrame, frameData.WebcamHead.Clone());
                currentResized.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"헤드 프레임 처리 중 오류: {ex.Message}");
                currentResized?.Dispose();
            }
        }

        /// <summary>
        /// 바디 카메라 프레임 처리 (지정 영역)
        /// </summary>
        private void ProcessBodyFrameWithRect(FrameData frameData, Mat combinedFrame, Rect targetRect)
        {
            if (frameData.WebcamBody == null || frameData.WebcamBody.Empty())
            {
                // 현재 프레임이 없고 이전 프레임이 있으면 이전 프레임 사용
                if (_lastBodyFrame != null && !_lastBodyFrame.Empty())
                {
                    Mat resized = new Mat();
                    Cv2.Resize(_lastBodyFrame, resized, new Size(targetRect.Width, targetRect.Height));

                    Mat bodyRegion = new Mat(combinedFrame, targetRect);
                    resized.CopyTo(bodyRegion);

                    resized.Dispose();
                }
                return;
            }

            // 바디 프레임 리사이징
            Mat resizedBody = new Mat();
            try
            {
                Cv2.Resize(frameData.WebcamBody, resizedBody, new Size(targetRect.Width, targetRect.Height));

                // 깜빡임 방지를 위한 프레임 혼합
                if (_isMixingFrames && _lastBodyFrame != null && !_lastBodyFrame.Empty())
                {
                    try
                    {
                        Mat lastResized = new Mat();
                        Cv2.Resize(_lastBodyFrame, lastResized, new Size(targetRect.Width, targetRect.Height));

                        Mat blendedBody = new Mat();
                        Cv2.AddWeighted(resizedBody, _frameAlpha, lastResized, 1.0 - _frameAlpha, 0, blendedBody);

                        // 혼합된 프레임 복사
                        Mat bodyRegion = new Mat(combinedFrame, targetRect);
                        blendedBody.CopyTo(bodyRegion);

                        blendedBody.Dispose();
                        lastResized.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"바디 프레임 혼합 중 오류: {ex.Message}");

                        // 오류 시 현재 프레임만 사용
                        Mat bodyRegion = new Mat(combinedFrame, targetRect);
                        resizedBody.CopyTo(bodyRegion);
                    }
                }
                else
                {
                    // 혼합 없이 현재 프레임 사용
                    Mat bodyRegion = new Mat(combinedFrame, targetRect);
                    resizedBody.CopyTo(bodyRegion);
                }

                // 이전 프레임 업데이트 (원본 크기로 저장)
                SafeDisposeAndUpdate(ref _lastBodyFrame, frameData.WebcamBody.Clone());
                resizedBody.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"바디 프레임 처리 중 오류: {ex.Message}");
                resizedBody?.Dispose();
            }
        }
        /// <summary>
        /// 압력 데이터 처리 및 PressureMapViewer 화면 캡처 (지정 영역)
        /// </summary>
        private void ProcessPressureViewWithRect(FrameData frameData, Mat combinedFrame, Rect targetRect, bool isWideMode)
        {
            try
            {
                // 캡처 큐에서 가장 최신 이미지 가져오기
                Bitmap captureBitmap = GetLatestCapture();
                Mat captureFrame = null;

                if (captureBitmap != null)
                {
                    try
                    {
                        // 비트맵을 Mat으로 변환
                        captureFrame = BitmapConverter.ToMat(captureBitmap);

                        // 크기 조정
                        if (captureFrame.Width != targetRect.Width || captureFrame.Height != targetRect.Height)
                        {
                            Mat resized = new Mat();
                            Cv2.Resize(captureFrame, resized, new Size(targetRect.Width, targetRect.Height));
                            captureFrame.Dispose();
                            captureFrame = resized;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"캡처 비트맵 변환 중 오류: {ex.Message}");
                        captureFrame?.Dispose();
                        captureFrame = null;
                    }
                    finally
                    {
                        // 사용한 비트맵은 정리
                        captureBitmap.Dispose();
                    }
                }

                // 캡처 실패 시 압력 데이터 직접 시각화
                if (captureFrame == null && frameData.PressureData != null)
                {
                    captureFrame = VisualizePressureData(frameData.PressureData, targetRect.Width, targetRect.Height, isWideMode);
                }

                // 이미지 처리 및 혼합
                if (captureFrame != null)
                {
                    try
                    {
                        // 깜빡임 방지를 위한 프레임 혼합
                        if (_isMixingFrames && _lastPressureFrame != null && !_lastPressureFrame.Empty())
                        {
                            try
                            {
                                Mat lastResized = new Mat();
                                Cv2.Resize(_lastPressureFrame, lastResized, new Size(targetRect.Width, targetRect.Height));

                                Mat blendedPressure = new Mat();
                                Cv2.AddWeighted(captureFrame, _frameAlpha, lastResized, 1.0 - _frameAlpha, 0, blendedPressure);

                                // 혼합된 프레임 복사
                                Mat pressureRegion = new Mat(combinedFrame, targetRect);
                                blendedPressure.CopyTo(pressureRegion);

                                blendedPressure.Dispose();
                                lastResized.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"압력 프레임 혼합 중 오류: {ex.Message}");

                                // 오류 시 현재 프레임만 사용
                                Mat pressureRegion = new Mat(combinedFrame, targetRect);
                                captureFrame.CopyTo(pressureRegion);
                            }
                        }
                        else
                        {
                            // 혼합 없이 현재 프레임 사용
                            Mat pressureRegion = new Mat(combinedFrame, targetRect);
                            captureFrame.CopyTo(pressureRegion);
                        }

                        // 압력맵 프레임 저장 (원본 크기 저장)
                        if (_lastPressureFrame == null || _lastPressureFrame.Empty())
                        {
                            // 첫 프레임은 그대로 저장
                            SafeDisposeAndUpdate(ref _lastPressureFrame, captureFrame.Clone());
                        }
                        else
                        {
                            // 그 외에는 원본 크기 확인 후 필요시 리사이징
                            if (_lastPressureFrame.Width != captureFrame.Width || _lastPressureFrame.Height != captureFrame.Height)
                            {
                                Mat resized = new Mat();
                                Cv2.Resize(captureFrame, resized, new Size(_lastPressureFrame.Width, _lastPressureFrame.Height));
                                SafeDisposeAndUpdate(ref _lastPressureFrame, resized);
                            }
                            else
                            {
                                SafeDisposeAndUpdate(ref _lastPressureFrame, captureFrame.Clone());
                            }
                        }
                    }
                    finally
                    {
                        captureFrame.Dispose();
                    }
                }
                else if (_lastPressureFrame != null && !_lastPressureFrame.Empty())
                {
                    // 이미지가 없을 경우 이전 프레임 사용
                    Mat resizedPressure = new Mat();
                    Cv2.Resize(_lastPressureFrame, resizedPressure, new Size(targetRect.Width, targetRect.Height));

                    Mat pressureRegion = new Mat(combinedFrame, targetRect);
                    resizedPressure.CopyTo(pressureRegion);

                    resizedPressure.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"압력 뷰 처리 중 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 압력 데이터를 시각화하여 Mat 이미지로 변환
        /// </summary>
        /// <param name="pressureData">압력 데이터 배열</param>
        /// <param name="width">출력 이미지 너비</param>
        /// <param name="height">출력 이미지 높이</param>
        /// <param name="isWideMode">와이드 모드 여부 (true=가로로 넓게, false=기본)</param>
        private Mat VisualizePressureData(ushort[] pressureData, int width = 0, int height = 0, bool isWideMode = false)
        {
            if (pressureData == null || pressureData.Length == 0)
                return null;

            try
            {
                // 출력 크기 설정 (지정되지 않은 경우 기본값 사용)
                int outputWidth = width > 0 ? width : _frameWidth;
                int outputHeight = height > 0 ? height : _frameHeight;

                Mat pressureFrame = new Mat(outputHeight, outputWidth, MatType.CV_8UC3, Scalar.Black);
                int dataSize = 96;  // 96x96 압력 데이터 크기

                // 와이드 모드에 따른 레이아웃 조정
                int cellSize;
                int offsetX, offsetY;

                if (isWideMode)
                {
                    // 넓은 화면에 맞게 압력 맵 표시 (더 크게 표시)
                    cellSize = Math.Min(outputWidth / dataSize, outputHeight / dataSize) * 2;

                    // 화면 중앙에 위치
                    offsetX = (outputWidth - (cellSize * dataSize)) / 2;
                    offsetY = (outputHeight - (cellSize * dataSize)) / 2;

                    // 화면에 맞지 않는 경우 추가 조정
                    if (offsetX < 0 || offsetY < 0)
                    {
                        cellSize = Math.Max(1, Math.Min(outputWidth / dataSize, outputHeight / dataSize));
                        offsetX = Math.Max(0, (outputWidth - (cellSize * dataSize)) / 2);
                        offsetY = Math.Max(0, (outputHeight - (cellSize * dataSize)) / 2);
                    }
                }
                else
                {
                    // 기본 모드 (이전과 동일)
                    cellSize = Math.Min(outputWidth, outputHeight) / dataSize;
                    offsetX = (outputWidth - (cellSize * dataSize)) / 2;
                    offsetY = (outputHeight - (cellSize * dataSize)) / 2;
                }

                // 최대값 찾기
                ushort maxValue = 0;
                foreach (var value in pressureData)
                {
                    if (value > maxValue) maxValue = value;
                }

                // 값이 너무 작으면 기본값 설정
                if (maxValue < 100) maxValue = 1024;

                // 압력 데이터 시각화
                for (int y = 0; y < dataSize; y++)
                {
                    for (int x = 0; x < dataSize; x++)
                    {
                        int index = y * dataSize + x;
                        if (index < pressureData.Length)
                        {
                            // 값을 0~1 사이로 정규화
                            float normalizedValue = Math.Min(1.0f, pressureData[index] / (float)maxValue);

                            // 낮은 값은 스킵 (노이즈 제거)
                            if (normalizedValue < 0.02) continue;

                            // PressureMapViewer와 동일한 색상 매핑
                            Scalar color;
                            if (normalizedValue < 0.1f)
                                color = new Scalar(255, 0, (byte)(255 * (normalizedValue / 0.1f)));
                            else if (normalizedValue < 0.2f)
                                color = new Scalar(255, (byte)(255 * ((normalizedValue - 0.1f) / 0.1f)), 255);
                            else if (normalizedValue < 0.4f)
                                color = new Scalar((byte)(255 * ((normalizedValue - 0.2f) / 0.2f)), 255, (byte)(255 * (1 - (normalizedValue - 0.2f) / 0.2f)));
                            else if (normalizedValue < 0.8f)
                                color = new Scalar(0, (byte)(255 * (1 - (normalizedValue - 0.4f) / 0.4f)), 255);
                            else
                                color = new Scalar(0, 0, 255);

                            // 셀 위치 계산 및 그리기
                            int px = offsetX + x * cellSize;
                            int py = offsetY + y * cellSize;

                            if (cellSize > 1)
                            {
                                Rect cellRect = new Rect(px, py, cellSize, cellSize);
                                pressureFrame.Rectangle(cellRect, color, -1);
                            }
                            else
                            {
                                if (px >= 0 && px < pressureFrame.Width && py >= 0 && py < pressureFrame.Height)
                                {
                                    pressureFrame.Set(py, px, color);
                                }
                            }
                        }
                    }
                }

                // 히트맵 영역에 테두리 추가
                Rect borderRect = new Rect(offsetX, offsetY, cellSize * dataSize, cellSize * dataSize);
                pressureFrame.Rectangle(borderRect, new Scalar(150, 150, 150), 1);

                return pressureFrame;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"압력 데이터 시각화 중 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// [이전 버전 호환성] 프레임 데이터를 결합하여 단일 이미지로 만듦
        /// </summary>
        private Mat CombineFrames(FrameData frameData)
        {
            // 레이아웃 모드에 따라 적절한 메서드 호출
            return CombineFramesWithLayout(frameData);
        }

        #endregion

        #region 내부 메서드 - 화면 캡처

        /// <summary>
        /// 화면 캡처 프로세스 시작
        /// </summary>
        private void StartCaptureProcess()
        {
            // 이미 실행 중이면 중단
            if (_isCaptureActive) return;

            _captureCts = new CancellationTokenSource();
            _isCaptureActive = true;

            // 캡처 큐 초기화
            ClearCaptureQueue();

            // 스크린샷 캡처 작업 시작
            _captureTask = Task.Run(async () =>
            {
                try
                {
                    while (!_captureCts.Token.IsCancellationRequested)
                    {
                        // 캡처 간격 조절을 위한 대기
                        await Task.Delay(_captureInterval, _captureCts.Token);

                        // 캡처 수행
                        await CaptureScreenshotAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 정상적인 취소
                    Console.WriteLine("화면 캡처 작업이 취소되었습니다.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"화면 캡처 중 오류: {ex.Message}");
                }
                finally
                {
                    _isCaptureActive = false;
                }
            }, _captureCts.Token);
        }

        /// <summary>
        /// 화면 캡처 프로세스 중지
        /// </summary>
        private async Task StopCaptureProcessAsync()
        {
            if (!_isCaptureActive) return;

            // 취소 요청
            _captureCts?.Cancel();

            try
            {
                // 작업 완료 대기 (최대 2초)
                if (_captureTask != null)
                {
                    var timeoutTask = Task.Delay(2000);
                    var completedTask = await Task.WhenAny(_captureTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        Console.WriteLine("화면 캡처 작업 종료 대기 시간 초과");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"캡처 프로세스 중지 중 오류: {ex.Message}");
            }
            finally
            {
                _isCaptureActive = false;
                _captureTask = null;
            }

            // 캡처 큐 정리
            ClearCaptureQueue();
        }

        /// <summary>
        /// WPF 윈도우의 스크린샷 캡처
        /// </summary>
        private async Task CaptureScreenshotAsync()
        {
            // 세마포어 획득 시도 (동시 캡처 방지)
            if (!await _captureSemaphore.WaitAsync(100))
                return;

            try
            {
                // UI 스레드에서 실행
                await _pressureMapWindow.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 윈도우 크기 확인
                        if (_pressureMapWindow.ActualWidth <= 0 || _pressureMapWindow.ActualHeight <= 0)
                            return;

                        // 윈도우 렌더링 캡처
                        RenderTargetBitmap renderTarget = new RenderTargetBitmap(
                            (int)_pressureMapWindow.ActualWidth,
                            (int)_pressureMapWindow.ActualHeight,
                            96, 96, PixelFormats.Pbgra32);

                        renderTarget.Render(_pressureMapWindow);

                        // 비트맵으로 변환
                        BitmapEncoder encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(renderTarget));

                        using (MemoryStream stream = new MemoryStream())
                        {
                            encoder.Save(stream);
                            stream.Position = 0;

                            // System.Drawing.Bitmap으로 변환
                            using (var originalBitmap = new Bitmap(stream))
                            {
                                // 리사이징
                                var resizedBitmap = ResizeWithAspectRatio(originalBitmap, _captureWidth, _captureHeight);

                                // 큐가 가득 차면 가장 오래된 항목 제거
                                while (_captureQueue.Count >= 3) // 큐 크기 증가 (60fps 지원)
                                {
                                    if (_captureQueue.TryTake(out Bitmap oldBitmap))
                                        oldBitmap?.Dispose();
                                }

                                // 큐에 추가
                                _captureQueue.Add(resizedBitmap);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UI 스레드 화면 캡처 중 오류: {ex.Message}");
                    }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"화면 캡처 작업 디스패처 오류: {ex.Message}");
            }
            finally
            {
                // 세마포어 해제
                _captureSemaphore.Release();
            }
        }

        /// <summary>
        /// 종횡비를 유지하면서 비트맵 리사이징
        /// </summary>
        private Bitmap ResizeWithAspectRatio(Bitmap original, int maxWidth, int maxHeight)
        {
            // 원본 크기
            int originalWidth = original.Width;
            int originalHeight = original.Height;

            // 종횡비 유지 크기 계산
            double ratio = Math.Min((double)maxWidth / originalWidth, (double)maxHeight / originalHeight);
            int newWidth = (int)(originalWidth * ratio);
            int newHeight = (int)(originalHeight * ratio);

            // 새 비트맵 생성
            Bitmap result = new Bitmap(maxWidth, maxHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            // 배경을 검은색으로 채우기
            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(System.Drawing.Color.Black);

                // 고품질 이미지 처리 설정
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                // 이미지를 중앙에 그리기
                int posX = (maxWidth - newWidth) / 2;
                int posY = (maxHeight - newHeight) / 2;

                g.DrawImage(original, posX, posY, newWidth, newHeight);
            }

            return result;
        }

        /// <summary>
        /// 최신 캡처 이미지 가져오기
        /// </summary>
        private Bitmap GetLatestCapture()
        {
            if (_captureQueue.Count == 0)
                return null;

            Bitmap result = null;

            try
            {
                // BlockingCollection에서 최신 항목 가져오기 (배열로 변환)
                Bitmap[] items = _captureQueue.ToArray();
                if (items.Length > 0)
                {
                    // 가장 마지막 항목이 최신 항목
                    Bitmap latestBitmap = items[items.Length - 1];
                    if (latestBitmap != null)
                    {
                        // 복제본 반환
                        result = (Bitmap)latestBitmap.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"최신 캡처 가져오기 중 오류: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 캡처 큐 정리
        /// </summary>
        private void ClearCaptureQueue()
        {
            try
            {
                while (_captureQueue.Count > 0)
                {
                    if (_captureQueue.TryTake(out Bitmap bitmap))
                        bitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"캡처 큐 정리 중 오류: {ex.Message}");
            }
        }

        #endregion

        #region 내부 메서드 - 비디오 처리

        /// <summary>
        /// 임시 파일 정리
        /// </summary>
        private void CleanupTempFile()
        {
            if (File.Exists(_tempFilePath))
            {
                try
                {
                    File.Delete(_tempFilePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"기존 임시 파일 삭제 중 오류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 비디오 작성기 초기화
        /// </summary>
        private void InitializeVideoWriter()
        {
            lock (_lockObject)
            {
                try
                {
                    // 이전 작성기가 있으면 정리
                    if (_videoWriter != null)
                    {
                        CleanupVideoWriter();
                    }

                    // 임시 파일 경로 확인
                    string directory = Path.GetDirectoryName(_tempFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 임시 파일이 존재하면 삭제
                    if (File.Exists(_tempFilePath))
                    {
                        try
                        {
                            File.Delete(_tempFilePath);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"임시 파일 삭제 중 오류: {ex.Message}");
                        }
                    }

                    // 더 안정적인 코덱 시도 순서
                    FourCC[] codecsToTry = new FourCC[] {
                        FourCC.DIVX,  // DivX 코덱
                        FourCC.XVID,  // Xvid 코덱
                        FourCC.MJPG,  // Motion JPEG
                        FourCC.MP4V   // 마지막 시도로 MP4V
                    };

                    Exception lastException = null;
                    foreach (var codec in codecsToTry)
                    {
                        try
                        {
                            // 비디오 작성기 초기화 (레이아웃 모드에 따라 해상도 다름)
                            _videoWriter = new VideoWriter(
                                _tempFilePath,
                                codec,
                                _fps,
                                new Size(_outputWidth, _outputHeight)
                            );

                            if (_videoWriter.IsOpened())
                            {
                                Console.WriteLine($"비디오 작성기가 성공적으로 초기화되었습니다. 코덱: {codec}, 해상도: {_outputWidth}x{_outputHeight}, FPS: {_fps}");
                                return; // 성공하면 반복 종료
                            }
                            else
                            {
                                // 열리지 않으면 다음 코덱 시도
                                Console.WriteLine($"{codec} 코덱을 사용할 수 없습니다. 다른 코덱을 시도합니다.");
                                _videoWriter.Dispose();
                                _videoWriter = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            Console.WriteLine($"{codec} 코덱 초기화 중 오류: {ex.Message}");

                            if (_videoWriter != null)
                            {
                                _videoWriter.Dispose();
                                _videoWriter = null;
                            }
                        }
                    }

                    // 모든 코덱이 실패한 경우
                    if (_videoWriter == null)
                    {
                        string errorMsg = "모든 코덱 시도가 실패했습니다. 비디오 작성기를 초기화할 수 없습니다.";
                        Console.WriteLine(errorMsg);
                        throw new Exception(errorMsg, lastException);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"비디오 작성기 초기화 중 치명적 오류: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 최종 비디오 프레임 처리 메인 루프
        /// </summary>
        private async Task ProcessFramesAsync(CancellationToken token)
        {
            try
            {
                int frameCounter = 0;
                bool errorReported = false; // 에러 중복 보고 방지

                while (!token.IsCancellationRequested || !_frameQueue.IsEmpty)
                {
                    // 작업 취소 요청 시 루프 종료
                    if (token.IsCancellationRequested && _frameQueue.IsEmpty)
                        break;

                    Mat frame = null;
                    bool dequeued = false;

                    try
                    {
                        // 프레임 가져오기 시도
                        dequeued = _frameQueue.TryDequeue(out frame);

                        if (dequeued && frame != null && !frame.Empty())
                        {
                            // 스레드 안전을 위한 락 사용
                            lock (_lockObject)
                            {
                                // null 체크와 IsOpened 확인을 반드시 수행
                                if (_videoWriter != null && _videoWriter.IsOpened())
                                {
                                    try
                                    {
                                        // 프레임 크기와 타입 확인 (레이아웃 모드에 따라 다름)
                                        if (frame.Width == _outputWidth && frame.Height == _outputHeight)
                                        {
                                            _videoWriter.Write(frame);
                                            frameCounter++;

                                            // 에러 플래그 리셋
                                            errorReported = false;

                                            // 주기적으로 로그 기록
                                            if (frameCounter % 100 == 0)
                                            {
                                                //Console.WriteLine($"영상에 {frameCounter}개 프레임이 기록되었습니다.");
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine($"프레임 크기 불일치: 예상 {_outputWidth}x{_outputHeight}, 실제 {frame.Width}x{frame.Height}");
                                        }
                                    }
                                    catch (AccessViolationException avEx)
                                    {
                                        // 심각한 오류 발생 - 비디오 작성기 재초기화 필요
                                        if (!errorReported)
                                        {
                                            Console.WriteLine($"비디오 작성 중 메모리 오류 발생: {avEx.Message}");
                                            Console.WriteLine("비디오 작성기를 재초기화합니다...");
                                            errorReported = true;
                                        }

                                        // 비디오 작성기 재생성 시도
                                        try
                                        {
                                            CleanupVideoWriter();
                                            InitializeVideoWriter();
                                        }
                                        catch (Exception reinitEx)
                                        {
                                            Console.WriteLine($"비디오 작성기 재초기화 실패: {reinitEx.Message}");
                                        }

                                        // 약간의 대기 후 계속 진행
                                        Task.Delay(100, token).Wait();
                                    }
                                }
                                else
                                {
                                    // 비디오 작성기가 없는 경우
                                    if (!errorReported)
                                    {
                                        Console.WriteLine("비디오 작성기가 초기화되지 않았거나 닫혔습니다. 재초기화를 시도합니다.");
                                        errorReported = true;

                                        try
                                        {
                                            CleanupVideoWriter();
                                            InitializeVideoWriter();
                                        }
                                        catch (Exception initEx)
                                        {
                                            Console.WriteLine($"비디오 작성기 초기화 실패: {initEx.Message}");
                                        }
                                    }
                                }
                            } // lock 종료
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!errorReported)
                        {
                            Console.WriteLine($"프레임 처리 중 오류: {ex.Message}");
                            errorReported = true;
                        }
                    }
                    finally
                    {
                        // 프레임 해제
                        if (dequeued && frame != null)
                        {
                            try
                            {
                                frame.Dispose();
                            }
                            catch { }
                        }
                    }

                    // 큐가 비어있거나 에러 발생 시 잠시 대기
                    if (!dequeued || errorReported)
                    {
                        try
                        {
                            await Task.Delay(10, token); // 더 짧은 대기 시간으로 FPS 유지 (60fps: ~16.6ms)
                        }
                        catch (OperationCanceledException)
                        {
                            break; // 취소 요청 시 즉시 종료
                        }
                    }
                }

                Console.WriteLine($"프레임 처리 완료: 총 {frameCounter}개 프레임이 저장되었습니다.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("프레임 처리 작업이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 중 치명적 오류: {ex.Message}");
                Console.WriteLine($"스택 트레이스: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 현재 프레임 저장
        /// </summary>
        private void SaveCurrentFrames()
        {
            lock (_lockObject)
            {
                // 비디오 작성기가 열려있는지 확인
                if (_videoWriter == null || !_videoWriter.IsOpened())
                {
                    Console.WriteLine("비디오 작성기가 준비되지 않았습니다. 프레임을 저장할 수 없습니다.");
                    return;
                }

                try
                {
                    // 현재 큐의 모든 프레임을 임시 큐로 복사
                    ConcurrentQueue<Mat> tempQueue = new ConcurrentQueue<Mat>();

                    // 큐 이동
                    Mat frame;
                    while (_frameQueue.TryDequeue(out frame))
                    {
                        if (frame != null)
                        {
                            tempQueue.Enqueue(frame);
                        }
                    }

                    // 임시 큐의 모든 프레임을 비디오에 기록
                    int count = 0;
                    int errorCount = 0;

                    while (tempQueue.TryDequeue(out frame))
                    {
                        try
                        {
                            if (frame != null && !frame.Empty() && _videoWriter.IsOpened())
                            {
                                _videoWriter.Write(frame);
                                count++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            if (errorCount <= 3) // 처음 몇 개의 오류만 보고
                            {
                                Console.WriteLine($"프레임 저장 중 오류: {ex.Message}");
                            }
                        }
                        finally
                        {
                            try
                            {
                                frame?.Dispose();
                            }
                            catch { }
                        }
                    }

                    Console.WriteLine($"총 {count}개 프레임이 비디오에 기록되었습니다. {errorCount}개 오류 발생.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"현재 프레임 저장 중 오류: {ex.Message}");
                }
                finally
                {
                    // 비디오 작성기 정리는 별도로 호출되므로 여기서는 하지 않음
                    // 대신 닫기 전에 파일을 완료하기 위해 Flush() 시도
                    try
                    {
                        if (_videoWriter != null && _videoWriter.IsOpened())
                        {
                            _videoWriter.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"비디오 파일 완료 중 오류: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 비디오 작성기 정리
        /// </summary>
        private void CleanupVideoWriter()
        {
            lock (_lockObject)
            {
                if (_videoWriter != null)
                {
                    try
                    {
                        if (_videoWriter.IsOpened())
                        {
                            _videoWriter.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"비디오 작성기 릴리스 중 오류: {ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            _videoWriter.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"비디오 작성기 해제 중 오류: {ex.Message}");
                        }
                        _videoWriter = null;
                    }
                }
            }
        }

        /// <summary>
        /// 최종 대상으로 비디오 파일 복사
        /// </summary>
        private void CopyToFinalDestination()
        {
            if (!File.Exists(_tempFilePath))
                return;

            try
            {
                // 파일명 생성
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string finalFileName = $"Recording_{timestamp}.mp4";

                // USB 메모리 감지 및 복사 시도
                bool copiedToUsb = TryCopyToUsbDrive(finalFileName);

                // USB 복사 실패 시 로컬 폴더에 복사
                if (!copiedToUsb)
                {
                    string localFilePath = Path.Combine(_targetDirectory, finalFileName);

                    // 디렉토리 확인
                    Directory.CreateDirectory(Path.GetDirectoryName(localFilePath));

                    // 파일 복사
                    File.Copy(_tempFilePath, localFilePath, true);

                    Console.WriteLine($"녹화 파일이 로컬 폴더에 저장되었습니다: {localFilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"최종 대상으로 파일 복사 중 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// USB 드라이브에 파일 복사 시도
        /// </summary>
        /// <param name="fileName">저장할 파일명</param>
        /// <returns>성공 여부</returns>
        private bool TryCopyToUsbDrive(string fileName)
        {
            try
            {
                // 사용 가능한 USB 드라이브 찾기
                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo drive in drives)
                {
                    // 이동식 드라이브(USB) 확인
                    if (drive.DriveType == DriveType.Removable && drive.IsReady)
                    {
                        try
                        {
                            // USB 드라이브 내 저장 폴더 경로
                            string usbFolderPath = Path.Combine(drive.RootDirectory.FullName, _usbDirectoryName);

                            // 폴더가 없으면 생성
                            if (!Directory.Exists(usbFolderPath))
                            {
                                Directory.CreateDirectory(usbFolderPath);
                            }

                            // 저장 경로
                            string destinationPath = Path.Combine(usbFolderPath, fileName);

                            // 여유 공간 확인
                            FileInfo tempFile = new FileInfo(_tempFilePath);
                            if (drive.AvailableFreeSpace > tempFile.Length * 1.1) // 10% 여유 공간 추가
                            {
                                // 파일 복사
                                File.Copy(_tempFilePath, destinationPath, true);
                                Console.WriteLine($"녹화 파일이 USB 드라이브({drive.Name})에 저장되었습니다: {destinationPath}");
                                return true;
                            }
                            else
                            {
                                Console.WriteLine($"USB 드라이브({drive.Name})의 여유 공간이 부족합니다. 필요: {tempFile.Length:N0} 바이트, 가용: {drive.AvailableFreeSpace:N0} 바이트");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"USB 드라이브({drive.Name})에 복사 중 오류: {ex.Message}");
                            // 계속해서 다른 드라이브 시도
                        }
                    }
                }

                // 모든 USB 드라이브 시도 실패
                Console.WriteLine("사용 가능한 USB 드라이브를 찾을 수 없습니다. 로컬 저장소에 저장합니다.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"USB 드라이브 검색 중 오류: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 자동 저장 타이머 콜백
        /// </summary>
        private void SaveVideoTimerCallback(object state)
        {
            try
            {
                // 녹화 중지되었으면 작업 중단
                if (!_isRecording)
                    return;

                // 현재 비디오 저장
                SaveCurrentFrames();

                // 최종 대상으로 복사
                CopyToFinalDestination();

                // 새 비디오 작성기 초기화
                InitializeVideoWriter();

                // 타이머 재설정
                if (_isRecording)
                {
                    _saveTimer.Change(TimeSpan.FromMinutes(_recordingIntervalMinutes), TimeSpan.FromMilliseconds(-1));
                }

                Console.WriteLine($"자동 저장 완료. 다음 저장까지 {_recordingIntervalMinutes}분");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"자동 저장 중 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 남은 프레임 처리 완료 대기
        /// </summary>
        private async Task FinishProcessingAsync()
        {
            try
            {
                // 프레임 처리 작업이 없으면 즉시 반환
                if (_recordingTask == null)
                    return;

                // 최대 5초 대기
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(_recordingTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Console.WriteLine("프레임 처리 완료 대기 시간 초과");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 완료 대기 중 오류: {ex.Message}");
            }
        }

        #endregion

        #region 내부 메서드 - 유틸리티

        /// <summary>
        /// 안전하게 Mat 객체 해제 및 업데이트
        /// </summary>
        private void SafeDisposeAndUpdate(ref Mat target, Mat newValue)
        {
            Mat oldFrame = Interlocked.Exchange(ref target, newValue);
            oldFrame?.Dispose();
        }

        /// <summary>
        /// 마지막 프레임 정리
        /// </summary>
        private void ReleaseLastFrames()
        {
            SafeDisposeAndUpdate(ref _lastHeadFrame, null);
            SafeDisposeAndUpdate(ref _lastBodyFrame, null);
            SafeDisposeAndUpdate(ref _lastPressureFrame, null);
        }

        #endregion
    }
}