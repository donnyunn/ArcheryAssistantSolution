using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace MultiWebcamApp
{
    public class RecordingManager : IDisposable
    {
        private readonly int _frameWidth = 960;
        private readonly int _frameHeight = 540;
        private readonly double _fps = 30.0;
        private readonly int _recordingIntervalMinutes = 1;
        private readonly string _tempFilePath;
        private readonly string _targetDirectory;

        private ConcurrentQueue<Mat> _frameQueue;
        private VideoWriter _videoWriter;
        private Task _recordingTask;
        private CancellationTokenSource _cts;
        private bool _isRecording;
        private readonly object _lockObject = new object();
        private DateTime _recordingStartTime;
        private System.Threading.Timer _saveTimer;

        // 깜빡임 방지를 위한 이전 프레임 보관
        private Mat _lastHeadFrame = null;
        private Mat _lastBodyFrame = null;
        private Mat _lastPressureFrame = null;
        private bool _isMixingFrames = false;
        private double _frameAlpha = 0.7; // 이전 프레임 비율 (0.7 현재, 0.3 이전)

        public bool IsRecording => _isRecording;

        public RecordingManager()
        {
            _frameQueue = new ConcurrentQueue<Mat>();

            // 임시 저장 경로: 바탕화면의 LastReplay.mp4
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _tempFilePath = Path.Combine(desktopPath, "LastReplay.mp4");

            // 최종 저장 경로: 바탕화면 (추후 변경 가능)
            _targetDirectory = desktopPath;

            // 타이머 설정 (1분 간격으로 저장)
            _saveTimer = new System.Threading.Timer(SaveVideoTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void StartRecording()
        {
            lock (_lockObject)
            {
                if (_isRecording)
                    return;

                _isRecording = true;
                _recordingStartTime = DateTime.Now;
                _frameQueue = new ConcurrentQueue<Mat>();
                _cts = new CancellationTokenSource();

                // 기존 임시 파일 삭제
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

                // 비디오 작성기 초기화 (세로로 적층된 3개 화면의 크기: 960 x 1620)
                _videoWriter = new VideoWriter(
                    _tempFilePath,
                    FourCC.MP4V,
                    _fps,
                    new OpenCvSharp.Size(_frameWidth, _frameHeight * 3)
                );

                if (!_videoWriter.IsOpened())
                {
                    throw new Exception("비디오 작성기를 초기화할 수 없습니다.");
                }

                // 프레임 처리 작업 시작
                _recordingTask = Task.Run(() => ProcessFrames(_cts.Token));

                // 저장 타이머 시작 (1분 간격)
                _saveTimer.Change(TimeSpan.FromMinutes(_recordingIntervalMinutes), TimeSpan.FromMilliseconds(-1));

                Console.WriteLine("녹화가 시작되었습니다.");
            }
        }

        public void StopRecording()
        {
            lock (_lockObject)
            {
                if (!_isRecording)
                    return;

                _isRecording = false;
                _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // 녹화 작업 취소
                _cts.Cancel();

                try
                {
                    // 남아있는 모든 프레임 처리
                    SaveCurrentFrames();

                    // 비디오 작성기 정리
                    CleanupVideoWriter();

                    // 최종 파일로 복사
                    CopyToFinalDestination();

                    // 이전 프레임 정리
                    ReleaseLastFrames();

                    Console.WriteLine("녹화가 중지되었습니다.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"녹화 중지 중 오류 발생: {ex.Message}");
                }
            }
        }

        public void AddFrame(FrameData frame)
        {
            if (!_isRecording || frame == null)
                return;

            try
            {
                // 3개의 프레임을 세로로 결합
                using (var combinedFrame = CombineFrames(frame))
                {
                    _frameQueue.Enqueue(combinedFrame.Clone());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 추가 중 오류: {ex.Message}");
            }
        }

        private Mat CombineFrames(FrameData frameData)
        {
            // 출력할 결합된 이미지 생성 (세로로 3개 적층)
            Mat combinedFrame = new Mat(_frameHeight * 3, _frameWidth, MatType.CV_8UC3, Scalar.Black);

            // Head 프레임 처리 (상단)
            ProcessHeadFrame(frameData, combinedFrame);

            // Body 프레임 처리 (중간)
            ProcessBodyFrame(frameData, combinedFrame);

            // Pressure 데이터 시각화 (하단)
            ProcessPressureData(frameData, combinedFrame);

            return combinedFrame;
        }

        private void ProcessHeadFrame(FrameData frameData, Mat combinedFrame)
        {
            if (frameData.WebcamHead != null && !frameData.WebcamHead.Empty())
            {
                // 헤드 프레임 리사이징
                Mat resizedHead = new Mat();
                Cv2.Resize(frameData.WebcamHead, resizedHead, new OpenCvSharp.Size(_frameWidth, _frameHeight));

                // 깜빡임 방지를 위한 프레임 혼합
                if (_isMixingFrames && _lastHeadFrame != null && !_lastHeadFrame.Empty())
                {
                    try
                    {
                        Mat blendedHead = new Mat();
                        Cv2.AddWeighted(resizedHead, _frameAlpha, _lastHeadFrame, 1.0 - _frameAlpha, 0, blendedHead);

                        // 혼합된 프레임 복사
                        Mat headRegion = new Mat(combinedFrame, new Rect(0, 0, _frameWidth, _frameHeight));
                        blendedHead.CopyTo(headRegion);

                        blendedHead.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"헤드 프레임 혼합 중 오류: {ex.Message}");

                        // 오류 시 현재 프레임만 사용
                        Mat headRegion = new Mat(combinedFrame, new Rect(0, 0, _frameWidth, _frameHeight));
                        resizedHead.CopyTo(headRegion);
                    }
                }
                else
                {
                    // 혼합 없이 현재 프레임 사용
                    Mat headRegion = new Mat(combinedFrame, new Rect(0, 0, _frameWidth, _frameHeight));
                    resizedHead.CopyTo(headRegion);
                }

                // 이전 프레임 정리 및 저장
                if (_lastHeadFrame != null)
                {
                    _lastHeadFrame.Dispose();
                }
                _lastHeadFrame = resizedHead.Clone();
            }
            else if (_lastHeadFrame != null && !_lastHeadFrame.Empty())
            {
                // 현재 프레임이 없으면 이전 프레임 사용
                Mat headRegion = new Mat(combinedFrame, new Rect(0, 0, _frameWidth, _frameHeight));
                _lastHeadFrame.CopyTo(headRegion);
            }
        }

        private void ProcessBodyFrame(FrameData frameData, Mat combinedFrame)
        {
            if (frameData.WebcamBody != null && !frameData.WebcamBody.Empty())
            {
                // 바디 프레임 리사이징
                Mat resizedBody = new Mat();
                Cv2.Resize(frameData.WebcamBody, resizedBody, new OpenCvSharp.Size(_frameWidth, _frameHeight));

                // 깜빡임 방지를 위한 프레임 혼합
                if (_isMixingFrames && _lastBodyFrame != null && !_lastBodyFrame.Empty())
                {
                    try
                    {
                        Mat blendedBody = new Mat();
                        Cv2.AddWeighted(resizedBody, _frameAlpha, _lastBodyFrame, 1.0 - _frameAlpha, 0, blendedBody);

                        // 혼합된 프레임 복사
                        Mat bodyRegion = new Mat(combinedFrame, new Rect(0, _frameHeight, _frameWidth, _frameHeight));
                        blendedBody.CopyTo(bodyRegion);

                        blendedBody.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"바디 프레임 혼합 중 오류: {ex.Message}");

                        // 오류 시 현재 프레임만 사용
                        Mat bodyRegion = new Mat(combinedFrame, new Rect(0, _frameHeight, _frameWidth, _frameHeight));
                        resizedBody.CopyTo(bodyRegion);
                    }
                }
                else
                {
                    // 혼합 없이 현재 프레임 사용
                    Mat bodyRegion = new Mat(combinedFrame, new Rect(0, _frameHeight, _frameWidth, _frameHeight));
                    resizedBody.CopyTo(bodyRegion);
                }

                // 이전 프레임 정리 및 저장
                if (_lastBodyFrame != null)
                {
                    _lastBodyFrame.Dispose();
                }
                _lastBodyFrame = resizedBody.Clone();
            }
            else if (_lastBodyFrame != null && !_lastBodyFrame.Empty())
            {
                // 현재 프레임이 없으면 이전 프레임 사용
                Mat bodyRegion = new Mat(combinedFrame, new Rect(0, _frameHeight, _frameWidth, _frameHeight));
                _lastBodyFrame.CopyTo(bodyRegion);
            }
        }

        private void ProcessPressureData(FrameData frameData, Mat combinedFrame)
        {
            if (frameData.PressureData != null)
            {
                // 새로운 압력 데이터 프레임 생성
                Mat pressureFrame = new Mat(_frameHeight, _frameWidth, MatType.CV_8UC3, Scalar.Black);
                VisualizePressureData(pressureFrame, frameData.PressureData);

                // 깜빡임 방지를 위한 프레임 혼합
                if (_isMixingFrames && _lastPressureFrame != null && !_lastPressureFrame.Empty())
                {
                    try
                    {
                        Mat blendedPressure = new Mat();
                        Cv2.AddWeighted(pressureFrame, _frameAlpha, _lastPressureFrame, 1.0 - _frameAlpha, 0, blendedPressure);

                        // 혼합된 프레임 복사
                        Mat pressureRegion = new Mat(combinedFrame, new Rect(0, _frameHeight * 2, _frameWidth, _frameHeight));
                        blendedPressure.CopyTo(pressureRegion);

                        blendedPressure.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"압력 프레임 혼합 중 오류: {ex.Message}");

                        // 오류 시 현재 프레임만 사용
                        Mat pressureRegion = new Mat(combinedFrame, new Rect(0, _frameHeight * 2, _frameWidth, _frameHeight));
                        pressureFrame.CopyTo(pressureRegion);
                    }
                }
                else
                {
                    // 혼합 없이 현재 프레임 사용
                    Mat pressureRegion = new Mat(combinedFrame, new Rect(0, _frameHeight * 2, _frameWidth, _frameHeight));
                    pressureFrame.CopyTo(pressureRegion);
                }

                // 이전 프레임 정리 및 저장
                if (_lastPressureFrame != null)
                {
                    _lastPressureFrame.Dispose();
                }
                _lastPressureFrame = pressureFrame.Clone();
                pressureFrame.Dispose();
            }
            else if (_lastPressureFrame != null && !_lastPressureFrame.Empty())
            {
                // 현재 압력 데이터가 없으면 이전 프레임 사용
                Mat pressureRegion = new Mat(combinedFrame, new Rect(0, _frameHeight * 2, _frameWidth, _frameHeight));
                _lastPressureFrame.CopyTo(pressureRegion);
            }
        }

        private void VisualizePressureData(Mat frame, ushort[] pressureData)
        {
            if (pressureData == null || pressureData.Length == 0)
                return;

            int padding = 40;
            int cellSize = (_frameWidth - (padding * 2)) / 96;
            int dataSize = 96;  // 96x96 pressure data

            // 최대값 찾기 (색상 매핑용)
            ushort maxValue = 0;
            foreach (var value in pressureData)
            {
                if (value > maxValue) maxValue = value;
            }

            // 값이 없으면 반환
            if (maxValue == 0) return;

            // 압력 데이터 시각화
            for (int y = 0; y < dataSize; y++)
            {
                for (int x = 0; x < dataSize; x++)
                {
                    int index = y * dataSize + x;
                    if (index < pressureData.Length)
                    {
                        // 값에 따라 색상 계산 (청색 -> 청록색 -> 녹색 -> 노란색 -> 빨간색)
                        float normalizedValue = Math.Min(1.0f, pressureData[index] / (float)maxValue);
                        Scalar color;

                        if (normalizedValue < 0.1f)
                            color = new Scalar(255, 0, (byte)(255 * (normalizedValue / 0.1f))); // 청색
                        else if (normalizedValue < 0.2f)
                            color = new Scalar(255, (byte)(255 * ((normalizedValue - 0.1f) / 0.1f)), 255); // 청록색
                        else if (normalizedValue < 0.4f)
                            color = new Scalar((byte)(255 * ((normalizedValue - 0.2f) / 0.2f)), 255, (byte)(255 * (1 - (normalizedValue - 0.2f) / 0.2f))); // 녹색
                        else if (normalizedValue < 0.8f)
                            color = new Scalar(0, (byte)(255 * (1 - (normalizedValue - 0.4f) / 0.4f)), 255); // 노란색
                        else
                            color = new Scalar(0, 0, 255); // 빨간색

                        // 셀 그리기
                        int cellX = padding + x * cellSize;
                        int cellY = padding + y * cellSize;

                        Rect cellRect = new Rect(cellX, cellY, cellSize, cellSize);
                        frame.Rectangle(cellRect, color, -1);
                    }
                }
            }
        }

        private async Task ProcessFrames(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested || !_frameQueue.IsEmpty)
                {
                    if (_frameQueue.TryDequeue(out Mat frame))
                    {
                        // 비디오 작성기가 열려있는지 확인
                        if (_videoWriter != null && _videoWriter.IsOpened())
                        {
                            _videoWriter.Write(frame);
                        }

                        // 프레임 해제
                        frame.Dispose();
                    }
                    else
                    {
                        // 큐가 비어있으면 잠시 대기
                        await Task.Delay(10, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 작업 취소는 정상적인 종료
                Console.WriteLine("프레임 처리 작업이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 중 오류: {ex.Message}");
            }
        }

        private void SaveVideoTimerCallback(object state)
        {
            try
            {
                // 현재 기록 중인 비디오 저장 및 새 파일 시작
                SaveCurrentFrames();

                // 최종 대상으로 복사
                CopyToFinalDestination();

                // 새 비디오 작성기 초기화
                ReinitializeVideoWriter();

                // 타이머 재설정
                if (_isRecording)
                {
                    _saveTimer.Change(TimeSpan.FromMinutes(_recordingIntervalMinutes), TimeSpan.FromMilliseconds(-1));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"비디오 저장 중 오류: {ex.Message}");
            }
        }

        private void SaveCurrentFrames()
        {
            lock (_lockObject)
            {
                // 비디오 작성기가 열려있는지 확인
                if (_videoWriter != null && _videoWriter.IsOpened())
                {
                    // 대기 중인 모든 프레임을 처리하기 위해 임시 큐 사용
                    var tempQueue = new ConcurrentQueue<Mat>();

                    // 현재 큐의 모든 프레임을 임시 큐로 이동
                    while (_frameQueue.TryDequeue(out Mat frame))
                    {
                        tempQueue.Enqueue(frame);
                    }

                    // 임시 큐의 모든 프레임을 처리
                    while (tempQueue.TryDequeue(out Mat frame))
                    {
                        _videoWriter.Write(frame);
                        frame.Dispose();
                    }

                    // 비디오 작성기 정리
                    CleanupVideoWriter();
                }
            }
        }

        private void CleanupVideoWriter()
        {
            if (_videoWriter != null)
            {
                if (_videoWriter.IsOpened())
                {
                    _videoWriter.Release();
                }
                _videoWriter.Dispose();
                _videoWriter = null;
            }
        }

        private void ReinitializeVideoWriter()
        {
            if (_isRecording)
            {
                _videoWriter = new VideoWriter(
                    _tempFilePath,
                    FourCC.MP4V,
                    _fps,
                    new OpenCvSharp.Size(_frameWidth, _frameHeight * 3)
                );

                if (!_videoWriter.IsOpened())
                {
                    throw new Exception("비디오 작성기를 재초기화할 수 없습니다.");
                }
            }
        }

        private void CopyToFinalDestination()
        {
            if (File.Exists(_tempFilePath))
            {
                try
                {
                    // 현재 날짜와 시간을 이용하여 파일명 생성
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string finalFileName = $"Recording_{timestamp}.mp4";
                    string finalFilePath = Path.Combine(_targetDirectory, finalFileName);

                    // 파일 복사
                    File.Copy(_tempFilePath, finalFilePath, true);
                    Console.WriteLine($"녹화 파일이 저장되었습니다: {finalFilePath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"최종 대상으로 파일 복사 중 오류: {ex.Message}");
                }
            }
        }

        private void ReleaseLastFrames()
        {
            if (_lastHeadFrame != null)
            {
                _lastHeadFrame.Dispose();
                _lastHeadFrame = null;
            }

            if (_lastBodyFrame != null)
            {
                _lastBodyFrame.Dispose();
                _lastBodyFrame = null;
            }

            if (_lastPressureFrame != null)
            {
                _lastPressureFrame.Dispose();
                _lastPressureFrame = null;
            }
        }

        public void EnableFrameMixing(bool enable)
        {
            _isMixingFrames = enable;
        }

        public void SetFrameMixingRatio(double alpha)
        {
            _frameAlpha = Math.Clamp(alpha, 0.0, 1.0);
        }

        public void Dispose()
        {
            StopRecording();

            _saveTimer?.Dispose();
            _saveTimer = null;

            CleanupVideoWriter();
            ReleaseLastFrames();

            _cts?.Dispose();
            _cts = null;
        }
    }
}