using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace MultiWebcamApp
{
    public class WebcamSource : IFrameSource
    {
        private readonly int _cameraIndex;
        private VideoCapture _capture;
        private readonly ConcurrentQueue<(Mat mat, long Timestamp)> _frameQueue;
        private CancellationTokenSource _cts;
        private Task _captureTask;

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
                switch (_cameraIndex)
                {
                    case 0:
                        frameData.WebcamHead = frame.mat.Clone();
                        break;
                    case 1:
                        frameData.WebcamBody = frame.mat.Clone();
                        break;
                }
            }
            frameData.Timestamp = frame.Timestamp;

            return frameData;
        }

        public void Start()
        {
            _capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
            _capture.Set(VideoCaptureProperties.FrameWidth, 960);
            _capture.Set(VideoCaptureProperties.FrameHeight, 540);
            _capture.Set(VideoCaptureProperties.Fps, 30);

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
                    _frameQueue.Enqueue((mat.Flip(FlipMode.Y).Clone(), DateTime.Now.Ticks));

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
