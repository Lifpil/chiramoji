using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BlindTouchOled.Services
{
    public sealed class UpdateCheckResult
    {
        public string StatusMessage { get; init; } = "更新情報を取得できませんでした。";
        public string FirmwareVersionText { get; init; } = "N/A";
        public string? SoftwareUrlToOpen { get; init; }
        public string? FirmwareUrlToOpen { get; init; }
        public List<string> LogMessages { get; init; } = new();
    }

    public sealed class UpdateService
    {
        private static readonly HttpClient HttpClient = new();

        public async Task<UpdateCheckResult> CheckAsync(string currentAppVersionText, string owner, string repo)
        {
            var release = await GetLatestReleaseAsync(owner, repo);
            if (release == null)
            {
                return new UpdateCheckResult
                {
                    StatusMessage = "更新情報を取得できませんでした。"
                };
            }

            var softwareAsset = FindSoftwareAsset(release);
            var firmwareAsset = FindFirmwareAsset(release);
            var currentVersion = NormalizeVersion(currentAppVersionText);
            var latestVersion = NormalizeVersion(release.TagName);
            var appUpdateAvailable = IsNewerVersion(latestVersion, currentVersion);

            var logs = new List<string>();
            string status;
            string? softwareUrl = null;
            string? firmwareUrl = null;

            if (appUpdateAvailable)
            {
                softwareUrl = softwareAsset?.BrowserDownloadUrl ?? release.HtmlUrl;
                status = $"新しいアプリ版 {release.TagName} が利用できます。";
                logs.Add($"Software update available: {release.TagName}");
            }
            else
            {
                status = "アプリは最新です。";
                logs.Add("Software is up to date.");
            }

            if (firmwareAsset != null)
            {
                logs.Add($"Firmware update asset found: {firmwareAsset.Name}");
                firmwareUrl = firmwareAsset.BrowserDownloadUrl;
                status += appUpdateAvailable
                    ? " FW更新ファイルも公開されています。"
                    : " FW更新ファイルを開きました。";
            }
            else
            {
                status += " FW更新ファイルは未検出です。";
                logs.Add("Firmware update asset not found in latest release.");
            }

            return new UpdateCheckResult
            {
                StatusMessage = status,
                FirmwareVersionText = firmwareAsset != null ? release.TagName : "N/A",
                SoftwareUrlToOpen = softwareUrl,
                FirmwareUrlToOpen = appUpdateAvailable ? null : firmwareUrl,
                LogMessages = logs
            };
        }

        private static async Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo)
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("chiramoji-updater/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        private static GitHubAsset? FindSoftwareAsset(GitHubRelease release)
        {
            return release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }

        private static GitHubAsset? FindFirmwareAsset(GitHubRelease release)
        {
            return release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".hex", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeVersion(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return "0.0.0";
            }

            var v = versionText.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                v = v[1..];
            }

            var dash = v.IndexOf('-');
            if (dash >= 0)
            {
                v = v[..dash];
            }

            return v;
        }

        private static bool IsNewerVersion(string latest, string current)
        {
            if (Version.TryParse(latest, out var latestVersion) &&
                Version.TryParse(current, out var currentVersion))
            {
                return latestVersion > currentVersion;
            }

            return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class GitHubRelease
        {
            public string TagName { get; set; } = "";
            public string HtmlUrl { get; set; } = "";
            public List<GitHubAsset> Assets { get; set; } = new();
        }

        private sealed class GitHubAsset
        {
            public string Name { get; set; } = "";
            public string BrowserDownloadUrl { get; set; } = "";
        }
    }
}


