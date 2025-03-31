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
        private readonly ConcurrentQueue<(Mat mat, long Timestamp)> _frameQueue;
        private CancellationTokenSource _cts;
        private Task _captureTask;
        private Mat _Lastmat = new Mat();

        private bool _isStopped;

        public WebcamSource(int cameraIndex)
        {
            _cameraIndex = cameraIndex;
            _frameQueue = new ConcurrentQueue<(Mat mat, long Timestamp)>();
            _cts = new CancellationTokenSource();
        }

        public FrameData CaptureFrame()
        {
            FrameData frameData = new FrameData();
            if (_frameQueue.TryDequeue(out var frame))
            {
                _Lastmat = frame.mat.Clone();
            }
            if (!_Lastmat.Empty())
            {
                switch (_cameraIndex)
                {
                    case 0:
                        frameData.WebcamHead = _Lastmat;
                        break;
                    case 1:
                        frameData.WebcamBody = _Lastmat;
                        break;
                }
            }
            frameData.Timestamp = frame.Timestamp;

            return frameData;
        }

        public void Start()
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
            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
        }

        public void Stop()
        {
            if (_isStopped) return;
            _isStopped = true;

            _cts.Cancel();
            try
            {
                _captureTask?.Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebcamSource {_cameraIndex} capture task wait failed: {ex.Message}");
            }

            if (_capture != null && _capture.IsOpened())
            {
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
                Console.WriteLine($"WebcamSource {_cameraIndex} stopped.");
            }
        }

        private void CaptureLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using var mat = new Mat();
                if (_capture.Read(mat) && !mat.Empty())
                {
                    _frameQueue.Enqueue((mat.Clone(), DateTime.Now.Ticks));

                    while (_frameQueue.Count > 5)
                    {
                        if (_frameQueue.TryDequeue(out var oldFrame))
                        {
                            oldFrame.mat.Dispose();
                        }
                    }
                }
            }
        }
    }
}
