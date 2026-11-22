using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Screen;

/// <summary>
/// H.264 through the Microsoft software encoder MFT of Media Foundation (<c>CLSID_MSH264EncoderMFT</c>),
/// driven synchronously: an NV12 sample in, the access units it produced out.
/// </summary>
/// <remarks>
/// <para>
/// The software encoder is used on purpose rather than a hardware one: at four pictures a
/// second of at most 1920 pixels it costs little, it behaves the same on every machine, and
/// it is synchronous, where hardware encoders are asynchronous MFTs with their own event model.
/// Baseline profile rules out reordered (B) frames, so decode order is presentation order and
/// the MP4 needs no composition offsets. Quality-based rate control keeps an unchanged screen
/// nearly free; a key frame is asked for when the capture wants one.
/// </para>
/// <para>
/// Windows N editions ship without Media Foundation's codecs until the Media Feature Pack is
/// installed: creating the encoder then fails with a message saying so, which ends the screen
/// track only.
/// </para>
/// <para>
/// Slots (mftransform.h, mfobjects.h, icodecapi.h): IMFAttributes GetBlobSize 14, GetBlob 15,
/// SetUINT32 21, SetUINT64 22, SetGUID 24; IMFSample GetSampleTime 35, SetSampleTime 36,
/// SetSampleDuration 38, GetBufferByIndex 40, ConvertToContiguousBuffer 41, AddBuffer 42;
/// IMFMediaBuffer Lock 3, Unlock 4, SetCurrentLength 6; IMFTransform GetOutputStreamInfo 7,
/// GetAttributes 8, GetOutputAvailableType 14, SetInputType 15, SetOutputType 16,
/// GetOutputCurrentType 18, ProcessMessage 23, ProcessInput 24, ProcessOutput 25;
/// ICodecAPI SetValue 9.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MediaFoundationH264Encoder : IVideoEncoder
{
    static readonly Guid ClsidH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    static readonly Guid IidTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    static readonly Guid IidCodecApi = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");

    static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    static readonly Guid MajorTypeKey = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    static readonly Guid SubtypeKey = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    static readonly Guid FrameSizeKey = new("1652C33D-D6B2-4012-B834-72030849A37D");
    static readonly Guid FrameRateKey = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    static readonly Guid PixelAspectRatioKey = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    static readonly Guid InterlaceModeKey = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    static readonly Guid AverageBitrateKey = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    static readonly Guid ProfileKey = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    static readonly Guid DefaultStrideKey = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    static readonly Guid YuvMatrixKey = new("3E23D450-2C75-4D25-A00E-B91670D12327");
    static readonly Guid NominalRangeKey = new("C21B8EE5-B956-4071-8DAF-325EDF5CAB11");
    static readonly Guid PrimariesKey = new("DBFBE4D7-0740-4EE0-8192-850AB0E21935");
    static readonly Guid TransferFunctionKey = new("5FB0FCE9-BE5C-4935-A811-EC838F8EED93");
    static readonly Guid MaxKeyframeSpacingKey = new("C16EB52B-73A1-476F-8D62-839D6A020652");
    static readonly Guid LowLatencyKey = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    static readonly Guid RateControlModeApi = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    static readonly Guid QualityApi = new("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");
    static readonly Guid GopSizeApi = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    static readonly Guid BPictureCountApi = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    static readonly Guid ForceKeyFrameApi = new("398C1B98-8353-475A-9EF2-8F265D260345");
    static readonly Guid LowLatencyApi = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    static readonly Guid SequenceHeaderKey = new("3C036DE7-3AD0-4C9E-9216-EE6D6AC21CB3");

    const int GetBlobSizeSlot = 14;
    const int GetBlobSlot = 15;
    const int GetBufferByIndexSlot = 40;
    const int GetOutputCurrentTypeSlot = 18;
    const int SetUInt32Slot = 21;
    const int SetUInt64Slot = 22;
    const int SetGuidSlot = 24;
    const int GetSampleTimeSlot = 35;
    const int SetSampleTimeSlot = 36;
    const int SetSampleDurationSlot = 38;
    const int ConvertToContiguousBufferSlot = 41;
    const int AddBufferSlot = 42;
    const int LockSlot = 3;
    const int UnlockSlot = 4;
    const int SetCurrentLengthSlot = 6;
    const int GetOutputStreamInfoSlot = 7;
    const int GetAttributesSlot = 8;
    const int GetOutputAvailableTypeSlot = 14;
    const int SetInputTypeSlot = 15;
    const int SetOutputTypeSlot = 16;
    const int ProcessMessageSlot = 23;
    const int ProcessInputSlot = 24;
    const int ProcessOutputSlot = 25;
    const int CodecSetValueSlot = 9;

    const int NeedMoreInput = unchecked((int)0xC00D6D72);
    const int StreamChange = unchecked((int)0xC00D6D61);
    const int NotAccepting = unchecked((int)0xC00D36B5);
    const uint BeginStreaming = 0x1000_0000;
    const uint EndStreaming = 0x1000_0001;
    const uint EndOfStream = 0x1000_0002;
    const uint StartOfStream = 0x1000_0003;
    const uint ProvidesSamples = 0x100;
    const uint CanProvideSamples = 0x200;

    const uint Progressive = 2;
    const uint BaselineProfile = 66;
    const uint QualityRateControl = 3;
    /// <summary>0–100; what an unchanged screen costs nothing at and text stays sharp at.</summary>
    const uint Quality = 62;
    /// <summary>A hint the encoder needs on its output type; quality-based control mostly ignores it.</summary>
    const uint AverageBitrate = 600_000;
    const uint FramesPerSecond = 4;
    const long FrameDurationTicks = 10_000_000 / FramesPerSecond;

    static readonly Logger Logger = Log.For("ScreenEncoder");
    static readonly Lock StartupGate = new();
    static bool mediaFoundationStarted;

    readonly int width;
    readonly int height;
    readonly int frameBytes;
    nint transform;
    nint codecApi;
    bool providesSamples;
    uint outputBufferSize;
    /// <summary>Our output sample between calls, when the encoder does not make its own.</summary>
    nint spareOutput;
    byte[]? sequenceHeader;

    MediaFoundationH264Encoder(int width, int height)
    {
        this.width = width;
        this.height = height;
        frameBytes = width * height * 3 / 2;
    }

    /// <summary>Creates and configures the encoder. Throws SlovoException.</summary>
    public static MediaFoundationH264Encoder Create(int width, int height)
    {
        var encoder = new MediaFoundationH264Encoder(width, height);
        try
        {
            encoder.Configure();
            return encoder;
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            encoder.Dispose();
            Logger.Error($"The H.264 encoder did not start: {error.Message} (0x{error.HResult:X8})");
            var missing = error is DllNotFoundException or EntryPointNotFoundException || error.HResult == Com.REGDB_E_CLASSNOTREG;
            throw new SlovoException(missing
                ? CoreStrings.Get("meetings.error.screenRecordingNoEncoder")
                : CoreStrings.Format("meetings.error.screenRecordingEncoderFailed", $"0x{error.HResult:X8}"), error);
        }
    }

    void Configure()
    {
        WinRT.EnsureInitialized();
        StartMediaFoundation();
        Com.Check(Ole32.CoCreateInstance(in ClsidH264Encoder, 0, Ole32.CLSCTX_INPROC_SERVER, in IidTransform, out transform),
            "CoCreateInstance(CLSID_MSH264EncoderMFT)");
        codecApi = Com.TryQuery(transform, IidCodecApi);
        // Rate control has to be chosen before the output type is set.
        SetCodecValue(RateControlModeApi, Variant.UInt32, QualityRateControl);
        SetCodecValue(QualityApi, Variant.UInt32, Quality);
        SetCodecValue(GopSizeApi, Variant.UInt32, (uint)(FramesPerSecond * Core.Meetings.MeetingScreenCapture.KeyFrameInterval));
        SetCodecValue(BPictureCountApi, Variant.UInt32, 0);
        SetCodecValue(LowLatencyApi, Variant.Boolean, 0xFFFF);
        SetTransformAttribute(LowLatencyKey, 1);

        var output = NewVideoType(FormatH264);
        try
        {
            SetUInt32(output, AverageBitrateKey, AverageBitrate);
            SetUInt32(output, ProfileKey, BaselineProfile);
            SetUInt32(output, MaxKeyframeSpacingKey, (uint)(FramesPerSecond * Core.Meetings.MeetingScreenCapture.KeyFrameInterval));
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.VTable(transform)[SetOutputTypeSlot])(transform, 0, output, 0),
                "IMFTransform.SetOutputType");
        }
        finally
        {
            Com.Release(output);
        }
        var input = NewVideoType(FormatNv12);
        try
        {
            SetUInt32(input, DefaultStrideKey, (uint)width);
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.VTable(transform)[SetInputTypeSlot])(transform, 0, input, 0),
                "IMFTransform.SetInputType");
        }
        finally
        {
            Com.Release(input);
        }
        var info = default(MftOutputStreamInfo);
        Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, MftOutputStreamInfo*, int>)Com.VTable(transform)[GetOutputStreamInfoSlot])(transform, 0, &info),
            "IMFTransform.GetOutputStreamInfo");
        providesSamples = (info.Flags & (ProvidesSamples | CanProvideSamples)) != 0;
        // An encoded picture is never larger than the raw one; the encoder's own figure when it gives one.
        outputBufferSize = Math.Max(info.Size, (uint)frameBytes);
        ProcessMessage(BeginStreaming);
        ProcessMessage(StartOfStream);
    }

    public IReadOnlyList<EncodedVideoFrame> Encode(ReadOnlySpan<byte> nv12, double time, bool keyFrame)
    {
        ObjectDisposedException.ThrowIf(transform == 0, this);
        if (nv12.Length < frameBytes)
        {
            throw new ArgumentException("The picture is smaller than the encoder's frame");
        }
        if (keyFrame)
        {
            SetCodecValue(ForceKeyFrameApi, Variant.UInt32, 1);
        }
        var results = new List<EncodedVideoFrame>();
        var sample = NewSample(nv12[..frameBytes], (long)Math.Round(time * 10_000_000));
        try
        {
            var hr = ProcessInput(sample);
            if (hr == NotAccepting)
            {
                Drain(results);
                hr = ProcessInput(sample);
            }
            Com.Check(hr, "IMFTransform.ProcessInput");
        }
        finally
        {
            Com.Release(sample);
        }
        Drain(results);
        return results;
    }

    public void Dispose()
    {
        if (transform != 0)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Com.VTable(transform)[ProcessMessageSlot])(transform, EndOfStream, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Com.VTable(transform)[ProcessMessageSlot])(transform, EndStreaming, 0);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Logger.Error("Ending the encoder's stream failed", error);
            }
        }
        Com.Release(ref spareOutput);
        Com.Release(ref codecApi);
        Com.Release(ref transform);
    }

    // MARK: - Output

    /// <summary>Collects every access unit the encoder has ready.</summary>
    void Drain(List<EncodedVideoFrame> results)
    {
        for (var attempts = 0; attempts < 64; attempts++)
        {
            nint allocated = 0;
            if (!providesSamples)
            {
                // One sample of our own, emptied and handed in again every time.
                allocated = spareOutput != 0 ? spareOutput : NewEmptySample(outputBufferSize);
                spareOutput = 0;
            }
            var buffer = new MftOutputDataBuffer { StreamId = 0, Sample = allocated };
            uint status = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, MftOutputDataBuffer*, uint*, int>)Com.VTable(transform)[ProcessOutputSlot])(
                transform, 0, 1, &buffer, &status);
            Com.Release(buffer.Events);
            if (hr == NeedMoreInput)
            {
                ReleaseOutput(ref buffer, allocated, keep: true);
                return;
            }
            if (hr == StreamChange)
            {
                ReleaseOutput(ref buffer, allocated, keep: false);
                Renegotiate();
                // The new type carries the parameter sets of the stream from here on.
                sequenceHeader = null;
                continue;
            }
            var kept = false;
            try
            {
                Com.Check(hr, "IMFTransform.ProcessOutput");
                if (buffer.Sample != 0)
                {
                    results.Add(WithParameterSets(ReadSample(buffer.Sample)));
                    kept = buffer.Sample == allocated && Empty(allocated);
                }
            }
            finally
            {
                ReleaseOutput(ref buffer, allocated, kept);
            }
        }
    }

    /// <summary>
    /// Releases what one ProcessOutput call left: a sample the encoder made is ours to release;
    /// our own is kept for the next call when <paramref name="keep"/>, released otherwise.
    /// </summary>
    void ReleaseOutput(ref MftOutputDataBuffer buffer, nint allocated, bool keep)
    {
        if (buffer.Sample != 0 && buffer.Sample != allocated)
        {
            Com.Release(buffer.Sample);
        }
        buffer.Sample = 0;
        if (keep && allocated != 0)
        {
            spareOutput = allocated;
        }
        else
        {
            Com.Release(allocated);
        }
    }

    /// <summary>Sets the length of a sample's buffer back to zero, so the encoder can fill it again.</summary>
    static bool Empty(nint sample)
    {
        nint buffer = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.VTable(sample)[GetBufferByIndexSlot])(sample, 0, &buffer) < 0 || buffer == 0)
        {
            return false;
        }
        try
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, int>)Com.VTable(buffer)[SetCurrentLengthSlot])(buffer, 0) >= 0;
        }
        finally
        {
            Com.Release(buffer);
        }
    }

    /// <summary>
    /// A key frame without its parameter sets gets them from the output type's sequence header:
    /// the MP4 header needs them, and the encoder usually, but not always, repeats them inline.
    /// </summary>
    EncodedVideoFrame WithParameterSets(EncodedVideoFrame frame)
    {
        var units = Core.Video.AvcNalUnits.Split(frame.Data);
        if (!units.Any(unit => Core.Video.AvcNalType.Of(unit.Span) == Core.Video.AvcNalType.IdrSlice)
            || units.Any(unit => Core.Video.AvcNalType.Of(unit.Span) == Core.Video.AvcNalType.SequenceParameterSet))
        {
            return frame;
        }
        sequenceHeader ??= ReadSequenceHeader();
        return sequenceHeader is { Length: > 0 } header ? frame with { Data = [.. header, .. frame.Data] } : frame;
    }

    /// <summary>MF_MT_MPEG_SEQUENCE_HEADER of the current output type (the parameter sets, Annex B); null when there is none.</summary>
    byte[]? ReadSequenceHeader()
    {
        nint type = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.VTable(transform)[GetOutputCurrentTypeSlot])(transform, 0, &type) < 0 || type == 0)
        {
            return null;
        }
        try
        {
            var key = SequenceHeaderKey;
            uint size = 0;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Com.VTable(type)[GetBlobSizeSlot])(type, &key, &size) < 0 || size == 0)
            {
                return null;
            }
            var bytes = new byte[size];
            fixed (byte* data = bytes)
            {
                return ((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, uint*, int>)Com.VTable(type)[GetBlobSlot])(type, &key, data, size, &size) >= 0
                    ? bytes[..(int)size]
                    : null;
            }
        }
        finally
        {
            Com.Release(type);
        }
    }

    /// <summary>The encoder changed its output format (it may, once it knows its parameter sets): take the first it offers.</summary>
    void Renegotiate()
    {
        nint type = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Com.VTable(transform)[GetOutputAvailableTypeSlot])(transform, 0, 0, &type),
            "IMFTransform.GetOutputAvailableType");
        try
        {
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.VTable(transform)[SetOutputTypeSlot])(transform, 0, type, 0),
                "IMFTransform.SetOutputType");
        }
        finally
        {
            Com.Release(type);
        }
    }

    static EncodedVideoFrame ReadSample(nint sample)
    {
        long time = 0;
        ((delegate* unmanaged[Stdcall]<nint, long*, int>)Com.VTable(sample)[GetSampleTimeSlot])(sample, &time);
        nint contiguous = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.VTable(sample)[ConvertToContiguousBufferSlot])(sample, &contiguous),
            "IMFSample.ConvertToContiguousBuffer");
        try
        {
            byte* data = null;
            uint maxLength = 0;
            uint length = 0;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Com.VTable(contiguous)[LockSlot])(contiguous, &data, &maxLength, &length),
                "IMFMediaBuffer.Lock");
            try
            {
                return new EncodedVideoFrame(new ReadOnlySpan<byte>(data, (int)length).ToArray(), time / 10_000_000.0);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)Com.VTable(contiguous)[UnlockSlot])(contiguous);
            }
        }
        finally
        {
            Com.Release(contiguous);
        }
    }

    // MARK: - Input

    int ProcessInput(nint sample) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.VTable(transform)[ProcessInputSlot])(transform, 0, sample, 0);

    void ProcessMessage(uint message) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Com.VTable(transform)[ProcessMessageSlot])(transform, message, 0),
            "IMFTransform.ProcessMessage");

    /// <summary>A sample holding a copy of <paramref name="bytes"/>, shown at <paramref name="time"/> (100 ns units) for one frame.</summary>
    static nint NewSample(ReadOnlySpan<byte> bytes, long time)
    {
        Com.Check(Mfplat.MFCreateMemoryBuffer((uint)bytes.Length, out var buffer), "MFCreateMemoryBuffer");
        nint sample = 0;
        try
        {
            byte* data = null;
            uint maxLength = 0;
            uint current = 0;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Com.VTable(buffer)[LockSlot])(buffer, &data, &maxLength, &current),
                "IMFMediaBuffer.Lock");
            try
            {
                bytes.CopyTo(new Span<byte>(data, (int)maxLength));
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)Com.VTable(buffer)[UnlockSlot])(buffer);
            }
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Com.VTable(buffer)[SetCurrentLengthSlot])(buffer, (uint)bytes.Length),
                "IMFMediaBuffer.SetCurrentLength");
            Com.Check(Mfplat.MFCreateSample(out sample), "MFCreateSample");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.VTable(sample)[AddBufferSlot])(sample, buffer), "IMFSample.AddBuffer");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Com.VTable(sample)[SetSampleTimeSlot])(sample, time), "IMFSample.SetSampleTime");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Com.VTable(sample)[SetSampleDurationSlot])(sample, FrameDurationTicks),
                "IMFSample.SetSampleDuration");
            return sample;
        }
        catch
        {
            Com.Release(sample);
            throw;
        }
        finally
        {
            Com.Release(buffer);
        }
    }

    /// <summary>A sample holding one empty memory buffer of <paramref name="capacity"/> bytes.</summary>
    static nint NewEmptySample(uint capacity)
    {
        Com.Check(Mfplat.MFCreateSample(out var sample), "MFCreateSample");
        nint buffer = 0;
        try
        {
            Com.Check(Mfplat.MFCreateMemoryBuffer(capacity, out buffer), "MFCreateMemoryBuffer");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.VTable(sample)[AddBufferSlot])(sample, buffer), "IMFSample.AddBuffer");
            return sample;
        }
        catch
        {
            Com.Release(sample);
            throw;
        }
        finally
        {
            Com.Release(buffer);
        }
    }

    // MARK: - Types and settings

    nint NewVideoType(Guid subtype)
    {
        Com.Check(Mfplat.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            SetGuid(type, MajorTypeKey, MediaTypeVideo);
            SetGuid(type, SubtypeKey, subtype);
            SetUInt64(type, FrameSizeKey, ((ulong)(uint)width << 32) | (uint)height);
            SetUInt64(type, FrameRateKey, ((ulong)FramesPerSecond << 32) | 1);
            SetUInt64(type, PixelAspectRatioKey, (1UL << 32) | 1);
            SetUInt32(type, InterlaceModeKey, Progressive);
            // BT.709, limited range: what the NV12 conversion produces and the MP4 declares.
            SetUInt32(type, YuvMatrixKey, 1);
            SetUInt32(type, NominalRangeKey, 2);
            SetUInt32(type, PrimariesKey, 2);
            SetUInt32(type, TransferFunctionKey, 5);
            return type;
        }
        catch
        {
            Com.Release(type);
            throw;
        }
    }

    static void SetGuid(nint attributes, Guid key, Guid value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.VTable(attributes)[SetGuidSlot])(attributes, &key, &value), "IMFAttributes.SetGUID");

    static void SetUInt32(nint attributes, Guid key, uint value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.VTable(attributes)[SetUInt32Slot])(attributes, &key, value), "IMFAttributes.SetUINT32");

    static void SetUInt64(nint attributes, Guid key, ulong value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Com.VTable(attributes)[SetUInt64Slot])(attributes, &key, value), "IMFAttributes.SetUINT64");

    /// <summary>Sets an attribute on the transform itself; best effort.</summary>
    void SetTransformAttribute(Guid key, uint value)
    {
        nint attributes = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.VTable(transform)[GetAttributesSlot])(transform, &attributes) < 0 || attributes == 0)
        {
            return;
        }
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.VTable(attributes)[SetUInt32Slot])(attributes, &key, value);
        }
        finally
        {
            Com.Release(attributes);
        }
    }

    /// <summary>An ICodecAPI setting; best effort, since the encoder works without any of them.</summary>
    void SetCodecValue(Guid key, ushort type, uint value)
    {
        if (codecApi == 0)
        {
            return;
        }
        var variant = new Variant { Type = type, Value = (nint)value };
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Com.VTable(codecApi)[CodecSetValueSlot])(codecApi, &key, &variant);
        if (hr < 0)
        {
            Logger.Info($"The encoder refused setting {key} (0x{hr:X8})");
        }
    }

    static void StartMediaFoundation()
    {
        lock (StartupGate)
        {
            if (mediaFoundationStarted)
            {
                return;
            }
            // Kept for the life of the process: the encoder is the only Media Foundation user that starts it.
            Com.Check(Mfplat.MFStartup(Mfplat.MF_VERSION, Mfplat.MFSTARTUP_LITE), "MFStartup");
            mediaFoundationStarted = true;
        }
    }
}
