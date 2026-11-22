using SherpaOnnx;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Engines.Native;

namespace Slovo.Engines.Recognition;

/// <summary>
/// One model loaded into a sherpa-onnx offline recognizer (the port of GigaAMRecognizer.swift,
/// shared by GigaAM and Parakeet).
///
/// Not thread-safe; the owner serializes every call.
/// </summary>
internal sealed class SherpaRecognizer : IDisposable
{
    public const int SampleRate = 16_000;

    /// <summary>
    /// Shorter input is padded with silence: with less than one feature frame ONNX
    /// Runtime throws inside sherpa-onnx and the uncaught exception aborts the process.
    /// It is also what Parakeet needs for a single short word.
    /// </summary>
    public const int MinimumSamples = SampleRate;

    IntPtr handle;

    SherpaRecognizer(IntPtr handle)
    {
        this.handle = handle;
    }

    /// <summary>Creates the recognizer. The caller has checked every file against the release sizes.</summary>
    public static SherpaRecognizer Create(SherpaModelSpec spec, string folder, int threads)
    {
        string File(string name) => Path.Combine(folder, name);
        using var config = new NativeConfig(typeof(OfflineRecognizerConfig));
        // GigaAM needs 64 mel bins; sherpa-onnx also forces this from the model metadata.
        config.Set("FeatConfig.SampleRate", SampleRate)
            .Set("FeatConfig.FeatureDim", spec.FeatureDim)
            .Set("ModelConfig.Tokens", File(spec.Tokens))
            .Set("ModelConfig.NumThreads", threads)
            .Set("ModelConfig.Provider", "cpu")
            // Beam search was no more accurate on the bench and dropped words.
            .Set("DecodingMethod", "greedy_search");
        switch (spec.Kind)
        {
            case SherpaModelKind.NemoTransducer:
                // The generic "transducer" type expects Zipformer metadata and fails with
                // "'vocab_size' does not exist in the metadata".
                config.Set("ModelConfig.ModelType", "nemo_transducer")
                    .Set("ModelConfig.Transducer.Encoder", File(spec.Encoder))
                    .Set("ModelConfig.Transducer.Decoder", File(spec.Decoder))
                    .Set("ModelConfig.Transducer.Joiner", File(spec.Joiner));
                break;
            case SherpaModelKind.NemoCtc:
                config.Set("ModelConfig.ModelType", "nemo_ctc")
                    .Set("ModelConfig.NeMoCtc.Model", File(spec.CtcModel));
                break;
        }
        // sherpa-onnx copies the strings while creating the recognizer.
        var created = SherpaNative.SherpaOnnxCreateOfflineRecognizer(config.Pointer);
        if (created == IntPtr.Zero)
        {
            var key = spec.Engine == EngineId.Parakeet ? "engines.parakeet.recognizerFailed" : "engines.gigaam.recognizerFailed";
            throw new SlovoException(CoreStrings.Format(key, folder));
        }
        return new SherpaRecognizer(created);
    }

    /// <summary>Recognizes 16 kHz mono samples in one pass.</summary>
    public unsafe string Recognize(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        var stream = SherpaNative.SherpaOnnxCreateOfflineStream(handle);
        if (stream == IntPtr.Zero)
        {
            throw new SlovoException(CoreStrings.Get("engines.gigaam.streamFailed"));
        }
        try
        {
            if (samples.Length < MinimumSamples)
            {
                var padded = new float[MinimumSamples];
                samples.CopyTo(padded);
                samples = padded;
            }
            fixed (float* pointer = samples)
            {
                SherpaNative.SherpaOnnxAcceptWaveformOffline(stream, SampleRate, pointer, samples.Length);
            }
            SherpaNative.SherpaOnnxDecodeOfflineStream(handle, stream);

            var result = SherpaNative.SherpaOnnxGetOfflineStreamResult(stream);
            if (result == IntPtr.Zero)
            {
                throw new SlovoException(CoreStrings.Get("engines.gigaam.noResult"));
            }
            try
            {
                // The text is the first field of SherpaOnnxOfflineRecognizerResult.
                var text = System.Runtime.InteropServices.Marshal.ReadIntPtr(result);
                return text == IntPtr.Zero ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text) ?? "";
            }
            finally
            {
                SherpaNative.SherpaOnnxDestroyOfflineRecognizerResult(result);
            }
        }
        finally
        {
            SherpaNative.SherpaOnnxDestroyOfflineStream(stream);
        }
    }

    public void Dispose()
    {
        var current = handle;
        handle = IntPtr.Zero;
        if (current != IntPtr.Zero)
        {
            SherpaNative.SherpaOnnxDestroyOfflineRecognizer(current);
        }
        GC.SuppressFinalize(this);
    }

    ~SherpaRecognizer()
    {
        if (handle != IntPtr.Zero)
        {
            SherpaNative.SherpaOnnxDestroyOfflineRecognizer(handle);
            handle = IntPtr.Zero;
        }
    }
}
