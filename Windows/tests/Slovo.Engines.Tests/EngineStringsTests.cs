using Slovo.Core.Localization;

namespace Slovo.Engines.Tests;

/// <summary>Every message the engines show exists in both languages, with the Windows overrides applied.</summary>
public class EngineStringsTests
{
    public static readonly TheoryData<string> Keys =
    [
        // Shared with the macOS app.
        "engines.gigaam.missingFiles", "engines.gigaam.damagedFiles", "engines.gigaam.expectedFolder",
        "engines.gigaam.recognizerFailed", "engines.gigaam.streamFailed", "engines.gigaam.noResult",
        "engines.parakeet.notDownloaded",
        "dictation.error.modelMissing", "dictation.error.modelDamaged", "dictation.error.modelDownloading",
        "dictation.error.downloadFailed", "dictation.error.deleteFailed", "dictation.error.deleteWhileDownloading",
        "dictation.error.httpStatus", "dictation.error.extractFailed", "dictation.error.archiveIncomplete",
        "dictation.model.missingFiles", "dictation.model.incompleteFiles",
        "audio.error.readFailed", "audio.error.unreadableWAV",
        "diarization.error.modelMissing", "diarization.error.modelDamaged", "diarization.error.loadFailed",
        "diarization.error.downloadFailed", "diarization.error.filesMissingAfterDownload",
        "diarization.error.deleteWhileDownloading", "diarization.error.unsupportedSystem",
        // Added for Windows.
        "engines.parakeet.damagedFiles", "engines.parakeet.recognizerFailed", "engines.apple.unavailable",
        "engines.download.truncated", "engines.download.stalled", "diarization.error.sherpaRejected",
    ];

    [Theory]
    [MemberData(nameof(Keys))]
    public void TheKeyExistsInBothLanguages(string key)
    {
        foreach (var code in CoreStrings.SupportedCodes)
        {
            var table = CoreStrings.TableFor(code);
            Assert.True(table.Contains(key), $"{key} is missing in {code}");
            Assert.NotEqual(key, table.Get(key));
        }
    }

    [Fact]
    public void WindowsNeverTalksAboutMacOSVersions()
    {
        Assert.DoesNotContain("macOS", CoreStrings.TableFor("en").Get("diarization.error.unsupportedSystem"));
        Assert.DoesNotContain("macOS", CoreStrings.TableFor("ru").Get("diarization.error.unsupportedSystem"));
    }

    [Fact]
    public void ArgumentsFillBothLanguages()
    {
        Assert.Equal("Parakeet v3 model files are damaged or incomplete: a, b. Expected in C:\\M",
            CoreStrings.TableFor("en").Format("engines.parakeet.damagedFiles", "a, b", "C:\\M"));
        Assert.Equal("данные не приходили 60 секунд", CoreStrings.TableFor("ru").Format("engines.download.stalled", 60L));
    }
}
