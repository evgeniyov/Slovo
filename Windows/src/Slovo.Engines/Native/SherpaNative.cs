using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Slovo.Engines.Native;

/// <summary>
/// The parts of the sherpa-onnx C API Slovo uses.
///
/// Called directly rather than through the managed wrapper, like GigaAMRecognizer.swift
/// does on macOS: the wrapper does not check for a failed create (the next call would
/// then crash on a null handle) and marshals paths in the ANSI code page on Windows,
/// while sherpa-onnx reads every path as UTF-8. Configurations are therefore built by
/// <see cref="NativeConfig"/> with the wrapper's struct layouts and UTF-8 strings.
/// </summary>
internal static unsafe partial class SherpaNative
{
    const string Library = "sherpa-onnx-c-api";

    // Offline recognizer.

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxCreateOfflineRecognizer(IntPtr config);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDestroyOfflineRecognizer(IntPtr recognizer);

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxCreateOfflineStream(IntPtr recognizer);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDestroyOfflineStream(IntPtr stream);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxAcceptWaveformOffline(IntPtr stream, int sampleRate, float* samples, int count);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxGetOfflineStreamResult(IntPtr stream);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDestroyOfflineRecognizerResult(IntPtr result);

    // Speaker diarization.

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxCreateOfflineSpeakerDiarization(IntPtr config);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDestroyOfflineSpeakerDiarization(IntPtr diarization);

    [LibraryImport(Library)]
    internal static partial int SherpaOnnxOfflineSpeakerDiarizationGetSampleRate(IntPtr diarization);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxOfflineSpeakerDiarizationSetConfig(IntPtr diarization, IntPtr config);

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxOfflineSpeakerDiarizationProcessWithCallback(
        IntPtr diarization, float* samples, int count,
        delegate* unmanaged[Cdecl]<int, int, IntPtr, int> callback, IntPtr argument);

    [LibraryImport(Library)]
    internal static partial int SherpaOnnxOfflineSpeakerDiarizationResultGetNumSegments(IntPtr result);

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxOfflineSpeakerDiarizationResultSortByStartTime(IntPtr result);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxOfflineSpeakerDiarizationDestroySegment(IntPtr segments);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxOfflineSpeakerDiarizationDestroyResult(IntPtr result);

    // Speaker embeddings.

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxCreateSpeakerEmbeddingExtractor(IntPtr config);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDestroySpeakerEmbeddingExtractor(IntPtr extractor);

    [LibraryImport(Library)]
    internal static partial int SherpaOnnxSpeakerEmbeddingExtractorDim(IntPtr extractor);

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxSpeakerEmbeddingExtractorCreateStream(IntPtr extractor);

    [LibraryImport(Library)]
    internal static partial int SherpaOnnxSpeakerEmbeddingExtractorIsReady(IntPtr extractor, IntPtr stream);

    [LibraryImport(Library)]
    internal static partial float* SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(IntPtr extractor, IntPtr stream);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(float* embedding);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxOnlineStreamAcceptWaveform(IntPtr stream, int sampleRate, float* samples, int count);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxOnlineStreamInputFinished(IntPtr stream);

    [LibraryImport(Library)]
    internal static partial void SherpaOnnxDestroyOnlineStream(IntPtr stream);

    [LibraryImport(Library)]
    internal static partial IntPtr SherpaOnnxGetVersionStr();

    /// <summary>The native library's version, for the log.</summary>
    internal static string Version => Marshal.PtrToStringUTF8(SherpaOnnxGetVersionStr()) ?? "?";

    /// <summary>One segment of a diarization result (<c>SherpaOnnxOfflineSpeakerDiarizationSegment</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiarizationSegment
    {
        public float Start;
        public float End;
        public int Speaker;
        public float Confidence;
    }
}

/// <summary>
/// A zero-filled native copy of one of the wrapper's configuration structs, filled field
/// by field. The wrapper's structs describe the C layout of the same release, so offsets
/// come from them; strings are written as UTF-8. sherpa-onnx treats every zero field as
/// "use the default", like the zeroed configs of its C examples.
/// </summary>
internal sealed class NativeConfig : IDisposable
{
    readonly Type layout;
    readonly List<IntPtr> strings = [];
    IntPtr buffer;

    public NativeConfig(Type layout)
    {
        this.layout = layout;
        var size = Marshal.SizeOf(layout);
        buffer = Marshal.AllocHGlobal(size);
        unsafe
        {
            new Span<byte>((void*)buffer, size).Clear();
        }
    }

    public IntPtr Pointer => buffer;

    /// <summary>Sets a string field, such as <c>ModelConfig.Transducer.Encoder</c>.</summary>
    public NativeConfig Set(string path, string value)
    {
        var (offset, type) = Field(path);
        Require(type == typeof(string), path);
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(pointer);
        Marshal.WriteIntPtr(buffer, offset, pointer);
        return this;
    }

    public NativeConfig Set(string path, int value)
    {
        var (offset, type) = Field(path);
        Require(type == typeof(int), path);
        Marshal.WriteInt32(buffer, offset, value);
        return this;
    }

    public NativeConfig Set(string path, float value)
    {
        var (offset, type) = Field(path);
        Require(type == typeof(float), path);
        Marshal.WriteInt32(buffer, offset, BitConverter.SingleToInt32Bits(value));
        return this;
    }

    public void Dispose()
    {
        foreach (var pointer in strings)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
        strings.Clear();
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
        }
    }

    /// <summary>The byte offset and type of a dotted field path inside the layout.</summary>
    internal (int Offset, Type Type) Field(string path)
    {
        var type = layout;
        var offset = 0;
        foreach (var name in path.Split('.'))
        {
            var field = type.GetField(name) ?? throw new InvalidOperationException($"{type.Name} has no field {name}");
            offset += (int)Marshal.OffsetOf(type, name);
            type = field.FieldType;
        }
        return (offset, type);
    }

    static void Require(bool condition, string path, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Field {path} does not have the expected type ({expression})");
        }
    }
}
