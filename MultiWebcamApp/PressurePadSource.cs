using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiWebcamApp
{
    public class PressurePadSource : IFrameSource
    {
        private readonly string[] _portNames = { "COM3", "COM4", "COM5", "COM6" };
        private readonly RJCP.IO.Ports.SerialPortStream[] _ports;
        private readonly ConcurrentQueue<(ushort[] Data, long Timestamp)> _dataQueue;
        private CancellationTokenSource _cts;
        private Task _captureTask;
        private ushort[] _combinedData = new ushort[9216]; // 96x96

        public PressurePadSource()
        {
            _ports = _portNames.Select(p => new RJCP.IO.Ports.SerialPortStream(p, 115200, 8, RJCP.IO.Ports.Parity.None, RJCP.IO.Ports.StopBits.One)).ToArray();
            _dataQueue = new ConcurrentQueue<(ushort[], long)>();
            _cts = new CancellationTokenSource();
        }

        public FrameData CaptureFrame()
        {
            if (_dataQueue.TryDequeue(out var data))
            {
                return new FrameData { PressureData = data.Data, Timestamp = data.Timestamp };
            }
            return new FrameData { Timestamp = DateTime.Now.Ticks };
        }

        public void Start()
        {
            foreach (var port in _ports) port.Open();
            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
            _captureTask?.Wait();
            foreach (var port in _ports) { port.Close(); port.Dispose(); }
        }

        private void CaptureLoop(CancellationToken token)
        {
            byte[] request = { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };
            while (!token.IsCancellationRequested)
            {
                var tasks = _ports.Select(port => Task.Run(() =>
                {
                    if (!port.IsOpen) return;
                    port.Write(request, 0, request.Length);
                    byte[] buffer = new byte[4622];
                    int bytesRead = port.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 4622) // 데이터 유효성 체크 필요
                    {
                        int quadrant = Array.IndexOf(_portNames, port.PortName);
                        int baseRow = (quadrant / 2) * 48;
                        int baseCol = (quadrant % 2) * 48;
                        lock (_combinedData)
                        {
                            for (int i = 0; i < 48; i++)
                                for (int j = 0; j < 48; j++)
                                    _combinedData[(baseRow + i) * 96 + (baseCol + j)] = BitConverter.ToUInt16(buffer, 11 + (i * 48 + j) * 2);
                        }
                    }
                })).ToArray();

                Task.WaitAll(tasks);                
                _dataQueue.Enqueue(((ushort[])_combinedData.Clone(), DateTime.Now.Ticks));

                // 큐 크기 제한
                while (_dataQueue.Count > 5)
                    _dataQueue.TryDequeue(out _);

                Thread.Sleep(1);
            }
        }
    }
}
