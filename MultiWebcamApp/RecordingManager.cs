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
    public class RecordingManager : IDisposable
    {
        private List<IFrameProvider> _frameSources;
        private OpenCvSharp.VideoWriter _writer;
        private System.Windows.Forms.Timer _fileRotationTimer;
        private string _savePath;
        private bool _isRecording = false;
        private const int MAX_FILES = 50; // 최대 저장 파일 수
        private const int FILE_ROTATION_MINUTES = 5; // 파일 교체 주기 (분)
        private const int FRAME_WIDTH = 960;
        private const int FRAME_HEIGHT = 1620; // 540 * 3 (세 개의 화면을 세로로 적층)
        private const double FPS = 15.0;

        public bool IsRecording => _isRecording;

        /// <summary>
        /// RecordingManager 생성자
        /// </summary>
        /// <param name="sources">프레임 소스 목록</param>
        /// <param name="savePath">녹화 파일 저장 경로</param>
        public RecordingManager(List<IFrameProvider> sources, string savePath = @"C:\Users\dulab\Downloads")
        {
            _frameSources = sources;
            _savePath = savePath;

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

            // 파일 교체 타이머 초기화
            _fileRotationTimer = new System.Windows.Forms.Timer { Interval = FILE_ROTATION_MINUTES * 60 * 1000 };
            _fileRotationTimer.Tick += RotateFile;

            Console.WriteLine($"녹화 관리자 초기화 완료: 저장 경로 = {_savePath}");
        }

        /// <summary>
        /// 녹화 시작
        /// </summary>
        public void StartRecording()
        {
            if (_isRecording) return;

            try
            {
                // 영상 파일 초기화
                string filename = GetNewFileName();
                Console.WriteLine($"녹화 파일 생성: {filename}");

                // OpenCV VideoWriter 초기화 - H.264 코덱 사용
                _writer = new OpenCvSharp.VideoWriter(
                    filename,
                    OpenCvSharp.VideoWriter.FourCC('H', '2', '6', '4'),
                    FPS,
                    new OpenCvSharp.Size(FRAME_WIDTH, FRAME_HEIGHT)
                );

                if (!_writer.IsOpened())
                {
                    Console.WriteLine("H264 코덱을 사용할 수 없습니다. MJPG로 대체합니다.");

                    // H.264 코덱을 사용할 수 없는 경우 MJPG로 대체
                    _writer = new OpenCvSharp.VideoWriter(
                        filename,
                        OpenCvSharp.VideoWriter.FourCC('M', 'J', 'P', 'G'),
                        FPS,
                        new OpenCvSharp.Size(FRAME_WIDTH, FRAME_HEIGHT)
                    );

                    if (!_writer.IsOpened())
                    {
                        throw new Exception("비디오 라이터를 초기화할 수 없습니다.");
                    }
                }

                _fileRotationTimer.Start();
                _isRecording = true;

                Console.WriteLine("녹화 시작: " + filename);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"녹화 시작 실패: {ex.Message}");
                StopRecording();
            }
        }

        /// <summary>
        /// 녹화 중지
        /// </summary>
        public void StopRecording()
        {
            if (!_isRecording) return;

            _fileRotationTimer.Stop();

            if (_writer != null)
            {
                _writer.Release();
                _writer.Dispose();
                _writer = null;
            }

            _isRecording = false;
            Console.WriteLine("녹화 중지");
        }

        /// <summary>
        /// 새 파일명 생성
        /// </summary>
        private string GetNewFileName()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(_savePath, $"Recording_{timestamp}.mp4");
        }

        /// <summary>
        /// 프레임 녹화 처리
        /// </summary>
        public void RecordFrame(List<(Bitmap frame, long timestamp)> frames)
        {
            if (_writer == null || _frameSources == null || _frameSources.Count == 0) return;

            try
            {
                // 세로로 결합할 큰 Mat 생성
                using (var combinedMat = new Mat(FRAME_HEIGHT, FRAME_WIDTH, MatType.CV_8UC3, Scalar.Black))
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var frame = frames[i].frame;
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
                    _writer.Write(combinedMat);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 녹화 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 파일 교체 처리 (정기적으로 새 파일 생성)
        /// </summary>
        private void RotateFile(object sender, EventArgs e)
        {
            try
            {
                if (_writer != null)
                {
                    _writer.Release();
                    _writer.Dispose();
                    _writer = null;
                }

                // 새 파일 생성
                string filename = GetNewFileName();
                Console.WriteLine($"새 녹화 파일 생성: {filename}");

                _writer = new OpenCvSharp.VideoWriter(
                    filename,
                    OpenCvSharp.VideoWriter.FourCC('H', '2', '6', '4'),
                    FPS,
                    new OpenCvSharp.Size(FRAME_WIDTH, FRAME_HEIGHT)
                );

                if (!_writer.IsOpened())
                {
                    // H.264 코덱을 사용할 수 없는 경우 MJPG로 대체
                    _writer = new OpenCvSharp.VideoWriter(
                        filename,
                        OpenCvSharp.VideoWriter.FourCC('M', 'J', 'P', 'G'),
                        FPS,
                        new OpenCvSharp.Size(FRAME_WIDTH, FRAME_HEIGHT)
                    );
                }

                // 디스크 정리
                CleanupOldFiles();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"파일 교체 오류: {ex.Message}");
                StopRecording();
            }
        }

        /// <summary>
        /// 오래된 파일 정리
        /// </summary>
        private void CleanupOldFiles()
        {
            try
            {
                // 디스크 여유 공간 확인
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_savePath));
                long freeSpaceGB = drive.AvailableFreeSpace / (1024 * 1024 * 1024);

                // 파일 목록 획득
                DirectoryInfo dir = new DirectoryInfo(_savePath);
                var files = dir.GetFiles("Recording_*.mp4").OrderBy(f => f.CreationTime).ToList();

                // 최대 파일 수 초과 시 오래된 파일부터 삭제
                //if (files.Count > MAX_FILES)
                //{
                //    int filesToDelete = files.Count - MAX_FILES;
                //    for (int i = 0; i < filesToDelete; i++)
                //    {
                //        if (i < files.Count)
                //        {
                //            files[i].Delete();
                //            Console.WriteLine($"오래된 파일 삭제: {files[i].Name}");
                //        }
                //    }
                //}

                // 디스크 공간 부족 시 추가 삭제
                //if (freeSpaceGB < 2) // 2GB 미만이면 추가 정리
                //{
                //    files = dir.GetFiles("Recording_*.mp4").OrderBy(f => f.CreationTime).ToList();

                //    foreach (var file in files)
                //    {
                //        file.Delete();
                //        Console.WriteLine($"공간 확보를 위해 삭제: {file.Name}");

                //        // 다시 공간 확인
                //        drive.Refresh();
                //        if (drive.AvailableFreeSpace / (1024 * 1024 * 1024) > 5)
                //            break; // 5GB 이상 확보되면 중단
                //    }
                //}
            }
            catch (Exception ex)
            {
                Console.WriteLine($"파일 정리 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 자원 해제
        /// </summary>
        public void Dispose()
        {
            StopRecording();

            if (_fileRotationTimer != null)
            {
                _fileRotationTimer.Dispose();
                _fileRotationTimer = null;
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