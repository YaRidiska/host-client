using Avalonia.Controls;
using AvaloniaApplication2.ViewModels;
using AvaloniaApplication2.Services;
using AvaloniaApplication2.Constants;
using System.Threading.Tasks;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using System;
using System.Threading;
using System.Net.Http;
using System.IO;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Input;

namespace AvaloniaApplication2.Views
{
    public partial class MainWindow : Window
    {
        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                _downloadProgress = value;
                if (ProgressBar != null)
                    ProgressBar.Value = value;
            }
        }

        private string _downloadStatus = "";
        public string DownloadStatus
        {
            get => _downloadStatus;
            set
            {
                _downloadStatus = value;
                if (StatusText != null)
                    StatusText.Text = value;
            }
        }

        private bool _isUpdateInProgress;
        public bool IsUpdateInProgress
        {
            get => _isUpdateInProgress;
            set
            {
                _isUpdateInProgress = value;
                if (UpdateProgressPanel != null)
                    UpdateProgressPanel.IsVisible = value;
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            Closing += MainWindow_Closing;

            _ = Task.Run(CheckForUpdatesAsync);

            PointerPressed += (s, e) =>
            {
                TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            };
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                var apiService = new ApiService(AppConstants.YandexCloudApiUrl);
                var updateInfo = await apiService.CheckForUpdatesAsync();

                if (updateInfo?.LatestVersion != null && updateInfo.DownloadUrl != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var messageBox = MessageBoxManager.GetMessageBoxStandard(
                            "Доступно обновление",
                            $"Доступна новая версия {updateInfo.LatestVersion}\n" +
                            $"Текущая версия: {AppConstants.AppVersion}\n\n" +
                            "Хотите скачать обновление?",
                            ButtonEnum.YesNo,
                            MsBox.Avalonia.Enums.Icon.Info
                        );

                        var result = await messageBox.ShowWindowDialogAsync(this);

                        if (result == ButtonResult.Yes)
                        {
                            await DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                        }
                    });
                }
            }
            catch
            {

            }
        }

        private async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            IsUpdateInProgress = true;
            try
            {
                var tempFile = Path.GetTempFileName();
                using var client = new HttpClient();

                var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = File.Create(tempFile);

                var buffer = new byte[8192];
                var bytesRead = 0;
                var totalBytesRead = 0L;

                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalBytesRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        DownloadProgress = (double)totalBytesRead / totalBytes;
                        DownloadStatus = $"Загружено: {FormatFileSize(totalBytesRead)} из {FormatFileSize(totalBytes)}";
                    }
                    else
                    {
                        DownloadStatus = $"Загружено: {FormatFileSize(totalBytesRead)}";
                    }
                }

                fileStream.Close();

                var updaterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updater.exe");
                if (File.Exists(updaterPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updaterPath,
                        Arguments = $"\"{tempFile}\" \"{AppDomain.CurrentDomain.BaseDirectory}\"",
                        UseShellExecute = true
                    });

                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                }
                else
                {
                    throw new FileNotFoundException($"Updater not found at path: {updaterPath}");
                }
            }
            catch (Exception ex)
            {
                DownloadStatus = "Ошибка загрузки";
                var messageBox = MessageBoxManager.GetMessageBoxStandard(
                    "Ошибка обновления",
                    $"Не удалось загрузить обновление: {ex.Message}",
                    ButtonEnum.Ok,
                    MsBox.Avalonia.Enums.Icon.Error);
                await messageBox.ShowAsync();
            }
            finally
            {
                IsUpdateInProgress = false;
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            e.Cancel = true;

            if (DataContext is MainWindowViewModel vm)
            {
                if (vm.IsServerRunning)
                {
                    vm.StatusMessage = "Остановка сервера...";
                }

                bool shutdownComplete = await vm.ShutdownAsync();

                if (shutdownComplete)
                {
                    Closing -= MainWindow_Closing;
                    Close();
                }
            }
            else
            {
                Closing -= MainWindow_Closing;
                Close();
            }
        }
    }
}