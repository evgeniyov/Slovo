using System.Globalization;

namespace Slovo.Core.Meetings;

/// <summary>
/// Every value that shapes a diarization run, in one place (the pure part of the macOS
/// <c>DiarizationTuning</c>; the FluidAudio configuration it builds there has no Windows
/// counterpart, where the sherpa-onnx engine maps these values itself).
/// <see cref="ToString"/> is what a meeting records in <c>MeetingDiarization.Configuration</c>.
/// </summary>
public sealed record DiarizationTuning
{
    /// <summary>A track with less energy-detected speech than this is not separated.</summary>
    public const double MinimumTrackSpeechSeconds = 5.0;

    /// <summary>
    /// The values the meeting pipeline uses. Step 0.1 with no minimum segment scored best on
    /// VoxConverse and keeps sub-second answers; threshold 0.775 is the current meaning of
    /// the value behind FluidAudio's published AMI result; zero-vote re-embedding counters
    /// the pull of vote-less frames toward the first speaker.
    /// </summary>
    public static readonly DiarizationTuning Standard = new()
    {
        SegmentationStepRatio = 0.1,
        MinimumSegmentSeconds = 0.0,
        ZeroVoteReembed = true,
        ZeroVoteMinimumSeconds = 0.4,
        ClusteringThreshold = 0.775,
        ExcludeOverlap = true,
        ExclusiveSegments = true,
    };

    /// <summary>Segmentation window step as a share of the 10 s window.</summary>
    public double SegmentationStepRatio { get; init; }
    /// <summary>Speech shorter than this gets no embedding of its own.</summary>
    public double MinimumSegmentSeconds { get; init; }
    /// <summary>Re-embeds frames without an embedding vote instead of giving them to the first cluster.</summary>
    public bool ZeroVoteReembed { get; init; }
    public double ZeroVoteMinimumSeconds { get; init; }
    /// <summary>Euclidean cut in (0, 2]; larger merges more voices.</summary>
    public double ClusteringThreshold { get; init; }
    public bool ExcludeOverlap { get; init; }
    /// <summary>One speaker at a time, as transcript lines need.</summary>
    public bool ExclusiveSegments { get; init; }

    /// <summary>
    /// Parses the bench's <c>step=0.1,minSeg=0,zeroVote=on,threshold=0.775</c> (commas or
    /// spaces); keys left out keep the standard values. Null for an unknown key or value.
    /// Also accepts <c>zeroVoteMin</c>, <c>excludeOverlap</c> and <c>exclusive</c>, and the
    /// form <see cref="ToString"/> prints, so a recorded configuration parses back.
    /// </summary>
    public static DiarizationTuning? Parse(string configuration)
    {
        var tuning = Standard;
        foreach (var item in Items(configuration))
        {
            var separator = item.IndexOf('=');
            if (separator < 0)
            {
                return null;
            }
            var value = item[(separator + 1)..];
            switch (item[..separator].ToLowerInvariant())
            {
                case "step":
                    if (Decimal(value) is not { } step || !(step > 0) || !(step <= 1)) return null;
                    tuning = tuning with { SegmentationStepRatio = step };
                    break;
                case "minseg":
                    if (Decimal(value) is not { } minimum || !(minimum >= 0)) return null;
                    tuning = tuning with { MinimumSegmentSeconds = minimum };
                    break;
                case "threshold":
                    if (Decimal(value) is not { } threshold || !(threshold > 0) || !(threshold <= 2)) return null;
                    tuning = tuning with { ClusteringThreshold = threshold };
                    break;
                case "zerovote":
                    if (Flag(value) is not { } zeroVote) return null;
                    tuning = tuning with { ZeroVoteReembed = zeroVote };
                    break;
                case "zerovotemin":
                    if (Decimal(value) is not { } zeroVoteMinimum || !(zeroVoteMinimum >= 0)) return null;
                    tuning = tuning with { ZeroVoteMinimumSeconds = zeroVoteMinimum };
                    break;
                case "excludeoverlap":
                    if (Flag(value) is not { } excludeOverlap) return null;
                    tuning = tuning with { ExcludeOverlap = excludeOverlap };
                    break;
                case "exclusive":
                    if (Flag(value) is not { } exclusive) return null;
                    tuning = tuning with { ExclusiveSegments = exclusive };
                    break;
                default:
                    return null;
            }
        }
        return tuning;
    }

    /// <summary>"step=0.1 minSeg=0 threshold=0.775 zeroVote=on"</summary>
    public override string ToString() =>
        $"step={Number(SegmentationStepRatio)} minSeg={Number(MinimumSegmentSeconds)} "
        + $"threshold={Number(ClusteringThreshold)} zeroVote={(ZeroVoteReembed ? "on" : "off")}";

    /// <summary>Swift's <c>String(format: "%g", value)</c>: six significant digits, the shortest form, no locale.</summary>
    internal static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }
        // %e with five decimals gives the exponent %g decides by.
        var scientific = value.ToString("E5", CultureInfo.InvariantCulture);
        var exponent = int.Parse(scientific[(scientific.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
        if (exponent < -4 || exponent >= 6)
        {
            var mantissa = TrimZeros(scientific[..scientific.IndexOf('E')]);
            var sign = exponent < 0 ? "-" : "+";
            return $"{mantissa}e{sign}{Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture)}";
        }
        var decimals = Math.Max(0, 5 - exponent);
        return TrimZeros(value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    /// <summary>The pieces between commas and whitespace, empty ones left out.</summary>
    static IEnumerable<string> Items(string configuration)
    {
        var start = 0;
        for (var index = 0; index <= configuration.Length; index++)
        {
            if (index < configuration.Length && configuration[index] != ',' && !char.IsWhiteSpace(configuration[index]))
            {
                continue;
            }
            if (index > start)
            {
                yield return configuration[start..index];
            }
            start = index + 1;
        }
    }

    static string TrimZeros(string text) =>
        text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;

    /// <summary>A plain decimal with a dot, whatever the locale.</summary>
    static double? Decimal(string text)
    {
        if (text.Length == 0 || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])
            || !double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                                CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            return null;
        }
        return value;
    }

    static bool? Flag(string text) => text.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        _ => null,
    };
}
