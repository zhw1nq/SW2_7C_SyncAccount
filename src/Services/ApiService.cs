using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncAccount.Models;

namespace SyncAccount.Services;

public sealed class ApiService(Func<PluginConfig> configProvider, ILogger logger)
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<PluginConfig> _configProvider = configProvider;
    private readonly ILogger _logger = logger;

    private readonly SemaphoreSlim _healthLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]> _avatarCache = new();

    private DateTime _lastHealthCheckTime = DateTime.MinValue;
    private bool _lastHealthCheckOk = false;

    public string LastHealthCheckReason { get; private set; } = "Health check has not run yet.";

    public void ResetCache()
    {
        _avatarCache.Clear();
        _lastHealthCheckOk = false;
        _lastHealthCheckTime = DateTime.MinValue;
        LastHealthCheckReason = "Health check cache was reset.";
    }

    public async Task<bool> CheckHealthAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var cfg = _configProvider();
        if (!cfg.EnableHealthCheck)
        {
            LastHealthCheckReason = "Health check is disabled in configuration.";
            return true;
        }

        var elapsed = (DateTime.UtcNow - _lastHealthCheckTime).TotalSeconds;
        if (!force && ((_lastHealthCheckOk && elapsed < 15) || (!_lastHealthCheckOk && elapsed < 5)))
        {
            return _lastHealthCheckOk;
        }

        await _healthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!cfg.EnableHealthCheck)
            {
                return true;
            }

            elapsed = (DateTime.UtcNow - _lastHealthCheckTime).TotalSeconds;
            if (!force && ((_lastHealthCheckOk && elapsed < 15) || (!_lastHealthCheckOk && elapsed < 5)))
            {
                return _lastHealthCheckOk;
            }

            string healthUrl = string.IsNullOrWhiteSpace(cfg.HealthUrl)
                ? $"{cfg.ApiBaseUrl.TrimEnd('/')}/health"
                : cfg.HealthUrl;

            bool apiKeyConfigured = !string.IsNullOrWhiteSpace(cfg.ApiKey);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(2, cfg.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            if (apiKeyConfigured)
            {
                request.Headers.Add("X-API-KEY", cfg.ApiKey);
            }
            if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
            {
                request.Headers.UserAgent.ParseAdd(cfg.UserAgent);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string bodySnippet = string.Empty;
                try
                {
                    bodySnippet = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                    if (bodySnippet.Length > 200) bodySnippet = bodySnippet[..200] + "...";
                }
                catch { }

                _logger.LogWarning("[SyncAccount] [HealthCheck] API health check failed: HTTP {StatusCode} {ReasonPhrase}. Details: {Details}", (int)response.StatusCode, response.ReasonPhrase, bodySnippet);
                _lastHealthCheckOk = false;
                _lastHealthCheckTime = DateTime.UtcNow;
                LastHealthCheckReason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {bodySnippet}";
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            var health = await JsonSerializer.DeserializeAsync<HealthResponse>(stream, _jsonOptions, linkedCts.Token).ConfigureAwait(false);
            bool isOk = string.Equals(health?.Status, "ok", StringComparison.OrdinalIgnoreCase);

            _lastHealthCheckOk = isOk;
            _lastHealthCheckTime = DateTime.UtcNow;
            LastHealthCheckReason = isOk
                ? $"API status is ok; db='{health?.Db}', redis='{health?.Redis}'."
                : $"API status was '{health?.Status ?? "missing"}'; db='{health?.Db}', redis='{health?.Redis}'.";


            if (!isOk)
            {
                _logger.LogWarning("[SyncAccount] [HealthCheck] API unhealthy (status: '{Status}', db: '{Db}', redis: '{Redis}')", health?.Status, health?.Db, health?.Redis);
            }

            return isOk;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[SyncAccount] [HealthCheck] API health check timed out.");
            _lastHealthCheckOk = false;
            _lastHealthCheckTime = DateTime.UtcNow;
            LastHealthCheckReason = "Health check timed out.";
            return false;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[SyncAccount] [HealthCheck] Invalid health response JSON: {Message}", ex.Message);
            _lastHealthCheckOk = false;
            _lastHealthCheckTime = DateTime.UtcNow;
            LastHealthCheckReason = "Health response JSON was invalid.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncAccount] [HealthCheck] Error checking health");
            _lastHealthCheckOk = false;
            _lastHealthCheckTime = DateTime.UtcNow;
            LastHealthCheckReason = $"Health check error: {ex.GetType().Name}.";
            return false;
        }
        finally
        {
            _healthLock.Release();
        }
    }

    public async Task<SyncResponse?> GetUserInfoAsync(ulong steamId64, CancellationToken cancellationToken = default)
    {
        var cfg = _configProvider();
        string syncEndpoint = $"{cfg.ApiBaseUrl.TrimEnd('/')}/api/sync/{steamId64}";

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(3, cfg.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, syncEndpoint);
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                request.Headers.Add("X-API-KEY", cfg.ApiKey);
            }
            if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
            {
                request.Headers.UserAgent.ParseAdd(cfg.UserAgent);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string bodySnippet = string.Empty;
                try
                {
                    bodySnippet = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                    if (bodySnippet.Length > 200) bodySnippet = bodySnippet[..200] + "...";
                }
                catch { }

                _logger.LogError("[SyncAccount] [Sync] API call failed for {SteamId}: HTTP {StatusCode} {ReasonPhrase}. Details: {Details}", steamId64, (int)response.StatusCode, response.ReasonPhrase, bodySnippet);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            var syncData = await JsonSerializer.DeserializeAsync<SyncResponse>(stream, _jsonOptions, linkedCts.Token).ConfigureAwait(false);
            if (syncData == null)
            {
                _logger.LogError("[SyncAccount] [Sync] Failed to deserialize sync response for {SteamId}.", steamId64);
                return null;
            }

            return syncData;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[SyncAccount] [Sync] Invalid JSON response for {SteamId}: {Message}", steamId64, ex.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[SyncAccount] [Sync] Sync request timed out for SteamID {SteamId}.", steamId64);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncAccount] [Sync] Unexpected error fetching user info for {SteamId}", steamId64);
            return null;
        }
    }

    /// <summary>
    /// Downloads avatar with cache — used only for MockAvatarUrl.
    /// </summary>
    public async Task<byte[]?> DownloadAvatarAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (_avatarCache.TryGetValue(url, out var cachedBytes) && cachedBytes.Length > 0)
        {
            return cachedBytes;
        }

        var bytes = await DownloadAvatarCoreAsync(url, cancellationToken).ConfigureAwait(false);
        if (bytes is { Length: > 0 })
        {
            _avatarCache[url] = bytes;
        }
        return bytes;
    }

    /// <summary>
    /// Downloads avatar without cache — used for per-player avatars to avoid cross-contamination.
    /// </summary>
    public async Task<byte[]?> DownloadAvatarFreshAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return await DownloadAvatarCoreAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> DownloadAvatarCoreAsync(string url, CancellationToken cancellationToken)
    {
        var cfg = _configProvider();
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(3, cfg.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                request.Headers.Add("X-API-KEY", cfg.ApiKey);
            }
            if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
            {
                request.Headers.UserAgent.ParseAdd(cfg.UserAgent);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[SyncAccount] [Avatar] Image download failed from {Url}: HTTP {StatusCode} {ReasonPhrase}", url, (int)response.StatusCode, response.ReasonPhrase);
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                _logger.LogWarning("[SyncAccount] [Avatar] Image response from {Url} was empty.", url);
                return null;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[SyncAccount] [Avatar] Image download timed out from {Url}.", url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncAccount] [Avatar] Image download error from {Url}", url);
            return null;
        }
    }

    public async Task<byte[]?> GetMockAvatarAsync(CancellationToken cancellationToken = default)
    {
        var cfg = _configProvider();
        if (string.IsNullOrWhiteSpace(cfg.MockAvatarUrl))
            return null;

        return await DownloadAvatarAsync(cfg.MockAvatarUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountSyncResult> FetchAsync(ulong steamId, CancellationToken cancellationToken)
    {
        var config = _configProvider();
        if (config.EnableHealthCheck)
        {
            if (!await CheckHealthAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("API health check failed; account sync was not requested.");
        }

        var response = await GetUserInfoAsync(steamId, cancellationToken).ConfigureAwait(false);
        if (response == null)
            throw new InvalidOperationException("The sync API returned no usable response.");

        if (!response.Success)
        {
            return AccountSyncResult.Guest(null, null);
        }

        if (string.IsNullOrWhiteSpace(response.Data?.Name))
            throw new InvalidOperationException("The API returned success without a player name.");

        byte[]? avatar = null;
        if (!string.IsNullOrWhiteSpace(response.Data.Image))
        {
            avatar = await DownloadAvatarFreshAsync(response.Data.Image, cancellationToken).ConfigureAwait(false);
            if (avatar == null)
                _logger.LogWarning("[SyncAccount] Registered avatar download failed for SteamID {SteamId}; name will still be applied.", steamId);
        }

        return AccountSyncResult.Registered(
            response.Data.Name,
            avatar,
            avatar == null ? "Registered name applied; no avatar was available." : "Registered name and avatar fetched.");
    }
}
