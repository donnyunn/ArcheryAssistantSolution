using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SharpDX;
using System.Collections.Concurrent;

namespace MultiWebcamApp
{
    /// <summary>
    /// 모든 프레임 소스의 공통 인터페이스
    /// </summary>
    public interface IFrameProvider
    {
        (Bitmap frame, long timestamp) GetCurrentFrame();
    }

    /// <summary>
    /// 영상 녹화 및 저장을 관리하는 클래스
    /// </summary>
    public class RecordingManager : IDisposable, ICameraControl
    {
        private List<IFrameProvider> _frameSources;
        private string _savePath;
        private bool _isRecordingRequested = false; // 녹화 요청 플래그
        private bool _isActuallyRecording = false; // 실제 녹화 진행 중 플래그
        private const int MAX_FILES = 50; // 최대 저장 파일 수
        private const int FILE_ROTATION_MINUTES = 5; // 파일 교체 주기 (분)
        private const int FRAME_WIDTH = 960;
        private const int FRAME_HEIGHT = 1620; // 540 * 3 (세 개의 화면을 세로로 적층)
        private const double FPS = 15.0;
        private const int BUFFER_CAPACITY = 900; // 60초 분량 (15fps * 60s)

        // 프레임 버퍼
        private ConcurrentQueue<(List<Bitmap> frames, long timestamp)> _frameBuffer;

        // 현재 작업 중인 비디오 파일 경로
        private string _currentVideoPath;

        // 비디오 처리 작업 및 취소 토큰
        private Task _videoProcessingTask;
        private bool _processingCancellationRequested = false;

        // ICameraControl 관련 변수
        private ICameraControl.OperationMode _currentMode = ICameraControl.OperationMode.Idle;

        public bool IsRecording => _isRecordingRequested;

        /// <summary>
        /// RecordingManager 생성자
        /// </summary>
        /// <param name="sources">프레임 소스 목록</param>
        /// <param name="savePath">녹화 파일 저장 경로</param>
        public RecordingManager(List<IFrameProvider> sources, string savePath = @"C:\Users\dulab\Downloads")
        {
            _frameSources = sources;
            _savePath = savePath;
            _frameBuffer = new ConcurrentQueue<(List<Bitmap> frames, long timestamp)>();

            // 저장 경로가 존재하지 않으면 생성
            if (!Directory.Exists(_savePath))
            {
                try
                {
                    Directory.CreateDirectory(_savePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"저장 경로 생성 실패: {ex.Message}");
                    _savePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }
            }

            Console.WriteLine($"녹화 관리자 초기화 완료: 저장 경로 = {_savePath}");
        }

        /// <summary>
        /// 녹화 시작 요청
        /// </summary>
        public void StartRecording()
        {
            if (_isRecordingRequested) return;

            try
            {
                // 녹화 요청 플래그 설정
                _isRecordingRequested = true;

                // 버퍼 초기화
                _frameBuffer = new ConcurrentQueue<(List<Bitmap> frames, long timestamp)>();

                Console.WriteLine("녹화 시작 요청됨");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"녹화 시작 요청 실패: {ex.Message}");
                StopRecording();
            }
        }

        /// <summary>
        /// 녹화 중지
        /// </summary>
        public void StopRecording()
        {
            if (!_isRecordingRequested) return;

            _isRecordingRequested = false;

            // 버퍼에 남아있는 프레임 처리
            if (_frameBuffer.Count > 0)
            {
                ProcessBufferedFrames();
            }

            Console.WriteLine("녹화 중지 요청됨");
        }

        /// <summary>
        /// 프레임 녹화 처리
        /// </summary>
        public void RecordFrame(List<(Bitmap frame, long timestamp)> frames)
        {
            // 녹화 요청 상태가 아니거나 Play 모드가 아닌 경우 무시
            if (!_isRecordingRequested || _currentMode != ICameraControl.OperationMode.Play)
            {
                return;
            }

            try
            {
                // 버퍼에 프레임 추가
                List<Bitmap> bitmaps = frames.Select(f => f.frame).ToList();
                _frameBuffer.Enqueue((bitmaps, frames[0].timestamp));

                // 실제 녹화가 진행 중이 아니고 버퍼가 찼으면 비디오 처리 시작
                if (!_isActuallyRecording && _frameBuffer.Count >= BUFFER_CAPACITY)
                {
                    ProcessBufferedFrames();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 버퍼링 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 버퍼에 저장된 프레임을 처리하여 비디오 파일 생성
        /// </summary>
        private void ProcessBufferedFrames()
        {
            // 이미 처리 중이면 새 작업 시작하지 않음
            if (_isActuallyRecording)
            {
                return;
            }

            _isActuallyRecording = true;
            _currentVideoPath = GetNewFileName();

            // 현재 버퍼의 프레임을 처리용 컬렉션으로 복사
            var framesToProcess = new List<(List<Bitmap> frames, long timestamp)>();
            int frameCount = _frameBuffer.Count;

            for (int i = 0; i < frameCount; i++)
            {
                if (_frameBuffer.TryDequeue(out var frameData))
                {
                    framesToProcess.Add(frameData);
                }
            }

            // 백그라운드에서 비디오 생성
            _videoProcessingTask = Task.Run(() => CreateVideoFromFrames(framesToProcess, _currentVideoPath));
        }

        /// <summary>
        /// 프레임 목록으로부터 비디오 파일 생성
        /// </summary>
        private void CreateVideoFromFrames(List<(List<Bitmap> frames, long timestamp)> framesToProcess, string outputPath)
        {
            Console.WriteLine($"비디오 생성 시작: {outputPath}, 프레임 수: {framesToProcess.Count}");

            OpenCvSharp.VideoWriter writer = null;

            try
            {
                // OpenCV VideoWriter 초기화 - H.264 코덱 사용
                writer = new OpenCvSharp.VideoWriter(
                    outputPath,
                    OpenCvSharp.VideoWriter.FourCC('H', '2', '6', '4'),
                    FPS,
                    new OpenCvSharp.Size(FRAME_WIDTH, FRAME_HEIGHT)
                );

                if (!writer.IsOpened())
                {
                    Console.WriteLine("H264 코덱을 사용할 수 없습니다. MJPG로 대체합니다.");

                    // H.264 코덱을 사용할 수 없는 경우 MJPG로 대체
                    writer = new OpenCvSharp.VideoWriter(
                        outputPath,
                        OpenCvSharp.VideoWriter.FourCC('M', 'J', 'P', 'G'),
                        FPS,
                        new OpenCvSharp.Size(FRAME_WIDTH, FRAME_HEIGHT)
                    );

                    if (!writer.IsOpened())
                    {
                        throw new Exception("비디오 라이터를 초기화할 수 없습니다.");
                    }
                }

                // 정렬된 프레임 처리
                var sortedFrames = framesToProcess.OrderBy(f => f.timestamp).ToList();

                foreach (var frameData in sortedFrames)
                {
                    // 취소 요청 확인
                    if (_processingCancellationRequested)
                    {
                        Console.WriteLine("비디오 처리 취소됨");
                        break;
                    }

                    // 세로로 결합할 큰 Mat 생성
                    using (var combinedMat = new Mat(FRAME_HEIGHT, FRAME_WIDTH, MatType.CV_8UC3, Scalar.Black))
                    {
                        for (int i = 0; i < frameData.frames.Count; i++)
                        {
                            var frame = frameData.frames[i];
                            if (frame != null)
                            {
                                // Bitmap을 Mat으로 변환
                                using (var frameMat = BitmapConverter.ToMat(frame))
                                {
                                    if (!frameMat.Empty())
                                    {
                                        // 각 프레임을 세로로 배치 (540 픽셀 간격)
                                        var roi = new OpenCvSharp.Rect(0, i * 540, Math.Min(frameMat.Width, FRAME_WIDTH), Math.Min(frameMat.Height, 540));
                                        var targetRegion = combinedMat[roi];
                                        frameMat.CopyTo(targetRegion);
                                    }
                                }
                            }
                        }

                        // 결합된 프레임 저장
                        writer.Write(combinedMat);
                    }

                    // 프레임의 Bitmap 리소스 해제
                    foreach (var bitmap in frameData.frames)
                    {
                        bitmap?.Dispose();
                    }
                }

                Console.WriteLine($"비디오 생성 완료: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"비디오 생성 오류: {ex.Message}");
            }
            finally
            {
                // 자원 해제
                writer?.Release();
                writer?.Dispose();

                _isActuallyRecording = false;
                _processingCancellationRequested = false;
            }
        }

        /// <summary>
        /// 새 파일명 생성
        /// </summary>
        private string GetNewFileName()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(_savePath, $"Recording_{timestamp}.mp4");
        }

        #region ICameraControl 구현

        /// <summary>
        /// 현재 작동 모드 반환
        /// </summary>
        public ICameraControl.OperationMode GetCurrentMode()
        {
            return _currentMode;
        }

        /// <summary>
        /// 키 입력 처리
        /// </summary>
        public void SetKey(string key)
        {
            switch (key.ToLower())
            {
                case "r": // 리셋/다시시작
                    if (_currentMode == ICameraControl.OperationMode.Idle)
                    {
                        _currentMode = ICameraControl.OperationMode.Play;
                    }
                    else
                    {
                        StopRecording();
                        _currentMode = ICameraControl.OperationMode.Idle;
                    }
                    break;

                case "p": // 재생/일시정지 토글
                    if (_currentMode == ICameraControl.OperationMode.Play)
                    {
                        StopRecording();
                        _currentMode = ICameraControl.OperationMode.Stop;
                    }
                    else if (_currentMode == ICameraControl.OperationMode.Stop)
                    {
                        _currentMode = ICameraControl.OperationMode.Play;
                    }
                    break;

                    // 기타 키 처리는 필요에 따라 추가
            }

            Console.WriteLine($"RecordingManager 모드 변경: {_currentMode}");
        }

        /// <summary>
        /// 지연 시간 설정 (녹화 매니저에서는 사용하지 않음)
        /// </summary>
        public void SetDelay(int delaySeconds)
        {
            // 필요하면 구현
        }

        /// <summary>
        /// 프레임 처리 (녹화 매니저에서는 사용하지 않음)
        /// </summary>
        public void ProcessFrame(long timestamp)
        {
            // 필요하면 구현
        }

        #endregion

        /// <summary>
        /// 자원 해제
        /// </summary>
        public void Dispose()
        {
            StopRecording();

            // 취소 요청 설정
            _processingCancellationRequested = true;

            // 비디오 처리 작업이 완료될 때까지 대기 (최대 5초)
            if (_videoProcessingTask != null && !_videoProcessingTask.IsCompleted)
            {
                try
                {
                    Task.WaitAny(new[] { _videoProcessingTask }, 5000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"비디오 처리 작업 종료 중 오류: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// WPF 이미지 변환 유틸리티
    /// </summary>
    public static class WpfToBitmapConverter
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>
        /// RenderTargetBitmap을 System.Drawing.Bitmap으로 변환
        /// </summary>
        public static Bitmap ConvertToBitmap(RenderTargetBitmap source)
        {
            if (source == null) return null;

            var bitmapEncoder = new BmpBitmapEncoder();
            bitmapEncoder.Frames.Add(BitmapFrame.Create(source));

            using (var stream = new MemoryStream())
            {
                bitmapEncoder.Save(stream);
                stream.Seek(0, SeekOrigin.Begin);
                return new Bitmap(stream);
            }
        }

        /// <summary>
        /// WPF Visual을 Bitmap으로 변환
        /// </summary>
        public static Bitmap CaptureVisual(System.Windows.Media.Visual visual, double dpiX = 96, double dpiY = 96)
        {
            if (visual == null) return null;

            var bounds = VisualTreeHelper.GetDescendantBounds(visual);
            if (bounds.IsEmpty) return null;

            var bitmap = new RenderTargetBitmap(
                (int)bounds.Width, (int)bounds.Height,
                dpiX, dpiY,
                PixelFormats.Pbgra32);

            var drawingVisual = new DrawingVisual();
            using (var context = drawingVisual.RenderOpen())
            {
                var brush = new VisualBrush(visual);
                context.DrawRectangle(brush, null, new System.Windows.Rect(new System.Windows.Point(), bounds.Size));
            }

            bitmap.Render(drawingVisual);
            return ConvertToBitmap(bitmap);
        }
    }
}