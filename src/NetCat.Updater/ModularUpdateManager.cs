using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NetCat.Core.Interfaces;
using NetCat.Core.Models;

namespace NetCat.Updater
{
    public class ChecksumValidator
    {
        public static string ComputeSha256(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static bool ValidateSha256(string filePath, string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash)) return true;
            string actualHash = ComputeSha256(filePath);
            return string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public class GitHubApiClient
    {
        private readonly HttpClient _httpClient;

        public GitHubApiClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "NetCat-Updater");
            }
        }

        public async Task<(string TagName, string AssetUrl, string AssetName)> GetLatestReleaseAssetAsync(string repo, string assetPattern)
        {
            string url = $"https://api.github.com/repos/{repo}/releases/latest";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            var assets = root.GetProperty("assets");

            var regex = new Regex(assetPattern, RegexOptions.IgnoreCase);

            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (regex.IsMatch(name))
                {
                    string downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    return (tagName, downloadUrl, name);
                }
            }

            throw new FileNotFoundException($"No asset matching pattern '{assetPattern}' in {repo} latest release ({tagName})");
        }

        public async Task DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;
                if (totalBytes.HasValue && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes.Value);
                }
            }
        }
    }

    public class ModularUpdateManager : IUpdateManager
    {
        private readonly string _manifestPath;
        private readonly GitHubApiClient _gitHubClient;

        public ModularUpdateManager(string? manifestPath = null, GitHubApiClient? client = null)
        {
            _manifestPath = manifestPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "modules_manifest.json");
            _gitHubClient = client ?? new GitHubApiClient();
        }

        public async Task<ManifestModel> CheckForUpdatesAsync()
        {
            var manifest = LoadManifest();
            manifest.LastCheck = DateTime.UtcNow;
            SaveManifest(manifest);
            return await Task.FromResult(manifest);
        }

        public async Task<bool> UpdateComponentAsync(string componentKey, IProgress<double>? progress = null)
        {
            var manifest = LoadManifest();
            if (!manifest.Modules.TryGetValue(componentKey, out var module))
            {
                throw new ArgumentException($"Component '{componentKey}' not found in manifest.");
            }

            if (module.Pinned)
            {
                System.Diagnostics.Debug.WriteLine($"Component '{componentKey}' is pinned. Skipping update.");
                return false;
            }

            var (tagName, downloadUrl, assetName) = await _gitHubClient.GetLatestReleaseAssetAsync(module.Repo, module.AssetPattern);

            string cleanRemoteVersion = tagName.TrimStart('v');
            if (string.Equals(cleanRemoteVersion, module.Version, StringComparison.OrdinalIgnoreCase))
            {
                return false; // Already up to date
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "NetCat_Updates", componentKey);
            Directory.CreateDirectory(tempDir);
            string downloadPath = Path.Combine(tempDir, assetName);

            try
            {
                await _gitHubClient.DownloadFileAsync(downloadUrl, downloadPath, progress);

                // If expected sha256 is present, validate
                if (!string.IsNullOrEmpty(module.ExpectedSha256))
                {
                    if (!ChecksumValidator.ValidateSha256(downloadPath, module.ExpectedSha256))
                    {
                        File.Delete(downloadPath);
                        throw new InvalidOperationException($"Checksum verification failed for {assetName}");
                    }
                }

                string targetBinary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, module.BinaryPath);
                string targetDir = Path.GetDirectoryName(targetBinary) ?? AppDomain.CurrentDomain.BaseDirectory;
                Directory.CreateDirectory(targetDir);

                // Backup old binary
                if (File.Exists(targetBinary))
                {
                    string backupPath = targetBinary + ".bak";
                    File.Copy(targetBinary, backupPath, true);
                }

                // Unpack or copy
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string extractDir = Path.Combine(tempDir, "extracted");
                    if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                    ZipFile.ExtractToDirectory(downloadPath, extractDir);

                    string exeName = Path.GetFileName(targetBinary);
                    string[] found = Directory.GetFiles(extractDir, exeName, SearchOption.AllDirectories);
                    if (found.Length > 0)
                    {
                        File.Copy(found[0], targetBinary, true);
                    }
                    else
                    {
                        // Copy all files if it's a full bundle (like Zapret)
                        foreach (var f in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(extractDir, f);
                            string dest = Path.Combine(targetDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Copy(f, dest, true);
                        }
                    }
                }
                else if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(downloadPath, targetBinary, true);
                }

                module.Version = cleanRemoteVersion;
                SaveManifest(manifest);
                return true;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        public async Task RollbackComponentAsync(string componentKey)
        {
            var manifest = LoadManifest();
            if (manifest.Modules.TryGetValue(componentKey, out var module))
            {
                string targetBinary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, module.BinaryPath);
                string backupPath = targetBinary + ".bak";
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, targetBinary, true);
                    File.Delete(backupPath);
                }
            }
            await Task.CompletedTask;
        }

        private ManifestModel LoadManifest()
        {
            if (File.Exists(_manifestPath))
            {
                string json = File.ReadAllText(_manifestPath);
                return JsonSerializer.Deserialize<ManifestModel>(json) ?? new ManifestModel();
            }
            return new ManifestModel();
        }

        private void SaveManifest(ManifestModel model)
        {
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_manifestPath, json);
        }
    }
}
