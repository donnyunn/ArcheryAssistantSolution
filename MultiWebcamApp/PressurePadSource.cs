using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MultiWebcamApp
{
    public class PressurePadSource : IFrameSource, IDisposable
    {
        private readonly string[] _portNames = { "COM3", "COM4", "COM5", "COM6" };
        private readonly RJCP.IO.Ports.SerialPortStream[] _ports;
        private readonly ConcurrentQueue<(ushort[] Data, long Timestamp)> _dataQueue;
        private ushort[] _lastData = new ushort[9216]; // 96x96
        private volatile bool _isInitialized;
        private bool _isDisposed;
        private readonly ushort[] _combinedData = new ushort[9216]; // 96x96
        private readonly Calibration _calibration = new Calibration();
        private readonly object _portLock = new object();
        private readonly object _dataLock = new object();

        private Task[] _portTasks; // One task per port
        private volatile bool _needsReset = false;

        public bool NeedsReset => _needsReset;
        public void ResetFlag() => _needsReset = false;

        private const int PACKET_SIZE = 4622;
        private const int DATA_OFFSET = 11;
        private const int HEADER_SIZE = 7;
        private const int DATA_SIZE = 4608;
        private const int TIMEOUT_MS = 300;
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

        // 예외 카운터를 포트별로 추적
        private readonly int[] _consecutiveErrorsPerPort = new int[4];
        private const int ERROR_THRESHOLD = 5;

        private ushort[][] _previousQuadrantDataArray;

        private Task _workTask;
        private AutoResetEvent _workSignal = new AutoResetEvent(false);
        private volatile bool _isRunning = false;

        private FrameData _frameData = new FrameData();
        private int _frameCount = 0;

        public PressurePadSource()
        {
            _ports = new RJCP.IO.Ports.SerialPortStream[_portNames.Length];
            _dataQueue = new ConcurrentQueue<(ushort[], long)>();
            InitializePorts();
            _previousQuadrantDataArray = new ushort[_ports.Length][];
        }

        public FrameData CaptureFrame()
        {
            // 초기화되지 않은 경우 빈 데이터 반환
            if (!_isInitialized)
            {
                return new FrameData
                {
                    PressureData = null,
                    Timestamp = DateTime.Now.Ticks
                };
            }

            // 재설정 필요 여부 확인
            if (_needsReset)
            {
                // 재설정 플래그 초기화
                _needsReset = false;

                // 별도 스레드에서 재설정 실행 (UI 블로킹 방지)
                Task.Run(() => ResetAllPorts());

                Console.WriteLine("CaptureFrame에서 재설정 필요성 감지: 포트 재설정 시작");
            }

            if (_frameCount++ % 3 == 0)
            {

                // WorkTask에 신호 보내기 (비동기적으로 패킷 수신/송신 트리거)
                _workSignal.Set();

                // 데이터 접근 동기화
                lock (_dataLock)
                {
                    // 큐에서 데이터 가져오기 시도
                    if (_dataQueue.TryDequeue(out var data))
                    {
                        // 가져온 데이터를 _lastData에 저장 (이후 큐가 비었을 때 사용)
                        Array.Copy(data.Data, 0, _lastData, 0, _lastData.Length);

                        // 새로 가져온 데이터 반환
                        _frameData.PressureData = data.Data;
                        _frameData.Timestamp = data.Timestamp;
                        return _frameData.Clone();
                        //return new FrameData
                        //{
                        //    PressureData = data.Data,
                        //    Timestamp = data.Timestamp
                        //};
                    }
                    else
                    {
                        _frameCount = 0;
                        // 큐가 비어있으면 마지막으로 저장된 데이터 복제하여 반환
                        _frameData.PressureData = _lastData;
                        _frameData.Timestamp = DateTime.Now.Ticks;
                        return _frameData.Clone();
                        //return new FrameData
                        //{
                        //    PressureData = (ushort[])_lastData.Clone(),
                        //    Timestamp = DateTime.Now.Ticks // 현재 시간으로 타임스탬프 갱신
                        //};
                    }
                }
            }
            else
            {
                _frameData.PressureData = _lastData;
                _frameData.Timestamp = DateTime.Now.Ticks;
                return _frameData.Clone();
            }
        }

        // WorkTask 시작 메서드
        public void StartWorkTask()
        {
            if (_workTask != null && !_workTask.IsCompleted)
                return; // 이미 실행 중이면 아무것도 하지 않음

            _isRunning = true;
            _workTask = Task.Run(WorkTaskLoop);
            Console.WriteLine("WorkTask 루프가 시작되었습니다.");
        }

        // WorkTask 중지 메서드
        public void StopWorkTask()
        {
            _isRunning = false;
            _workSignal.Set(); // 대기 중인 경우 해제하여 루프가 종료되도록 함

            // 태스크가 정상적으로 종료될 때까지 최대 1초 대기
            if (_workTask != null && !_workTask.IsCompleted)
            {
                try
                {
                    _workTask.Wait(1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WorkTask 중지 중 오류: {ex.Message}");
                }
            }

            Console.WriteLine("WorkTask 루프가 중지되었습니다.");
        }

        // WorkTask 메인 루프
        private async Task WorkTaskLoop()
        {
            while (_isRunning)
            {
                Stopwatch stepStopwatch = new Stopwatch();

                try
                {
                    // 오류 카운터 확인 (각 WorkTask 사이클 시작 시)
                    CheckErrorThresholds();

                    // CaptureFrame에서 신호가 올 때까지 대기
                    _workSignal.WaitOne();

                    if (!_isRunning)
                        break; // 중지 신호가 왔으면 루프 종료

                    // 2단계: 요청 패킷 송신 (4개 포트)
                    //await SendRequestPackets();
                    SendRequestPackets();

                    //stepStopwatch.Restart();
                    // 1단계: 응답 패킷 수신 (4개 포트)
                    //await SendRequestPackets();
                    await ReceiveResponsePackets();
                    //stepStopwatch.Stop();
                    //Console.WriteLine($"{stepStopwatch.ElapsedMilliseconds}ms");

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WorkTask 처리 중 오류 발생: {ex.Message}");
                    await Task.Delay(100); // 오류 발생 시 약간 대기
                }
            }

            Console.WriteLine("WorkTask 루프가 종료되었습니다.");
        }

        // 오류 임계값 확인 메서드
        private void CheckErrorThresholds()
        { 
            // 모든 포트의 오류 카운터 확인
            for (int i = 0; i < _consecutiveErrorsPerPort.Length; i++)
            {
                if (_consecutiveErrorsPerPort[i] >= ERROR_THRESHOLD)
                {
                    //Console.WriteLine($"포트 {_portNames[i]}에서 연속 오류 임계값({ERROR_THRESHOLD})에 도달: 재설정 필요");
                    
                    // 카운터 리셋 (중복 재설정 방지)
                    _consecutiveErrorsPerPort[i] = 0;

                    int portIndex = i;
                    Task.Run(() => ResetPort(portIndex));
                }
            }

            // 하나 이상의 포트가 임계값에 도달했다면 재설정 실행
            //if (resetNeeded)
            //{
            //    Console.WriteLine("오류 임계값에 도달한 포트가 감지되어 모든 포트를 재설정합니다...");

            //    // WorkTask 내에서 동기적으로 실행하는 것보다 별도의 태스크로 실행
            //    Task.Run(() => ResetAllPorts());
            //}
        }

        // 응답 패킷 수신 처리
        private async Task<bool> ReceiveResponsePackets()
        {
            // 사분면 데이터를 저장할 배열
            ushort[][] quadrantDataArray = new ushort[_ports.Length][];

            // 각 포트별로 동시에 수신 처리
            var receiveTasks = new Task<ushort[]>[_ports.Length];

            for (int i = 0; i < _ports.Length; i++)
            {
                int portIndex = i;
                if (_ports[portIndex] != null && _ports[portIndex].IsOpen)
                {
                    receiveTasks[i] = Task.Run(() => ReceiveFromPort(portIndex));
                }
                else
                {
                    //_consecutiveErrorsPerPort[i]++;
                    receiveTasks[i] = Task.FromResult<ushort[]>(null);
                }
            }

            // 모든 포트의 수신이 완료될 때까지 대기 (최대 300ms)
            try
            {
                await Task.WhenAll(receiveTasks).TimeoutAfter(500);

                // 각 태스크의 결과를 배열에 저장
                for (int i = 0; i < _ports.Length; i++)
                {
                    quadrantDataArray[i] = receiveTasks[i].Result;

                    //if (quadrantDataArray[i] == null)
                    //{
                    //    quadrantDataArray[i] = _previousQuadrantDataArray[i];
                    //}

                    if (IsDataIdentical(_previousQuadrantDataArray[i], quadrantDataArray[i]))
                    {
                        //Console.WriteLine($"COM{i+3}: {_consecutiveErrorsPerPort[i]}");
                        _consecutiveErrorsPerPort[i]++;
                    }
                    else
                    {
                        _consecutiveErrorsPerPort[i] = 0;
                    }

                    // 현재 데이터를 이전 데이터로 저장
                    _previousQuadrantDataArray[i] = quadrantDataArray[i]?.ToArray(); // 깊은 복사

                }
            }
            catch (TimeoutException)
            {
                Console.WriteLine("일부 포트 수신 태스크가 타임아웃 되었습니다.");

                // 완료된 태스크만 결과 수집
                for (int i = 0; i < _ports.Length; i++)
                {
                    if (receiveTasks[i].IsCompleted && !receiveTasks[i].IsFaulted)
                    {
                        quadrantDataArray[i] = receiveTasks[i].Result;

                        if (IsDataIdentical(_previousQuadrantDataArray[i], quadrantDataArray[i]))
                        {
                            _consecutiveErrorsPerPort[i]++;
                        }
                        else
                        {
                            _consecutiveErrorsPerPort[i] = 0;
                        }

                        // 현재 데이터를 이전 데이터로 저장
                        _previousQuadrantDataArray[i] = quadrantDataArray[i]?.ToArray(); // 깊은 복사
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"응답 패킷 수신 중 오류 발생: {ex.Message}");
                return false;
            }

            // 수집된 데이터를 결합하여 96x96 배열 생성
            bool anyDataProcessed = CombineQuadrantData(quadrantDataArray);
            return anyDataProcessed;
        }

        // 두 데이터 배열이 동일한지 비교하는 메소드
        private bool IsDataIdentical(ushort[] previous, ushort[] current)
        {
            if (previous == null || current == null) return true;
            if (previous.Length != current.Length) return false;

            for (int i = 0; i < previous.Length; i++)
            {
                if (previous[i] != current[i]) return false;
            }
            return true;
        }

        // 사분면 데이터를 결합하여 96x96 배열 생성
        private bool CombineQuadrantData(ushort[][] quadrantDataArray)
        {
            if (quadrantDataArray == null || quadrantDataArray.Length != _ports.Length)
            {
                Console.WriteLine("유효하지 않은 사분면 데이터 배열");
                return false;
            }

            // 유효한 데이터가 있는지 확인
            bool anyValidData = false;
            for (int i = 0; i < quadrantDataArray.Length; i++)
            {
                if (quadrantDataArray[i] != null && quadrantDataArray[i].Length == 2304) // 48x48
                {
                    anyValidData = true;
                    break;
                }
            }

            if (!anyValidData)
            {
                Console.WriteLine("유효한 사분면 데이터가 없습니다");
                return false;
            }

            // 데이터 결합 작업 수행
            lock (_dataLock)
            {
                // 이전 상태 백업 (필요시 롤백)
                ushort[] previousState = (ushort[])_combinedData.Clone();
                bool dataUpdated = false;

                try
                {
                    // 각 사분면 데이터를 _combinedData에 배치
                    for (int portIndex = 0; portIndex < quadrantDataArray.Length; portIndex++)
                    {
                        ushort[] quadrantData = quadrantDataArray[portIndex];
                        if (quadrantData == null || quadrantData.Length != 2304)
                        {
                            continue; // 유효하지 않은 데이터는 건너뜀
                        }

                        Point startPos = StartPositions[portIndex];

                        // 결합 데이터에 복사
                        for (int col = 0; col < 48; col++)
                        {
                            for (int row = 0; row < 48; row++)
                            {
                                int srcIndex = col * 48 + row;
                                int destRow, destCol;

                                switch (portIndex)
                                {
                                    case 0: // 좌측 상단: Y축 대칭
                                        destRow = (int)startPos.Y + (row);
                                        destCol = (int)startPos.X + (col);
                                        break;
                                    case 1: // 좌측 하단: Y축 대칭
                                        destRow = (int)startPos.Y + (row);
                                        destCol = (int)startPos.X + (col);
                                        break;
                                    case 2: // 우측 상단: X축 대칭
                                        destRow = (int)startPos.Y + row;
                                        destCol = (int)startPos.X + (col);
                                        break;
                                    case 3: // 우측 하단: 정상
                                        destRow = (int)startPos.Y + row;
                                        destCol = (int)startPos.X + col;
                                        break;
                                    default:
                                        continue;
                                }

                                int destIndex = destRow * 96 + destCol;
                                if (destIndex >= 0 && destIndex < _combinedData.Length)
                                {
                                    _combinedData[destIndex] = quadrantData[srcIndex];
                                    dataUpdated = true;
                                }
                            }
                        }
                    }

                    // 데이터가 업데이트되었으면 큐에 추가
                    if (dataUpdated)
                    {
                        ushort[] currentSnapshot = (ushort[])_combinedData.Clone();
                        _dataQueue.Enqueue((currentSnapshot, DateTime.Now.Ticks));

                        // 큐 크기 제한
                        while (_dataQueue.Count > 10 && _dataQueue.TryDequeue(out _)) { }
                    }

                    return dataUpdated;
                }
                catch (Exception ex)
                {
                    // 오류 발생 시 이전 상태로 롤백
                    Console.WriteLine($"데이터 결합 중 오류 발생: {ex.Message}");
                    Array.Copy(previousState, 0, _combinedData, 0, _combinedData.Length);
                    return false;
                }
            }
        }

        private async Task<ushort[]> ReceiveFromPortAsync(int portIndex)
        {
            var port = _ports[portIndex];
            if (port == null || !port.IsOpen)
                return null;

            try
            {
                // 응답 패킷 사이즈: 4622 바이트
                byte[] buffer = new byte[PACKET_SIZE];
                int bytesRead = 0;
                DateTime startTime = DateTime.Now;

                // 타임아웃 내에 데이터 수신 시도
                while (bytesRead < PACKET_SIZE && (DateTime.Now - startTime).TotalMilliseconds < TIMEOUT_MS)
                {
                    if (port.BytesToRead > 0)
                    {
                        int read = await port.ReadAsync(buffer, bytesRead, Math.Min(PACKET_SIZE - bytesRead, port.BytesToRead));
                        if (read == 0) break;
                        bytesRead += read;
                    }
                    else
                    {
                        await Task.Delay(1);
                    }
                }

                //port.DiscardInBuffer();

                if (bytesRead >= PACKET_SIZE)
                {
                    // 수신된 데이터 처리
                    ushort[] processedData = Process(buffer, portIndex);
                    return processedData;
                }
                else
                {
                    Console.WriteLine($"포트 {port.PortName}에서 불완전한 패킷 수신: {bytesRead}/{PACKET_SIZE} 바이트");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"포트 {port.PortName}에서 응답 수신 중 오류: {ex.Message}");
                return null;
            }
        }

        // 단일 포트에서 응답 패킷 수신
        private ushort[] ReceiveFromPort(int portIndex)
        {
            var port = _ports[portIndex];
            if (port == null || !port.IsOpen)
                return null;

            try
            {
                // 응답 패킷 사이즈: 4622 바이트
                byte[] buffer = new byte[PACKET_SIZE];
                int bytesRead = 0;
                DateTime startTime = DateTime.Now;

                // 타임아웃 내에 데이터 수신 시도
                while (bytesRead < PACKET_SIZE && (DateTime.Now - startTime).TotalMilliseconds < TIMEOUT_MS)
                {
                    if (port.BytesToRead > 0)
                    {
                        int read = port.Read(buffer, bytesRead, Math.Min(PACKET_SIZE - bytesRead, port.BytesToRead));
                        bytesRead += read;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }

                //port.DiscardInBuffer();

                if (bytesRead >= PACKET_SIZE)
                {
                    // 수신된 데이터 처리
                    ushort[] processedData = Process(buffer, portIndex);
                    return processedData;
                }
                else
                {
                    Console.WriteLine($"포트 {port.PortName}에서 불완전한 패킷 수신: {bytesRead}/{PACKET_SIZE} 바이트");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"포트 {port.PortName}에서 응답 수신 중 오류: {ex.Message}");
                return null;
            }
        }

        // 요청 패킷 송신 처리
        //private async Task SendRequestPackets()
        private void SendRequestPackets()
        {
            // 각 포트별로 동시에 송신 처리
            var sendTasks = new Task[_ports.Length];

            for (int i = 0; i < _ports.Length; i++)
            {
                int portIndex = i;
                if (_ports[portIndex] != null && _ports[portIndex].IsOpen)
                {
                    sendTasks[i] = Task.Run(() => SendToPort(portIndex));
                }
                else
                {
                    sendTasks[i] = Task.CompletedTask;
                }
                //SendToPort(portIndex);
            }

            // 모든 포트의 송신이 완료될 때까지 대기 (최대 300ms)
            await Task.WhenAll(sendTasks).TimeoutAfter(300);
        }

        // 단일 포트에 요청 패킷 송신
        private bool SendToPort(int portIndex)
        {
            var port = _ports[portIndex];
            if (port == null || !port.IsOpen)
                return false;

            try
            {
                // 포트가 닫혀 있으면 다시 열기 시도
                if (!port.IsOpen)
                {
                    try
                    {
                        Console.WriteLine($"포트 {port.PortName}가 닫혀 있어 다시 열기 시도");
                        port.Open();
                        Thread.Sleep(50); // 포트가 안정화될 시간 제공
                        Console.WriteLine($"포트 {port.PortName} 재연결 성공");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"포트 {port.PortName} 재연결 실패: {ex.Message}");
                        _consecutiveErrorsPerPort[portIndex]++;
                        return false;
                    }
                }

                // 송신 전 버퍼 비우기
                port.DiscardOutBuffer();
                port.DiscardInBuffer();

                //Thread.Sleep(5);

                // 요청 패킷 전송 (14바이트)
                port.Write(REQUEST_PATTERN, 0, REQUEST_SIZE);
                //port.Flush();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"포트 {port.PortName}에 요청 송신 중 오류: {ex.Message}");

                Console.WriteLine($"스택 추적: {ex.StackTrace}");

                if (ex.InnerException != null)
                    Console.WriteLine($"내부 예외: {ex.InnerException.Message}");

                // 포트 상태 출력
                Console.WriteLine($"포트 상태: IsOpen={port.IsOpen}, BytesToWrite={port.BytesToWrite}, BytesToRead={port.BytesToRead}");

                return false;
            }
        }
        
        // 수신된 패킷 버퍼를 처리하여 48x48 배열을 추출하는 함수
        private ushort[] Process(byte[] buffer, int portIndex)
        {
            if (buffer == null || buffer.Length < PACKET_SIZE)
            {
                Console.WriteLine($"Process: 유효하지 않은 버퍼 (길이: {(buffer == null ? "null" : buffer.Length.ToString())})");
                return null;
            }

            try
            {
                // 1. 버퍼에서 헤더 위치 찾기
                int headerPos = FindHeader(buffer, buffer.Length);
                if (headerPos < 0)
                {
                    Console.WriteLine($"Process: 헤더를 찾을 수 없음 (포트 {portIndex})");
                    return null;
                }

                // 2. 테일 확인
                if (!CheckTail(buffer, headerPos + PACKET_SIZE - 3))
                {
                    Console.WriteLine($"Process: 잘못된 테일 (포트 {portIndex})");
                    return null;
                }

                // 3. 데이터 부분 추출
                ushort[] quadrantData = new ushort[DATA_SIZE / 2]; // 48x48 = 2304 elements
                for (int i = 0; i < DATA_SIZE / 2; i++)
                {
                    int index = headerPos + DATA_OFFSET + i * 2;
                    if (index + 1 < buffer.Length)
                    {
                        quadrantData[i] = BitConverter.ToUInt16(buffer, index);
                    }
                }

                // 4. 보정 적용 (필요한 경우)
                if (_calibration != null)
                {
                    quadrantData = _calibration.Work(quadrantData, portIndex);
                }

                return quadrantData;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process: 데이터 처리 중 오류 발생 (포트 {portIndex}): {ex.Message}");
                return null;
            }
        }

        private void InitializePorts()
        {
            lock (_portLock)
            {
                _isInitialized = false;
                bool anyPortInitialized = false;

                for (int i = 0; i < _portNames.Length; i++)
                {
                    try
                    {
                        // 이미 포트가 설정되었다면 정리
                        if (_ports[i] != null)
                        {
                            try
                            {
                                if (_ports[i].IsOpen) _ports[i].Close();
                                _ports[i].Dispose();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"포트 정리 중 오류: {ex.Message}");
                            }
                        }

                        _ports[i] = new RJCP.IO.Ports.SerialPortStream(_portNames[i], 3000000, 8, RJCP.IO.Ports.Parity.None, RJCP.IO.Ports.StopBits.One)
                        {
                            ReadTimeout = 300,
                            ReadBufferSize = 32768,
                            WriteBufferSize = 4096,
                        };
                        _ports[i].Open();
                        _consecutiveErrorsPerPort[i] = 0; // 에러 카운터 초기화
                        Console.WriteLine($"포트 열림: {_portNames[i]}");
                        anyPortInitialized = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"포트 {_portNames[i]} 열기 실패: {ex.Message}");
                        _ports[i] = null;
                    }
                }

                _isInitialized = anyPortInitialized;

                if (!_isInitialized)
                {
                    Console.WriteLine("압력패드 장치가 초기화되지 않았습니다. 카메라 전용 모드로 실행합니다.");
                }
            }
        }

        public void Start()
        {
            StartWorkTask();
            return;
        }

        public void Stop()
        {
            StopWorkTask();
            return;
        }

        public void ResetPort(int portIndex)
        {
            if (portIndex < 0 || portIndex >= _ports.Length)
                return;

            try
            {
                // 기존 포트 정리
                if (_ports[portIndex] != null)
                {
                    try
                    {
                        if (_ports[portIndex].IsOpen)
                            _ports[portIndex].Close();
                        _ports[portIndex].Dispose();
                    }
                    catch { }
                }

                Thread.Sleep(1);

                // 포트 새로 열기
                _ports[portIndex] = new RJCP.IO.Ports.SerialPortStream(_portNames[portIndex], 3000000, 8, RJCP.IO.Ports.Parity.None, RJCP.IO.Ports.StopBits.One)
                {
                    ReadTimeout = 300,
                    ReadBufferSize = 32768,
                    WriteBufferSize = 4096
                };
                _ports[portIndex].Open();

                // 오류 카운터 초기화
                _consecutiveErrorsPerPort[portIndex] = 0;
                Console.WriteLine($"포트 {_portNames[portIndex]} 복구 성공");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"포트 {_portNames[portIndex]} 복구 실패: {ex.Message}");
            }
        }

        public void ResetAllPorts()
        {
            Console.WriteLine("압력패드 포트 재설정 시작...");

            // 1. 먼저 실행 중지
            Stop();

            // 2. 포트 정리
            lock (_portLock)
            {
                // 포트 배열 복사 (락 내 작업 최소화)
                var portsToDispose = _ports.Where(p => p != null).ToArray();

                // 포트 배열 초기화
                Array.Clear(_ports, 0, _ports.Length);

                // 포트 닫기 및 해제
                foreach (var port in portsToDispose)
                {
                    try
                    {
                        if (port.IsOpen) port.Close();
                        Console.WriteLine($"{port.PortName} 닫힘 완료");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"포트 정리 중 오류: {ex.Message}");
                    }
                }
                foreach (var port in portsToDispose)
                {
                    port.Dispose();
                }

                // 큐 및 데이터 초기화
                while (_dataQueue.TryDequeue(out _)) { }
                Array.Clear(_combinedData, 0, _combinedData.Length);
                Array.Clear(_lastData, 0, _lastData.Length);

                // 에러 카운터 초기화
                for (int i = 0; i < _consecutiveErrorsPerPort.Length; i++)
                {
                    _consecutiveErrorsPerPort[i] = 0;
                }
            }

            // 3. USB 장치 재인식 대기
            Thread.Sleep(1000);

            // 4. 포트 재초기화
            InitializePorts();

            _needsReset = false;
            Console.WriteLine("포트 재설정 완료");

            // 5. 작업 재시작
            if (_isInitialized)
            {
                Start();
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
            if (startIndex < 0 || startIndex + TAIL_PATTERN.Length > buffer.Length)
                return false;

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
                try
                {
                    Stop();
                    _workSignal?.Dispose();

                    lock (_portLock)
                    {
                        foreach (var port in _ports.Where(p => p != null))
                        {
                            try
                            {
                                if (port.IsOpen) port.Close();
                                Task.Delay(10).Wait();
                                port.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"포트 해제 중 오류 {port.PortName}: {ex.Message}");
                            }
                        }

                        Array.Clear(_ports, 0, _ports.Length);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Dispose 중 오류: {ex.Message}");
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