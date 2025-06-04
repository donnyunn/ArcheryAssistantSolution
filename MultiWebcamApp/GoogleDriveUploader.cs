using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Upload; // IUploadProgress와 UploadStatus를 위해 추가

namespace MultiWebcamApp // 프로젝트의 적절한 네임스페이스로 변경하세요.
{
    public class GoogleDriveUploader
    {
        private readonly string _applicationName = "archeryassistantsolution";
        private readonly string _credentialsPath = "token.json"; // 인증 토큰이 저장될 경로
        private readonly string _clientSecretsFileName = "client_secrets.json"; // Google Cloud에서 다운로드한 JSON 파일 이름
        private readonly string _logFilePath; // history.log 파일 경로
        private readonly Logger _Logger; // 로그 기록을 위한 로거 (Logger로 네임스페이스 변경)
        private const string LastUploadDateKey = "LastGoogleDriveUploadDate"; // 마지막 업로드 날짜를 저장할 키
        private readonly string _settingsFilePath; // 마지막 업로드 날짜를 저장할 설정 파일 경로

        // Google Drive 업로드 대상 폴더 이름 (Google Drive에 미리 생성되어 있어야 합니다)
        private readonly string _targetFolderName = "ArcheryAssistantLog";

        /// <summary>
        /// GoogleDriveUploader 클래스의 새 인스턴스를 초기화합니다.
        /// </summary>
        /// <param name="logFilePath">업로드할 history.log 파일의 전체 경로</param>
        /// <param name="Logger">활동 로그를 기록할 Logger 인스턴스</param>
        public GoogleDriveUploader(string logFilePath, Logger Logger) // Logger 타입으로 변경
        {
            _logFilePath = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
            _Logger = Logger ?? throw new ArgumentNullException(nameof(Logger));

            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _settingsFilePath = Path.Combine(appDirectory, "uploader_settings.ini"); // 설정 파일 경로
        }

        /// <summary>
        /// 인터넷 연결을 확인합니다.
        /// </summary>
        /// <returns>인터넷 연결이 가능하면 true, 아니면 false</returns>
        private bool IsInternetConnected()
        {
            try
            {
                using (var ping = new Ping())
                {
                    PingReply reply = ping.Send("8.8.8.8", 1000); // 1초 타임아웃
                    return reply.Status == IPStatus.Success;
                }
            }
            catch (PingException ex)
            {
                _Logger.LogActivity($"인터넷 연결 확인 중 Ping 오류: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _Logger.LogActivity($"인터넷 연결 확인 중 일반 오류: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Google Drive로 history.log 파일을 업로드합니다.
        /// 하루에 한 번만 업로드하며, 인터넷 연결을 확인합니다.
        /// </summary>
        public async Task UploadLogFileAsync()
        {
            if (!IsInternetConnected())
            {
                _Logger.LogActivity("인터넷 연결이 없어 Google Drive 업로드를 건너뜁니다");
                return;
            }

            if (HasUploadedToday())
            {
                _Logger.LogActivity("오늘 이미 Google Drive에 업로드되었습니다. 업로드를 건너뜁니다");
                return;
            }

            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string clientSecretsPath = Path.Combine(appDirectory, _clientSecretsFileName);

            if (!File.Exists(clientSecretsPath))
            {
                _Logger.LogActivity($"오류: '{_clientSecretsFileName}' 파일을 찾을 수 없습니다. Google Drive API 설정이 필요합니다");
                //_Logger.LogActivity("Google Cloud Console에서 OAuth 클라이언트 ID를 생성하고, 다운로드한 JSON 파일을 애플리케이션 실행 경로에 넣어주세요");
                return;
            }

            UserCredential credential;
            try
            {
                using (var stream = new FileStream(clientSecretsPath, FileMode.Open, FileAccess.Read))
                {
                    credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.FromStream(stream).Secrets,
                        new[] { DriveService.Scope.DriveFile },
                        "user",
                        CancellationToken.None,
                        new FileDataStore(_credentialsPath, true));
                }
                _Logger.LogActivity("Google Drive 인증 성공");
            }
            catch (Exception ex)
            {
                _Logger.LogActivity($"Google Drive 인증 실패: {ex.Message}");
                //_Logger.LogActivity("Google Drive 인증 실패 시, 'token.json' 파일을 삭제하고 다시 시도해볼 수 있습니다");
                return;
            }

            var service = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = _applicationName,
            });

            string folderId = await GetOrCreateTargetFolder(service, _targetFolderName);
            if (string.IsNullOrEmpty(folderId))
            {
                _Logger.LogActivity($"Google Drive 대상 폴더 '{_targetFolderName}'를 찾거나 생성할 수 없습니다");
                return;
            }

            using (var fileStream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read))
            {
                try
                {
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File()
                    {
                        Name = Path.GetFileName(_logFilePath),
                        Parents = new List<string> { folderId }
                    };

                    FilesResource.ListRequest listRequest = service.Files.List();
                    listRequest.Q = $"name = '{fileMetadata.Name}' and '{folderId}' in parents and trashed = false";
                    listRequest.Fields = "files(id, name)";
                    var files = await listRequest.ExecuteAsync();

                    // 업로드 요청 객체를 저장할 변수를 var로 선언하여 타입 추론에 맡깁니다.
                    // 이 변수들은 UploadAsync()가 완료된 후에도 계속 유효합니다.
                    dynamic requestToExecute; // <-- dynamic 키워드 사용 (가장 간단하고 확실한 방법)

                    if (files.Files.Any())
                    {
                        string existingFileId = files.Files.First().Id;
                        _Logger.LogActivity($"기존 파일 '{fileMetadata.Name}' (ID: {existingFileId})을(를) 업데이트 중...");
                        requestToExecute = service.Files.Update(null, existingFileId, fileStream, "text/plain");
                        
                    }
                    else
                    {
                        _Logger.LogActivity($"새 파일 '{fileMetadata.Name}'을(를) 업로드 중...");
                        requestToExecute = service.Files.Create(fileMetadata, fileStream, "text/plain");
                        
                    }

                    // UploadAsync()를 호출하여 업로드를 시작하고 진행 상태를 반환받습니다.
                    IUploadProgress uploadProgress = await requestToExecute.UploadAsync(); // <-- UploadAsync 호출

                    // 업로드 상태 확인
                    switch (uploadProgress.Status)
                    {
                        case UploadStatus.Completed:
                            // 업로드 요청 객체 (requestToExecute) 자체에 포함된 ResponseBody를 사용합니다.
                            // 이 ResponseBody는 이미 Google.Apis.Drive.v3.Data.File 타입입니다.
                            var uploadedFile = (Google.Apis.Drive.v3.Data.File)requestToExecute.ResponseBody;
                            _Logger.LogActivity($"파일 '{uploadedFile.Name}'이(가) Google Drive에 성공적으로 업로드되었습니다 (ID: {uploadedFile.Id})");
                            MarkUploadedToday();
                            break;
                        case UploadStatus.Failed:
                            _Logger.LogActivity($"Google Drive 업로드 실패: {uploadProgress.Exception?.Message ?? "알 수 없는 오류"}");
                            break;
                        case UploadStatus.Uploading:
                            // 이 경우는 UploadAsync가 완료될 때까지 await하므로 여기에 도달하지 않습니다.
                            break;
                        default:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _Logger.LogActivity($"Google Drive 업로드 중 치명적인 오류 발생: {ex.Message}");
                }
            }
        }

        private async Task<string> GetOrCreateTargetFolder(DriveService service, string folderName)
        {
            try
            {
                FilesResource.ListRequest listRequest = service.Files.List();
                listRequest.Q = $"name = '{folderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
                listRequest.Fields = "files(id)";
                var folders = await listRequest.ExecuteAsync();

                if (folders.Files.Any())
                {
                    return folders.Files.First().Id;
                }
                else
                {
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File()
                    {
                        Name = folderName,
                        MimeType = "application/vnd.google-apps.folder"
                    };
                    var request = service.Files.Create(fileMetadata);
                    request.Fields = "id";
                    var folder = await request.ExecuteAsync();
                    _Logger.LogActivity($"Google Drive에 새 폴더 '{folderName}' 생성: {folder.Id}");
                    return folder.Id;
                }
            }
            catch (Exception ex)
            {
                _Logger.LogActivity($"Google Drive 대상 폴더 검색/생성 오류: {ex.Message}");
                return null;
            }
        }

        private bool HasUploadedToday()
        {
            if (!File.Exists(_settingsFilePath))
            {
                return false;
            }

            try
            {
                string[] lines = File.ReadAllLines(_settingsFilePath);
                foreach (string line in lines)
                {
                    if (line.StartsWith(LastUploadDateKey + "="))
                    {
                        string dateString = line.Substring(LastUploadDateKey.Length + 1);
                        if (DateTime.TryParse(dateString, out DateTime lastUploadDate))
                        {
                            return lastUploadDate.Date == DateTime.Today.Date;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _Logger.LogActivity($"설정 파일에서 마지막 업로드 날짜 읽기 오류: {ex.Message}");
            }
            return false;
        }

        private void MarkUploadedToday()
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(_settingsFilePath))
                {
                    lines = File.ReadAllLines(_settingsFilePath).ToList();
                }

                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith(LastUploadDateKey + "="))
                    {
                        lines[i] = $"{LastUploadDateKey}={DateTime.Today.Date:yyyy-MM-dd}";
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    lines.Add($"{LastUploadDateKey}={DateTime.Today.Date:yyyy-MM-dd}");
                }

                File.WriteAllLines(_settingsFilePath, lines, Encoding.UTF8);
                _Logger.LogActivity($"마지막 Google Drive 업로드 날짜를 {DateTime.Today.Date:yyyy-MM-dd}로 기록했습니다");
            }
            catch (Exception ex)
            {
                _Logger.LogActivity($"설정 파일에 마지막 업로드 날짜 쓰기 오류: {ex.Message}");
            }
        }
    }
}