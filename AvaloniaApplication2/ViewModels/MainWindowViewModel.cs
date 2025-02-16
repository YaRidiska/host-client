using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaApplication2.Services;
using System.Collections.Generic;
using Tmds.DBus.Protocol;
using Avalonia.Threading;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Diagnostics;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls.ApplicationLifetimes;


using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using System.Net.Sockets;
using System.Text;
using AvaloniaApplication2.Models;
using AvaloniaApplication2.Constants;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Models;

namespace AvaloniaApplication2.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

        private TunnelClient? _tunnelClient;

        public ObservableCollection<string> AvailableVersions { get; } = new ObservableCollection<string>
        {
            "1.20.6",
            "Fabric 1.20.6",
            "1.20.4",
            "Fabric 1.20.4",
            "1.20.2",
            "Fabric 1.20.2",
            "1.20.1",
            "Fabric 1.20.1",
        };

        [ObservableProperty]
        private string selectedVersion = "Fabric 1.20.1";

        private readonly ApiService _apiService;
        private readonly HttpClient _httpClient;

        private Process? _serverProcess;

        [ObservableProperty]
        private bool isServerRunning;

        [ObservableProperty]
        private bool _isServerFullyStarted;

        private bool _stopRequested;

        private string _javaExecutablePath = "java";


        private string? _desiredSubdomain;
        [ObservableProperty]
        private string _statusMessage = "Введите поддомен для сервера";
        private bool _isSubdomainAvailable;

        [ObservableProperty]
        private bool _isSubdomainLocked;

        [ObservableProperty]
        private string _fullDomainName = ".yaridiska.ru";

        public string DesiredSubdomain
        {
            get => _desiredSubdomain ?? string.Empty;
            set
            {
                if (SetProperty(ref _desiredSubdomain, value))
                {
                    FullDomainName = $"IP: {value}.{AppConstants.TunnelDomain}";
                }
            }
        }

        public bool IsSubdomainAvailable
        {
            get => _isSubdomainAvailable;
            set => SetProperty(ref _isSubdomainAvailable, value);
        }

        public ICommand ToggleSubdomainCommand { get; }

        [ObservableProperty]
        private bool _isTunnelRunning;

        private UserData? _userData;
        public ICommand ResetSubdomainCommand { get; }

        private TaskCompletionSource<bool>? _serverStoppedTaskSource;

        [ObservableProperty]
        private string _subdomainButtonText = "Выбрать";

        [ObservableProperty]
        private string _subdomainButtonTooltip = "Выбрать поддомен";

        public ICommand ToggleServerCommand { get; }

        [ObservableProperty]
        private string _serverButtonText = "СТАРТ";

        public ICommand CopyServerIpCommand { get; }

        [ObservableProperty]
        private string _logText = string.Empty;

        public ICommand CopyLogsCommand { get; }

        private readonly SubdomainGeneratorService _subdomainGenerator;

        public ICommand GenerateSubdomainCommand { get; }

        [ObservableProperty]
        private bool _isToastVisible;

        [ObservableProperty]
        private string _toastMessage;

        private bool _isToastInProgress;

        [ObservableProperty]
        private bool _isSettingsOpen;

        public ICommand OpenSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }

        [ObservableProperty]
        private bool _showServerLogs = false;

        [ObservableProperty]
        private bool _useNoGui = true;

        [ObservableProperty]
        private double _serverMemoryGB = 1.5;

        [ObservableProperty]
        private double _maxServerMemoryGB;

        public MainWindowViewModel()
        {
            try
            {
                var totalMemoryMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
                _maxServerMemoryGB = Math.Min(totalMemoryMB / 1024.0 * 0.25, 32); // 25% от доступной памяти, но не больше 32 ГБ
                _maxServerMemoryGB = Math.Floor(_maxServerMemoryGB * 2) / 2; // Округляем до 0.5
            }
            catch
            {
                _maxServerMemoryGB = 1.5;
            }

            _apiService = new ApiService(AppConstants.YandexCloudApiUrl);
            _httpClient = new HttpClient();

            LoadOrCreateUserData();

            if (_userData != null)
            {
                DesiredSubdomain = _userData.Subdomain;
                IsSubdomainLocked = _userData.IsSubdomainLocked;
                if (IsSubdomainLocked)
                {
                    IsSubdomainAvailable = true;
                    AddLogMessage($"[UserData] Загружен сохраненный поддомен: {_userData.Subdomain}");
                }
            }

            ToggleSubdomainCommand = new AsyncRelayCommand(OnToggleSubdomainAsync, CanToggleSubdomain);
            ResetSubdomainCommand = new RelayCommand(ResetSubdomain, CanResetSubdomain);

            ToggleServerCommand = new AsyncRelayCommand(OnToggleServerAsync, CanToggleServer);

            CopyServerIpCommand = new RelayCommand(CopyServerIp);

            CopyLogsCommand = new AsyncRelayCommand(CopyLogsAsync);

            this.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IsSubdomainLocked))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                        (ToggleServerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    });
                }
            };

            this.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IsServerRunning))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        (ResetSubdomainCommand as RelayCommand)?.NotifyCanExecuteChanged();
                        (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                        (ToggleServerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    });
                }
            };

            _subdomainGenerator = new SubdomainGeneratorService();
            GenerateSubdomainCommand = new RelayCommand(GenerateSubdomain, () => !IsSubdomainLocked);

            // Инициализируем команды настроек
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            CloseSettingsCommand = new RelayCommand(CloseSettings);
        }

        private void LoadOrCreateUserData()
        {
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_data.dat");
            AddLogMessage(filePath);
            try
            {
                if (File.Exists(filePath))
                {
                    AddLogMessage(filePath);
                    using (FileStream fs = new FileStream(filePath, FileMode.Open))
                    using (BinaryReader reader = new BinaryReader(fs))
                    {
                        var userGuid = reader.ReadString();
                        AddLogMessage(userGuid);
                        var subdomain = reader.ReadString();
                        AddLogMessage(subdomain);
                        var isLocked = reader.ReadBoolean();
                        var selectedVersion = reader.ReadString();
                        var showLogs = reader.ReadBoolean();
                        var useNoGui = reader.ReadBoolean();
                        var serverMemory = reader.ReadDouble();

                        _userData = new UserData
                        {
                            UserGuid = userGuid,
                            Subdomain = subdomain,
                            IsSubdomainLocked = isLocked,
                            SelectedVersion = selectedVersion,
                            ShowServerLogs = showLogs,
                            UseNoGui = useNoGui,
                            ServerMemoryGB = Math.Min(serverMemory, MaxServerMemoryGB)
                        };

                        DesiredSubdomain = subdomain;
                        IsSubdomainLocked = isLocked;
                        SelectedVersion = selectedVersion;
                        ShowServerLogs = showLogs;
                        UseNoGui = useNoGui;
                        ServerMemoryGB = _userData.ServerMemoryGB;

                        if (isLocked)
                        {
                            IsSubdomainAvailable = true;
                            AddLogMessage($"[UserData] Загружен зарезервированный поддомен: {subdomain}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[UserData] Ошибка при загрузке данных пользователя: {ex.Message}");
            }

            if (_userData == null)
            {
                _userData = new UserData
                {
                    UserGuid = Guid.NewGuid().ToString(),
                    Subdomain = "",
                    IsSubdomainLocked = false,
                    SelectedVersion = "Fabric 1.20.1",
                    ShowServerLogs = false,
                    UseNoGui = true,
                    ServerMemoryGB = ServerMemoryGB
                };
                SaveUserData();
            }
        }

        private void SaveUserData()
        {
            if (_userData == null) return;

            _userData.Subdomain = DesiredSubdomain;
            _userData.IsSubdomainLocked = IsSubdomainLocked;
            _userData.SelectedVersion = SelectedVersion;
            _userData.ShowServerLogs = ShowServerLogs;
            _userData.UseNoGui = UseNoGui;
            _userData.ServerMemoryGB = ServerMemoryGB;

            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_data.dat");

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    writer.Write(_userData.UserGuid);
                    writer.Write(_userData.Subdomain);
                    writer.Write(_userData.IsSubdomainLocked);
                    writer.Write(_userData.SelectedVersion);
                    writer.Write(_userData.ShowServerLogs);
                    writer.Write(_userData.UseNoGui);
                    writer.Write(_userData.ServerMemoryGB);
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[UserData] Ошибка при сохранении данных пользователя: {ex.Message}");
            }
        }

        private async void CheckSubdomainAvailability()
        {
            if (string.IsNullOrEmpty(DesiredSubdomain))
            {
                StatusMessage = "Введите поддомен для сервера";
                return;
            }

            bool isWeak;
            if (!ValidateSubdomain(DesiredSubdomain, out isWeak))
            {
                return;
            }

            try
            {
                var isAvailable = await CheckSubdomain(DesiredSubdomain);
                if (isAvailable)
                {
                    if (isWeak)
                    {
                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            var messageBox = MessageBoxManager.GetMessageBoxCustom(
                                new MessageBoxCustomParams
                                {
                                    ButtonDefinitions = new[]
                                    {
                                        new ButtonDefinition { Name = "Все равно выбрать" },
                                        new ButtonDefinition { Name = "Изменить домен" }
                                    },
                                    ContentTitle = "Внимание: слабый поддомен",
                                    ContentMessage = "Такой поддомен может быть случайно угадан другими людьми.\nИз-за этого они могут зайти в вам на сервер.\n\n" +
                                                   "Рекомендуется использовать поддомен длиной\nне менее 8 символов" +
                                                   " и содержащий хотя бы пару цифр\n" +
                                                   "или использовать случайно сгенерированный домен.\n\n",
                                    Icon = Icon.Warning,

                                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                                });

                            var result = await messageBox.ShowWindowDialogAsync(desktop.MainWindow);
                            if (result == null || result == "Изменить IP")
                            {
                                return;
                            }
                        }
                    }

                    IsSubdomainAvailable = true;
                    StatusMessage = "Поддомен доступен";
                    IsSubdomainLocked = true;

                    if (_userData != null)
                    {
                        _userData.Subdomain = DesiredSubdomain;
                        _userData.IsSubdomainLocked = true;
                        SaveUserData();
                        AddLogMessage($"[UserData] Поддомен {DesiredSubdomain} сохранен");
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                        (ToggleServerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    });
                }
                else
                {
                    IsSubdomainAvailable = false;
                    StatusMessage = "Поддомен уже занят";

                    Dispatcher.UIThread.Post(() =>
                    {
                        (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                        (ToggleServerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    });
                }
            }
            catch
            {
                StatusMessage = "Ошибка проверки поддомена";
            }
        }

        public async Task<bool> CheckSubdomain(string? subdomain)
        {
            if (string.IsNullOrEmpty(subdomain))
            {
                AddLogMessage("[Client] Subdomain is null or empty");
                return false;
            }
            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
                    await tcpClient.ConnectAsync(AppConstants.ServerIp, AppConstants.ServerPort);
                    NetworkStream networkStream = tcpClient.GetStream();
                    StreamWriter writer = new StreamWriter(networkStream, Encoding.UTF8) { AutoFlush = true };
                    StreamReader reader = new StreamReader(networkStream, Encoding.UTF8);

                    string request = $"CHECK_SUBDOMAIN {_userData.UserGuid} {subdomain}\n";
                    AddLogMessage($"[Client] Отправка запроса: {request.Trim()}");
                    await writer.WriteAsync(request);

                    string? response = await reader.ReadLineAsync();
                    if (response == null)
                    {
                        AddLogMessage("[Client] No response from server.");
                        return false;
                    }

                    AddLogMessage($"[Client] Получен ответ: {response}");

                    if (response.StartsWith("SUBDOMAIN_AVAILABLE"))
                    {
                        AddLogMessage("[Client] Поддомен доступен.");
                        return true;
                    }
                    else if (response.StartsWith("SUBDOMAIN_TAKEN"))
                    {
                        AddLogMessage("[Client] Поддомен уже занят.");
                        return false;
                    }
                    else
                    {
                        AddLogMessage($"[Client] Получен неизвестный ответ от сервера: {response}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Client] Ошибка при проверке поддомена: {ex.Message}");
                return false;
            }
        }

        private async void ResetSubdomain()
        {
            if (_userData == null) return;

            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
                    await tcpClient.ConnectAsync(AppConstants.ServerIp, AppConstants.ServerPort);
                    NetworkStream networkStream = tcpClient.GetStream();
                    StreamWriter writer = new StreamWriter(networkStream, Encoding.UTF8) { AutoFlush = true };
                    StreamReader reader = new StreamReader(networkStream, Encoding.UTF8);

                    string request = $"RESET_SUBDOMAIN {_userData.UserGuid}\n";
                    AddLogMessage($"[Client] Отправка запроса сброса поддомена: {request.Trim()}");
                    await writer.WriteAsync(request);

                    string? response = await reader.ReadLineAsync();
                    if (response == null)
                    {
                        AddLogMessage("[Client] No response from server.");
                        return;
                    }

                    AddLogMessage($"[Client] Получен ответ: {response}");

                    if (response.StartsWith("SUBDOMAIN_RESET_OK"))
                    {
                        IsSubdomainLocked = false;
                        IsSubdomainAvailable = false;
                        StatusMessage = "Введите поддомен для сервера";


                        _userData.IsSubdomainLocked = false;
                        SaveUserData();

                        AddLogMessage("[Client] Поддомен успешно сброшен");
                        Dispatcher.UIThread.Post(() =>
                        {
                            (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                        });
                    }
                    else
                    {
                        AddLogMessage($"[Client] Неожиданный ответ при сбросе поддомена: {response}");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Client] Ошибка при сбросе поддомена: {ex.Message}");
            }
        }

        private bool CanResetSubdomain()
        {
            return !IsServerRunning;
        }

        // ------------------------------------------------------------------------------------
        //                         ОСНОВНАЯ ЛОГИКА ЗАПУСКА/ОСТАНОВКИ СЕРВЕРА
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Обработчик нажатия «Запустить сервер».
        /// </summary>
        private async Task OnStartServerAsync()
        {
            IsServerRunning = true;
            _isServerFullyStarted = false;

            Dispatcher.UIThread.Post(() =>
            {
                (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            });

            StatusMessage = string.Empty;

            string? javaPath = GetJavaFromPath();

            if (javaPath == null)
            {
                AddLogMessage("[Java] Java не найдена в PATH.");

                string projectDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string javaDirectory = Path.Combine(projectDirectory, "java");

                if (Directory.Exists(javaDirectory))
                {
                    var javaSubdirectories = Directory.GetDirectories(javaDirectory)
                                                     .Where(dir => File.Exists(Path.Combine(dir, "bin", "java.exe")))
                                                     .ToList();

                    if (javaSubdirectories.Any())
                    {
                        string existingJavaPath = Path.Combine(javaSubdirectories.First(), "bin", "java.exe");
                        _javaExecutablePath = existingJavaPath;
                        AddLogMessage($"[Java] Найдена локальная Java: {_javaExecutablePath}");
                    }
                    else
                    {
                        AddLogMessage("[Java] Директория 'java' существует, но java.exe не найдена. Необходимо скачать Java.");
                        Dispatcher.UIThread.Post(() => StatusMessage = "Загрузка Java...");
                        await DownloadAndSetupJavaAsync(javaDirectory);
                    }
                }
                else
                {
                    AddLogMessage("[Java] Директория 'java' не найдена. Начинаем скачивание Java.");
                    Dispatcher.UIThread.Post(() => StatusMessage = "Загрузка Java...");
                    await DownloadAndSetupJavaAsync(javaDirectory);
                }
            }
            else
            {
                _javaExecutablePath = "java";
                AddLogMessage($"[Java] Используется Java из PATH: {javaPath}");
            }

            bool inboundRuleExists = IsPortInboundRulePresent(AppConstants.LocalPort);

            if (!inboundRuleExists)
            {
                Dispatcher.UIThread.Post(() => StatusMessage = "Открываем порт 25565...");
                var messageBox = MessageBoxManager.GetMessageBoxStandard(
                    "Требуется открыть порт 25565",
                    "Чтобы хостить Minecraft-сервер, нужно разрешить входящие подключения на порт 25565.\n" +
                    "Мы можем автоматически открыть этот порт в брандмауэре Windows.\n\n" +
                    "Нажмите \"ОК\", чтобы создать правило.\nНажмите \"Cancel\", чтобы отменить запуск сервера.",
                    ButtonEnum.OkCancel,
                    Icon.Info);

                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var result = await messageBox.ShowWindowDialogAsync(desktop.MainWindow);

                    if (result == ButtonResult.Ok)
                    {
                        bool success = AddFirewallRuleNetsh("Minecraft Inbound 25565", AppConstants.LocalPort);
                        if (!success)
                        {
                            AddLogMessage("[Firewall] Не удалось открыть порт через netsh. Запуск сервера прерван.");
                            IsServerRunning = false;
                            return;
                        }
                    }
                    else
                    {
                        AddLogMessage("[Firewall] Пользователь отказался открывать порт. Запуск сервера прерван.");
                        IsServerRunning = false;
                        return;
                    }
                }
            }

            try
            {
                _stopRequested = false;
                _isServerFullyStarted = false;

                AddLogMessage($"[Server] Запуск серверной версии: {SelectedVersion}");

                var (serverDirectory, serverFile, serverPath) = GetServerPaths();

                if (!File.Exists(serverPath))
                {
                    Dispatcher.UIThread.Post(() => StatusMessage = "Скачиваем выделенный сервер...");
                    AddLogMessage($"[Server] Файл не найден: {serverPath}; Начинаем установку...");
                    await SetupServerAsync(serverDirectory, serverFile);
                }
                else
                {
                    AddLogMessage("[Server] Файл сервера найден. Запускаем...");
                    await StartServerProcessAsync(serverDirectory, serverFile);
                }

                Dispatcher.UIThread.Post(() => StatusMessage = "Сервер запускается...");



                if (IsServerRunning && !_stopRequested)
                {
                    await WaitForServerFullyStarted();

                    if (_isServerFullyStarted && !_stopRequested)
                    {
                        AddLogMessage("[Tunnel] Сервер полностью загрузился. Пробуем поднять туннель...");
                        await TryStartTunnelWithRetries();
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Error] Ошибка при запуске: {ex.Message}");
            }
        }


        /// <summary>
        /// Скачивает и распаковывает Java в указанную директорию.
        /// </summary>
        private async Task DownloadAndSetupJavaAsync(string javaDirectory)
        {
            var javaLinks = await GetJavaLinksAsync();

            if (javaLinks == null || !javaLinks.Any())
            {
                AddLogMessage("[Java] Не удалось получить ссылки для скачивания Java.");
                return;
            }

            var firstLink = javaLinks.First();
            string javaZipName = firstLink.Key;
            string javaDownloadUrl = firstLink.Value;

            string projectDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string javaZipPath = Path.Combine(projectDirectory, javaZipName);

            try
            {
                if (!Directory.Exists(javaDirectory))
                {
                    Directory.CreateDirectory(javaDirectory);
                    AddLogMessage($"[Java] Создана директория для Java: {javaDirectory}");
                }

                AddLogMessage($"[Java] Скачивание Java с {javaDownloadUrl}");
                await DownloadFileAsync(javaDownloadUrl, javaZipPath);

                AddLogMessage($"[Java] Распаковка Java в {javaDirectory}");
                await UnzipFileAsync(javaZipPath, javaDirectory);

                File.Delete(javaZipPath);
                AddLogMessage($"[Java] Архив {javaZipName} удалён после распаковки.");

                var javaSubdirectories = Directory.GetDirectories(javaDirectory)
                                                 .Where(dir => File.Exists(Path.Combine(dir, "bin", "java.exe")))
                                                 .ToList();

                if (javaSubdirectories.Any())
                {
                    string localJavaPath = Path.Combine(javaSubdirectories.First(), "bin", "java.exe");
                    _javaExecutablePath = localJavaPath;
                    AddLogMessage($"[Java] Установлен путь к локальной Java: {_javaExecutablePath}");
                }
                else
                {
                    AddLogMessage($"[Java] Не удалось найти java.exe после распаковки в {javaDirectory}");
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Java] Ошибка при скачивании или распаковке Java: {ex.Message}");
            }
        }


        /// <summary>
        /// Проверяет, доступна ли команда 'java' из PATH.
        /// </summary>
        private string? GetJavaFromPath()
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                });

                ProcessStartInfo whereJava = new ProcessStartInfo("where", "java")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var whereProcess = Process.Start(whereJava);
                string? javaPath = whereProcess?.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                whereProcess?.WaitForExit();

                if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
                {
                    return null;
                }

                ProcessStartInfo versionCheck = new ProcessStartInfo(javaPath, "-version")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var versionProcess = Process.Start(versionCheck);
                string? versionOutput = versionProcess?.StandardError.ReadToEnd();
                versionProcess?.WaitForExit();

                if (versionOutput != null)
                {
                    var versionMatch = System.Text.RegularExpressions.Regex.Match(versionOutput, @"version ""(\d+)");
                    if (versionMatch.Success && int.TryParse(versionMatch.Groups[1].Value, out int majorVersion))
                    {
                        if (majorVersion >= 21)
                        {
                            AddLogMessage($"[Java] Найдена подходящая Java {majorVersion} в PATH: {javaPath}");
                            return javaPath;
                        }
                        else
                        {
                            AddLogMessage($"[Java] Найденная Java {majorVersion} слишком старая, требуется версия 21 или выше");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Java] Ошибка при проверке Java: {ex.Message}");
                Dispatcher.UIThread.Post(() => StatusMessage = "Ошибка при проверке Java");
            }

            return null;
        }

        /// <summary>
        /// Получает ссылки для скачивания Java с вашего API Gateway.
        /// </summary>
        private async Task<Dictionary<string, string>?> GetJavaLinksAsync()
        {
            try
            {
                string endpoint = "java-links";

                var response = await _apiService.GetLinksAsync(endpoint);
                if (response != null && response.Any())
                {
                    AddLogMessage("[Java] Получены ссылки для скачивания Java.");
                    return response;
                }
                else
                {
                    AddLogMessage("[Java] Получены пустые ссылки для скачивания Java.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Java] Ошибка при получении ссылок для скачивания: {ex.Message}");
                return null;
            }
        }

        // -------------------------------------------------------------------
        //           Есть ли Inbound-правило на порт?
        // -------------------------------------------------------------------
        private bool IsPortInboundRulePresent(int port)
        {
            var showCommand = "advfirewall firewall show rule name=all";
            var psi = new ProcessStartInfo("netsh", showCommand)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                bool RuleFound = output.Contains($"Minecraft Inbound 25565");

                return RuleFound;
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Firewall] Не смогли прочитать правила: {ex.Message}");
                return false;
            }
        }

        // -------------------------------------------------------------------
        //           Добавить inbound-правило через netsh
        // -------------------------------------------------------------------
        private bool AddFirewallRuleNetsh(string ruleName, int port)
        {
            AddLogMessage("[Firewall] Попытка добавить inbound-правило...");

            var command = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in protocol=TCP localport={port} action=allow";
            var psi = new ProcessStartInfo("netsh", command)
            {
                Verb = "runas",
                CreateNoWindow = true,
                UseShellExecute = true
            };

            try
            {
                using var process = Process.Start(psi);
                process?.WaitForExit();

                AddLogMessage("[Firewall] Правило добавлено (возможно успешно).");
                return true;
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Firewall] Ошибка при добавлении правила: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Обработчик нажатия Остановить сервер
        /// </summary>
        private async Task OnStopServer()
        {
            if (_serverProcess == null || _serverProcess.HasExited)
                return;

            _stopRequested = true;
            IsServerFullyStarted = false;

            _serverProcess.StandardInput.WriteLine("stop");
            _serverProcess.StandardInput.Flush();

            try
            {
                await _serverProcess.WaitForExitAsync();
            }
            finally
            {
                _stopRequested = false;
                IsServerRunning = false;
                _serverProcess = null;
                StatusMessage = "Сервер готов к запуску";
            }
        }

        /// <summary>
        /// Возвращаем пути к серверной папке, jar-файлу и полному пути jar-файла.
        /// </summary>
        private (string serverDirectory, string serverFile, string serverPath) GetServerPaths()
        {
            string projectDirectory = AppDomain.CurrentDomain.BaseDirectory;

            string serverFile = SelectedVersion.Contains("Fabric")
                ? "fabric-server-launch.jar"
                : "server.jar";

            string serverDirectory = Path.Combine(projectDirectory, "minecraft-dedicate-server", SelectedVersion);
            string serverPath = Path.Combine(serverDirectory, serverFile);
            return (serverDirectory, serverFile, serverPath);
        }

        // ------------------------------------------------------------------------------------
        //                   ЛОГИКА МЯГКОГО ОСТАНОВА MINECRAFT (КОМАНДА stop)
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Отправляем команду "stop" в консоль Minecraft
        /// </summary>
        private async Task SoftStopMinecraftServer()
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                AddLogMessage("[Server] Сервер не запущен или уже остановлен.");
                IsServerRunning = false;
                return;
            }

            try
            {
                AddLogMessage("[Server] Отправляем команду stop для мягкого завершения...");
                if (_serverProcess.StartInfo.RedirectStandardInput)
                {
                    _serverProcess.StandardInput.WriteLine("stop");
                    _serverProcess.StandardInput.Flush();
                }
                else
                {
                    AddLogMessage("[Warning] Не удалось отправить 'stop'. Input не редиректится.");
                    return;
                }

                var timeout = TimeSpan.FromSeconds(600);
                var completedTask = await Task.WhenAny(_serverStoppedTaskSource?.Task ?? Task.CompletedTask, Task.Delay(timeout));

                if (completedTask == (_serverStoppedTaskSource?.Task ?? Task.CompletedTask))
                {
                    AddLogMessage("[Server] Сервер остановлен мягко.");
                }
                else
                {
                    AddLogMessage("[Server] Сервер не завершился за отведённое время. Принудительно убиваем...");
                }

                IsServerRunning = false;
                AddLogMessage("[Server] Сервер остановлен мягко (или принудительно).");
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Error] Ошибка при остановке сервера: {ex.Message}");
            }
        }

        /// <summary>
        /// Принудительный Kill сервера без сохранения
        /// </summary>
        private void HardKillServer()
        {
            try
            {
                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    _serverProcess.Kill();
                    _serverProcess.Dispose();
                    _serverProcess = null;
                    IsServerRunning = false;
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Error] Ошибка при Kill: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        //                   ЛОГИКА ПОЛНОГО СТАРТА
        // -----------------------------------------------------------------

        private async Task WaitForServerFullyStarted()
        {
            var timeout = TimeSpan.FromMinutes(20);
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < timeout && !_stopRequested && IsServerRunning)
            {
                if (_isServerFullyStarted)
                {
                    return;
                }
                await Task.Delay(500);
            }

            if (!_isServerFullyStarted)
            {
                AddLogMessage("[Server] Сервер не успел полностью загрузиться. Останавливаем...");
                OnStopServer();
            }
        }

        // -----------------------------------------------------------
        //           ЛОГИКА СКАЧИВАНИЯ/РАСПАКОВКИ
        // -----------------------------------------------------------

        private async Task SetupServerAsync(string serverDirectory, string serverFile)
        {
            try
            {
                if (!Directory.Exists(serverDirectory))
                {
                    Directory.CreateDirectory(serverDirectory);
                    AddLogMessage($"[Setup] Создана директория: {serverDirectory}");
                }

                string endpoint = "links";

                var links = await _apiService.GetLinksAsync(endpoint);
                if (links == null || links.Count == 0)
                {
                    AddLogMessage("[Setup] links.json пуст или не содержит данных.");
                    return;
                }

                string archiveKey = $"{SelectedVersion}.zip";
                if (!links.TryGetValue(archiveKey, out string? downloadUrl) || downloadUrl == null)
                {
                    AddLogMessage($"[Setup] Не найдена ссылка для '{archiveKey}' в links.json.");
                    return;
                }

                string tempZip = Path.Combine(serverDirectory, archiveKey);

                AddLogMessage($"[Setup] Скачиваем {downloadUrl} -> {tempZip}");
                await DownloadFileAsync(downloadUrl, tempZip);

                AddLogMessage("[Setup] Распаковка архива...");
                await UnzipFileAsync(tempZip, serverDirectory);

                AddLogMessage("[Setup] Создание опциональных папок...");
                await CreateModsConfigDirs(serverDirectory);

                File.Delete(tempZip);
                AddLogMessage("[Setup] Архив удалён после распаковки.");

                // Проверяем итоговый jar
                string serverPath = Path.Combine(serverDirectory, serverFile);
                if (File.Exists(serverPath))
                {
                    AddLogMessage("[Setup] Файл сервера получен успешно. Запускаем...");
                    await StartServerProcessAsync(serverDirectory, serverFile);
                }
                else
                {
                    AddLogMessage("[Setup] Ошибка: не найден jar после распаковки.");
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Error] SetupServerAsync: {ex.Message}");
            }
        }

        private Task StartServerProcessAsync(string serverDirectory, string serverFile)
        {
            try
            {
                if (_stopRequested) return Task.CompletedTask;

                _serverStoppedTaskSource = new TaskCompletionSource<bool>();


                string javaPath = _javaExecutablePath;
                int memoryMB = (int)(ServerMemoryGB * 1024);
                string arguments = $"-Xmx{memoryMB}M -jar \"{serverFile}\" {(UseNoGui ? "nogui" : "")}";

                _serverProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = javaPath,
                        Arguments = arguments,
                        WorkingDirectory = serverDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                _serverProcess.OutputDataReceived += ServerOutputDataReceived;
                _serverProcess.ErrorDataReceived += ServerErrorDataReceived;
                _serverProcess.Exited += ServerExited;

                bool started = _serverProcess.Start();
                if (!started)
                {
                    AddLogMessage("[Server] Не удалось стартовать процесс!");
                    return Task.CompletedTask;
                }

                IsServerRunning = true;
                AddLogMessage("[Server] Серверный процесс запущен.");

                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();

                Dispatcher.UIThread.Post(() =>
                                {
                                    (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                                });
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Error] Ошибка при запуске сервера: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------------------------
        //                       ОБРАБОТЧИКИ ВЫВОДА (ЛОГ) И ЗАВЕРШЕНИЯ
        // ------------------------------------------------------------------------------------

        private void ServerOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            AddLogMessage(e.Data);

            if (e.Data.Contains("All dimensions are saved", StringComparison.OrdinalIgnoreCase))
            {
                AddLogMessage("[Server] Получено сообщение о завершении работы сервера.");
                _serverStoppedTaskSource?.TrySetResult(true);
            }
            if (e.Data.Contains("Done", StringComparison.OrdinalIgnoreCase))
            {
                IsServerFullyStarted = true;
            }
        }

        private void ServerErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            AddLogMessage($"[Ошибка сервера] {e.Data}");
        }

        private void ServerExited(object? sender, EventArgs e)
        {
            AddLogMessage("[Server] Процесс сервера завершился.");

            Dispatcher.UIThread.Post(() =>
            {
                IsServerRunning = false;
                IsServerFullyStarted = false;
                StatusMessage = "Сервер готов к запуску";

                (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (ToggleServerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            });

            StopTunnel();

            if (_serverProcess != null)
            {
                _serverProcess.OutputDataReceived -= ServerOutputDataReceived;
                _serverProcess.ErrorDataReceived -= ServerErrorDataReceived;
                _serverProcess.Exited -= ServerExited;
            }
        }

        // --------------------------------------------------------
        //                       ЛОГИКА ТУННЕЛЯ
        // --------------------------------------------------------

        private async Task TryStartTunnelWithRetries()
        {
            const int maxAttempts = 3;
            const int delaySeconds = 20;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (_stopRequested || !IsServerRunning) return;

                AddLogMessage($"[Tunnel] Попытка #{attempt} создать туннель...");
                bool ok = await StartTunnelInternalAsync();

                if (ok)
                {
                    AddLogMessage("[Tunnel] Успешно запущен!");
                    return;
                }
                else
                {
                    AddLogMessage($"[Tunnel] Не вышло. Ждём {delaySeconds} сек перед повтором...");
                    await Task.Delay(delaySeconds * 1000);
                }
            }

            AddLogMessage("[Tunnel] Не удалось создать туннель за 5 минут. Останавливаем сервер...");
            OnStopServer();
        }

        private async Task<bool> StartTunnelInternalAsync()
        {
            try
            {
                if (_tunnelClient != null && _tunnelClient.IsRunning)
                {
                    AddLogMessage("[Tunnel] Уже запущен.");
                    return true;
                }

                if (_userData == null)
                {
                    AddLogMessage("[Tunnel] Ошибка: отсутствуют данные пользователя");
                    return false;
                }

                _tunnelClient = new TunnelClient(AppConstants.ServerIp, AppConstants.ServerPort, AppConstants.LocalPort, _userData);
                _tunnelClient.TunnelStopped += OnTunnelStopped;
                _tunnelClient.LogAction = AddLogMessage;

                await _tunnelClient.StartAsync();
                if (_tunnelClient.IsRunning)
                {
                    IsTunnelRunning = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    });
                }
                return _tunnelClient.IsRunning;
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Tunnel] Ошибка при запуске: {ex.Message}");
                return false;
            }
        }

        private async void StopTunnel()
        {
            if (_tunnelClient != null && _tunnelClient.IsRunning)
            {
                try
                {
                    using (TcpClient tcpClient = new TcpClient())
                    {
                        await tcpClient.ConnectAsync(AppConstants.ServerIp, AppConstants.ServerPort);
                        NetworkStream networkStream = tcpClient.GetStream();
                        StreamWriter writer = new StreamWriter(networkStream, Encoding.UTF8) { AutoFlush = true };
                        StreamReader reader = new StreamReader(networkStream, Encoding.UTF8);

                        string request = $"STOP_TUNNEL {_userData?.UserGuid}\n";
                        AddLogMessage($"[Client] Отправка запроса остановки туннеля: {request.Trim()}");
                        await writer.WriteAsync(request);

                        string? response = await reader.ReadLineAsync();
                        if (response == null)
                        {
                            AddLogMessage("[Client] No response from server.");
                            return;
                        }

                        AddLogMessage($"[Client] Получен ответ: {response}");

                        if (response.StartsWith("TUNNEL_STOPPED_OK") || response.StartsWith("NO_ACTIVE_TUNNELS"))
                        {
                            AddLogMessage("[Tunnel] Останавливаем локальный туннель...");
                            _tunnelClient.Stop();
                            IsTunnelRunning = false;
                            Dispatcher.UIThread.Post(() =>
                            {
                                (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                            });
                        }
                        else
                        {
                            AddLogMessage($"[Client] Неожиданный ответ при остановке туннеля: {response}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddLogMessage($"[Client] Ошибка при остановке туннеля: {ex.Message}");
                    _tunnelClient.Stop();
                    IsTunnelRunning = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    });
                }
            }
        }

        private void OnTunnelStopped(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AddLogMessage("[Tunnel] Туннель остановлен (TunnelStopped).");
                if (_tunnelClient != null)
                {
                    _tunnelClient.TunnelStopped -= OnTunnelStopped;
                }

                if (IsServerRunning)
                {
                    AddLogMessage("[Server] Останавливаем сервер после остановки туннеля...");
                    SoftStopMinecraftServer();
                }

                Dispatcher.UIThread.Post(() =>
                {
                    (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                });
            });
        }

        // ------------------------------------------------------------------------------------
        //                                СКАЧИВАНИЕ / РАСПАКОВКА
        // ------------------------------------------------------------------------------------

        private async Task DownloadFileAsync(string url, string destination)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);

                AddLogMessage($"[Setup] Загружен файл: {destination}");
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Setup] Ошибка загрузки: {ex.Message}");
                throw;
            }
        }

        private async Task UnzipFileAsync(string zipPath, string extractPath)
        {
            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractPath));
                AddLogMessage($"[Setup] Архив распакован в: {extractPath}");
            }
            catch (Exception ex)
            {
                AddLogMessage($"[Setup] Ошибка распаковки: {ex.Message}");
                throw;
            }
        }

        private async Task CreateModsConfigDirs(string extractPath)
        {
            await Task.Run(() =>
            {
                string modsPath = Path.Combine(extractPath, "mods");
                string configPath = Path.Combine(extractPath, "config");

                if (!Directory.Exists(modsPath))
                {
                    Directory.CreateDirectory(modsPath);
                    AddLogMessage($"[Setup] Создана директория: {modsPath}");
                }
                else
                {
                    AddLogMessage($"[Setup] Директория уже существует: {modsPath}");
                }

                if (!Directory.Exists(configPath))
                {
                    Directory.CreateDirectory(configPath);
                    AddLogMessage($"[Setup] Создана директория: {configPath}");
                }
                else
                {
                    AddLogMessage($"[Setup] Директория уже существует: {configPath}");
                }

                AddLogMessage($"[Setup] Необходимые директории созданы в: {extractPath}");
            });
        }


        // ------------------------------------------------------------------------------------
        //                             ЛОГИКА CANEXECUTE
        // ------------------------------------------------------------------------------------

        private bool CanToggleSubdomain()
        {
            if (IsSubdomainLocked)
            {
                return !IsServerRunning;
            }
            return true;
        }

        private async Task OnToggleSubdomainAsync()
        {
            if (IsSubdomainLocked)
            {
                ResetSubdomain();
            }
            else
            {
                CheckSubdomainAvailability();
            }
        }

        partial void OnIsSubdomainLockedChanged(bool value)
        {
            SubdomainButtonText = value ? "Сбросить" : "Выбрать";
            SubdomainButtonTooltip = value ? "Сбросить выбранный поддомен" : "Выбрать поддомен";
            StatusMessage = value ? "Сервер готов к запуску" : "Введите поддомен для сервера";
            Dispatcher.UIThread.Post(() =>
            {
                (ToggleSubdomainCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (GenerateSubdomainCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
        }

        // ------------------------------------------------------------------------------------
        //                          ВСПОМОГАТЕЛЬНЫЙ МЕТОД ЛОГОВ
        // ------------------------------------------------------------------------------------

        private void AddLogMessage(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                string timeStamp = DateTime.Now.ToString("HH:mm:ss");
                string logMessage = $"[{timeStamp}] {message}";

                LogMessages.Insert(0, logMessage);
                LogText = string.Join("\n", LogMessages.Reverse());
            });
        }

        public async Task<bool> ShutdownAsync()
        {
            SaveUserData();

            AddLogMessage("[App] Начинаем корректное завершение работы...");

            _stopRequested = true;

            if (_tunnelClient != null && _tunnelClient.IsRunning)
            {
                AddLogMessage("[App] Останавливаем туннель...");
                StopTunnel();
                await Task.Delay(1000);
            }

            if (IsServerRunning && _serverProcess != null && !_serverProcess.HasExited)
            {
                AddLogMessage("[App] Отправляем команду stop для сервера...");

                var stopTimeout = TimeSpan.FromSeconds(300);
                var stopTask = new TaskCompletionSource<bool>();

                _serverProcess.StandardInput.WriteLine("stop");
                _serverProcess.StandardInput.Flush();

                var timeoutTask = Task.Delay(stopTimeout);
                var completedTask = await Task.WhenAny(_serverStoppedTaskSource?.Task ?? Task.CompletedTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    AddLogMessage("[App] Сервер не остановился мягко, выполняем принудительную остановку");
                    HardKillServer();
                }
                else
                {
                    AddLogMessage("[App] Сервер корректно остановлен");
                }
            }

            AddLogMessage("[App] Завершение работы выполнено");
            return true;
        }

        private bool CanToggleServer()
        {
            if (IsServerRunning)
            {
                return true;
            }
            else
            {
                if (!IsSubdomainLocked || string.IsNullOrEmpty(_userData?.Subdomain))
                {
                    return false;
                }
                return true;
            }
        }

        private async Task OnToggleServerAsync()
        {
            if (IsServerRunning)
            {
                await OnStopServer();
            }
            else
            {
                await OnStartServerAsync();
            }
        }

        partial void OnIsServerRunningChanged(bool value)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (value && !IsServerFullyStarted)
                {
                    StatusMessage = "Сервер запускается...";
                }
                (ToggleServerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            });
        }

        partial void OnIsServerFullyStartedChanged(bool value)
        {
            if (value)
            {
                StatusMessage = "Сервер запущен";
            }
            else if (IsServerRunning)
            {
                StatusMessage = "Сервер останавливается\nпожалуйста подождите...";
            }
            else
            {
                StatusMessage = "Сервер готов к запуску";
            }
        }

        private bool ValidateSubdomain(string subdomain, out bool isWeak)
        {
            isWeak = false;

            if (string.IsNullOrEmpty(subdomain))
                return false;

            bool isLongEnough = subdomain.Length >= 4;
            bool hasNumber = System.Text.RegularExpressions.Regex.IsMatch(subdomain, @"\d");

            if (!isLongEnough)
            {
                StatusMessage = "Поддомен должен быть не короче\n4 символов";
                isWeak = true;
                return false;
            }

            string allowedCharsPattern = @"^[а-яА-Яa-zA-Z0-9_-]+$";
            bool hasOnlyAllowedChars = System.Text.RegularExpressions.Regex.IsMatch(subdomain, allowedCharsPattern);

            if (!hasOnlyAllowedChars)
            {
                StatusMessage = "Поддомен может содержать только\nбуквы, цифры, - и _";
                return false;
            }

            string edgesPattern = @"^[а-яА-Яa-zA-Z0-9].*[а-яА-Яa-zA-Z0-9]$";
            bool hasValidEdges = System.Text.RegularExpressions.Regex.IsMatch(subdomain, edgesPattern);

            if (!hasValidEdges)
            {
                StatusMessage = "Поддомен должен начинаться и\nзаканчиваться буквой или цифрой";
                return false;
            }

            if (!hasNumber)
            {
                isWeak = true;
            }

            return true;
        }

        private async void CopyServerIp()
        {
            var serverIp = string.IsNullOrEmpty(DesiredSubdomain)
                ? $".{AppConstants.TunnelDomain}"
                : $"{DesiredSubdomain}.{AppConstants.TunnelDomain}";

            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var mainWindow = desktop.MainWindow;
                    if (mainWindow?.Clipboard != null)
                    {
                        await mainWindow.Clipboard.SetTextAsync(serverIp);
                        await ShowToast("IP сервера скопирован");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[App] Ошибка копирования: {ex.Message}");
            }
        }

        private async Task CopyLogsAsync()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = desktop.MainWindow;
                if (mainWindow?.Clipboard != null)
                {
                    await mainWindow.Clipboard.SetTextAsync(LogText);
                    await ShowToast("Логи скопированы");
                }
            }
        }

        private void GenerateSubdomain()
        {
            DesiredSubdomain = _subdomainGenerator.GenerateSubdomain();
        }

        private async Task ShowToast(string message, int durationMs = 2000)
        {
            if (_isToastInProgress)
                return;

            try
            {
                _isToastInProgress = true;
                ToastMessage = message;
                IsToastVisible = true;
                await Task.Delay(durationMs);
            }
            finally
            {
                IsToastVisible = false;
                _isToastInProgress = false;
            }
        }

        private void OpenSettings()
        {
            IsSettingsOpen = true;
            AddLogMessage("[Settings] Открытие настроек");
        }

        private void CloseSettings()
        {
            IsSettingsOpen = false;
            AddLogMessage("[Settings] Закрытие настроек");
        }
    }
}
