using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SharpDX.Direct3D9;
using SharpDX.DXGI;

namespace MultiWebcamApp
{
    public partial class WebcamForm : Form, IFrameProvider, ICameraControl
    {
        private readonly int _cameraIndex;
        private VideoCapture? _capture;
        private CameraViewer.MainWindow _viewer = new CameraViewer.MainWindow();

        private const int MaxBufferSize = 60 * 90 + 1;
        private CircularBuffer _frameBuffer;
        private readonly object _bufferLock = new object();

        private int _playPoint = 0;
        private int _delayPoint = 0;
        private int _slowLevel = 1;
        private int _slowCnt = 0;
        private long _lastSlowUpdateTime = 0;

        private Mat _frameMat;

        // ��� ���� ����
        private ICameraControl.OperationMode _state = ICameraControl.OperationMode.Idle;
        private string _key = "";

        private double actualFps = 30.0;
        private bool _keyProcessing = false;
        private object _keyLock = new object();

        private CancellationTokenSource _cts;
        ConcurrentQueue<Mat> _frameQueue = new ConcurrentQueue<Mat>();
        private int _maxQueueSize = 2;
        private object _queueLock = new object();
        Task _taskCamera = null;
        Task _taskWork = null;

        public WebcamForm(int cameraIndex)
        {
            InitializeComponent();

            _cameraIndex = cameraIndex;
            _frameMat = new Mat();
            _frameBuffer = new CircularBuffer(MaxBufferSize);

            Load += WebcamForm_Load;
            FormClosing += WebcamForm_FormClosing;
        }

        private void WebcamForm_Load(object? sender, EventArgs e)
        {
            Screen currentScreen = Screen.FromControl(this);
            Rectangle screenBounds = currentScreen.Bounds;
            _viewer.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            _viewer.WindowStyle = System.Windows.WindowStyle.None;
            _viewer.ResizeMode = System.Windows.ResizeMode.NoResize;

            _viewer.Left = screenBounds.Left;
            _viewer.Top = screenBounds.Top;
            _viewer.Width = screenBounds.Width;
            _viewer.Height = screenBounds.Height;
            
            _viewer.Show();

            // ��ķ ĸó ����
            //_ = StartCamera().ContinueWith(t =>
            //{
            //    if (t.IsFaulted && t.Exception != null)
            //    {
            //        Console.WriteLine($"ī�޶� {_cameraIndex} ���� �� ����: {t.Exception.InnerException?.Message}");
            //    }
            //});

            //_ = mainWork();
        }

        private async Task StartCamera()
        {
            try
            {
                _capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
                if (!_capture.IsOpened())
                {
                    Console.WriteLine($"ī�޶� {_cameraIndex}�� �� �� �����ϴ�.");
                    return;
                }

                _capture.Set(VideoCaptureProperties.FrameWidth, 960);
                _capture.Set(VideoCaptureProperties.FrameHeight, 540);
                _capture.Set(VideoCaptureProperties.Fps, 30.0f);

                actualFps = _capture.Get(VideoCaptureProperties.Fps);
                Console.WriteLine($"{actualFps}");

                for (int i = 0; i < 5; i++) // 5 ������ ������
                {
                    _capture.Read(new Mat());
                }

                _cts = new CancellationTokenSource();

                await CameraLoop(_cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ī�޶� {_cameraIndex} ���� ����: {ex.Message}");
                CleanupCamera();
            }
        }

        private async Task CameraLoop(CancellationToken cancellationToken)
        {
            Mat frame = new Mat();

            _taskCamera = Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // ������ �о� �α�
                        if (_capture.IsOpened())
                        {
                            if (_capture.Read(frame) && !frame.Empty())
                            {
                                EnqueueFrame(frame.Flip(FlipMode.Y));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // �������� ���
                        Console.WriteLine($"ī�޶� {_cameraIndex} ���� ����");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ī�޶� {_cameraIndex} ���� ����: {ex.Message}");
                    }

                    //Task.Delay(1, cancellationToken);
                }
            });
            await _taskCamera;
        }

        private void EnqueueFrame(Mat frame)
        {
            lock (_queueLock)
            {
                if (_frameQueue.Count >= _maxQueueSize)
                {
                    if (_frameQueue.TryDequeue(out Mat oldFrame))
                    {
                        oldFrame.Dispose();
                    }
                }

                _frameQueue.Enqueue(frame.Clone());
                _frameMat = frame.Clone();
            }
        }

        private async Task mainWork()
        {
            _taskWork = Task.Run(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (_frameQueue.TryDequeue(out Mat frame))
                    {
                        try
                        {
                            ProcessFrameInternal(frame, 0);
                        }
                        finally
                        {
                            //frame.Dispose();
                            HandleKeyInput();
                        }
                    }
                }
            });
            await _taskWork;
        }

        public void work(long timestamp)
        {
            //ProcessFrame(timestamp);

            if (_frameQueue.TryDequeue(out Mat frame))
            {
                try
                {
                    ProcessFrameInternal(frame, timestamp);
                }
                finally
                {
                    //frame.Dispose();
                    HandleKeyInput();
                }
            }
        }

        public ICameraControl.OperationMode GetCurrentMode()
        {
            return _state;
        }

        private void ProcessFrameInternal(Mat frame, long timestamp)
        {
            Mat frameData;
            var msg = "";
            var msg2 = "";
            switch (_state)
            {
                case ICameraControl.OperationMode.Play:
                    // Play mode: Store frame in buffer
                    _frameBuffer.Enqueue(frame.Clone());
                    var countdown = 0;
                    if (_delayPoint >= _frameBuffer.Count)
                    {
                        _playPoint = 0;
                        countdown = (int)((_delayPoint - _frameBuffer.Count - 1) / actualFps) + 1;
                    }
                    else
                    {
                        _playPoint = _frameBuffer.Count - _delayPoint - 1;
                        countdown = 0;
                    }

                    // Play mode: Display frame at playback point
                    frameData = _frameBuffer.Get(_playPoint);
                    msg = statusMessage(_playPoint / actualFps);
                    msg2 = countdown != 0 ? countdown.ToString() : "��";
                    _viewer.UpdateFrame(frameData, msg, msg2);
                    break;
                case ICameraControl.OperationMode.Replay:
                    long currentTime = Stopwatch.GetTimestamp();
                    double elapsedMs = ((currentTime - _lastSlowUpdateTime) * 1000.0) / Stopwatch.Frequency;

                    if (elapsedMs >= (16.67 * _slowLevel))
                    {
                        if (_playPoint < _frameBuffer.Count - 1)
                        {
                            _playPoint++;
                            frameData = _frameBuffer.Get(_playPoint);
                            msg = statusMessage(_playPoint / actualFps, _slowLevel);
                            _viewer.UpdateFrame(frameData, msg, " ");
                        }
                        else
                        {
                            _state = ICameraControl.OperationMode.Stop;
                        }
                        _lastSlowUpdateTime = currentTime;
                    }
                    break;
                case ICameraControl.OperationMode.Stop:
                    frameData = _frameBuffer.Get(_playPoint);
                    msg = statusMessage(_playPoint / actualFps, _slowLevel);
                    _viewer.UpdateFrame(frameData, msg, " ");
                    break;
                case ICameraControl.OperationMode.Idle:
                    // Real-time mode: Display current frame
                    msg = statusMessage(_playPoint / actualFps);
                    _viewer.UpdateFrame(frame, msg, " ");
                    break;
            }
        }

        public void SetKey(string key)
        {
            lock (_keyLock)
            {
                if (!_keyProcessing)
                {
                    _key = key;
                    _keyProcessing = true;
                }
            }
        }

        private void HandleKeyInput()
        {
            if (!_keyProcessing || string.IsNullOrEmpty(_key))
                return;

            lock (_keyLock)
            {
                switch (_key)
                {
                    case "r":
                        _state = _state == ICameraControl.OperationMode.Idle ?
                            ICameraControl.OperationMode.Play :
                            ICameraControl.OperationMode.Idle;
                        ClearBuffer();

                        _playPoint = 0;
                        _slowLevel = 1;

                        Console.WriteLine($"{_state}");
                        break;
                    case "p":
                        _state = _state == ICameraControl.OperationMode.Play ||
                            _state == ICameraControl.OperationMode.Replay ?
                            ICameraControl.OperationMode.Stop :
                            ICameraControl.OperationMode.Replay;
                        break;
                    case "d":
                        if (_state != ICameraControl.OperationMode.Idle)
                        {
                            _state = ICameraControl.OperationMode.Stop;
                            _playPoint = Math.Clamp(_playPoint + (int)(actualFps / 2), 0, _frameBuffer.Count - 1);
                        }
                        break;
                    case "a":
                        if (_state != ICameraControl.OperationMode.Idle)
                        {
                            _state = ICameraControl.OperationMode.Stop;
                            _playPoint = Math.Clamp(_playPoint - (int)(actualFps / 2), 0, _frameBuffer.Count - 1);
                        }
                        break;
                    case "s":
                        if (_state == ICameraControl.OperationMode.Stop || _state == ICameraControl.OperationMode.Replay)
                        {
                            _slowLevel *= 2;
                            if (_slowLevel > 8)
                            {
                                _slowLevel = 1;
                            }
                            _slowCnt = 0;
                        }
                        break;
                    default:
                        break;
                }

                _key = "";
                _keyProcessing = false;
            }
        }

        public void SetDelay(int delaySeconds)
        {
            _delayPoint = delaySeconds * (int)actualFps; // FPS �������� ���� ������ �� ���

            ClearBuffer();
            _state = ICameraControl.OperationMode.Idle;

            _playPoint = 0;
            _slowLevel = 1;
        }

        public (Bitmap frame, long timestamp) GetCurrentFrame()
        {
            try
            {
                // _frameBuffer���� ���� ��� ���� ��ġ�� ������ ��������
                //var frameMat = _state == ICameraControl.OperationMode.Idle
                //    ? _frameMat?.Clone()
                //    : _frameBuffer?.Get(_playPoint-1);
                var frameMat = _frameMat?.Clone();

                if (frameMat != null && !frameMat.Empty())
                {
                    // OpenCV Mat�� Bitmap���� ��ȯ
                    var bitmap = frameMat.ToBitmap();
                    return (bitmap, DateTime.Now.Ticks);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"��ķ ������ ȹ�� ����: {ex.Message}");
            }

            // ���� �� �� �̹��� ��ȯ
            return (new Bitmap(960, 540), DateTime.Now.Ticks);
        }

        private string statusMessage(double seconds, int slowLevel =1)
        {
            string message = "";

            message += $"{seconds,5:F1}s\t ";
            message += $"x{1.0 / slowLevel,1:F2}";

            return message;
        }

        private void ClearBuffer()
        {
            // ���� ����
            _frameBuffer.Clear();
        }

        private void WebcamForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            CleanupCamera();
            _viewer.Close();
        }

        private void CleanupCamera()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _taskCamera.Wait();
                _taskWork.Wait();
                _cts.Dispose();
                _cts = null;
            }

            if (_capture != null && _capture.IsOpened())
            {
                _capture.Release();
                _capture.Dispose();
                _capture = null;
            }
        }
    }

    public class CircularBuffer
    {
        private Mat[] _buffer;
        private readonly object _lockObject = new object();
        private int _start;  // ������ ���� ��ġ
        private int _count;  // ���� ����� �׸� ��

        public CircularBuffer(int capacity)
        {
            _buffer = new Mat[capacity];
            _start = 0;
            _count = 0;
        }

        public void Enqueue(Mat item)
        {
            lock (_lockObject)
            {
                int index = (_start + _count) % _buffer.Length;
                if (_count == _buffer.Length)
                {
                    // ���۰� ���� á�� �� ���� ������ �׸� ����
                    _buffer[_start]?.Dispose();
                    _start = (_start + 1) % _buffer.Length;
                    _count--;
                }
                _buffer[index] = item;
                _count++;
            }
        }

        public Mat Get(int index)
        {
            lock (_lockObject)
            {
                if (index >= _count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _buffer[(_start + index) % _buffer.Length];
            }
        }

        public void Clear()
        {
            lock (_lockObject)
            {
                for (int i = 0; i < _count; i++)
                {
                    int index = (_start + i) % _buffer.Length;
                    _buffer[index]?.Dispose();
                    _buffer[index] = null;
                }
                _start = 0;
                _count = 0;
            }
        }

        public int Count
        {
            get
            {
                lock (_lockObject)
                {
                    return _count;
                }
            }
        }
    }
}
