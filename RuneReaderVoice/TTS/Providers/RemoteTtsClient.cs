using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
        // PooledConnectionLifetime=90s and IdleTimeout=60s ensure no connection
        // is old enough for the server to close it, eliminating the idle
        // IOException(SocketException 995) that surfaces as an unobserved Task.
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

    public async Task<IReadOnlyList<VoiceInfo>> GetAvailableVoiceSourcesAsync(ProviderDescriptor descriptor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(descriptor.RemoteProviderId))
            return Array.Empty<VoiceInfo>();

        var route = descriptor.VoiceSourceKind switch
        {
            RemoteVoiceSourceKind.Voices => $"/api/v1/providers/{descriptor.RemoteProviderId}/voices",
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
                throw new InvalidOperationException($"Voice source lookup failed: {(int)response.StatusCode} {body}");

            if (descriptor.VoiceSourceKind == RemoteVoiceSourceKind.Voices)
            {
                var voices = JsonSerializer.Deserialize<List<RemoteVoiceDto>>(body, JsonOptions) ?? new List<RemoteVoiceDto>();
                return voices.Select(v => new VoiceInfo
                {
                    VoiceId = v.VoiceId ?? string.Empty,
                    Name = v.VoiceId ?? string.Empty,
                    Description = string.IsNullOrWhiteSpace(v.DisplayName) ? string.Empty : v.DisplayName!,
                    Language = v.Language ?? string.Empty,
                    Gender = ParseGender(v.Gender),
                }).ToList();
            }

            var samples = JsonSerializer.Deserialize<List<RemoteSampleDto>>(body, JsonOptions) ?? new List<RemoteSampleDto>();
            var providerId = descriptor.RemoteProviderId ?? string.Empty;
            IEnumerable<RemoteSampleDto> filtered = samples;

            if (providerId.Contains("f5", StringComparison.OrdinalIgnoreCase))
                filtered = filtered.Where(s => (s.DurationSeconds ?? 0f) > 0f && (s.DurationSeconds ?? 0f) <= 11.0f);
            else if (providerId.Contains("chatterbox", StringComparison.OrdinalIgnoreCase))
                filtered = filtered.Where(s => (s.DurationSeconds ?? 0f) <= 0f || (s.DurationSeconds ?? 0f) <= 41.0f);

            return filtered.Select(s => new VoiceInfo
            {
                VoiceId = s.SampleId ?? string.Empty,
                Name = s.SampleId ?? string.Empty,
                Description = !string.IsNullOrWhiteSpace(s.Description) ? s.Description! : (s.Filename ?? s.SampleId ?? string.Empty),
                Language = string.Empty,
                Gender = Protocol.Gender.Unknown,
            }).ToList();
        } // end try
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Voice source lookup failed: {ex.Message}", ex);
        }
    }

    public async Task<byte[]> SynthesizeAsync(RemoteSynthesizeRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(BuildUri("/api/v1/synthesize"), content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Remote synthesis failed: {(int)response.StatusCode} {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

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
            "male" => Protocol.Gender.Male,
            "female" => Protocol.Gender.Female,
            _ => Protocol.Gender.Unknown,
        };
    }
}

public sealed class RemoteVoiceDto
{
    [JsonPropertyName("voice_id")] public string? VoiceId { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("gender")] public string? Gender { get; set; }
}

public sealed class RemoteSampleDto
{
    [JsonPropertyName("sample_id")] public string? SampleId { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("duration_seconds")] public float? DurationSeconds { get; set; }
}

public sealed class RemoteSynthesizeRequest
{
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("voice")] public RemoteVoiceSpec Voice { get; set; } = new();
    [JsonPropertyName("lang_code")] public string LangCode { get; set; } = "en";
    [JsonPropertyName("speech_rate")] public float SpeechRate { get; set; } = 1.0f;
    [JsonPropertyName("cfg_weight")] public float? CfgWeight { get; set; }
    [JsonPropertyName("exaggeration")] public float? Exaggeration { get; set; }
}

public sealed class RemoteVoiceSpec
{
    [JsonPropertyName("type")] public string Type { get; set; } = "base";
    [JsonPropertyName("voice_id")] public string? VoiceId { get; set; }
    [JsonPropertyName("sample_id")] public string? SampleId { get; set; }
    [JsonPropertyName("blend")] public List<RemoteBlendSpec> Blend { get; set; } = new();
}

public sealed class RemoteBlendSpec
{
    [JsonPropertyName("voice_id")] public string VoiceId { get; set; } = string.Empty;
    [JsonPropertyName("weight")] public float Weight { get; set; }
}
