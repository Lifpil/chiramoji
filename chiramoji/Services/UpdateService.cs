using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Chiramoji.Services
{
    public sealed class UpdateCheckResult
    {
        public bool IsReleaseFetchSucceeded { get; init; }
        public string StatusMessage { get; init; } = "更新情報を取得できませんでした。";
        public string FirmwareVersionText { get; init; } = "N/A";
        public string LatestTag { get; init; } = "";
        public string? FirmwareAssetName { get; init; }
        public string? SoftwareAssetName { get; init; }
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
                    IsReleaseFetchSucceeded = false,
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
                softwareUrl = !string.IsNullOrWhiteSpace(softwareAsset?.BrowserDownloadUrl)
                    ? softwareAsset.BrowserDownloadUrl
                    : release.HtmlUrl;
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
                status += " FW更新ファイルが利用できます。";
            }
            else
            {
                status += " FW更新ファイルは未検出です。";
                logs.Add("Firmware update asset not found in latest release.");
            }

            return new UpdateCheckResult
            {
                IsReleaseFetchSucceeded = true,
                StatusMessage = status,
                FirmwareVersionText = firmwareAsset != null
                    ? (ExtractVersionFromText(firmwareAsset.Name) ?? release.TagName)
                    : "N/A",
                LatestTag = release.TagName,
                FirmwareAssetName = firmwareAsset?.Name,
                SoftwareAssetName = softwareAsset?.Name,
                SoftwareUrlToOpen = softwareUrl,
                FirmwareUrlToOpen = firmwareUrl,
                LogMessages = logs
            };
        }


        public async Task<string?> DownloadSoftwareInstallerAsync(string softwareAssetUrl, string? softwareAssetName, string? latestTag = null)
        {
            if (string.IsNullOrWhiteSpace(softwareAssetUrl))
            {
                return null;
            }

            var assetName = softwareAssetName;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                try
                {
                    assetName = Path.GetFileName(new Uri(softwareAssetUrl).LocalPath);
                }
                catch
                {
                    assetName = null;
                }
            }

            var extension = Path.GetExtension(assetName ?? string.Empty);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, softwareAssetUrl);
            request.Headers.UserAgent.ParseAdd("chiramoji-updater/1.0");
            request.Headers.Accept.ParseAdd("*/*");
            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var tempDir = Path.Combine(Path.GetTempPath(), "chiramoji-updater");
            Directory.CreateDirectory(tempDir);

            var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(assetName)
                ? $"chiramoji-{NormalizeVersion(latestTag ?? "latest")}{extension}"
                : assetName);
            var filePath = Path.Combine(tempDir, fileName);
            await File.WriteAllBytesAsync(filePath, bytes);
            return filePath;
        }

        public async Task<string?> DownloadFirmwareMainPyAsync(string firmwareAssetUrl, string? firmwareAssetName = null)
        {
            if (string.IsNullOrWhiteSpace(firmwareAssetUrl))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, firmwareAssetUrl);
            request.Headers.UserAgent.ParseAdd("chiramoji-updater/1.0");
            request.Headers.Accept.ParseAdd("*/*");
            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var lowerName = (firmwareAssetName ?? firmwareAssetUrl).ToLowerInvariant();

            if (lowerName.EndsWith(".py"))
            {
                return DecodeFirmwareText(bytes);
            }

            if (lowerName.EndsWith(".zip"))
            {
                return TryExtractMainPyFromZip(bytes);
            }

            return null;
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
            static int ScoreAssetName(string name)
            {
                var n = name.ToLowerInvariant();
                var containsFwKeyword = n.Contains("fw") || n.Contains("firmware");

                if (n.EndsWith("main.py")) return 0;
                if (containsFwKeyword && n.EndsWith(".py")) return 1;
                if (containsFwKeyword && n.EndsWith(".zip")) return 2;
                if (containsFwKeyword && n.EndsWith(".uf2")) return 3;
                if (containsFwKeyword && n.EndsWith(".bin")) return 4;
                if (containsFwKeyword && n.EndsWith(".hex")) return 5;
                if (n.EndsWith(".py")) return 6;
                if (n.EndsWith(".zip")) return 7;
                if (n.EndsWith(".uf2")) return 8;
                if (n.EndsWith(".bin")) return 9;
                if (n.EndsWith(".hex")) return 10;
                return int.MaxValue;
            }

            return release.Assets
                .Select(a => new { Asset = a, Score = ScoreAssetName(a.Name) })
                .Where(x => x.Score != int.MaxValue)
                .OrderBy(x => x.Score)
                .Select(x => x.Asset)
                .FirstOrDefault();
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

        private static string? ExtractVersionFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var source = text.Trim();
            var buffer = new StringBuilder();
            bool hasDot = false;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (char.IsDigit(c))
                {
                    buffer.Append(c);
                    continue;
                }

                if (c == '.')
                {
                    hasDot = true;
                    buffer.Append(c);
                    continue;
                }

                if ((c == 'v' || c == 'V') && buffer.Length == 0)
                {
                    continue;
                }

                if (buffer.Length > 0)
                {
                    break;
                }
            }

            if (!hasDot)
            {
                return null;
            }

            var candidate = buffer.ToString().Trim('.');
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
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

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var buffer = fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            return new string(buffer);
        }

        private static string DecodeFirmwareText(byte[] bytes)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                var sjis = Encoding.GetEncoding("shift_jis");
                return sjis.GetString(bytes);
            }
        }

        private static string? TryExtractMainPyFromZip(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            var entry = archive.Entries
                .FirstOrDefault(e =>
                    e.FullName.EndsWith("main.py", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(e.Name));

            if (entry == null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var outMs = new MemoryStream();
            stream.CopyTo(outMs);
            return DecodeFirmwareText(outMs.ToArray());
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = "";

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; } = "";

            public List<GitHubAsset> Assets { get; set; } = new();
        }

        private sealed class GitHubAsset
        {
            public string Name { get; set; } = "";

            [JsonPropertyName("browser_download_url")]
            public string BrowserDownloadUrl { get; set; } = "";
        }
    }
}
