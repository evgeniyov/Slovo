using Slovo.Core.Meetings;
using System.Text.Json;
using Slovo.Engines.Audio;
using Slovo.Core.Model;
using Slovo.Engines.Diarization;

namespace Slovo.Engines.Tests.Diarization;

/// <summary>
/// An evaluation, not a check: separates speakers on real meetings recorded by the macOS app
/// and prints how many voices each track got here next to what the macOS app found. Runs
/// only with SLOVO_EVAL_MEETINGS pointing at a Meetings folder (read-only) and the speaker
/// models in SLOVO_DIARIZATION_MODELS_DIR.
/// </summary>
public class MeetingEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CompareSpeakerCountsWithTheMacApp()
    {
        var meetings = Environment.GetEnvironmentVariable("SLOVO_EVAL_MEETINGS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(meetings), "SLOVO_EVAL_MEETINGS is not set");
        var models = TestSupport.RequireDiarizationModels();
        using var diarizer = SherpaSpeakerDiarizer.Load(models);
        foreach (var folder in Directory.GetDirectories(meetings!))
        {
            var record = Path.Combine(folder, "meeting.json");
            if (!File.Exists(record))
            {
                continue;
            }
            using var json = JsonDocument.Parse(File.ReadAllText(record));
            var mac = json.RootElement.TryGetProperty("diarization", out var d) && d.TryGetProperty("speakersPerTrack", out var perTrack)
                ? perTrack.ToString() : "{}";
            foreach (var track in new[] { "mic.caf", "system.caf" })
            {
                var path = Path.Combine(folder, track);
                if (!File.Exists(path))
                {
                    continue;
                }
                var samples = AudioFileReader.ReadMono16k(path, TestContext.Current.CancellationToken);
                var started = DateTime.UtcNow;
                var turns = await diarizer.DiarizeAsync(samples, SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken);
                var normalized = SpeakerTurns.Normalize(turns, samples.Length / 16_000.0, SpeakerCountHint.Automatic);
                output.WriteLine($"{Path.GetFileName(folder)[..8]} {track,-10} {samples.Length / 16_000.0 / 60,5:0.0} min: " +
                                 $"raw {turns.Select(t => t.Cluster).Distinct().Count()} voices, normalized {normalized.Select(t => t.Cluster).Distinct().Count()} " +
                                 $"in {(DateTime.UtcNow - started).TotalSeconds:0} s; macOS {mac}");
            }
        }
    }
}
