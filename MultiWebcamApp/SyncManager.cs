using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SharpDX.Direct3D11;

namespace MultiWebcamApp
{
    public class SyncManager
    {
        private readonly List<IFrameSource> _sources;
        private readonly ConcurrentQueue<FrameData> _frameQueue;
        private readonly int _targetFps = 60;
        private CancellationTokenSource _cts;
        private Task _syncTask;
        private readonly FrameBuffer _buffer;

        public SyncManager(IFrameSource[] sources, FrameBuffer buffer)
        {
            if (sources == null || sources.Length < 2)
            {
                throw new ArgumentNullException("At least two camera sources are required.");
            }
            _sources = new List<IFrameSource>(sources);
            _frameQueue = new ConcurrentQueue<FrameData>();
            _buffer = buffer;
        }

        public void Initialize()
        {
            foreach (var source in _sources)
            {
                source.Start();
            }
        }

        private int _frameCnt = 0;
        // 한 번의 호출로 모든 소스에서 프레임 캡처
        public void CaptureFrames()
        {
            FrameData frame = new FrameData();
            frame.Timestamp = DateTime.Now.Ticks;

            try
            {
                // 모든 소스에서 프레임 캡처
                foreach (var source in _sources)
                {
                    //var frameData = source.CaptureFrame();
                    if (source is not PressurePadSource)
                    {
                        var frameData = source.CaptureFrame();

                        // 카메라 소스인 경우 헤드/바디 할당 (소스 인덱스 기반)
                        if (source is WebcamSource ws)
                        {
                            if (frameData.WebcamHead != null)
                                frame.WebcamHead = frameData.WebcamHead;

                            if (frameData.WebcamBody != null)
                                frame.WebcamBody = frameData.WebcamBody;
                        }
                    }
                    else
                    {
                        if (++_frameCnt % 5 == 0)
                        {
                            var frameData = source.CaptureFrame();
                            // 압력 센서 소스인 경우 압력 데이터 할당
                            if (source is PressurePadSource ps && frameData.PressureData != null)
                            {
                                frame.PressureData = frameData.PressureData;
                            }
                        }
                    }
                }

                _frameQueue.Enqueue(frame);
                while (_frameQueue.Count > 5)
                {
                    _frameQueue.TryDequeue(out _);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 캡처 중 오류 발생: {ex.Message}");
            }
        }

        public void Start()
        {
            foreach (var source in _sources) source.Start();
        }

        public async Task StopAsync()
        {
            foreach (var source in _sources) source.Stop();
        }

        public bool TryGetFrame(out FrameData frame)
        {
            return _frameQueue.TryDequeue(out frame);
        }
    }
}
