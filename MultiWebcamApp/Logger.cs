using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MultiWebcamApp
{
    public class Logger
    {
        private readonly string _logFilePath;
        private static readonly object _fileLock = new object(); // 파일 쓰기 동시성 제어를 위한 락 객체

        /// <summary>
        /// ActivityLogger 클래스의 새 인스턴스를 초기화합니다.
        /// 로그 파일은 애플리케이션 실행 파일과 동일한 디렉토리에 생성됩니다.
        /// </summary>
        /// <param name="logFileName">생성할 로그 파일의 이름 (기본값: history.log)</param>
        public Logger(string logFileName = "history.log")
        {
            // 애플리케이션이 실행되는 디렉토리를 가져와 로그 파일 경로를 구성합니다.
            string appDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _logFilePath = Path.Combine(appDirectory, logFileName);

            // 로그 파일이 없으면 생성합니다.
            if (!File.Exists(_logFilePath))
            {
                try
                {
                    File.Create(_logFilePath).Dispose(); // 파일을 생성하고 즉시 닫습니다.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ActivityLogger] 로그 파일 생성 실패: {ex.Message}");
                    // 파일 생성 실패 시 추가적인 예외 처리 (예: 로깅 비활성화) 고려
                }
            }
        }

        /// <summary>
        /// 지정된 메시지를 로그 파일에 한 줄로 추가합니다.
        /// 로그 형식: YYYY-MM-DD_HH:MM:SS [메시지]
        /// </summary>
        /// <param name="message">로그에 기록할 메시지</param>
        public void LogActivity(string message)
        {
            // 현재 시간을 "YYYY-MM-DD_HH:MM:SS" 형식으로 포맷합니다.
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss");
            string logEntry = $"{timestamp} [{message}]";

            // 파일 쓰기 시 동시성 문제를 방지하기 위해 락을 사용합니다.
            lock (_fileLock)
            {
                try
                {
                    // 파일을 열고 한 줄을 추가한 후 즉시 닫습니다.
                    // File.AppendAllText는 파일을 열고, 내용을 쓰고, 파일을 닫는 작업을 수행합니다.
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    // 로그 파일 쓰기 실패 시 예외 처리 (콘솔에 출력)
                    Console.WriteLine($"[ActivityLogger] 로그 쓰기 오류: {ex.Message} - '{logEntry}'");
                }
            }
        }

        /// <summary>
        /// 지정된 메시지를 비동기적으로 로그 파일에 한 줄로 추가합니다.
        /// UI 스레드를 블록하지 않아 반응성을 유지할 때 유용합니다.
        /// </summary>
        /// <param name="message">로그에 기록할 메시지</param>
        public async Task LogActivityAsync(string message)
        {
            await Task.Run(() => LogActivity(message)); // 동기 메서드를 새 스레드에서 실행
        }
    }
}
