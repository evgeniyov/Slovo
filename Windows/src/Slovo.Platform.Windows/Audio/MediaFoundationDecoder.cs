using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;
using Slovo.Core.Audio;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Audio;

/// <summary>
/// Decodes audio files the core cannot read itself (m4a from the phone, mp3, …) through
/// Media Foundation (NAudio's MediaFoundationReader), then mixes to mono and resamples to
/// 16 kHz with <see cref="Resampler"/>. Synchronous; call it off the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationDecoder : IAudioDecoder
{
    /// <summary>Containers Windows 10 and later decode without extra codecs.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp4", ".aac", ".mp3", ".wma", ".flac", ".wav", ".3gp", ".m4b",
    };

    public bool CanDecode(string path) => Extensions.Contains(Path.GetExtension(path));

    public float[] DecodeToMono16k(string path, CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            throw new SlovoException(CoreStrings.Format("audio.error.readFailed", Path.GetFileName(path), new FileNotFoundException().Message));
        }
        try
        {
            using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            var format = reader.WaveFormat;
            var layout = CaptureFormat.LayoutOf(format)
                ?? throw new SlovoException(CoreStrings.Get("audio.error.formatUnsupported"));
            var resampler = new Resampler(format.SampleRate);
            var output = new List<float>(reader.Length > 0 ? (int)Math.Min(int.MaxValue / 2, reader.Length / layout.BlockAlign / 3) : 16_000);
            var buffer = new byte[layout.BlockAlign * 16_384];
            var carried = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = reader.Read(buffer.AsSpan(carried));
                if (read <= 0)
                {
                    break;
                }
                var available = carried + read;
                var whole = available - available % layout.BlockAlign;
                output.AddRange(resampler.Process(AudioMixdown.ToMono(buffer.AsSpan(0, whole), layout)));
                // A partial frame waits for the rest of its bytes.
                carried = available - whole;
                buffer.AsSpan(whole, carried).CopyTo(buffer);
            }
            output.AddRange(resampler.Flush());
            return [.. output];
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or IOException or ArgumentException)
        {
            throw new SlovoException(CoreStrings.Format("audio.error.readFailed", Path.GetFileName(path), AudioEndpoints.Describe(exception)), exception);
        }
    }
}
