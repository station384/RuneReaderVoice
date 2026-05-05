// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.UI.Views;

internal enum TestQrScenario
{
    Small,
    Medium,
    Full,
}

internal sealed record TestQrRaceOption(string Label, int RaceId, Gender Gender)
{
    public override string ToString() => Label;
}

internal sealed record TestQrPacket(int SeqIndex, int SubIndex, int SeqTotal, int SubTotal, string RawPacket, string EncodedQrText);

internal sealed record TestQrBuildOptions(
    TestQrRaceOption Race,
    TestQrScenario Scenario,
    bool IncludeNarrator,
    string? ForceGenerationNonce);

internal static class TestQrGenerator
{
    private const int ProtocolVersion = 5;
    private const int HeaderLength = 26;
    private const int MinimumPacketLength = 50;
    private const int TextPayloadBytesPerPacket = MinimumPacketLength - HeaderLength;
    private const int TestNpcId = 0x0F00D0;

    private static int _dialogCounter = Random.Shared.Next(1, 0xFFFF);

    public static IReadOnlyList<TestQrRaceOption> BuildRaceOptions()
    {
        var result = new List<TestQrRaceOption>();

        foreach (var item in NpcVoiceSlotCatalog.All
                     .Where(i => !i.Slot.IsNarrator)
                     .OrderBy(i => i.SortOrder)
                     .ThenBy(i => i.NpcLabel))
        {
            if (item.Slot.Gender is not (Gender.Male or Gender.Female))
                continue;

            var raceId = TryGetRaceId(item.Slot.Group);
            if (raceId is null or < 0 or > 0xFF)
                continue;

            result.Add(new TestQrRaceOption(item.NpcLabel, raceId.Value, item.Slot.Gender));
        }

        return result;
    }

    public static IReadOnlyList<TestQrPacket> BuildPackets(TestQrBuildOptions options)
    {
        var dialogId = NextDialogId();
        var segments = BuildSegments(options).ToList();
        var packets = new List<TestQrPacket>();

        for (var seq = 0; seq < segments.Count; seq++)
        {
            var segment = segments[seq];
            var chunks = SplitUtf8ByByteLimit(segment.Text, TextPayloadBytesPerPacket).ToList();
            if (chunks.Count == 0)
                chunks.Add(string.Empty);

            for (var sub = 0; sub < chunks.Count; sub++)
            {
                var rawPayload = PadUtf8ToByteLength(chunks[sub], TextPayloadBytesPerPacket);
                var rawPacket = BuildRawPacket(
                    dialogId,
                    seq,
                    segments.Count,
                    sub,
                    chunks.Count,
                    segment.Flags,
                    segment.RaceId,
                    segment.NpcId,
                    rawPayload);

                packets.Add(new TestQrPacket(
                    seq,
                    sub,
                    segments.Count,
                    chunks.Count,
                    rawPacket,
                    Base45Simple.EncodeUtf8(rawPacket)));
            }
        }

        return packets;
    }

    private sealed record TestSegment(string Text, int Flags, int RaceId, int NpcId);

    private static IEnumerable<TestSegment> BuildSegments(TestQrBuildOptions options)
    {
        string? nonce = string.IsNullOrWhiteSpace(options.ForceGenerationNonce)
            ? null
            : $" Test run {options.ForceGenerationNonce}.";

        var raceFlags = options.Race.Gender == Gender.Female
            ? RvFlags.GenderFemale
            : RvFlags.GenderMale;

        TestSegment RaceLine(string text, bool nonceTarget = false) => new(
            nonceTarget && nonce != null ? text + nonce : text,
            raceFlags,
            options.Race.RaceId,
            TestNpcId);

        TestSegment NarratorLine(string text) => new(
            text,
            RvFlags.FlagNarrator | RvFlags.GenderMale,
            0,
            0);

        switch (options.Scenario)
        {
            case TestQrScenario.Small:
                yield return RaceLine("This is a small test detection from RuneReader Voice.", nonceTarget: true);
                yield break;

            case TestQrScenario.Medium:
                if (options.IncludeNarrator)
                    yield return NarratorLine("Testing RuneReader Voice.");
                yield return RaceLine("This is a medium test detection from RuneReader Voice.", nonceTarget: true);
                yield break;

            default:
                if (options.IncludeNarrator)
                    yield return NarratorLine("Testing RuneReader Voice.");
                yield return RaceLine("This is a full test of the detection system.");
                yield return RaceLine("We are making sure things work end to end.");
                if (options.IncludeNarrator)
                    yield return NarratorLine("The developer is hoping this works correctly.");
                yield return RaceLine("If you hear this, everything is working well.", nonceTarget: true);
                yield break;
        }
    }

    private static int? TryGetRaceId(AccentGroup group)
    {
        if (RaceAccentMapping.PlayerRaceIds.TryGetValue(group, out var playerRace))
            return playerRace;
        if (RaceAccentMapping.CreatureTypeIds.TryGetValue(group, out var creatureType))
            return creatureType;
        return null;
    }

    private static int NextDialogId()
    {
        var next = System.Threading.Interlocked.Increment(ref _dialogCounter);
        if (next > 0xFFFF)
        {
            _dialogCounter = 1;
            next = 1;
        }
        return next & 0xFFFF;
    }

    private static string BuildRawPacket(
        int dialogId,
        int seq,
        int seqTotal,
        int sub,
        int subTotal,
        int flags,
        int race,
        int npcId,
        string payload)
    {
        var header = string.Create(HeaderLength, (dialogId, seq, seqTotal, sub, subTotal, flags, race, npcId), static (span, state) =>
        {
            var (dialogId, seq, seqTotal, sub, subTotal, flags, race, npcId) = state;
            $"RV{ProtocolVersion:X2}{dialogId:X4}{seq:X2}{seqTotal:X2}{sub:X2}{subTotal:X2}{flags:X2}{race:X2}{npcId:X6}".AsSpan().CopyTo(span);
        });

        return header + payload;
    }

    private static IEnumerable<string> SplitUtf8ByByteLimit(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var sb = new StringBuilder();
        var currentBytes = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (currentBytes > 0 && currentBytes + runeBytes > maxBytes)
            {
                yield return sb.ToString();
                sb.Clear();
                currentBytes = 0;
            }

            sb.Append(rune.ToString());
            currentBytes += runeBytes;
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string PadUtf8ToByteLength(string text, int byteLength)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > byteLength)
            throw new InvalidOperationException("QR test text chunk exceeded byte limit.");

        return text + new string(' ', byteLength - bytes.Length);
    }
}
