using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AvaloniaApplication2.Constants;
using System.Threading;

namespace AvaloniaApplication2.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public ApiService(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<Dictionary<string, string>> GetLinksAsync(string apiPath = "")
        {
            var requestUrl = $"{_baseUrl}/{apiPath}";
            try
            {
                var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();

                var links = JsonSerializer.Deserialize<Dictionary<string, string>>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return links;
            }
            catch (HttpRequestException httpEx)
            {
                throw new Exception($"Ошибка при обращении к API: {httpEx.Message}", httpEx);
            }
            catch (JsonException jsonEx)
            {
                throw new Exception($"Ошибка при обработке ответа от API: {jsonEx.Message}", jsonEx);
            }
            catch (Exception ex)
            {
                throw new Exception($"Произошла непредвиденная ошибка: {ex.Message}", ex);
            }
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                var requestUrl = $"{_baseUrl}/check-update?version={AppConstants.AppVersion}";
                var response = await _httpClient.GetAsync(requestUrl, cts.Token);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cts.Token);
                var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(content);

                if (updateInfo?.HasUpdate == true &&
                    !string.IsNullOrEmpty(updateInfo.LatestVersion) &&
                    !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                {
                    return updateInfo;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public class UpdateInfo
        {
            public bool HasUpdate { get; set; }
            public string? LatestVersion { get; set; }
            public string? DownloadUrl { get; set; }
        }
    }
}
