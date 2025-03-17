using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Documents;
using RJCP.IO.Ports;
using System.Globalization;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Drawing.Interop;
using System.Windows.Controls;

namespace MultiWebcamApp
{
    public partial class FootpadForm : Form, IFrameProvider, ICameraControl
    {
        // 제조사 가이드에 따른 COM 포트 배치
        // 1사분면(좌측 상단): COM3 -> 장치 인덱스 0
        // 2사분면(좌측 하단): COM4 -> 장치 인덱스 1
        // 3사분면(우측 상단): COM5 -> 장치 인덱스 2
        // 4사분면(우측 하단): COM6 -> 장치 인덱스 3
        private string[] _portNames = { "COM3", "COM4", "COM5", "COM6" };

        private Dictionary<int, FootpadDevice> devices = new Dictionary<int, FootpadDevice>();
        private PressureMapViewer.MainWindow pressureMapWindow;
        private readonly object dataLock = new object();
        private bool isRunning = false;
        private Calibration calibration;

        private Bitmap _currentVisualization;
        private readonly object _visualizationLock = new object();
        private bool _captureInitialized = false;
        // 캡처 해상도 조정 (절반으로 줄임)
        private const float SCALE_FACTOR = 0.5f;

        private ICameraControl.OperationMode _state = ICameraControl.OperationMode.Idle;
        private readonly object _keyLock = new object();
        private string _key = "";
        private bool _keyProcessing = false;

        private const int MaxBufferSize = 60 * 90 + 1;
        private CircularBuffer2 _dataBuffer;
        private int _playPoint = 0;
        private int _delayPoint = 0;
        private int _delaySeconds = 0;
        private int _slowLevel = 1;
        private int _slowCnt = 0;
        private long _lastSlowUpdateTime = 0;
        private double actualFps = 15.0;

        private ushort[] combinedData = new ushort[9216]; // 96 * 96
        private int lastProcessedQuadrant = 3;

        private System.Windows.Forms.Timer _renderTimer;

        public FootpadForm()
        {
            InitializeComponent();
            InitializeDevices();

            calibration = new Calibration();
            _dataBuffer = new CircularBuffer2(MaxBufferSize, 9216);

            _renderTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _renderTimer.Tick += (s, e) => renderTick(s, e);
            _renderTimer.Start();
        }

        private void renderTick(Object? sender, EventArgs e)
        {
            if (isRunning)
            {
                isRunning = false;
            }
        }

        private void InitializeDevices()
        {
            // 각 포트에 해당하는 사분면 인덱스를 초기화
            for (int i = 0; i < _portNames.Length; i++)
            {
                try
                {
                    devices[i] = new FootpadDevice(_portNames[i], i);
                    Console.WriteLine($"초기화 성공: 사분면 {i + 1} - {_portNames[i]}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"초기화 실패: 사분면 {i + 1} - {_portNames[i]}: {ex.Message}");
                }
            }
        }

        private void FootpadForm_Load(object sender, EventArgs e)
        {
            pressureMapWindow = new PressureMapViewer.MainWindow();

            Screen currentScreen = Screen.FromControl(this);
            Rectangle screenBounds = currentScreen.Bounds;

            pressureMapWindow.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            pressureMapWindow.WindowStyle = System.Windows.WindowStyle.None;
            pressureMapWindow.ResizeMode = System.Windows.ResizeMode.NoResize;

            pressureMapWindow.Left = screenBounds.Left;
            pressureMapWindow.Top = screenBounds.Top;
            pressureMapWindow.Width = screenBounds.Width;
            pressureMapWindow.Height = screenBounds.Height;
            
            pressureMapWindow.Show();
        }

        public void ResetAllPorts()
        {
            _renderTimer.Stop();
            isRunning = true;
            _renderTimer.Start();
            Task.Run(() =>
            {
                try
                {
                    Console.WriteLine("모든 COM 포트 재설정 시작...");

                    // 모든 장치의 포트 닫기
                    foreach (var device in devices.Values)
                    {
                        try
                        {
                            device.Close();
                            Console.WriteLine($"사분면 {device.QuadrantIndex + 1} - 포트 닫기 성공");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"사분면 {device.QuadrantIndex + 1} - 포트 닫기 실패: {ex.Message}");
                        }
                    }

                    // 잠시 대기하여 포트가 완전히 닫히도록 함
                    System.Threading.Thread.Sleep(1000);

                    // 모든 장치 다시 초기화
                    for (int i = 0; i < _portNames.Length; i++)
                    {
                        try
                        {
                            if (devices.ContainsKey(i))
                            {
                                // 기존 장치가 있는 경우 해당 포트 다시 열기
                                devices[i] = new FootpadDevice(_portNames[i], i);
                                Console.WriteLine($"사분면 {i + 1} - {_portNames[i]} 재연결 성공");
                            }
                            else
                            {
                                // 기존 장치가 없는 경우 새로 생성
                                devices[i] = new FootpadDevice(_portNames[i], i);
                                Console.WriteLine($"사분면 {i + 1} - {_portNames[i]} 신규 연결 성공");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"사분면 {i + 1} - {_portNames[i]} 연결 실패: {ex.Message}");
                        }
                    }

                    // 첫 번째 프레임 업데이트 시도
                    //isRunning = false;
                    //UpdateFrameAll();
                    isRunning = false;

                    Console.WriteLine("모든 COM 포트 재설정 완료");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"포트 재설정 중 오류 발생: {ex.Message}");
                }
            });
        }

        public bool UpdateFrame()
        {
            bool ret = false;
            if (isRunning) return ret;
            _renderTimer.Start();
            isRunning = true;

            try
            {
                // 순환식으로 사분면 선택 (0->1->2->3->0->...)
                int currentQuadrant = (lastProcessedQuadrant + 1) % 4;
                lastProcessedQuadrant = currentQuadrant;

                // 선택된 사분면만 처리
                if (devices.ContainsKey(currentQuadrant))
                {
                    var device = devices[currentQuadrant];

                    // 현재 사분면 데이터 수집
                    bool deviceHasData = device.RequestAndReceiveData();

                    if (deviceHasData)
                    {
                        // 현재 사분면의 위치 계산
                        int baseRow = (currentQuadrant / 2) * 48;
                        int baseCol = (currentQuadrant % 2) * 48;
                        var deviceData = device.LastData;

                        // 해당 사분면 영역만 업데이트
                        for (int i = 0; i < 48; i++)
                        {
                            for (int j = 0; j < 48; j++)
                            {
                                combinedData[(baseRow + i) * 96 + (baseCol + j)] = deviceData[i * 48 + j];
                            }
                        }

                        // UI 업데이트
                        pressureMapWindow.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            pressureMapWindow.UpdatePressureData(combinedData);
                        }), System.Windows.Threading.DispatcherPriority.Background);

                        ret = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating frame: {ex.Message}");
                ret = false;
            }
            finally
            {
                isRunning = false;
                //_renderTimer.Stop();
            }

            return ret;
        }

        // FootpadForm 클래스 내에 추가할 메소드
        public bool UpdateFrameAll()
        {
            bool ret = false;
            if (isRunning) return ret;
            _renderTimer.Start();
            isRunning = true;

            try
            {
                bool dataUpdated = false;

                // 각 사분면을 처리할 병렬 작업 배열
                Task<bool>[] quadrantTasks = new Task<bool>[4];

                // 각 사분면의 데이터를 저장할 임시 배열
                ushort[][] quadrantData = new ushort[4][];

                // 사분면 데이터를 처리하기 위한 맵핑 정의
                // 제조사 가이드에 따라: 
                // - COM3(index 0)은 1사분면(좌측 상단)
                // - COM5(index 1)은 2사분면(좌측 하단)
                // - COM4(index 2)은 3사분면(우측 상단)
                // - COM6(index 3)은 4사분면(우측 하단)
                int[] quadrantToIndex = { 0, 1, 2, 3 };

                // 96x96 combined 맵에서 각 사분면의 시작 위치 정의
                Point[] startPositions = new Point[] {
                    new Point(0, 0),      // 1사분면: 좌측 상단 (0,0)
                    new Point(0, 48),     // 2사분면: 좌측 하단 (0,48)
                    new Point(48, 0),     // 3사분면: 우측 상단 (48,0)
                    new Point(48, 48)     // 4사분면: 우측 하단 (48,48)
                };

                // 각 사분면에 대한 병렬 작업 생성
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    int currentQuadrant = quadrant;

                    quadrantTasks[quadrant] = Task.Run(() =>
                    {
                        // 현재 사분면의 장치 인덱스
                        int deviceIndex = quadrantToIndex[currentQuadrant];

                        // 장치가 없으면 처리하지 않음
                        if (!devices.ContainsKey(deviceIndex))
                            return false;

                        var device = devices[deviceIndex];

                        // 현재 사분면 데이터 수집
                        bool deviceHasData = device.ProcessResponseAndSendRequest();

                        if (deviceHasData)
                        {
                            // 캘리브레이션 처리
                            //quadrantData[currentQuadrant] = calibration.Work(device.LastData, currentQuadrant);
                            quadrantData[currentQuadrant] = device.LastData;
                            return true;
                        }

                        return false;
                    });
                }
                // 모든 병렬 작업이 완료될 때까지 기다림
                Task.WaitAll(quadrantTasks);

                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    if (quadrantTasks[quadrant].Result)
                    {
                        // 해당 사분면의 데이터가 성공적으로 수집된 경우
                        Point startPos = startPositions[quadrant];
                        ushort[] deviceData = quadrantData[quadrant];

                        if (deviceData != null)
                        {
                            // 48x48 사분면 데이터를 96x96 combinedData에 복사
                            // 세로로 먼저 증가(위에서 아래로), 그 다음 가로 방향으로 이동
                            for (int col = 0; col < 48; col++)
                            {
                                for (int row = 0; row < 48; row++)
                                {
                                    // 디바이스 데이터의 인덱스
                                    // 세로 방향이 먼저 증가하고, 그 다음 가로 방향으로 이동
                                    int index = col * 48 + row;

                                    // combinedData의 인덱스 계산
                                    int combinedRow = startPos.Y + row;
                                    int combinedCol = startPos.X + col;
                                    int combinedIndex = combinedRow * 96 + combinedCol;

                                    // 데이터 복사
                                    combinedData[combinedIndex] = deviceData[index];
                                }
                            }

                            dataUpdated = true;
                        }
                    }
                }

                // 데이터가 업데이트되었으면 UI 갱신
                if (dataUpdated)
                {
                    // UI 업데이트
                    //pressureMapWindow.UpdatePressureData(combinedData);

                    ProcessFrame(Stopwatch.GetTimestamp());

                    ret = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating all frames: {ex.Message}");
                ret = false;
            }
            finally
            {
                isRunning = false;
                HandleKeyInput();
            }

            return ret;
        }

        /// <summary>
        /// 생성자 이후에 이 메서드를 호출하여 캡처 시작 (MainForm에서 호출)
        /// </summary>
        public void InitializeForRecording()
        {
            try
            {
                // WPF 윈도우 캡처 초기화
                Console.WriteLine($"FootpadForm: PressureMapViewer 크기 = {pressureMapWindow.Width}x{pressureMapWindow.Height}");

                // 첫 번째 캡처 수행 (테스트)
                //CaptureWindowContent();
                StartBackgroundCapture();

                _captureInitialized = true;
                Console.WriteLine("FootpadForm: 녹화를 위한 캡처 기능 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FootpadForm: 캡처 초기화 오류 - {ex.Message}");
            }
        }

        // 클래스 멤버 변수로 추가
        private Bitmap _cachedBitmap = null;
        private DateTime _lastCaptureTime = DateTime.MinValue;
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMilliseconds(33); // 약 30fps
        private readonly ConcurrentQueue<Bitmap> _captureQueue = new ConcurrentQueue<Bitmap>();
        private Task _backgroundCaptureTask;
        private CancellationTokenSource _captureCts = new CancellationTokenSource();
        private int _captureWidth = 960;
        private int _captureHeight = 540;
        private bool _backgroundCaptureRunning = false;

        // 백그라운드 캡처 시작 메소드
        public void StartBackgroundCapture()
        {
            if (_backgroundCaptureRunning)
                return;

            _backgroundCaptureRunning = true;
            _captureCts = new CancellationTokenSource();

            _backgroundCaptureTask = Task.Run(async () => {
                try
                {
                    while (!_captureCts.Token.IsCancellationRequested)
                    {
                        // UI 스레드에서 작업 요청
                        Bitmap bitmap = null;

                        var captureTask = pressureMapWindow.Dispatcher.InvokeAsync(() => {
                            try
                            {
                                return CaptureDirectly();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"FootpadForm: 백그라운드 Invoke 캡처 오류 - {ex.Message}");
                                return null;
                            }
                        }, System.Windows.Threading.DispatcherPriority.Render);

                        // 캡처 작업이 완료될 때까지 대기 (최대 100ms)
                        await Task.Delay(5); // 짧은 대기로 UI 스레드 여유 제공

                        try
                        {
                            if (captureTask.Status == System.Windows.Threading.DispatcherOperationStatus.Completed ||
                                captureTask.Wait(TimeSpan.FromMilliseconds(100)) == DispatcherOperationStatus.Completed)
                            {
                                bitmap = captureTask.Result;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"FootpadForm: 백그라운드 캡처 대기 오류 - {ex.Message}");
                        }

                        // 큐 크기 제한 (최대 2개만 유지)
                        while (_captureQueue.Count > 1)
                        {
                            Bitmap oldBmp;
                            if (_captureQueue.TryDequeue(out oldBmp))
                                oldBmp?.Dispose();
                        }

                        if (bitmap != null)
                            _captureQueue.Enqueue(bitmap);

                        await Task.Delay(33, _captureCts.Token); // 약 30fps
                    }
                }
                catch (TaskCanceledException)
                {
                    // 정상적인 취소
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FootpadForm: 백그라운드 캡처 오류 - {ex.Message}");
                }
                finally
                {
                    _backgroundCaptureRunning = false;
                }
            }, _captureCts.Token);
        }

        // 백그라운드 캡처 중지 메소드
        public void StopBackgroundCapture()
        {
            _captureCts.Cancel();

            // 큐에 있는 모든 비트맵 정리
            while (_captureQueue.Count > 0)
            {
                Bitmap bitmap;
                if (_captureQueue.TryDequeue(out bitmap))
                    bitmap?.Dispose();
            }
        }

        // 직접 캡처 메소드 (UI 스레드에서 호출)
        private Bitmap CaptureDirectly()
        {
            // 이 메소드는 반드시 UI 스레드에서 호출되어야 함
            if (!pressureMapWindow.Dispatcher.CheckAccess())
            {
                Console.WriteLine("FootpadForm: 잘못된 스레드에서 CaptureDirectly 호출됨");
                return null;
            }

            try
            {
                // 창 크기 확인
                if (pressureMapWindow.ActualWidth <= 0 || pressureMapWindow.ActualHeight <= 0)
                {
                    return null;
                }

                // 1. 전체 창을 원본 해상도로 캡처
                var renderTargetBitmap = new RenderTargetBitmap(
                    (int)pressureMapWindow.ActualWidth,
                    (int)pressureMapWindow.ActualHeight,
                    96, 96, PixelFormats.Pbgra32);

                renderTargetBitmap.Render(pressureMapWindow);

                // 2. 캡처한 이미지를 System.Drawing.Bitmap으로 변환
                BitmapEncoder encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));

                // 메모리 스트림으로 저장
                using (MemoryStream stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    stream.Position = 0;

                    // 3. 원본 비트맵 생성
                    using (var originalBitmap = new Bitmap(stream))
                    {
                        // 4. 축소된 비트맵 생성 (960x540)
                        var resizedBitmap = new Bitmap(_captureWidth, _captureHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                        using (Graphics g = Graphics.FromImage(resizedBitmap))
                        {
                            // 고품질 이미지 축소 설정
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                            // 배경색 설정 (선택 사항)
                            g.Clear(System.Drawing.Color.Black);

                            // 가로세로 비율을 유지하면서 이미지 축소
                            Rectangle destRect = CalculateAspectRatioFit(
                                originalBitmap.Width,
                                originalBitmap.Height,
                                _captureWidth,
                                _captureHeight);

                            // 전체 창을 축소된 사이즈로 그리기
                            g.DrawImage(originalBitmap, destRect);
                        }

                        return resizedBitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FootpadForm: 직접 캡처 오류 - {ex.Message}");
                return null;
            }
        }

        // 가로세로 비율을 유지하며 이미지 크기 조정 계산
        private Rectangle CalculateAspectRatioFit(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
        {
            var ratio = Math.Min((float)maxWidth / srcWidth, (float)maxHeight / srcHeight);

            var newWidth = (int)(srcWidth * ratio);
            var newHeight = (int)(srcHeight * ratio);

            // 이미지를 화면 중앙에 배치
            var newX = (maxWidth - newWidth) / 2;
            var newY = (maxHeight - newHeight) / 2;

            return new Rectangle(newX, newY, newWidth, newHeight);
        }

        // 최적화된 CaptureWindowContent 메소드
        private Bitmap CaptureWindowContent()
        {
            // 백그라운드 캡처가 실행 중인 경우 큐에서 가져오기
            if (_backgroundCaptureRunning)
            {
                Bitmap result = null;
                if (_captureQueue.TryPeek(out result) && result != null)
                {
                    return (Bitmap)result.Clone();
                }
            }

            // 백그라운드 캡처가 없거나 실패한 경우 동기 캡처 시도
            try
            {
                // 캐시된 결과가 있고 충분히 최신인 경우 사용
                if (_cachedBitmap != null && (DateTime.Now - _lastCaptureTime) < _cacheTimeout)
                {
                    return (Bitmap)_cachedBitmap.Clone();
                }

                Bitmap result = null;

                // UI 스레드에서 접근 가능한지 확인
                if (pressureMapWindow.Dispatcher.CheckAccess())
                {
                    result = CaptureDirectly();
                }
                else
                {
                    // UI 스레드에서 실행
                    var tcs = new TaskCompletionSource<Bitmap>();

                    pressureMapWindow.Dispatcher.BeginInvoke(new Action(() => {
                        try
                        {
                            // UI 스레드에서 직접 실행
                            var capturedBitmap = CaptureDirectly();
                            tcs.SetResult(capturedBitmap);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"FootpadForm: Dispatcher 캡처 오류 - {ex.Message}");
                            tcs.SetResult(null);
                        }
                    }));

                    result = tcs.Task.Result;
                }

                // 결과 캐싱
                if (result != null)
                {
                    // 이전 캐시 정리
                    _cachedBitmap?.Dispose();
                    _cachedBitmap = result;
                    _lastCaptureTime = DateTime.Now;

                    return (Bitmap)result.Clone();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FootpadForm: CaptureWindowContent 오류 - {ex.Message}");
            }

            return null;
        }

        // GetCurrentFrame 메소드 (IFrameProvider 인터페이스 구현)
        public (Bitmap frame, long timestamp) GetCurrentFrame()
        {
            var bitmap = CaptureWindowContent();
            return (bitmap, DateTime.Now.Ticks);
        }

        public void Cleanup()
        {
            StopBackgroundCapture();
            _cachedBitmap?.Dispose();
            _cachedBitmap = null;
        }

        public ICameraControl.OperationMode GetCurrentMode()
        {
            return _state;
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
            _delayPoint = delaySeconds * (int)actualFps;

            ClearBuffer();
            _state = ICameraControl.OperationMode.Idle;

            _playPoint = 0;
            _slowLevel = 1;
        }

        public void ProcessFrame(long timestamp)
        {
            ushort[] data;
            try
            {
                switch (_state)
                {
                    case ICameraControl.OperationMode.Play:
                        _dataBuffer.Enqueue(combinedData);
                        var countdown = 0;
                        if (_delayPoint >= _dataBuffer.Count)
                        {
                            _playPoint = 0;
                            countdown = (int)((_delayPoint - _dataBuffer.Count - 1) / actualFps) + 1;
                        }
                        else
                        {
                            _playPoint = _dataBuffer.Count - _delayPoint - 1;
                            countdown = 0;
                        }

                        data = _dataBuffer.Get(_playPoint);
                        pressureMapWindow.UpdatePressureData(data);
                        break;
                    case ICameraControl.OperationMode.Replay:
                        long currentTime = Stopwatch.GetTimestamp();
                        double elapsedMs = ((currentTime - _lastSlowUpdateTime) * 1000.0) / Stopwatch.Frequency;

                        if (elapsedMs >= (66.67 * _slowLevel))
                        {
                            if (_playPoint < _dataBuffer.Count - 1)
                            {
                                _playPoint++;
                                data = _dataBuffer.Get(_playPoint);
                                pressureMapWindow.UpdatePressureData(data);
                            }
                            else
                            {
                                _state = ICameraControl.OperationMode.Stop;
                            }
                            _lastSlowUpdateTime = currentTime;
                        }
                        break;
                    case ICameraControl.OperationMode.Stop:
                        data = _dataBuffer.Get(_playPoint);
                        pressureMapWindow.UpdatePressureData(data);
                        break;
                    case ICameraControl.OperationMode.Idle:
                        pressureMapWindow.UpdatePressureData(combinedData);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Footpad processing error: {ex.Message}");
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
                        Console.WriteLine($"Footpad state changed to: {_state}");

                        // Idle 상태로 변경된 경우에만 포트 재설정
                        if (_state == ICameraControl.OperationMode.Idle)
                        {
                            Console.WriteLine("Resetting ports after returning to Idle state");
                            ResetAllPorts();
                        }
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
                            _playPoint = Math.Clamp(_playPoint + (int)(actualFps / 2), 0, _dataBuffer.Count - 1);
                        }
                        break;
                    case "a":
                        if (_state != ICameraControl.OperationMode.Idle)
                        {
                            _state = ICameraControl.OperationMode.Stop;
                            _playPoint = Math.Clamp(_playPoint - (int)(actualFps / 2), 0, _dataBuffer.Count - 1);
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

        private void ClearBuffer()
        {
            _dataBuffer.Clear();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Cleanup();
            isRunning = false;
            foreach (var device in devices.Values)
            {
                device.Close();
            }
            base.OnFormClosed(e);
        }
    }

    public class Calibration
    {
        private Dictionary<int, Dictionary<int, List<CalibrationRange>>> calibrationFactors;
        private readonly object lockObject = new object();
        private bool isInitialized = false;

        private class CalibrationRange
        {
            public float AdcThreshold { get; set; }
            public float Slope { get; set; }
            public float Intercept { get; set; }
        }

        public Calibration()
        {
            LoadCalibrationFiles();
        }

        private void LoadCalibrationFiles()
        {
            lock (lockObject)
            {
                try
                {
                    calibrationFactors = new Dictionary<int, Dictionary<int, List<CalibrationRange>>>();

                    // 각 사분면에 대한 보정 파일 로드 (1.csv, 2.csv, 3.csv, 4.csv)
                    for (int quadrant = 0; quadrant < 4; quadrant++)
                    {
                        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{quadrant + 1}.csv");
                        if (File.Exists(filePath))
                        {
                            calibrationFactors[quadrant] = LoadCalibrationFile(filePath);
                            Console.WriteLine($"사분면 {quadrant + 1}의 보정 파일을 성공적으로 로드했습니다.");
                        }
                        else
                        {
                            // 파일이 없으면 기본값 설정
                            var defaultCalibration = new Dictionary<int, List<CalibrationRange>>();

                            // 모든 센서 인덱스(1~2304)에 대한 기본값 설정
                            for (int sensorIndex = 1; sensorIndex <= 2304; sensorIndex++)
                            {
                                defaultCalibration[sensorIndex] = new List<CalibrationRange>
                                {
                                    new CalibrationRange { AdcThreshold = float.MaxValue, Slope = 1.0f, Intercept = 0.0f }
                                };
                            }

                            calibrationFactors[quadrant] = defaultCalibration;
                            Console.WriteLine($"경고: 사분면 {quadrant + 1}의 보정 파일이 없습니다. 기본값(1.0)을 사용합니다.");
                        }
                    }
                    isInitialized = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"보정 파일 로드 중 오류 발생: {ex.Message}");
                    // 초기화 실패 시 기본값으로 설정
                    InitializeDefaultCalibration();
                }
            }
        }

        private void InitializeDefaultCalibration()
        {
            calibrationFactors = new Dictionary<int, Dictionary<int, List<CalibrationRange>>>();

            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                var defaultCalibration = new Dictionary<int, List<CalibrationRange>>();

                // 모든 센서 인덱스(1~2304)에 대한 기본값 설정
                for (int sensorIndex = 1; sensorIndex <= 2304; sensorIndex++)
                {
                    defaultCalibration[sensorIndex] = new List<CalibrationRange>
                    {
                        new CalibrationRange { AdcThreshold = float.MaxValue, Slope=1.0f, Intercept = 0.0f }
                    };
                }

                calibrationFactors[quadrant] = defaultCalibration;
            }

            isInitialized = true;
            Console.WriteLine("모든 사분면에 기본 보정값(1.0)을 적용했습니다.");
        }

        private Dictionary<int, List<CalibrationRange>> LoadCalibrationFile(string filePath)
        {
            var sensorCalibration = new Dictionary<int, List<CalibrationRange>>();
            List<int> sensorIndices = new List<int>();

            try
            {
                string[] lines = File.ReadAllLines(filePath);

                int currentLineIndex = 0;
                int sectionCount = 0;

                while (currentLineIndex < lines.Length)
                {
                    string[] columns = lines[currentLineIndex++].Split(',');

                    // 헤더 행 건너뛰기
                    if (columns.Length > 0 && (columns[0] == "Calibration Result" || columns[0] == "Pressure"))
                        continue;

                    // 인덱스 행 처리
                    if (columns.Length > 0 && (columns[0] == ""))
                    {
                        for (int col = 2; col < columns.Length; col++)
                        {
                            if (int.TryParse(columns[col], out int index))
                            {
                                sensorIndices.Add(index);
                                sensorCalibration.Add(index, new List<CalibrationRange>());
                            }
                        }
                        continue;
                    }

                    // Adc 행 처리
                    if (columns.Length > 0 && columns[0] == "Adc")
                    {
                        for (int col = 0, i = 0; col < columns.Length; col++)
                        {
                            if (float.TryParse(columns[col], NumberStyles.Any, CultureInfo.InvariantCulture, out float adcValue))
                            {
                                CalibrationRange calibrationRange = new CalibrationRange { AdcThreshold = adcValue };
                                sensorCalibration[sensorIndices[i++]].Add(calibrationRange);
                            }
                        }
                        sectionCount++;
                        continue;
                    }

                    // Slope 행 처리
                    if (columns.Length > 0 && columns[0] == "Slope")
                    {
                        for (int col = 0, i = 0; col < columns.Length; col++)
                        {
                            if (float.TryParse(columns[col], NumberStyles.Any, CultureInfo.InvariantCulture, out float slopeValue))
                            {
                                sensorCalibration[sensorIndices[i++]][sectionCount-1].Slope = slopeValue;
                            }
                        }
                        continue;
                    }

                    // Intercept 행 처리
                    if (columns.Length > 0 && columns[0] == "Intercept")
                    {
                        for (int col = 0, i = 0; col < columns.Length; col++)
                        {
                            if (float.TryParse(columns[col], NumberStyles.Any, CultureInfo.InvariantCulture, out float interceptValue))
                            {
                                sensorCalibration[sensorIndices[i++]][sectionCount - 1].Intercept = interceptValue;
                            }
                        }
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"파일 읽기 오류 ({filePath}): {ex.Message}");
                // 오류 발생 시 기본값 1.0으로 설정된 배열 반환
            }

            return sensorCalibration;
        }

        public ushort[] Work(ushort[] rawData, int quadrant)
        {
            // 초기화되지 않았거나 해당 사분면 데이터가 없으면 원본 반환
            if (!isInitialized || !calibrationFactors.ContainsKey(quadrant))
            {
                return rawData;
            }

            lock (lockObject)
            {
                ushort[] calibratedData = new ushort[rawData.Length];

                for (int i = 0; i < rawData.Length; i++)
                {
                    int sensorIndex = i + 1;

                    if (calibrationFactors[quadrant].ContainsKey(sensorIndex))
                    {
                        var ranges = calibrationFactors[quadrant][sensorIndex];
                        float adcValue = rawData[i];

                        // 적절한 보정 범위 찾기
                        CalibrationRange selectedRange = null;
                        foreach (var range in ranges)
                        {
                            if (adcValue <= range.AdcThreshold)
                            {
                                selectedRange = range;
                                break;
                            }
                        }

                        // 범위를 찾지 못했으면 마지막 범위 사용
                        if (selectedRange == null && ranges.Count > 0)
                        {
                            selectedRange = ranges.Last();
                        }

                        // 보정 실시
                        if (selectedRange != null)
                        {
                            float calibratedValue = selectedRange.Slope * adcValue + selectedRange.Intercept;

                            // 음수 값 방지
                            calibratedValue = Math.Max(0, calibratedValue);
                            //calibratedValue = Math.Min(ushort.MaxValue, calibratedValue);

                            calibratedData[i] = (ushort)Math.Round(calibratedValue);
                        }
                        else
                        {
                            // 보정 정보가 없으면 원본 값 유지
                            calibratedData[i] = rawData[i];
                        }
                    }
                    else
                    {
                        // 센서 인덱스에 대한 보정 정보가 없으면 원본 값 유지
                        calibratedData[i] = rawData[i];
                    }
                }

                return calibratedData;
            }
        }

        public void ReloadCalibration()
        {
            lock (lockObject)
            {
                Console.WriteLine("보정 파일을 다시 로드합니다...");
                LoadCalibrationFiles();
            }
        }

        public bool IsCalibrationInitialized()
        {
            return isInitialized;
        }
    }

    public class FootpadDevice
    {
        public SerialPortStream Port { get; private set; }
        public int QuadrantIndex { get; private set; }
        public ushort[] LastData { get; private set; }
        private static readonly byte[] RequestPacket = new byte[] { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };
        private bool isProcessing = false;
        private bool firstCall = true;

        public FootpadDevice(string portName, int quadrantIndex)
        {
            QuadrantIndex = quadrantIndex;
            LastData = new ushort[48 * 48];
            InitializePort(portName);
        }

        private void InitializePort(string portName)
        {
            try
            {
                Port = new SerialPortStream(portName, 3000000, 8, Parity.None, StopBits.One)
                {
                    ReadTimeout = 100,
                    WriteTimeout = 100,
                    ReadBufferSize = 16384,
                };
                Port.Open();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to open oprt {portName}: {ex.Message}");
            }
        }

        public bool ProcessResponseAndSendRequest()
        {
            if (Port?.IsDisposed == true) return false;
            if (!Port?.IsOpen == true || isProcessing) return false;

            isProcessing = true;
            bool responseProcessed = false;

            try
            {
                // 첫 호출인 경우, 응답을 기다리지 않고 바로 요청 패킷만 보냄
                if (firstCall)
                {
                    SendRequest();
                    firstCall = false;
                    return false;
                }

                // 이전 요청에 대한 응답을 처리
                responseProcessed = ReceiveAndProcessResponse();

                // 다음 데이터를 위한 요청 패킷을 보냄
                SendRequest();

                return responseProcessed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in device {QuadrantIndex}: {ex.Message}");
                return false;
            }
            finally
            {
                isProcessing = false;
            }
        }

        // 요청 패킷만 보내는 메소드
        private void SendRequest()
        {
            if (Port?.IsOpen == true)
            {
                // 필요 시 출력 버퍼 비우기
                Port.DiscardOutBuffer();

                // 요청 패킷 전송
                Port.Write(RequestPacket, 0, RequestPacket.Length);
            }
        }

        // 응답을 받아 처리하는 메소드
        private bool ReceiveAndProcessResponse()
        {
            if (!Port?.IsOpen == true) return false;

            try
            {
                // 잠시 대기하여 응답 데이터가 들어올 시간을 줌
                if (Port.BytesToRead == 0)
                {
                    System.Threading.Thread.Sleep(5); // 짧은 대기 시간으로 시작

                    // 여전히 데이터가 없으면 더 기다림
                    if (Port.BytesToRead == 0)
                    {
                        System.Threading.Thread.Sleep(10); // 조금 더 대기
                    }
                }

                // 충분한 데이터가 들어왔는지 확인
                if (Port.BytesToRead < 100) // 최소한의 데이터만 확인
                {
                    // 데이터가 충분하지 않으면 입력 버퍼 비우기
                    Port.DiscardInBuffer();
                    return false;
                }

                // 예상되는 전체 응답 크기
                const int EXPECTED_SIZE = 4622;  // 48*48*2 + overhead
                byte[] buffer = new byte[EXPECTED_SIZE];
                int totalBytesRead = 0;

                // 타임아웃 설정
                var startTime = DateTime.Now;
                const int TIMEOUT_MS = 50;  // 50ms로 감소 (파이프라이닝에서는 더 짧은 시간이 적합)

                // 2단계 읽기 접근법: 첫 번째로 헤더를 찾고, 그 다음 나머지 데이터를 읽음
                // 헤더 읽기 (최대 20바이트 정도면 충분)
                const int HEADER_SIZE = 20;
                int headerBytesRead = 0;
                while (headerBytesRead < HEADER_SIZE)
                {
                    if ((DateTime.Now - startTime).TotalMilliseconds > TIMEOUT_MS)
                    {
                        Port.DiscardInBuffer(); // 타임아웃 시 버퍼 비우기
                        return false;
                    }

                    int bytesToRead = Port.BytesToRead;
                    if (bytesToRead == 0) continue;

                    int maxRead = Math.Min(bytesToRead, HEADER_SIZE - headerBytesRead);
                    int bytesRead = Port.Read(buffer, headerBytesRead, maxRead);

                    if (bytesRead == 0) continue;

                    headerBytesRead += bytesRead;
                }

                // 헤더 확인
                int headerStart = FindHeader(buffer, headerBytesRead);
                if (headerStart == -1)
                {
                    Port.DiscardInBuffer(); // 헤더를 찾지 못하면 버퍼 비우기
                    return false;
                }

                // 길이 확인
                int dataLength = GetDataLength(buffer, headerStart);
                if (dataLength != 4608)
                {
                    Port.DiscardInBuffer(); // 길이가 잘못되었으면 버퍼 비우기
                    return false;
                }

                // 총 패킷 길이 계산
                int totalPacketLength = headerStart + 11 + dataLength + 3;
                totalBytesRead = headerBytesRead;

                // 나머지 데이터 읽기
                while (totalBytesRead < totalPacketLength)
                {
                    // 타임아웃 체크
                    if ((DateTime.Now - startTime).TotalMilliseconds > TIMEOUT_MS)
                    {
                        Port.DiscardInBuffer(); // 타임아웃 시 버퍼 비우기
                        return false;
                    }

                    int bytesToRead = Port.BytesToRead;
                    if (bytesToRead == 0) continue;

                    int maxRead = Math.Min(bytesToRead, totalPacketLength - totalBytesRead);
                    int bytesRead = Port.Read(buffer, totalBytesRead, maxRead);

                    if (bytesRead == 0) continue;

                    totalBytesRead += bytesRead;
                }

                // 테일 확인
                if (!CheckTail(buffer, totalPacketLength - 3))
                {
                    Port.DiscardInBuffer(); // 테일이 잘못되었으면 버퍼 비우기
                    return false;
                }

                // 데이터 처리
                ProcessData(buffer, headerStart + 11, dataLength);

                // 남은 데이터가 있다면 버퍼 비우기
                if (Port.BytesToRead > 0)
                {
                    Port.DiscardInBuffer();
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error receiving data from device {QuadrantIndex}: {ex.Message}");
                Port.DiscardInBuffer(); // 예외 발생 시 버퍼 비우기
                return false;
            }
        }

        public bool RequestAndReceiveData()
        {
            if (!Port?.IsOpen == true || isProcessing) return false;

            isProcessing = true;

            try
            {
                // Clear buffers
                if (Port.BytesToRead > 1000)
                {
                    Port.DiscardInBuffer();
                    Port.DiscardOutBuffer();
                }

                // Send request
                Port.Write(RequestPacket, 0, RequestPacket.Length);

                // 예상되는 전체 응답 크기
                const int EXPECTED_SIZE = 4622;  // 48*48*2 + overhead
                byte[] buffer = new byte[EXPECTED_SIZE];
                int totalBytesRead = 0;

                // 타임아웃 설정
                var startTime = DateTime.Now;
                const int TIMEOUT_MS = 100;  // 1000ms에서 100ms로 감소

                // 2단계 읽기 접근법: 첫 번째로 헤더를 찾고, 그 다음 나머지 데이터를 읽음
                // 헤더 읽기 (최대 20바이트 정도면 충분)
                const int HEADER_SIZE = 20;
                int headerBytesRead = 0;
                while (headerBytesRead < HEADER_SIZE)
                {
                    if ((DateTime.Now - startTime).TotalMilliseconds > TIMEOUT_MS)
                        return false;

                    int bytesToRead = Port.BytesToRead;
                    if (bytesToRead == 0) continue;

                    int maxRead = Math.Min(bytesToRead, HEADER_SIZE - headerBytesRead);
                    int bytesRead = Port.Read(buffer, headerBytesRead, maxRead);

                    if (bytesRead == 0) continue;

                    headerBytesRead += bytesRead;
                }

                // 헤더 확인
                int headerStart = FindHeader(buffer, headerBytesRead);
                if (headerStart == -1)
                    return false;

                // 길이 확인
                int dataLength = GetDataLength(buffer, headerStart);
                if (dataLength != 4608)
                    return false;

                // 총 패킷 길이 계산
                int totalPacketLength = headerStart + 11 + dataLength + 3;
                totalBytesRead = headerBytesRead;

                // 나머지 데이터 읽기
                while (totalBytesRead < totalPacketLength)
                {
                    // 타임아웃 체크
                    if ((DateTime.Now - startTime).TotalMilliseconds > TIMEOUT_MS)
                        return false;

                    int bytesToRead = Port.BytesToRead;
                    if (bytesToRead == 0) continue;

                    int maxRead = Math.Min(bytesToRead, totalPacketLength - totalBytesRead);
                    int bytesRead = Port.Read(buffer, totalBytesRead, maxRead);

                    if (bytesRead == 0) continue;

                    totalBytesRead += bytesRead;
                }

                // 테일 확인
                if (!CheckTail(buffer, totalPacketLength - 3))
                    return false;

                // 데이터 처리
                ProcessData(buffer, headerStart + 11, dataLength);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in device {QuadrantIndex}: {ex.Message}");
            }
            finally
            {
                isProcessing = false;
            }

            return false;
        }

        private int FindHeader(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 7; i++)
            {
                if (buffer[i] == 0x53 && buffer[i + 1] == 0x41 &&
                    buffer[i + 2] == 0x31 && buffer[i + 3] == 0x41 &&
                    buffer[i + 4] == 0x33 && buffer[i + 5] == 0x35 &&
                    buffer[i + 6] == 0x35)
                {
                    return i;
                }
            }
            return -1;
        }

        private int GetDataLength(byte[] buffer, int headerStart)
        {
            try
            {
                string lengthHex = System.Text.Encoding.ASCII.GetString(buffer, headerStart + 7, 4);
                return Convert.ToInt32(lengthHex, 16);
            }
            catch
            {
                return -1;
            }
        }

        private bool CheckTail(byte[] buffer, int tailStart)
        {
            return buffer[tailStart] == 0x46 &&
                   buffer[tailStart + 1] == 0x46 &&
                   buffer[tailStart + 2] == 0x45;
        }

        private void ProcessData(byte[] buffer, int dataStart, int dataLength)
        {
            ushort[] newData = new ushort[48 * 48];
            int numValues = Math.Min(dataLength / 2, newData.Length);

            for (int i = 0; i < numValues; i++)
            {
                newData[i] = BitConverter.ToUInt16(buffer, dataStart + (i * 2));
            }

            LastData = newData;
        }

        public void Close()
        {
            if (Port?.IsOpen == true)
            {
                // 입출력 버퍼 비우기
                Port.DiscardInBuffer();
                Port.DiscardOutBuffer();

                // 포트 닫기
                Port.Close();
            }

            // 명시적 Dispose 호출
            Port?.Dispose();
            Port = null;
        }
    }

    public class CircularBuffer2
    {
        private ushort[][] _buffer;
        private readonly object _lockObject = new object();
        private int _start;  // 버퍼의 시작 위치
        private int _count;  // 현재 저장된 항목 수
        private readonly int _dataSize;

        public CircularBuffer2(int capacity, int dataSize = 9216)
        {
            _buffer = new ushort[capacity][];
            _start = 0;
            _count = 0;
            _dataSize = dataSize;
        }

        public void Enqueue(ushort[] data)
        {
            lock (_lockObject)
            {
                int index = (_start + _count) % _buffer.Length;
                if (_count == _buffer.Length)
                {
                    // 버퍼가 가득 찼을 때 가장 오래된 항목 제거
                    //_buffer[_start]?.Dispose();
                    _start = (_start + 1) % _buffer.Length;
                    _count--;
                }
                _buffer[index] = new ushort[_dataSize];
                Array.Copy(data, _buffer[index], _dataSize);
                _count++;
            }
        }

        public ushort[] Get(int index)
        {
            lock (_lockObject)
            {
                if (index >= _count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                ushort[] result = new ushort[_dataSize];
                Array.Copy(_buffer[(_start + index) % _buffer.Length], result, _dataSize);
                return result;
            }
        }

        public ushort[] GetReference(int index)
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
                Array.Clear(_buffer, 0, _buffer.Length);
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

        public int Capacity
        {
            get { return _buffer.Length; }
        }

        public int DataSize
        {
            get { return _dataSize; }
        }
    }
}