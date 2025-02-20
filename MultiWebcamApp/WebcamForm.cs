using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Interop;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SharpDX.DXGI;

namespace MultiWebcamApp
{
    public partial class WebcamForm : Form
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

        private readonly Mat _frameMat;

        // 재생 상태 관리
        private enum _mode { Idle, Play, Replay, Stop }
        private _mode _state = _mode.Idle;
        private string _key = "";

        private double actualFps = 30.0;
        private bool _keyProcessing = false;
        private object _keyLock = new object();

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

            // 웹캠 캡처 시작
            Task.Run(() => StartCamera());
        }

        private void StartCamera()
        {
            _capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                Console.WriteLine($"카메라 {_cameraIndex}를 열 수 없습니다.");
                return;
            }

            _capture.Set(VideoCaptureProperties.FrameWidth, 1366);
            _capture.Set(VideoCaptureProperties.FrameHeight, 768);
            _capture.Set(VideoCaptureProperties.Fps, 60.0f);

            actualFps = _capture.Get(VideoCaptureProperties.Fps);
            Console.WriteLine($"{actualFps}");
            //for (int i = 0; i < 5; i++) // 5 프레임 버리기
            //{
            //    _capture.Read(new Mat());
            //}
        }

        public void work(long timestamp)
        {
            try
            {
                if (_capture == null || !_capture.IsOpened())
                    return;

                if (_capture.Read(_frameMat) && !_frameMat.Empty())
                {
                    var frame = _frameMat.Flip(FlipMode.Y);
                    ProcessFrame(frame, timestamp);
                    frame.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Frame capture error: {ex.Message}");
            }
            finally
            {
                HandleKeyInput();
            }
        }

        private void ProcessFrame(Mat frame, long timestamp)
        {
            Mat frameData;
            var msg = "";
            var msg2 = "";
            switch (_state)
            {
                case _mode.Play:
                    // 지연 모드: 프레임을 버퍼로 저장
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

                    // 지연 모드: 재생 지점 프레임 표시
                    frameData = _frameBuffer.Get(_playPoint);
                    msg = statusMessage(_playPoint / actualFps);
                    msg2 = countdown != 0 ? countdown.ToString() : "●";
                    _viewer.UpdateFrame(frameData, msg, msg2);
                    break;
                case _mode.Replay:
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
                            _state = _mode.Stop;
                        }
                        _lastSlowUpdateTime  = currentTime;
                    }
                    break;
                case _mode.Stop:
                    frameData = _frameBuffer.Get(_playPoint);
                    msg = statusMessage(_playPoint / actualFps, _slowLevel);
                    _viewer.UpdateFrame(frameData, msg, " ");
                    break;
                case _mode.Idle:
                    // 실시간 모드: 프레임을 바로 표시
                    msg = statusMessage(_playPoint / actualFps);
                    _viewer.UpdateFrame(frame, msg, " ");
                    break;
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
                        _state = _state == _mode.Idle ? _mode.Play : _mode.Idle;
                        ClearBuffer();

                        _playPoint = 0;
                        _slowLevel = 1;

                        Console.WriteLine($"{_state}");
                        break;
                    case "p":
                        _state = _state == _mode.Play || _state == _mode.Replay ? _mode.Stop : _mode.Replay;
                        break;
                    case "d":
                        if (_state != _mode.Idle)
                        {
                            _state = _mode.Stop;
                            _playPoint = Math.Clamp(_playPoint + (int)(actualFps / 2), 0, _frameBuffer.Count - 1);
                        }
                        break;
                    case "a":
                        if (_state != _mode.Idle)
                        {
                            _state = _mode.Stop;
                            _playPoint = Math.Clamp(_playPoint - (int)(actualFps / 2), 0, _frameBuffer.Count - 1);
                        }
                        break;
                    case "s":
                        if (_state == _mode.Stop || _state == _mode.Replay)
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

        private string statusMessage(double seconds, int slowLevel =1)
        {
            string message = "";

            message += $"{seconds,5:F1}s\t ";
            message += $"x{1.0 / slowLevel,1:F2}";

            return message;
        }

        private void ClearBuffer()
        {
            // 버퍼 비우기
            _frameBuffer.Clear();
        }

        private void WebcamForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _viewer.Close();
            _capture?.Release();
            _capture?.Dispose();
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

        public void SetDelay(int delaySeconds)
        {
            _delayPoint = delaySeconds * (int)actualFps; // FPS 기준으로 지연 프레임 수 계산

            ClearBuffer();
            _state = _mode.Idle;

            _playPoint = 0;
            _slowLevel = 1;
        }
    }

    public class CircularBuffer
    {
        private Mat[] _buffer;
        private readonly object _lockObject = new object();
        private int _start;  // 버퍼의 시작 위치
        private int _count;  // 현재 저장된 항목 수

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
                    // 버퍼가 가득 찼을 때 가장 오래된 항목 제거
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
