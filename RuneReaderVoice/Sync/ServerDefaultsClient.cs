// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;

namespace RuneReaderVoice.Sync;

// Sync/ServerDefaultsClient.cs
// HTTP client for server-side defaults and NPC override community endpoints.
//
// Endpoints used:
//   GET  /api/v1/defaults/{type}          — pull seed data
//   PUT  /api/v1/defaults/{type}          — push seed data (admin)
//   GET  /api/v1/npc-overrides/since?t=   — poll for new NPC override records
//   POST /api/v1/npc-overrides            — contribute a record

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed class ServerNpcOverrideRecord
{
    [JsonPropertyName("npc_id")]               public int     NpcId               { get; set; }
    [JsonPropertyName("catalog_id")]           public string? CatalogId           { get; set; }
    [JsonPropertyName("race_id")]              public int     RaceId              { get; set; }
    [JsonPropertyName("notes")]                public string? Notes               { get; set; }
    [JsonPropertyName("bespoke_sample_id")]    public string? BespokeSampleId     { get; set; }
    [JsonPropertyName("bespoke_exaggeration")] public float?  BespokeExaggeration { get; set; }
    [JsonPropertyName("bespoke_cfg_weight")]   public float?  BespokeCfgWeight    { get; set; }
    [JsonPropertyName("gender_override")]      public string? GenderOverride      { get; set; }
    [JsonPropertyName("source")]               public string  Source              { get; set; } = "crowdsourced";
    [JsonPropertyName("confidence")]           public int     Confidence          { get; set; }
    [JsonPropertyName("updated_at")]           public double  UpdatedAt           { get; set; }
}

public sealed class ServerNpcOverrideSinceResponse
{
    [JsonPropertyName("records")] public List<ServerNpcOverrideRecord> Records { get; set; } = new();
    [JsonPropertyName("count")]   public int Count { get; set; }
}


public sealed class ServerNpcOverrideBatchRequest
{
    [JsonPropertyName("records")] public List<ServerNpcOverrideBatchRecord> Records { get; set; } = new();
}

public sealed class ServerNpcOverrideBatchRecord
{
    [JsonPropertyName("npc_id")] public int NpcId { get; set; }
    [JsonPropertyName("catalog_id")] public string? CatalogId { get; set; }
    [JsonPropertyName("race_id")] public int RaceId { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = string.Empty;
    [JsonPropertyName("bespoke_sample_id")] public string? BespokeSampleId { get; set; }
    [JsonPropertyName("bespoke_exaggeration")] public float? BespokeExaggeration { get; set; }
    [JsonPropertyName("bespoke_cfg_weight")] public float? BespokeCfgWeight { get; set; }
    [JsonPropertyName("gender_override")] public string GenderOverride { get; set; } = "auto";
}

public sealed class ServerNpcOverrideBatchResponse
{
    [JsonPropertyName("upserted")] public int Upserted { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class ServerDefaultsResponse
{
    [JsonPropertyName("data_type")] public string       DataType { get; set; } = string.Empty;
    [JsonPropertyName("payload")]   public JsonElement? Payload  { get; set; }
    [JsonPropertyName("exists")]    public bool         Exists   { get; set; }
}

public sealed class ServerProviderSlotProfileRecord
{
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = string.Empty;
    [JsonPropertyName("profile_kind")] public string ProfileKind { get; set; } = string.Empty;
    [JsonPropertyName("profile_id")] public string ProfileId { get; set; } = string.Empty;
    [JsonPropertyName("profile_json")] public JsonElement ProfileJson { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("created_at")] public double? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public double? UpdatedAt { get; set; }
}

public sealed class ServerProviderSlotProfilesResponse
{
    [JsonPropertyName("records")] public List<ServerProviderSlotProfileRecord> Records { get; set; } = new();
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class ServerProviderSlotProfileBatchRequest
{
    [JsonPropertyName("records")] public List<ServerProviderSlotProfileBatchRecord> Records { get; set; } = new();
}

public sealed class ServerProviderSlotProfileBatchRecord
{
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = string.Empty;
    [JsonPropertyName("profile_kind")] public string ProfileKind { get; set; } = string.Empty;
    [JsonPropertyName("profile_id")] public string ProfileId { get; set; } = string.Empty;
    [JsonPropertyName("profile_json")] public object ProfileJson { get; set; } = new();
}

public sealed class ServerProviderSlotProfilesBatchResponse
{
    [JsonPropertyName("upserted")] public int Upserted { get; set; }
}

// ── Client ────────────────────────────────────────────────────────────────────

public sealed class ServerDefaultsClient
{
    private static string ToServerGenderOverride(NpcGenderOverride value)
        => value switch
        {
            NpcGenderOverride.Male => "male",
            NpcGenderOverride.Female => "female",
            _ => "auto",
        };

    private readonly HttpClient _http;
    private readonly string     _contributeKey;
    private readonly string     _adminKey;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ServerDefaultsClient(string serverUrl, string contributeKey = "", string adminKey = "")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout     = TimeSpan.FromSeconds(15),
        };
        _contributeKey = contributeKey;
        _adminKey      = adminKey;
    }

    // ── NPC overrides ─────────────────────────────────────────────────────────

    /// <summary>
    /// Polls for NPC override records updated after since_ts.
    /// since_ts = 0 returns all records (full pull).
    /// Returns null on failure.
    /// </summary>
    public async Task<List<ServerNpcOverrideRecord>?> GetNpcOverridesSinceAsync(
        double sinceTs, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/v1/npc-overrides/since?t={sinceTs}");

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<ServerNpcOverrideSinceResponse>(
                _jsonOptions,
                ct).ConfigureAwait(false);

            return payload?.Records;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] GetNpcOverridesSince failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Contributes a local NPC override to the server (crowd-source).
    /// Fire-and-forget friendly — returns false on failure, no throw.
    /// </summary>
    public async Task<bool> ContributeNpcOverrideAsync(
        NpcRaceOverride entry, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                npc_id               = entry.NpcId,
                catalog_id           = string.IsNullOrWhiteSpace(entry.CatalogId) ? null : entry.CatalogId,
                race_id              = entry.RaceId,
                notes                = entry.Notes ?? string.Empty,
                bespoke_sample_id    = entry.BespokeSampleId,
                bespoke_exaggeration = entry.BespokeExaggeration,
                bespoke_cfg_weight   = entry.BespokeCfgWeight,
                gender_override      = ToServerGenderOverride(entry.GenderOverride),
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/npc-overrides")
            {
                Content = JsonContent.Create(payload),
            };

            if (!string.IsNullOrWhiteSpace(_contributeKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _contributeKey);

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] ContributeNpcOverride failed: {ex.Message}");
            return false;
        }
    }


    public async Task<int> ContributeNpcOverridesBatchAsync(
        IReadOnlyList<NpcRaceOverride> entries,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new ServerNpcOverrideBatchRequest
            {
                Records = entries.Select(entry => new ServerNpcOverrideBatchRecord
                {
                    NpcId = entry.NpcId,
                    CatalogId = string.IsNullOrWhiteSpace(entry.CatalogId) ? null : entry.CatalogId,
                    RaceId = entry.RaceId,
                    Notes = entry.Notes ?? string.Empty,
                    BespokeSampleId = entry.BespokeSampleId,
                    BespokeExaggeration = entry.BespokeExaggeration,
                    BespokeCfgWeight = entry.BespokeCfgWeight,
                    GenderOverride = ToServerGenderOverride(entry.GenderOverride),
                }).ToList(),
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/npc-overrides/batch")
            {
                Content = JsonContent.Create(payload),
            };

            if (!string.IsNullOrWhiteSpace(_contributeKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _contributeKey);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return 0;

            var body = await response.Content.ReadFromJsonAsync<ServerNpcOverrideBatchResponse>(
                _jsonOptions,
                ct).ConfigureAwait(false);
            return body?.Upserted ?? 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] ContributeNpcOverridesBatch failed: {ex.Message}");
            return 0;
        }
    }

    // ── Defaults ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the raw JSON payload for a defaults type from the server.
    /// Returns null if not found or on error.
    /// Valid types: "voice-profiles", "pronunciation", "text-shaping", "npc-overrides"
    /// </summary>
    public async Task<string?> GetDefaultsJsonAsync(
        string dataType, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/defaults/{dataType}");
            using var responseMessage = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            responseMessage.EnsureSuccessStatusCode();

            var response = await responseMessage.Content.ReadFromJsonAsync<ServerDefaultsResponse>(
                _jsonOptions,
                ct).ConfigureAwait(false);

            if (response == null || !response.Exists || response.Payload == null)
                return null;

            return response.Payload.Value.GetRawText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] GetDefaults({dataType}) failed: {ex.Message}");
            return null;
        }
    }

    public async Task<ServerDefaultsResponse?> GetNpcPeopleCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/defaults/npc-people-catalog");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ServerDefaultsResponse>(_jsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] GetNpcPeopleCatalog failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> PutNpcPeopleCatalogAsync(object payload, CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "api/v1/defaults/npc-people-catalog")
            {
                Content = JsonContent.Create(payload),
            };

            if (!string.IsNullOrWhiteSpace(_adminKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminKey);

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] PutNpcPeopleCatalog failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pushes a JSON payload to the server as the defaults for a type.
    /// Requires admin key if server is configured with one.
    /// </summary>
    public async Task<bool> PutDefaultsJsonAsync(
        string dataType, string json, CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/defaults/{dataType}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(_adminKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminKey);

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] PutDefaults({dataType}) failed: {ex.Message}");
            return false;
        }
    }

    public async Task<List<ServerProviderSlotProfileRecord>?> GetProviderSlotProfilesAsync(
        string? providerId = null, string? kind = null, double? sinceTs = null, CancellationToken ct = default)
    {
        try
        {
            var path = sinceTs.HasValue ? $"api/v1/provider-slot-profiles/since?t={sinceTs.Value}" : "api/v1/provider-slot-profiles";
            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(providerId))
                queryParts.Add($"provider_id={Uri.EscapeDataString(providerId)}");
            if (!string.IsNullOrWhiteSpace(kind))
                queryParts.Add($"kind={Uri.EscapeDataString(kind)}");
            if (queryParts.Count > 0)
                path += (path.Contains('?') ? "&" : "?") + string.Join("&", queryParts);

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<ServerProviderSlotProfilesResponse>(_jsonOptions, ct).ConfigureAwait(false);
            return payload?.Records;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] GetProviderSlotProfiles failed: {ex.Message}");
            return null;
        }
    }

    public async Task<int?> UpsertProviderSlotProfilesBatchAsync(
        IEnumerable<ServerProviderSlotProfileBatchRecord> records, CancellationToken ct = default)
    {
        try
        {
            var body = new ServerProviderSlotProfileBatchRequest { Records = records.ToList() };
            var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/provider-slot-profiles/batch")
            {
                Content = JsonContent.Create(body),
            };

            if (!string.IsNullOrWhiteSpace(_adminKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminKey);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<ServerProviderSlotProfilesBatchResponse>(_jsonOptions, ct).ConfigureAwait(false);
            return payload?.Upserted ?? 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerDefaultsClient] UpsertProviderSlotProfilesBatch failed: {ex.Message}");
            return null;
        }
    }
}