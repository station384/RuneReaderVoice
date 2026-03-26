using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RuneReaderVoice.TTS.Providers;

public sealed class RemoteTtsClient
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RemoteTtsClient(string baseUrl, string apiKey)
    {
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        _apiKey  = apiKey ?? string.Empty;

        // SocketsHttpHandler configured to recycle connections before Caddy's
        // idle keep-alive timeout (default 5 min) kills them underneath us.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime    = TimeSpan.FromSeconds(90),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout              = TimeSpan.FromSeconds(10),
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(300),
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);
    }


    private static bool LooksLikeForeignProviderSample(RemoteSampleDto sample, string providerId)
    {
        if (sample == null) return false;

        var isF5         = providerId.Contains("f5", StringComparison.OrdinalIgnoreCase);
        var isChatterbox = providerId.Contains("chatterbox", StringComparison.OrdinalIgnoreCase);
        if (!isF5 && !isChatterbox)
            return false;

        var foreignMarker = isF5 ? "-chatterbox" : "-f5";
        return ContainsMarker(sample.SampleId, foreignMarker)
            || ContainsMarker(sample.Filename, foreignMarker)
            || ContainsMarker(sample.Description, foreignMarker);
    }

    private static bool ContainsMarker(string? value, string marker)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ── v1 endpoints (unchanged) ──────────────────────────────────────────────

    public async Task<IReadOnlyList<RemoteProviderInfoDto>> GetProvidersAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BuildUri("/api/v1/providers"), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Provider discovery failed: {(int)response.StatusCode} {body}");

            return JsonSerializer.Deserialize<List<RemoteProviderInfoDto>>(body, JsonOptions)
                   ?? new List<RemoteProviderInfoDto>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Provider discovery failed: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<VoiceInfo>> GetAvailableVoiceSourcesAsync(
        ProviderDescriptor descriptor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(descriptor.RemoteProviderId))
            return Array.Empty<VoiceInfo>();

        var route = descriptor.VoiceSourceKind switch
        {
            RemoteVoiceSourceKind.Voices  => $"/api/v1/providers/{descriptor.RemoteProviderId}/voices",
            RemoteVoiceSourceKind.Samples => $"/api/v1/providers/{descriptor.RemoteProviderId}/samples",
            _ => null,
        };
        if (route == null)
            return Array.Empty<VoiceInfo>();

        try
        {
            using var response = await _httpClient.GetAsync(BuildUri(route), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Voice source lookup failed: {(int)response.StatusCode} {body}");

            if (descriptor.VoiceSourceKind == RemoteVoiceSourceKind.Voices)
            {
                var voices = JsonSerializer.Deserialize<List<RemoteVoiceDto>>(body, JsonOptions)
                             ?? new List<RemoteVoiceDto>();
                return voices.Select(v => new VoiceInfo
                {
                    VoiceId     = v.VoiceId ?? string.Empty,
                    Name        = v.VoiceId ?? string.Empty,
                    Description = string.IsNullOrWhiteSpace(v.DisplayName) ? string.Empty : v.DisplayName!,
                    Language    = v.Language ?? string.Empty,
                    Gender      = ParseGender(v.Gender),
                }).ToList();
            }

            var samples    = JsonSerializer.Deserialize<List<RemoteSampleDto>>(body, JsonOptions)
                             ?? new List<RemoteSampleDto>();
            var providerId = descriptor.RemoteProviderId ?? string.Empty;
            IEnumerable<RemoteSampleDto> filtered = samples;

            if (providerId.Contains("f5", StringComparison.OrdinalIgnoreCase))
                filtered = filtered.Where(s => (s.DurationSeconds ?? 0f) > 0f
                                            && (s.DurationSeconds ?? 0f) <= 11.0f);

            return filtered
                .GroupBy(s => s.SampleId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(s => new VoiceInfo
                {
                    VoiceId     = s.SampleId ?? string.Empty,
                    Name        = s.SampleId ?? string.Empty,
                    Description = !string.IsNullOrWhiteSpace(s.Description)
                                  ? s.Description!
                                  : (s.Filename ?? s.SampleId ?? string.Empty),
                    Language    = string.Empty,
                    Gender      = Protocol.Gender.Unknown,
                })
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Voice source lookup failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// v1 synchronous synthesis — POST, returns OGG bytes.
    /// Used as fallback when v2 is unavailable.
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(RemoteSynthesizeRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content  = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(BuildUri("/api/v1/synthesize"), content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Remote synthesis failed: {(int)response.StatusCode} {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ── v2 endpoints ──────────────────────────────────────────────────────────

    /// <summary>
    /// Submit a v2 async synthesis job.
    /// Returns immediately with a progress_key for SSE tracking and result fetch.
    /// If Cached=true the result is already available — skip SSE, call GetV2ResultAsync directly.
    /// </summary>
    public async Task<V2SubmitResponse> SynthesizeV2Async(
        RemoteSynthesizeV2Request request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content  = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            BuildUri("/api/v1/synthesize/v2"), content, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"v2 synthesis submit failed: {(int)response.StatusCode} {body}");

        return JsonSerializer.Deserialize<V2SubmitResponse>(body, JsonOptions)
               ?? throw new InvalidOperationException("v2 submit returned empty response");
    }

    /// <summary>
    /// Fetch OGG bytes for a completed v2 job.
    /// Returns null if the job is still in progress (202 Accepted).
    /// </summary>
    public async Task<byte[]?> GetV2ResultAsync(string progressKey, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
            BuildUri($"/api/v1/synthesize/v2/{progressKey}/result"), ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            return null;  // still in progress

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"v2 result fetch failed: {(int)response.StatusCode} {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Wait for a specific job to complete via its individual SSE stream.
    /// Returns when the job emits "complete" or "error", or ct is cancelled.
    /// More responsive than polling — use this instead of polling GetV2ResultAsync.
    /// </summary>
    public async Task WaitForJobAsync(string progressKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri($"/api/v1/synthesize/v2/{progressKey}/progress"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) { return; }
        catch { return; }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch { return; }

                if (line == null) return;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var json = line["data:".Length..].Trim();
                if (string.IsNullOrWhiteSpace(json)) continue;

                try
                {
                    using var doc    = JsonDocument.Parse(json);
                    var       status = doc.RootElement.GetProperty("status").GetString() ?? string.Empty;
                    if (string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "error",    StringComparison.OrdinalIgnoreCase))
                        return;
                }
                catch { continue; }
            }
        }
    }

    /// <summary>
    /// Subscribe to batch-level progress events via SSE.
    /// Yields V2BatchProgressEvent until the batch completes or ct is cancelled.
    /// Open this before or immediately after submitting the batch jobs.
    /// </summary>
    public async IAsyncEnumerable<V2BatchProgressEvent> GetBatchProgressAsync(
        string batchId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri($"/api/v1/synthesize/v2/batch/{batchId}/progress"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) { yield break; }
        catch { yield break; }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                yield break;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { yield break; }
                catch { yield break; }

                if (line == null) yield break;  // stream ended
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var json = line["data:".Length..].Trim();
                if (string.IsNullOrWhiteSpace(json)) continue;

                V2BatchProgressEvent? evt = null;
                try { evt = JsonSerializer.Deserialize<V2BatchProgressEvent>(json, JsonOptions); }
                catch { continue; }

                if (evt == null) continue;
                yield return evt;

                if (string.Equals(evt.Status, "complete", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evt.Status, "error",    StringComparison.OrdinalIgnoreCase))
                    yield break;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildUri(string path)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("Remote server URL is not configured.");
        return _baseUrl + path;
    }

    private static Protocol.Gender ParseGender(string? value)
    {
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "male"   => Protocol.Gender.Male,
            "female" => Protocol.Gender.Female,
            _        => Protocol.Gender.Unknown,
        };
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class RemoteVoiceDto
{
    [JsonPropertyName("voice_id")]     public string? VoiceId     { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("language")]     public string? Language    { get; set; }
    [JsonPropertyName("gender")]       public string? Gender      { get; set; }
}

public sealed class RemoteSampleDto
{
    [JsonPropertyName("sample_id")]        public string? SampleId       { get; set; }
    [JsonPropertyName("filename")]         public string? Filename        { get; set; }
    [JsonPropertyName("description")]      public string? Description     { get; set; }
    [JsonPropertyName("duration_seconds")] public float?  DurationSeconds { get; set; }
}

/// <summary>v1 synthesis request body.</summary>
public sealed class RemoteSynthesizeRequest
{
    [JsonPropertyName("provider_id")]  public string          ProviderId   { get; set; } = string.Empty;
    [JsonPropertyName("text")]         public string          Text         { get; set; } = string.Empty;
    [JsonPropertyName("voice")]        public RemoteVoiceSpec  Voice        { get; set; } = new();
    [JsonPropertyName("lang_code")]    public string          LangCode     { get; set; } = "en";
    [JsonPropertyName("speech_rate")]  public float           SpeechRate   { get; set; } = 1.0f;
    [JsonPropertyName("cfg_weight")]   public float?          CfgWeight    { get; set; }
    [JsonPropertyName("exaggeration")] public float?          Exaggeration { get; set; }
}

/// <summary>
/// v2 synthesis request body.
/// Adds batch tracking fields and F5-TTS synthesis controls.
/// </summary>
public sealed class RemoteSynthesizeV2Request
{
    // ── Core (same as v1) ─────────────────────────────────────────────────────
    [JsonPropertyName("provider_id")]  public string          ProviderId   { get; set; } = string.Empty;
    [JsonPropertyName("text")]         public string          Text         { get; set; } = string.Empty;
    [JsonPropertyName("voice")]        public RemoteVoiceSpec  Voice        { get; set; } = new();
    [JsonPropertyName("lang_code")]    public string          LangCode     { get; set; } = "en";
    [JsonPropertyName("speech_rate")]  public float           SpeechRate   { get; set; } = 1.0f;
    [JsonPropertyName("cfg_weight")]   public float?          CfgWeight    { get; set; }
    [JsonPropertyName("exaggeration")] public float?          Exaggeration { get; set; }

    // ── Batch tracking ────────────────────────────────────────────────────────
    /// <summary>Client-generated UUID grouping all segments in one dialog.</summary>
    [JsonPropertyName("batch_id")]    public string? BatchId    { get; set; }
    /// <summary>Total segments in this batch (required if batch_id is set).</summary>
    [JsonPropertyName("batch_total")] public int?    BatchTotal { get; set; }

    // ── F5-TTS synthesis controls ─────────────────────────────────────────────
    /// <summary>Reference adherence (default 2.0, range 0.5–5.0).</summary>
    [JsonPropertyName("cfg_strength")]        public float? CfgStrength       { get; set; }
    /// <summary>ODE solver steps (default 48 Vocos / 32 BigVGAN, max 64).</summary>
    [JsonPropertyName("nfe_step")]            public int?   NfeStep            { get; set; }
    /// <summary>Cross-fade duration between internal chunks in seconds (default 0.15).</summary>
    [JsonPropertyName("cross_fade_duration")] public float? CrossFadeDuration  { get; set; }
    /// <summary>ODE time step distribution: -1.0 = sway optimal, 0.0 = uniform.</summary>
    [JsonPropertyName("sway_sampling_coef")] public float? SwaysamplingCoef   { get; set; }

    // ── Cache discrimination ───────────────────────────────────────────────────
    /// <summary>
    /// Slot identity string (e.g. "NightElf/Female", "Narrator") included in the
    /// server cache key. Prevents two different voice slots that happen to use the
    /// same reference sample and same text from sharing a cache entry and returning
    /// the wrong audio (e.g. narrator text cached from an NPC slot being served back
    /// for a narrator segment, or male NPC audio returned for a female NPC request).
    /// </summary>
    [JsonPropertyName("voice_context")] public string? VoiceContext { get; set; }
}

public sealed class RemoteVoiceSpec
{
    [JsonPropertyName("type")]      public string               Type     { get; set; } = "base";
    [JsonPropertyName("voice_id")]  public string?              VoiceId  { get; set; }
    [JsonPropertyName("sample_id")] public string?              SampleId { get; set; }
    [JsonPropertyName("blend")]     public List<RemoteBlendSpec> Blend   { get; set; } = new();
}

public sealed class RemoteBlendSpec
{
    [JsonPropertyName("voice_id")] public string VoiceId { get; set; } = string.Empty;
    [JsonPropertyName("weight")]   public float  Weight  { get; set; }
}

/// <summary>Response from POST /api/v1/synthesize/v2</summary>
public sealed class V2SubmitResponse
{
    [JsonPropertyName("progress_key")] public string ProgressKey { get; set; } = string.Empty;
    [JsonPropertyName("cache_key")]    public string CacheKey    { get; set; } = string.Empty;
    /// <summary>
    /// True when the result is already in cache.
    /// Skip SSE and call GetV2ResultAsync immediately.
    /// </summary>
    [JsonPropertyName("cached")]       public bool   Cached      { get; set; }
}

/// <summary>SSE event from GET /api/v1/synthesize/v2/batch/{id}/progress</summary>
public sealed class V2BatchProgressEvent
{
    [JsonPropertyName("status")]    public string Status    { get; set; } = string.Empty;
    [JsonPropertyName("completed")] public int    Completed { get; set; }
    [JsonPropertyName("failed")]    public int    Failed    { get; set; }
    [JsonPropertyName("total")]     public int    Total     { get; set; }
}
