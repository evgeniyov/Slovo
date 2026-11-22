using Slovo.Core.Meetings;
using System.Globalization;

namespace Slovo.Engines.Diarization;

/// <summary>
/// Every value that shapes a diarization run, in one place (the Windows counterpart of
/// SherpaDiarizationTuning.swift). <see cref="Description"/> is what a meeting records in
/// <c>MeetingDiarization.configuration</c>.
/// </summary>
/// <param name="SegmentationStepRatio">Segmentation window step as a share of the 10 s window.</param>
/// <param name="SplitThreshold">
/// The first pass: cosine distance (0…2) up to which sherpa-onnx's complete-linkage
/// clustering keeps window embeddings in one voice. Deliberately tight, so two similar
/// people are never merged here; the second pass joins what belongs together.
/// </param>
/// <param name="ClusteringThreshold">
/// The second pass (<see cref="SpeakerClusterMerger"/>): Euclidean distance between unit
/// centroids (0…2) up to which voices merge; larger merges more voices. The same kind of
/// value as the macOS threshold and pyannote's.
/// </param>
/// <param name="MinDurationOn">sherpa-onnx drops a voice's segments this short or shorter.</param>
/// <param name="MinDurationOff">sherpa-onnx joins a voice's segments separated by less than this.</param>
public sealed record SherpaDiarizationTuning(
    double SegmentationStepRatio,
    double SplitThreshold,
    double ClusteringThreshold,
    double MinDurationOn,
    double MinDurationOff)
{
    /// <summary>A track with less energy-detected speech than this is not separated.</summary>
    public const double MinimumTrackSpeechSeconds = 5.0;

    /// <summary>
    /// The values the meeting pipeline uses.
    ///
    /// Step 0.1 is the value the macOS evaluation chose (and sherpa-onnx's default); it
    /// costs about a tenth of the audio's duration on four cores. The merge threshold
    /// 0.7045 is pyannote 3.1's centroid-linkage threshold for this very embedding model
    /// (WeSpeaker ResNet34-LM). The split threshold 0.2 kept five synthetic voices apart,
    /// whose embeddings are far closer to each other than two real people's usually are;
    /// the second pass costs a few seconds however finely the first one splits.
    /// Post-processing is left to <see cref="SpeakerTurns.Normalize"/>, as on macOS, so
    /// sherpa-onnx's own minimum durations are off. An evaluation on real meetings should
    /// confirm or replace these values before release, as it did on macOS.
    /// </summary>
    public static readonly SherpaDiarizationTuning Standard = new(0.1, 0.2, 0.7045, 0, 0);

    /// <summary>
    /// Parses <c>step=0.1,split=0.2,threshold=0.7045,minOn=0,minOff=0</c> (commas or
    /// spaces); keys left out keep the standard values. Null for an unknown key or a value
    /// out of range (including the macOS-only keys, which do not apply here). The form
    /// <see cref="Description"/> prints parses back.
    /// </summary>
    public static SherpaDiarizationTuning? Parse(string configuration)
    {
        var tuning = Standard;
        foreach (var item in configuration.Split([',', ' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2 || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number))
            {
                return null;
            }
            switch (pair[0].ToLowerInvariant())
            {
                case "step" when number is > 0 and <= 1:
                    tuning = tuning with { SegmentationStepRatio = number };
                    break;
                case "split" when number is > 0 and <= 2:
                    tuning = tuning with { SplitThreshold = number };
                    break;
                case "threshold" when number is > 0 and <= 2:
                    tuning = tuning with { ClusteringThreshold = number };
                    break;
                case "minon" when number >= 0:
                    tuning = tuning with { MinDurationOn = number };
                    break;
                case "minoff" when number >= 0:
                    tuning = tuning with { MinDurationOff = number };
                    break;
                default:
                    return null;
            }
        }
        return tuning;
    }

    /// <summary>"step=0.1 split=0.2 threshold=0.7045 minOn=0 minOff=0"</summary>
    public string Description =>
        $"step={Number(SegmentationStepRatio)} split={Number(SplitThreshold)} threshold={Number(ClusteringThreshold)} "
        + $"minOn={Number(MinDurationOn)} minOff={Number(MinDurationOff)}";

    public override string ToString() => Description;

    /// <summary>Shortest form without a locale, so the text is the same on every computer.</summary>
    static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
