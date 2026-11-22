using Slovo.Core.Dictation;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Dictation;

/// <summary>
/// The dictation parts of DictationAudioTests.swift: speech detection and the sound cues.
/// WAV files are covered by Audio/WavFileTests and the meter arithmetic by
/// Audio/LevelMeterTests; the recorder and device list are platform code.
/// </summary>
public sealed class DictationAudioTests
{
    static float[] Tone(int count, double rate, double frequency = 220, float amplitude = 0.3f) =>
        Enumerable.Range(0, count).Select(i => (float)Math.Sin(i * 2 * Math.PI * frequency / rate) * amplitude).ToArray();

    [Fact]
    public void SpeechEnergyDecisions()
    {
        Assert.True(SpeechEnergy.IsEffectivelySilent(RecordedAudio.Empty));
        Assert.True(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(Tone(4_600, 16_000))), "shorter than 0.3 s");
        Assert.True(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(new float[16_000])));
        Assert.True(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(Enumerable.Range(0, 16_000).Select(i => i % 2 == 0 ? 0.002f : -0.002f).ToArray())),
                    "room noise");
        Assert.True(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(Enumerable.Repeat(0.1f, 16_000).ToArray())), "a DC offset is not sound");

        var click = new float[16_000];
        for (var index = 8_000; index < 8_200; index++)
        {
            click[index] = index % 2 == 0 ? -0.5f : 0.5f;
        }
        Assert.True(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(click)), "a single click is not speech");

        Assert.False(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(Tone(16_000, 16_000, amplitude: 0.05f))));
        Assert.False(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(Tone(96_000, 48_000), 48_000)));
        Assert.True(SpeechEnergy.IsEffectivelySilent(new RecordedAudio(Tone(16_000, 16_000), 0)), "no sample rate");
    }

    [Fact]
    public void VarianceIgnoresTheMean()
    {
        Assert.Equal(0, SpeechEnergy.VarianceRms([]));
        Assert.Equal(0, SpeechEnergy.VarianceRms([0.3f, 0.3f, 0.3f]), 6);
        Assert.Equal(0.5f, SpeechEnergy.VarianceRms([0.6f, -0.4f, 0.6f, -0.4f]), 5);
    }

    [Fact]
    public void WindowsTablesDoNotSendPeopleToMacSettings()
    {
        foreach (var code in new[] { "en", "ru" })
        {
            var table = CoreStrings.TableFor(code);
            foreach (var key in new[] { "dictation.error.appleAssetsMissing", "dictation.error.appleUnsupported" })
            {
                var text = table.Get(key);
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("macOS", text, StringComparison.Ordinal);
                Assert.DoesNotContain("System Settings", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Системные настройки", text, StringComparison.Ordinal);
            }
        }
    }
}
