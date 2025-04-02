using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SharpDX.Direct3D9;
using SharpDX.Multimedia;

namespace MultiWebcamApp
{
    public class WebcamSource : IFrameSource
    {
        private readonly int _cameraIndex;
        private VideoCapture _capture;
        private readonly object _captureLock = new object();
        private bool _isDisposed;

        public WebcamSource(int cameraIndex)
        {
            _cameraIndex = cameraIndex;
        }

        public FrameData CaptureFrame()
        {
            FrameData frameData = new FrameData();

            try
            {
                using var mat = new Mat();
                if (_capture != null && _capture.IsOpened() && _capture.Read(mat) && !mat.Empty())
                {
                    // 카메라 인덱스에 따라 헤드/바디 할당
                    if (_cameraIndex == 0)
                        frameData.WebcamHead = mat.Clone();
                    else if (_cameraIndex == 1)
                        frameData.WebcamBody = mat.Clone();

                    frameData.Timestamp = DateTime.Now.Ticks;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"카메라 {_cameraIndex} 프레임 캡처 오류: {ex.Message}");
            }

            return frameData;
        }

        public void Start()
        {
            try
            {
                lock (_captureLock)
                {
                    //_capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
                    _capture = new VideoCapture();
                    _capture.Open(_cameraIndex, VideoCaptureAPIs.MSMF);
                    System.Threading.Thread.Sleep(500);

                    _capture.Set(VideoCaptureProperties.FrameWidth, 1920);
                    _capture.Set(VideoCaptureProperties.FrameHeight, 1080);
                    _capture.Set(VideoCaptureProperties.Fps, 60);
                    _capture.Set(VideoCaptureProperties.BufferSize, 5);

                    // MJPEG 포맷 설정
                    int fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G');
                    _capture.Set(VideoCaptureProperties.FourCC, fourcc);
#if (DEBUG)
                    // 설정이 적용되었는지 확인
                    double fps = _capture.Get(VideoCaptureProperties.Fps);
                    fourcc = (int)_capture.Get(VideoCaptureProperties.FourCC);
                    // 설정 확인
                    Console.WriteLine($"포맷: {BitConverter.GetBytes(fourcc).Aggregate("", (c, b) => c + (char)b)}");
                    Console.WriteLine($"FPS: {fps}");
                    Console.WriteLine($"노출: {_capture.Get(VideoCaptureProperties.Exposure)}");
                    Console.WriteLine($"해상도: {_capture.Get(VideoCaptureProperties.FrameWidth)}x{_capture.Get(VideoCaptureProperties.FrameHeight)}");
#endif
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"카메라 {_cameraIndex} 시작 오류: {ex.Message}");
                Stop(); // 오류 발생시 리소스 정리
            }
        }

        public void Stop()
        {
            try
            {
                lock (_captureLock)
                {
                    if (_capture != null && _capture.IsOpened())
                    {
                        _capture.Release();
                        _capture.Dispose();
                        _capture = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"카메라 {_cameraIndex} 정지 오류: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                Stop();
            }

            _isDisposed = true;
        }

        ~WebcamSource()
        {
            Dispose(false);
        }
    }
}
