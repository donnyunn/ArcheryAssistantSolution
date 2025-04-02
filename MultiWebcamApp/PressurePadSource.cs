using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiWebcamApp
{
    public class PressurePadSource : IFrameSource, IDisposable
    {
        private readonly string[] _portNames = { "COM3", "COM4", "COM5", "COM6" };
        private readonly RJCP.IO.Ports.SerialPortStream[] _ports;
        private readonly ConcurrentQueue<(ushort[] Data, long Timestamp)> _dataQueue;
        private ushort[] _lastData = new ushort[9216]; // 96x96
        private bool _isStopped;
        private bool _isInitialized;
        private bool _isDisposed;
        private ushort[] _combinedData = new ushort[9216]; // 96x96
        private readonly Calibration _calibration = new Calibration();
        private readonly object _portLock = new object();
        private readonly object _dataLock = new object();

        private Task _readTask;
        private CancellationTokenSource _readCts;
        private volatile int _isReading = 0;

        // 자동 리셋 관련 속성
        private volatile bool _needsReset = false;
        public bool NeedsReset => _needsReset;
        public void ResetFlag() => _needsReset = false;

        private const int PACKET_SIZE = 4622;
        private const int DATA_OFFSET = 11;
        private const int HEADER_SIZE = 7;
        private const int DATA_SIZE = 4608;
        private const int TIMEOUT_MS = 100;
        private static readonly byte[] HEADER_PATTERN = { 0x53, 0x41, 0x31, 0x41, 0x33, 0x35, 0x35 };
        private static readonly byte[] TAIL_PATTERN = { 0x46, 0x46, 0x45 };

        private const int REQUEST_SIZE = 14;
        private static readonly byte[] REQUEST_PATTERN = { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };

        private static readonly Point[] StartPositions = new Point[]
        {
            new Point(0, 0),   // COM3: 1사분면 (좌측 상단)
            new Point(0, 48),  // COM4: 2사분면 (좌측 하단)
            new Point(48, 0),  // COM5: 3사분면 (우측 상단)
            new Point(48, 48)  // COM6: 4사분면 (우측 하단)
        };

        public PressurePadSource()
        {
            _ports = new RJCP.IO.Ports.SerialPortStream[_portNames.Length];
            _dataQueue = new ConcurrentQueue<(ushort[], long)>();
            _readCts = new CancellationTokenSource();

            // COM 포트 초기화 시도
            InitializePorts();
        }

        private void InitializePorts()
        {
            lock (_portLock)
            {
                _isInitialized = false;
                for (int i = 0; i < _portNames.Length; i++)
                {
                    try
                    {
                        _ports[i] = new RJCP.IO.Ports.SerialPortStream(_portNames[i], 3000000, 8, RJCP.IO.Ports.Parity.None, RJCP.IO.Ports.StopBits.One)
                        {
                            ReadTimeout = 50,
                            ReadBufferSize = 16384
                        };
                        _ports[i].Open();
                        _isInitialized = true;
                        Console.WriteLine($"Opened COM port: {_portNames[i]}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to open COM port {_portNames[i]}: {ex.Message}");
                        _ports[i] = null;
                    }
                }

                if (!_isInitialized)
                {
                    Console.WriteLine("No pressure pad devices initialized. Running in camera-only mode.");
                }
            }
        }

        public FrameData CaptureFrame()
        {
            FrameData frameData = new FrameData();

            if (!_isInitialized)
                return new FrameData { PressureData = null, Timestamp = DateTime.Now.Ticks };

            // 1. 큐에서 가장 최근 데이터 가져오기 (없으면 마지막 데이터 사용)
            lock (_dataLock)
            {
                if (_dataQueue.TryDequeue(out var data))
                {
                    Array.Copy(data.Data, 0, _lastData, 0, 9216);
                    frameData.PressureData = data.Data;
                    frameData.Timestamp = data.Timestamp;
                }
                else
                {
                    // 새 데이터가 없으면 마지막 데이터 사용
                    frameData.PressureData = _lastData;
                    frameData.Timestamp = DateTime.Now.Ticks;
                }
            }

            // 2. 백그라운드 태스크가 아직 실행 중이 아니면 시작
            StartBackgroundReading();

            return frameData;
        }

        private void StartBackgroundReading()
        {
            // 중지 상태면 시작하지 않음
            if (_isStopped || !_isInitialized)
                return;

            // 이미 읽기 작업이 실행 중이면 패스
            if (Interlocked.CompareExchange(ref _isReading, 1, 0) == 1)
                return;

            // 현재 작업이 실행 중인지 확인
            if (_readTask != null && !_readTask.IsCompleted)
            {
                // 이미 실행 중인 작업이 있으면 기존 작업 취소 후 대기
                try
                {
                    _readCts?.Cancel();
                    Task.WaitAny(new[] { _readTask }, 500);
                }
                catch { }
            }

            // 새 캔슬레이션 토큰 필요시 생성
            if (_readCts == null || _readCts.IsCancellationRequested)
            {
                _readCts?.Dispose();
                _readCts = new CancellationTokenSource();
            }

            // 새로운 태스크 시작
            _readTask = Task.Run(() => BackgroundReadingLoop(), _readCts.Token);
        }

        private async Task BackgroundReadingLoop()
        {
            try
            {
                while (!_isStopped && _isInitialized)
                {
                    try
                    {
                        // 디바이스에 데이터 요청 및 읽기
                        await RequestAndReadData();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"압력패드 데이터 읽기 오류: {ex.Message}");
                        await Task.Delay(100); // 오류 발생 시 잠시 대기
                    }
                }
            }
            finally
            {
                // 읽기 작업 종료 표시
                Interlocked.Exchange(ref _isReading, 0);
            }
        }

        private async Task RequestAndReadData()
        {
            // 시작 전에 중지 상태 확인
            if (_isStopped)
                return;

            // 활성 포트 목록 - 스냅샷 생성 (동시 접근 방지)
            RJCP.IO.Ports.SerialPortStream[] activePorts;
            lock (_portLock)
            {
                activePorts = _ports.Where(p => p != null && p.IsOpen).ToArray();
            }

            if (activePorts.Length == 0) return;

            // 모든 활성 포트에 데이터 요청
            foreach (var port in activePorts)
            {
                try
                {
                    if (port.IsOpen)  // 추가 검증
                    {
                        port.Write(REQUEST_PATTERN, 0, REQUEST_SIZE);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 객체가 이미 폐기됨 - 무시하고 계속 진행
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Request error on port: {ex.Message}");
                }
            }

            // 각 포트에서 데이터 읽기
            if (activePorts.Length == 0) return;

            var tasks = activePorts.Select((port, index) => Task.Run(() =>
            {
                try
                {
                    if (port.IsOpen)  // 추가 검증
                    {
                        return ReadPortData(port, index);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 무시
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Task creation error: {ex.Message}");
                }
                return false;
            })).ToArray();

            try
            {
                // 모든 포트 데이터 읽기 완료 대기 (짧은 타임아웃 적용)
                if (tasks.Length > 0)
                {
                    var timeoutTask = Task.WhenAll(tasks).TimeoutAfter(150);
                    if (await timeoutTask)
                    {
                        // 모든 포트에서 읽기 성공
                        lock (_dataLock)
                        {
                            _dataQueue.Enqueue(((ushort[])_combinedData.Clone(), DateTime.Now.Ticks));

                            // 큐 크기 제한
                            while (_dataQueue.Count > 5)
                            {
                                _dataQueue.TryDequeue(out _);
                            }
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                // 타임아웃은 정상적인 상황으로 처리
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during port read: {ex.Message}");
            }
        }

        private bool ReadPortData(RJCP.IO.Ports.SerialPortStream port, int portIndex)
        {
            if (port == null || !port.IsOpen) return false;

            byte[] buffer = new byte[PACKET_SIZE];
            int totalBytesRead = 0;
            var packetStartTime = DateTime.Now;

            try
            {
                // 1. 헤더 탐지
                while (true)
                {
                    if ((DateTime.Now - packetStartTime).TotalMilliseconds > TIMEOUT_MS)
                    {
                        port.DiscardInBuffer();
                        return false;
                    }

                    int bytesToRead = Math.Min(port.BytesToRead, HEADER_SIZE - totalBytesRead);
                    if (bytesToRead <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int bytesRead = port.Read(buffer, totalBytesRead, bytesToRead);
                    totalBytesRead += bytesRead;

                    int headerStart = FindHeader(buffer, totalBytesRead);
                    if (headerStart >= 0)
                    {
                        totalBytesRead = totalBytesRead - headerStart;
                        if (headerStart > 0)
                            Array.Copy(buffer, headerStart, buffer, 0, totalBytesRead);
                        break;
                    }
                }

                // 2. 데이터 길이 및 나머지 데이터 읽기
                while (totalBytesRead < PACKET_SIZE)
                {
                    if ((DateTime.Now - packetStartTime).TotalMilliseconds > TIMEOUT_MS)
                    {
                        port.DiscardInBuffer();
                        return false;
                    }

                    int bytesToRead = Math.Min(port.BytesToRead, PACKET_SIZE - totalBytesRead);
                    if (bytesToRead <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int bytesRead = port.Read(buffer, totalBytesRead, bytesToRead);
                    totalBytesRead += bytesRead;
                }

                // 3. 테일 검증
                if (!CheckTail(buffer, PACKET_SIZE - 3))
                {
                    port.DiscardInBuffer();
                    return false;
                }

                // 4. 유효한 패킷 처리
                port.DiscardInBuffer();

                // 데이터 변환 (바이트 → ushort)
                ushort[] quadrantData = new ushort[DATA_SIZE / 2];
                for (int i = 0; i < DATA_SIZE / 2; i++)
                {
                    quadrantData[i] = BitConverter.ToUInt16(buffer, DATA_OFFSET + i * 2);
                }

                // 보정 적용
                quadrantData = _calibration.Work(quadrantData, portIndex);

                // 사분면 위치 가져오기
                int portNameIndex = Array.IndexOf(_portNames, port.PortName);
                if (portNameIndex < 0 || portNameIndex >= StartPositions.Length)
                {
                    return false;
                }

                Point startPos = StartPositions[portNameIndex];

                // 조합 데이터에 적용
                lock (_combinedData)
                {
                    for (int col = 0; col < 48; col++)
                    {
                        for (int row = 0; row < 48; row++)
                        {
                            int srcIndex = col * 48 + row;
                            int destRow, destCol;

                            // 사분면별 매핑 조정
                            switch (portNameIndex)
                            {
                                case 0: // 좌측 상단: Y축 대칭
                                    destRow = (int)startPos.Y + (47 - row);
                                    destCol = (int)startPos.X + (47 - col);
                                    break;
                                case 1: // 좌측 하단: Y축 대칭
                                    destRow = (int)startPos.Y + (47 - row);
                                    destCol = (int)startPos.X + col;
                                    break;
                                case 2: // 우측 상단: X축 대칭
                                    destRow = (int)startPos.Y + row;
                                    destCol = (int)startPos.X + (47 - col);
                                    break;
                                case 3: // 우측 하단: 정상
                                    destRow = (int)startPos.Y + row;
                                    destCol = (int)startPos.X + col;
                                    break;
                                default:
                                    return false;
                            }

                            int destIndex = destRow * 96 + destCol;
                            if (destIndex >= 0 && destIndex < _combinedData.Length)
                            {
                                _combinedData[destIndex] = quadrantData[srcIndex];
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading from port {port.PortName}: {ex.Message}");
                port.DiscardInBuffer();
                return false;
            }
        }

        public void Start()
        {
            if (!_isInitialized) return;
            _isStopped = false;

            // 백그라운드 읽기 작업 시작
            StartBackgroundReading();
        }

        public void Stop()
        {
            _isStopped = true;
            _readCts?.Cancel();

            // 작업이 완료될 때까지 대기
            _readTask?.Wait(500);

            _readCts?.Dispose();
            _readCts = new CancellationTokenSource();

            Interlocked.Exchange(ref _isReading, 0);
        }

        public void ResetAllPorts()
        {
            Console.WriteLine("Resetting all pressure pad ports...");

            // 1. 먼저 실행 중인 모든 작업을 중지하고 완료될 때까지 대기
            _isStopped = true;

            // 캔슬레이션 토큰으로 작업 취소
            try
            {
                _readCts?.Cancel();

                // 백그라운드 작업 완료 대기 (더 긴 타임아웃 설정)
                if (_readTask != null && !_readTask.IsCompleted)
                {
                    // 최대 1초 동안 작업 완료 대기
                    Task.WaitAny(new[] { _readTask }, 1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"백그라운드 작업 중지 중 오류: {ex.Message}");
            }

            // 작업 상태 초기화
            Interlocked.Exchange(ref _isReading, 0);

            // 2. 이제 포트 처리 진행
            lock (_portLock)
            {
                foreach (var port in _ports.Where(p => p != null))
                {
                    try
                    {
                        if (port.IsOpen)
                        {
                            port.Close();
                            Console.WriteLine($"Closed COM port: {port.PortName}");
                        }
                        port.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error closing COM port {port.PortName}: {ex.Message}");
                    }
                }

                // 배열을 초기화하여 이전 포트에 대한 모든 참조 제거
                Array.Clear(_ports, 0, _ports.Length);

                // 큐 비우기
                while (_dataQueue.TryDequeue(out _)) { }

                Thread.Sleep(1000); // 포트 안정화 대기

                // 새 포트 초기화
                InitializePorts();

                _needsReset = false;
                Console.WriteLine("Pressure pad ports reset completed.");
            }

            // 초기화 성공 시 작업 재시작 준비
            if (_isInitialized)
            {
                _isStopped = false;

                // 이전 캔슬레이션 토큰 폐기 및 새 토큰 생성
                if (_readCts != null)
                {
                    _readCts.Dispose();
                    _readCts = new CancellationTokenSource();
                }

                // 새 작업 시작
                StartBackgroundReading();
            }
        }

        private int FindHeader(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - HEADER_PATTERN.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < HEADER_PATTERN.Length; j++)
                {
                    if (buffer[i + j] != HEADER_PATTERN[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private bool CheckTail(byte[] buffer, int startIndex)
        {
            for (int i = 0; i < TAIL_PATTERN.Length; i++)
            {
                if (buffer[startIndex + i] != TAIL_PATTERN[i]) return false;
            }
            return true;
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

                foreach (var port in _ports.Where(p => p != null))
                {
                    try
                    {
                        if (port.IsOpen)
                        {
                            port.Close();
                        }
                        port.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error disposing port {port.PortName}: {ex.Message}");
                    }
                }
            }

            _isDisposed = true;
        }

        ~PressurePadSource()
        {
            Dispose(false);
        }
    }

    public struct Point
    {
        public int X { get; }
        public int Y { get; }

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    // 확장 메서드: Task 시간 초과 구현
    public static class TaskExtensions
    {
        public static async Task<bool> TimeoutAfter(this Task task, int millisecondsTimeout)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            using var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(task, Task.Delay(millisecondsTimeout, timeoutCancellationTokenSource.Token));

            if (completedTask == task)
            {
                timeoutCancellationTokenSource.Cancel();
                await task;  // 예외 전파
                return true;
            }
            else
            {
                throw new TimeoutException("The operation has timed out.");
            }
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
                                sensorCalibration[sensorIndices[i++]][sectionCount - 1].Slope = slopeValue;
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
}