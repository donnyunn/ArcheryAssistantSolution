using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MultiWebcamApp
{
    public class SyncManager
    {
        private readonly IFrameSource[] _sources;
        private readonly ConcurrentQueue<FrameData> _frameQueue;
        private readonly int _targetFps = 30;
        private CancellationTokenSource _cts;
        private Task _syncTask;
        private readonly FrameBuffer _buffer;

        public SyncManager(IFrameSource[] sources, FrameBuffer buffer)
        {
            if (sources == null || sources.Length < 2)
            {
                throw new ArgumentNullException("At least two camera sources are required.");
            }
            _sources = sources;
            _frameQueue = new ConcurrentQueue<FrameData>();
            _buffer = buffer;
        }

        public void Start()
        {
            foreach (var source in _sources) source.Start();
            _cts = new CancellationTokenSource();
            _syncTask = Task.Run(() => RunSyncLoop(_cts.Token));
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            try
            {
                await _syncTask;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Sync task was canceled as expected.");
            }
            foreach (var source in _sources) source.Stop();
        }

        public bool TryGetFrame(out FrameData frame)
        {
            return _frameQueue.TryDequeue(out frame);
        }

        private async Task RunSyncLoop(CancellationToken token)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            while (!token.IsCancellationRequested)
            {
                var startTime = stopwatch.Elapsed;

                // 각 소스에서 최신 프레임 가져오기
                var frames = new FrameData[_sources.Length];
                for (int i = 0; i < _sources.Length; i++)
                {
                    frames[i] = _sources[i].CaptureFrame();
                }

                // 동기화된 프레임 데이터 생성
                var frame = new FrameData
                {
                    WebcamHead = frames[0].WebcamHead,
                    WebcamBody = frames[1].WebcamBody,
                    PressureData = frames.Length > 2 ? frames[2].PressureData : null,
                    Timestamp = frames.Min(f => f.Timestamp)
                };

                _frameQueue.Enqueue(frame);

                // 프레임 간격 유지
                var elapsed = stopwatch.Elapsed - startTime;
                var delay = frameInterval - elapsed;
                if (delay.TotalMilliseconds > 0)
                    await Task.Delay(delay, token);
            }
        }
    }
}
