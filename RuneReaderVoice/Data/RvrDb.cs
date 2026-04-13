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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.TextSwap;

namespace RuneReaderVoice.Data;

public enum RvrTable
{
    NpcRaceOverrides,
    PronunciationRules,
    TextSwapRules,
    AudioCacheManifest,
    NpcPeopleCatalog,
    ProviderSlotProfiles,
}

// ── Row types ─────────────────────────────────────────────────────────────────

[Table("NpcRaceOverrides")]
public sealed class NpcRaceOverrideRow
{
    [PrimaryKey]
    public int     NpcId               { get; set; }
    public int     RaceId              { get; set; }
    public string  Notes               { get; set; } = string.Empty;

    // Bespoke voice override — null means "inherit from race slot"
    public string? BespokeSampleId     { get; set; } = null;
    public float?  BespokeExaggeration { get; set; } = null;
    public float?  BespokeCfgWeight    { get; set; } = null;
    public bool    UseNpcIdAsSeed     { get; set; } = false;

    // Sync metadata
    // Source: "Local" | "CrowdSourced" | "Confirmed"
    // Local always wins over server-sourced entries.
    public string  Source              { get; set; } = "Local";
    public int     Confidence          { get; set; } = 0;
    public double  UpdatedAt           { get; set; } = 0.0;  // Unix timestamp
}

[Table("PronunciationRules")]
public sealed class PronunciationRuleRow
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string MatchText { get; set; } = string.Empty;
    public string PhonemeText { get; set; } = string.Empty;
    public string Scope { get; set; } = "Global";
    public string? AccentGroup { get; set; }
    public bool WholeWord { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string Notes { get; set; } = string.Empty;
}

[Table("TextSwapRules")]
public sealed class TextSwapRuleRow
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
    public bool ReplaceWithCrLf { get; set; }
    public bool WholeWord { get; set; }
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string Notes { get; set; } = string.Empty;
}



[Table("NpcPeopleCatalog")]
public sealed class NpcPeopleCatalogRow
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AccentGroupName { get; set; } = string.Empty;
    public string AccentLabel { get; set; } = string.Empty;
    public bool HasMale { get; set; }
    public bool HasFemale { get; set; }
    public bool HasNeutral { get; set; }
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public string Source { get; set; } = "Seeded";
    public long UpdatedUtc { get; set; }
}

[Table("ProviderSlotProfiles")]
public sealed class ProviderSlotProfileRow
{
    [Indexed(Name = "IX_ProviderSlotProfile", Order = 1, Unique = true)]
    public string ProviderId { get; set; } = string.Empty;
    [Indexed(Name = "IX_ProviderSlotProfile", Order = 2, Unique = true)]
    public string SlotId { get; set; } = string.Empty;
    public string ProfileJson { get; set; } = string.Empty;
    public string Source { get; set; } = "Seeded";
    public long UpdatedUtc { get; set; }
}

[Table("AudioCacheManifest")]
public sealed class AudioCacheManifestRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public long LastAccessedUtcTicks { get; set; }
    public bool IsCompressed { get; set; }
}

// ── Unified DB wrapper ────────────────────────────────────────────────────────

public sealed class RvrDb : IDisposable
{
    private readonly string _dbPath;
    private SQLiteAsyncConnection? _conn;

    public RvrDb(string dbPath)
    {
        _dbPath = dbPath;
    }

    public SQLiteAsyncConnection Connection =>
        _conn ?? throw new InvalidOperationException("RvrDb not initialized. Call InitializeAsync first.");

    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SQLiteAsyncConnection(_dbPath);

        await _conn.CreateTableAsync<NpcRaceOverrideRow>();
        await _conn.CreateTableAsync<PronunciationRuleRow>();
        await _conn.CreateTableAsync<TextSwapRuleRow>();
        await _conn.CreateTableAsync<AudioCacheManifestRow>();
        await _conn.CreateTableAsync<NpcPeopleCatalogRow>();
        await _conn.CreateTableAsync<ProviderSlotProfileRow>();
        await EnsureNpcRaceOverrideSchemaAsync();

    }

    private async Task EnsureNpcRaceOverrideSchemaAsync()
    {
        var cols = await Connection.QueryAsync<TableInfoRow>("PRAGMA table_info('NpcRaceOverrides')");
        if (!cols.Any(c => string.Equals(c.name, "UseNpcIdAsSeed", StringComparison.OrdinalIgnoreCase)))
            await Connection.ExecuteAsync("ALTER TABLE NpcRaceOverrides ADD COLUMN UseNpcIdAsSeed INTEGER NOT NULL DEFAULT 0");
    }

    private sealed class TableInfoRow
    {
        public int cid { get; set; }
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public int notnull { get; set; }
        public string dflt_value { get; set; } = string.Empty;
        public int pk { get; set; }
    }

    public Task VacuumAsync()
        => Connection.ExecuteAsync("VACUUM");

    public long GetDbFileSizeBytes()
        => File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0L;

    public Task ClearTableAsync(RvrTable table)
    {
        return table switch
        {
            RvrTable.NpcRaceOverrides    => Connection.DeleteAllAsync<NpcRaceOverrideRow>(),
            RvrTable.PronunciationRules  => Connection.DeleteAllAsync<PronunciationRuleRow>(),
            RvrTable.TextSwapRules       => Connection.DeleteAllAsync<TextSwapRuleRow>(),
            RvrTable.AudioCacheManifest  => Connection.DeleteAllAsync<AudioCacheManifestRow>(),
            RvrTable.NpcPeopleCatalog    => Connection.DeleteAllAsync<NpcPeopleCatalogRow>(),
            RvrTable.ProviderSlotProfiles=> Connection.DeleteAllAsync<ProviderSlotProfileRow>(),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
    }

    public void Dispose()
    {
        _conn?.CloseAsync().GetAwaiter().GetResult();
        _conn = null;
    }
}