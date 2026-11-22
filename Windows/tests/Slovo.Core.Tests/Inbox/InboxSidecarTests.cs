using System.Text.Json;
using Slovo.Core.Inbox;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Inbox;

/// <summary>
/// The phone's sidecar: format 1 as the contract writes it, and what a newer build or a write
/// cut off half-way leaves behind (port of the sidecar parts of InboxImporterTests.swift).
/// </summary>
public sealed class InboxSidecarTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "SlovoSidecarTests-" + Guid.NewGuid().ToString("N"));

    public InboxSidecarTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void AFileThatIsNotAJsonObjectReadsAsNoSidecar()
    {
        var broken = Path.Combine(folder, "broken.json");
        File.WriteAllText(broken, "{ not JSON at all");
        Assert.Null(InboxSidecar.Read(broken));
        Assert.Null(InboxSidecar.Read(Path.Combine(folder, "never-written.json")));
        var array = Path.Combine(folder, "array.json");
        File.WriteAllText(array, "[1, 2]");
        Assert.Null(InboxSidecar.Read(array));
    }

    [Fact]
    public void FormatOneIsReadAsTheContractSays()
    {
        var sidecar = Parse("""
            {"version": 1, "kind": "meeting", "recordedAt": "2026-09-16T12:54:58+06:00", "durationMs": 15700,
             "device": "Demo Phone", "title": null, "highlights": [{"atMs": 123456, "note": null}]}
            """);

        Assert.Equal(InboxSidecar.SidecarKind.Meeting, sidecar.Kind);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 12, 54, 58, TimeSpan.FromHours(6)), sidecar.RecordedAt);
        Assert.Equal("Demo Phone", sidecar.Device);
        Assert.Null(sidecar.Title);
        Assert.Equal([new InboxSidecar.Moment(123.456, null)], sidecar.Moments);
    }

    [Fact]
    public void DatesMayCarryFractionsOrAZuluOffset()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 6, 54, 58, 250, TimeSpan.Zero),
                     Parse("""{"recordedAt": "2026-09-16T12:54:58.250+06:00"}""").RecordedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 12, 54, 58, TimeSpan.Zero), Parse("""{"recordedAt": "2026-09-16T12:54:58Z"}""").RecordedAt);
        // A date without an offset says nothing about the instant.
        Assert.Null(Parse("""{"recordedAt": "2026-09-16T12:54:58"}""").RecordedAt);
        Assert.Null(Parse("""{"recordedAt": 1758013200}""").RecordedAt);
    }

    [Fact]
    public void WhatANewerBuildOrACutOffWriteLeavesIsDroppedFieldByField()
    {
        var device = new string('п', 200);
        var odd = Parse($$"""
            {"version": 7, "kind": "podcast", "recordedAt": "yesterday", "device": "{{device}}",
             "title": "  Планёрка\nв 10  ",
             "highlights": ["not an object", {"note": "no position"}, {"atMs": -5}, {"atMs": "12"}, {"atMs": 1e400}]}
            """);

        Assert.Equal(InboxSidecar.SidecarKind.Note, odd.Kind);
        Assert.Null(odd.RecordedAt);
        Assert.Equal(MeetingSource.MaximumDeviceCharacters, odd.Device?.Length);
        Assert.Equal("Планёрка в 10", odd.Title);
        Assert.Empty(odd.Moments);
    }

    [Fact]
    public void TitlesAndNotesAreKeptToOneShortLine()
    {
        var sidecar = Parse($$"""
            {"title": "{{new string('a', 300)}}", "highlights": [{"atMs": 1000, "note": "  дед\nлайн  "}, {"atMs": 2500}]}
            """);

        Assert.Equal(InboxSidecar.MaximumTitleCharacters, sidecar.Title?.Length);
        Assert.Equal([new InboxSidecar.Moment(1, "дед лайн"), new InboxSidecar.Moment(2.5, null)], sidecar.Moments);
        Assert.Null(Parse("""{"title": "   "}""").Title);
    }

    [Fact]
    public void TheSidecarOfAClipSharesItsName()
    {
        Assert.Equal("slovo-20260916-125458.json", Path.GetFileName(InboxSidecar.PathFor(Path.Combine(folder, "slovo-20260916-125458.m4a"))));
        Assert.Equal(Path.Combine(folder, "Запись 1.json"), InboxSidecar.PathFor(Path.Combine(folder, "Запись 1.caf")));
    }

    static InboxSidecar Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return InboxSidecar.Parse(document.RootElement);
    }
}
