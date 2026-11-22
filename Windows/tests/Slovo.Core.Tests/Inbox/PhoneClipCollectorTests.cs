using System.Diagnostics;
using System.Text;
using Slovo.Core.Dictation;
using Slovo.Core.Inbox;
using Slovo.Core.Settings;
using Slovo.Core.Tests.Dictation;
using Slovo.Core.Tests.Meetings;
using CommandResult = Slovo.Core.Inbox.PhoneClipCollector.CommandResult;
using PhoneClip = Slovo.Core.Inbox.PhoneClipCollector.PhoneClip;
using PhoneTimes = Slovo.Core.Inbox.PhoneClipCollector.PhoneTimes;

namespace Slovo.Core.Tests.Inbox;

/// <summary>
/// What <see cref="PhoneClipCollector"/> reads and refuses: the device list, the folder listing
/// that pairs a clip with its sidecar, the ages the phone reports for the clips that came
/// without one, names that must not reach a shell or a Windows folder, where adb is looked for,
/// and the process runner itself (port of InboxPhoneClipsTests.swift). The collecting tests put
/// a stand-in for adb at the end of the cable — a runner the collector is given in place of the
/// process — so what a phone would do (answer, hand over a file, refuse to delete one) is a
/// folder on this computer and code that reads it. The few tests that start real processes go
/// through /bin/sh and are skipped on Windows.
/// </summary>
public sealed class PhoneClipCollectorTests : IDisposable
{
    const string PosixOnly = "Starts /bin/sh and /bin/echo, which Windows does not have; the collecting tests cover the rest there.";
    const string Phone = "TEST-USB-A";

    readonly TemporaryMeetingsFolder folder = new();
    readonly TestMainThread mainThread = new();
    readonly AppSettings settings = new(SettingsStore.InMemory()) { InboxEnabled = true };
    readonly FakeAdb adb;

    public PhoneClipCollectorTests()
    {
        adb = new FakeAdb(Path.Combine(folder.Base, "Phone"));
    }

    public void Dispose() => folder.Dispose();

    Task Run(Func<Task> body) => mainThread.RunAsync(body);

    string InboxFolder => folder.Paths.InboxFolder;

    // MARK: - Reading what adb printed

    [Fact]
    public void OnlyPhonesThatAnsweredAreTakenFromTheDeviceList()
    {
        const string output = "List of devices attached\n"
                              + "TEST-USB-A\tdevice\n"
                              + "emulator-5554\toffline\n"
                              + "TEST-UNAUTHORIZED\tunauthorized\n"
                              + "TEST-USB-B           device product:demo model:Demo_Phone transport_id:3\n\n";
        Assert.Equal(["TEST-USB-A", "TEST-USB-B"], PhoneClipCollector.AuthorizedSerials(output));
        Assert.Empty(PhoneClipCollector.AuthorizedSerials("List of devices attached\n\n"));
        Assert.Empty(PhoneClipCollector.AuthorizedSerials("* daemon started successfully"));
        Assert.Empty(PhoneClipCollector.AuthorizedSerials(""));
        // adb for Windows ends its lines with CRLF.
        Assert.Equal(["TEST-USB-A"], PhoneClipCollector.AuthorizedSerials("List of devices attached\r\nTEST-USB-A\tdevice\r\n\r\n"));
    }

    [Fact]
    public void AListingPairsEachClipWithItsSidecar()
    {
        const string listing = "slovo-20260916-131002.m4a\r\n"
                               + "slovo-20260916-125458.m4a\r\n"
                               + "slovo-20260916-125458.json\r\n"
                               + "stray-metadata.json\r\n"
                               + "notes.txt\r\n";
        Assert.Equal([new PhoneClip("slovo-20260916-125458.m4a", "slovo-20260916-125458.json"),
                      new PhoneClip("slovo-20260916-131002.m4a", null)],
                     PhoneClipCollector.Clips(listing));
        // What `ls` prints when the folder is not there at all.
        Assert.Empty(PhoneClipCollector.Clips("ls: /sdcard/Download/Slovo: No such file or directory"));
        Assert.Empty(PhoneClipCollector.Clips(""));
    }

    [Fact]
    public void NamesThatCouldLeaveTheFolderOrTheQuotesAreRefused()
    {
        foreach (var windows in new[] { false, true })
        {
            Assert.True(PhoneClipCollector.IsSafeName("slovo-20260916-125458.m4a", windows));
            Assert.True(PhoneClipCollector.IsSafeName("Запись 1.m4a", windows));
            foreach (var name in new[] { "", ".", "..", "../escape.m4a", "folder/clip.m4a", "it's here.m4a", "back\\slash.m4a",
                                         "tick`.m4a", "$HOME.m4a", "quote\".m4a", "line\nbreak.m4a", "tab\tstop.m4a" })
            {
                Assert.False(PhoneClipCollector.IsSafeName(name, windows), name);
            }
        }
        Assert.Empty(PhoneClipCollector.Clips("../escape.m4a\nit's here.m4a"));
    }

    [Fact]
    public void TheNameMediaStoreGivesAClipItIsStillWritingIsRefused()
    {
        // MediaStore parks a pending row under `.pending-<n>-<name>`, which ends in .m4a and holds
        // nothing else a name check would object to. Refusing the leading dot is what keeps it
        // out — not `ls` happening to hide dotfiles.
        Assert.False(PhoneClipCollector.IsSafeName(".pending-42-slovo-20260916-125458.m4a"));
        Assert.False(PhoneClipCollector.IsSafeName(".slovo-20260916-125458.json"));
        Assert.Equal([new PhoneClip("slovo.m4a", null)], PhoneClipCollector.Clips(".pending-42-slovo-20260916-125458.m4a\nslovo.m4a"));
    }

    [Fact]
    public void NamesWindowsCannotStoreAreRefusedThere()
    {
        foreach (var name in new[] { "a:b.m4a", "star*.m4a", "what?.m4a", "less<.m4a", "more>.m4a", "pipe|.m4a",
                                     "CON.m4a", "nul.json", "Com1.m4a", "LPT9.tar.m4a", "AUX .m4a", "clip.m4a.", "clip.m4a " })
        {
            Assert.False(PhoneClipCollector.IsSafeName(name, windows: true), name);
        }
        // A Mac stores them as they are, as the Swift app has it.
        Assert.True(PhoneClipCollector.IsSafeName("a:b.m4a", windows: false));
        Assert.True(PhoneClipCollector.IsSafeName("CON.m4a", windows: false));
        // Names that only look like a device are files.
        foreach (var name in new[] { "CONSOLE.m4a", "com10.m4a", "nul-talk.m4a", "Запись 1.m4a" })
        {
            Assert.True(PhoneClipCollector.IsSafeName(name, windows: true), name);
        }
    }

    [Fact]
    public void ANameTooLongToCopyUnderItsHiddenNameIsRefused()
    {
        var longest = new string('a', 255 - PhoneClipCollector.PartialPrefix.Length - 4) + ".m4a";
        Assert.True(PhoneClipCollector.IsSafeName(longest));
        Assert.False(PhoneClipCollector.IsSafeName("a" + longest));
        foreach (var windows in new[] { false, true })
        {
            Assert.True(PhoneClipCollector.IsSafeName(longest, windows));
            Assert.False(PhoneClipCollector.IsSafeName("a" + longest, windows));
        }
    }

    [Fact]
    public void NtfsCountsANameInUtf16UnitsWhereAMacCountsBytes()
    {
        var room = 255 - PhoneClipCollector.PartialPrefix.Length - ".m4a".Length;
        // A Cyrillic letter is one UTF-16 unit and two bytes of UTF-8.
        var cyrillic = new string('я', room) + ".m4a";
        Assert.True(PhoneClipCollector.IsSafeName(cyrillic, windows: true));
        Assert.False(PhoneClipCollector.IsSafeName("я" + cyrillic, windows: true));
        Assert.False(PhoneClipCollector.IsSafeName(cyrillic, windows: false));
        Assert.True(PhoneClipCollector.IsSafeName(new string('я', room / 2) + ".m4a", windows: false));
        // A letter beyond the BMP is two UTF-16 units.
        var face = char.ConvertFromUtf32(0x1F600);
        var faces = string.Concat(Enumerable.Repeat(face, room / 2)) + ".m4a";
        Assert.True(PhoneClipCollector.IsSafeName(faces, windows: true));
        Assert.False(PhoneClipCollector.IsSafeName(face + faces, windows: true));
    }

    [Fact]
    public void TheClockAndTheAgesAreReadFromWhatThePhonePrinted()
    {
        const string output = "1758013200\r\n"
                              + "1758013100 /sdcard/Download/Slovo/slovo-20260916-125458.m4a\r\n"
                              + "1758012000 /sdcard/Download/Slovo/Запись 1.m4a\r\n"
                              + "stat: '/sdcard/Download/Slovo/gone.m4a': No such file or directory\r\n";
        var times = PhoneClipCollector.ParsePhoneTimes(output);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_758_013_200), times.Now);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_758_013_100), times.Modified["slovo-20260916-125458.m4a"]);
        // A name can hold spaces, so the line is split once and the rest is a path.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_758_012_000), times.Modified["Запись 1.m4a"]);
        Assert.False(times.Modified.ContainsKey("gone.m4a"));

        var nothing = PhoneClipCollector.ParsePhoneTimes("");
        Assert.Null(nothing.Now);
        Assert.Empty(nothing.Modified);
        Assert.Null(PhoneClipCollector.ParsePhoneTimes("stat: unknown option -c").Now);
    }

    [Fact]
    public void AClipWithoutASidecarIsTakenOnlyOnceItHasStoppedChanging()
    {
        PhoneClip[] clips = [new("paired.m4a", "paired.json"), new("fresh.m4a", null), new("settled.m4a", null), new("undated.m4a", null)];
        var now = DateTimeOffset.FromUnixTimeSeconds(1_758_013_200);
        var times = new PhoneTimes(now, new Dictionary<string, DateTimeOffset>
        {
            ["fresh.m4a"] = now.AddSeconds(-PhoneClipCollector.SidecarGraceSeconds + 1),
            ["settled.m4a"] = now.AddSeconds(-PhoneClipCollector.SidecarGraceSeconds),
        });
        // A clip that came with its sidecar is finished by the rule the phone follows; one without
        // waits, and one the phone would not date is not guessed at.
        Assert.Equal(["paired.m4a", "settled.m4a"], PhoneClipCollector.Collectable(clips, times).Select(clip => clip.Audio));
        Assert.Equal(["paired.m4a"], PhoneClipCollector.Collectable(clips, PhoneTimes.None).Select(clip => clip.Audio));
    }

    [Fact]
    public void OneClipMissingFromTheAnswerIsNotAPhoneThatCannotBeRead()
    {
        string[] asked = ["settled.m4a", "gone.m4a"];
        var now = DateTimeOffset.FromUnixTimeSeconds(1_758_013_200);
        var dated = new Dictionary<string, DateTimeOffset> { ["settled.m4a"] = now };
        // stat naming a file that has just gone still leaves an answer to compare against.
        Assert.True(PhoneClipCollector.TimesAreReadable(new PhoneTimes(now, dated), asked));
        // A clock with nothing dated, a date for a file with no clock to compare it to, and an
        // answer holding neither: none of them can say how old a clip is.
        Assert.False(PhoneClipCollector.TimesAreReadable(new PhoneTimes(now, new Dictionary<string, DateTimeOffset>()), asked));
        Assert.False(PhoneClipCollector.TimesAreReadable(new PhoneTimes(null, dated), asked));
        Assert.False(PhoneClipCollector.TimesAreReadable(PhoneTimes.None, asked));
    }

    [Fact]
    public void AClipInTheInboxBeingImportedOrAlreadyImportedCountsAsAlreadyHere()
    {
        Assert.False(PhoneClipCollector.IsAlreadyHere("slovo-20260916-125458.m4a", InboxFolder));

        File.WriteAllText(Path.Combine(InboxFolder, "waiting.m4a"), "clip");
        var imported = Path.Combine(InboxFolder, InboxImporter.ImportedFolderName);
        Directory.CreateDirectory(imported);
        File.WriteAllText(Path.Combine(imported, "slovo-20260916-125458.m4a"), "clip");
        File.WriteAllText(Path.Combine(InboxFolder, InboxImporter.ClaimName(Guid.NewGuid(), "claimed.m4a")), "clip");

        Assert.True(PhoneClipCollector.IsAlreadyHere("waiting.m4a", InboxFolder));
        // The importer moved it on; without this the clip would arrive a second time.
        Assert.True(PhoneClipCollector.IsAlreadyHere("slovo-20260916-125458.m4a", InboxFolder));
        // Taken by an import that is still decoding it, under its claim name.
        Assert.True(PhoneClipCollector.IsAlreadyHere("claimed.m4a", InboxFolder));
        Assert.False(PhoneClipCollector.IsAlreadyHere("other.m4a", InboxFolder));
    }

    // MARK: - Finding adb

    [Fact]
    public void OnWindowsAdbComesFromTheVariableThenPathThenTheAndroidSdk()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADB"] = @"D:\tools\adb.exe",
            ["Path"] = @"C:\Windows\system32;""C:\Program Files\platform-tools"";;C:\Android\platform-tools\",
            ["LOCALAPPDATA"] = @"C:\Users\anna\AppData\Local",
        };
        string? Variable(string name) => environment.GetValueOrDefault(name);
        const string chosen = @"D:\tools\adb.exe";
        const string onPath = @"C:\Android\platform-tools\adb.exe";
        const string sdk = @"C:\Users\anna\AppData\Local\Android\Sdk\platform-tools\adb.exe";
        Assert.Equal([chosen, @"C:\Windows\system32\adb.exe", @"C:\Program Files\platform-tools\adb.exe", onPath, sdk],
                     PhoneClipCollector.AdbCandidates(windows: true, Variable));

        string? Find(params string[] present) => PhoneClipCollector.FindAdb(windows: true, Variable, present.ToHashSet().Contains);
        Assert.Equal(chosen, Find(chosen, onPath, sdk));
        // %ADB% naming a file that is not there is passed over.
        Assert.Equal(onPath, Find(onPath, sdk));
        Assert.Equal(sdk, Find(sdk));
        Assert.Null(Find());
        // With nothing set there is nowhere to look, which is an ordinary computer without adb.
        Assert.Null(PhoneClipCollector.FindAdb(windows: true, _ => null, _ => true));
    }

    [Fact]
    public void OnTheMacAdbComesFromAndroidStudioThenHomebrewThenPath()
    {
        var environment = new Dictionary<string, string?> { ["HOME"] = "/Users/anna", ["PATH"] = "/usr/bin::/usr/local/bin/" };
        string? Variable(string name) => environment.GetValueOrDefault(name);
        const string studio = "/Users/anna/Library/Android/sdk/platform-tools/adb";
        const string homebrew = "/opt/homebrew/bin/adb";
        const string onPath = "/usr/local/bin/adb";
        Assert.Equal([studio, homebrew, "/usr/bin/adb", onPath], PhoneClipCollector.AdbCandidates(windows: false, Variable));

        string? Find(params string[] present) => PhoneClipCollector.FindAdb(windows: false, Variable, present.ToHashSet().Contains);
        // A known place wins over PATH: an app started from the Dock has a short one.
        Assert.Equal(studio, Find(onPath, homebrew, studio));
        Assert.Equal(homebrew, Find(onPath, homebrew));
        Assert.Equal(onPath, Find(onPath));
        Assert.Null(Find());
    }

    [Fact]
    public void AdbIsFoundOnThePathWhenTheUsualPlacesHaveNone()
    {
        var windows = OperatingSystem.IsWindows();
        var tools = Path.Combine(folder.Base, "tools");
        Directory.CreateDirectory(tools);
        var executable = Path.Combine(tools, windows ? "adb.exe" : "adb");
        Assert.False(PhoneClipCollector.IsExecutableFile(executable));
        File.WriteAllText(executable, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            // On the Mac a file nobody may run is not adb, whatever its name.
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.False(PhoneClipCollector.IsExecutableFile(executable));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        Assert.True(PhoneClipCollector.IsExecutableFile(executable));
        Assert.False(PhoneClipCollector.IsExecutableFile(tools));

        // Only this test's folder may answer: the computer running it may have adb of its own.
        bool Here(string path) => path.StartsWith(folder.Base, StringComparison.Ordinal) && PhoneClipCollector.IsExecutableFile(path);
        Assert.Equal(executable, PhoneClipCollector.FindAdb(windows, name => name == "PATH" ? tools : null, Here));
        Assert.Null(PhoneClipCollector.FindAdb(windows, name => name == "PATH" ? Path.Combine(tools, "missing") : null, Here));
    }

    // MARK: - Running a command

    [Fact]
    public async Task ACommandThatCannotStartSaysSo()
    {
        var missing = await PhoneClipCollector.RunAsync(Path.Combine(folder.Base, "nowhere", OperatingSystem.IsWindows() ? "adb.exe" : "adb"), ["devices"],
                                                        cancellation: TestContext.Current.CancellationToken);
        Assert.False(missing.Succeeded);
        Assert.Equal("", missing.Output);
    }

    [Fact]
    public async Task RunningACommandReturnsWhatItPrintedAndHowItEnded()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        var listed = await PhoneClipCollector.RunAsync("/bin/echo", ["TEST-USB-A\tdevice"], cancellation: TestContext.Current.CancellationToken);
        Assert.True(listed.Succeeded);
        Assert.Equal(["TEST-USB-A"], PhoneClipCollector.AuthorizedSerials(listed.Output));

        var failed = await PhoneClipCollector.RunAsync("/bin/sh", ["-c", "exit 3"], cancellation: TestContext.Current.CancellationToken);
        Assert.False(failed.Succeeded);
        Assert.Equal(3, failed.Status);
    }

    [Fact]
    public async Task OutputAndErrorsArriveTogetherAndEachArgumentArrivesWhole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        const string script = "echo '* daemon started successfully' >&2; printf 'ADB_MDNS=%s\\n' \"$ADB_MDNS\"; printf '%s\\n' \"$@\"";
        var result = await PhoneClipCollector.RunAsync("/bin/sh", ["-c", script, "sh", "'%Y %n'", "'/sdcard/Download/Slovo/Запись 1.m4a'"],
                                                       cancellation: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("* daemon started successfully", lines);
        // An adb server the command starts does not look for phones on the network.
        Assert.Contains("ADB_MDNS=0", lines);
        // The quotes the phone's shell needs reach adb as they are, and the name as UTF-8.
        Assert.Equal(["'%Y %n'", "'/sdcard/Download/Slovo/Запись 1.m4a'"], lines.Where(line => line.StartsWith('\'')));
    }

    [Fact]
    public async Task ACommandThatDoesNotAnswerIsKilled()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        var clock = Stopwatch.StartNew();
        var stuck = await PhoneClipCollector.RunAsync("/bin/sh", ["-c", "exec sleep 30"], TimeSpan.FromMilliseconds(300),
                                                      TestContext.Current.CancellationToken);
        Assert.False(stuck.Succeeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");

        // Stopping the collector kills the command in flight the same way.
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        clock.Restart();
        var stopped = await PhoneClipCollector.RunAsync("/bin/sh", ["-c", "exec sleep 30"], cancellation: stopping.Token);
        Assert.False(stopped.Succeeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AServerTheCommandLeftRunningDoesNotHoldItsAnswer()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        // As adb can the first time it runs: what it started in the background keeps its output
        // open after the command itself has ended.
        var clock = Stopwatch.StartNew();
        var result = await PhoneClipCollector.RunAsync("/bin/sh", ["-c", "sleep 15 & echo started"],
                                                       cancellation: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal("started\n", result.Output);
        Assert.True(clock.Elapsed < PhoneClipCollector.OutputDrainTime + TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    // MARK: - Collecting over the cable

    [Fact]
    public Task EveryConnectedPhoneIsCollectedAndAClipMidPublishIsLeftBehind() => Run(async () =>
    {
        adb.Connect(["TEST-USB-A", "TEST-USB-B"], unauthorized: ["TEST-UNAUTHORIZED"]);
        adb.Put("slovo-20260916-125458.m4a", "TEST-USB-A", age: 4);
        adb.Put("slovo-20260916-125458.json", "TEST-USB-A", age: 4);
        // Audio published seconds ago with no sidecar beside it: the phone may still be writing
        // the moments that belong to it.
        adb.Put("slovo-20260916-131002.m4a", "TEST-USB-A", age: 4);
        // Audio some other app left in the folder long ago, which is nobody's meeting.
        adb.Put("voice-message.m4a", "TEST-USB-A", age: 3_600);
        adb.Put("slovo-20260916-140000.m4a", "TEST-USB-B", age: 30);
        adb.Put("slovo-20260916-140000.json", "TEST-USB-B", age: 30);

        await MakeCollector().CollectAsync();

        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a",
                      "slovo-20260916-140000.json", "slovo-20260916-140000.m4a",
                      "voice-message.m4a"], Inbox());
        Assert.Equal(["slovo-20260916-131002.m4a"], adb.OnPhone("TEST-USB-A"));
        // The second phone was collected too, not left waiting for the first to be unplugged.
        Assert.Empty(adb.OnPhone("TEST-USB-B"));
        // Nothing was asked of the phone whose owner has not allowed USB debugging.
        Assert.DoesNotContain(adb.Commands, command => command.Contains("TEST-UNAUTHORIZED"));

        // Once the grace period has passed the clip is taken as the plain note it looks like.
        adb.SetAge("slovo-20260916-131002.m4a", "TEST-USB-A", PhoneClipCollector.SidecarGraceSeconds + 5);
        await MakeCollector().CollectAsync();
        Assert.Contains("slovo-20260916-131002.m4a", Inbox());
        Assert.Empty(adb.OnPhone("TEST-USB-A"));
        Assert.Empty(adb.Unexpected);
    });

    [Fact]
    public Task NothingIsDeletedFromThePhoneUntilBothFilesHaveArrived() => Run(async () =>
    {
        adb.Connect([Phone]);
        adb.Put("slovo-20260916-125458.m4a", Phone, age: 4);
        adb.Put("slovo-20260916-125458.json", Phone, age: 4);
        adb.RefusePull.Add("slovo-20260916-125458.json");

        await MakeCollector().CollectAsync();

        // The sidecar could not be copied, so the audio was never even asked for.
        Assert.Empty(Inbox());
        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], adb.OnPhone(Phone));
        Assert.Equal(["slovo-20260916-125458.json"], adb.Pulls);

        // The other way round: the sidecar arrives and its audio does not. It leaves the inbox
        // again rather than waiting there for audio that is still on the phone.
        adb.RefusePull.Clear();
        adb.RefusePull.Add("slovo-20260916-125458.m4a");
        await MakeCollector().CollectAsync();
        Assert.Empty(Inbox());
        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], adb.OnPhone(Phone));
        Assert.Empty(adb.Deleted);
    });

    [Fact]
    public Task APullThatFailsHalfWayOrBringsNothingDoesNotCount() => Run(async () =>
    {
        adb.Connect([Phone]);
        adb.Put("cut-off.m4a", Phone, age: 3_600);
        adb.Put("empty.m4a", Phone, age: 3_600);
        // adb copied part of the file and then said it failed; adb said it worked and wrote nothing.
        adb.FailAfterCopy.Add("cut-off.m4a");
        adb.EmptyPull.Add("empty.m4a");

        await MakeCollector().CollectAsync();

        // Nothing arrived, not even under the hidden name, and the phone keeps both.
        Assert.Empty(Inbox());
        Assert.Equal(["cut-off.m4a", "empty.m4a"], adb.OnPhone(Phone));
        Assert.Empty(adb.Deleted);
    });

    [Fact]
    public Task AClipThePhoneRefusedToDeleteDoesNotArriveTwice() => Run(async () =>
    {
        adb.Connect([Phone]);
        adb.Put("slovo-20260916-125458.m4a", Phone, age: 4);
        adb.Put("slovo-20260916-125458.json", Phone, age: 4);
        adb.RefuseDelete.Add("slovo-20260916-125458.m4a");

        await MakeCollector().CollectAsync();

        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], Inbox());
        // The audio would not go, so the sidecar stays with it: while the pair is whole, the next
        // pass sees a clip with its moments rather than audio waiting out a grace period.
        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], adb.OnPhone(Phone));

        // The importer has since turned it into a meeting and moved both files on.
        var imported = Path.Combine(InboxFolder, InboxImporter.ImportedFolderName);
        Directory.CreateDirectory(imported);
        foreach (var name in Inbox())
        {
            File.Move(Path.Combine(InboxFolder, name), Path.Combine(imported, name));
        }

        await MakeCollector().CollectAsync();

        // One clip, one meeting: the copy still on the phone is recognised as already here.
        Assert.Empty(Inbox());
        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], adb.OnPhone(Phone));
        Assert.Equal(2, adb.PullTargets.Count);
    });

    /// <summary>
    /// adb writes under a hidden name the importer never takes, and the file gets its own name
    /// only once it arrived whole, so a pull that stalls is never imported half copied.
    /// </summary>
    [Fact]
    public Task AClipIsCopiedUnderAHiddenNameAndRenamedOnceWhole() => Run(async () =>
    {
        adb.Connect([Phone]);
        adb.Put("slovo-20260916-125458.m4a", Phone, age: 4);
        adb.Put("slovo-20260916-125458.json", Phone, age: 4);
        // A sidecar an earlier pass left behind is replaced rather than blocking the rename.
        File.WriteAllText(Path.Combine(InboxFolder, "slovo-20260916-125458.json"), "stale");

        await MakeCollector().CollectAsync();

        Assert.Equal([PhoneClipCollector.PartialName("slovo-20260916-125458.json"),
                      PhoneClipCollector.PartialName("slovo-20260916-125458.m4a")], adb.PullTargets);
        Assert.All(adb.PullTargets, target => Assert.StartsWith(".", target, StringComparison.Ordinal));
        Assert.Equal(["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], Inbox());
        Assert.Equal("clip", File.ReadAllText(Path.Combine(InboxFolder, "slovo-20260916-125458.json")));
        // The audio leaves the phone first, so a delete that fails in between leaves a whole pair.
        Assert.Equal(["slovo-20260916-125458.m4a", "slovo-20260916-125458.json"], adb.Deleted);
        Assert.Empty(adb.Unexpected);
    });

    [Fact]
    public Task APhoneThatCannotDateItsClipsIsSaidOnceAndNotEveryPoll() => Run(async () =>
    {
        adb.Connect([Phone]);
        // Old enough to be taken, if only the phone would say how old it is.
        adb.Put("voice-message.m4a", Phone, age: 3_600);
        adb.RefuseTimes = true;
        var collector = MakeCollector();

        await collector.CollectAsync();

        // The clip stays where it is, and the reason is now in the log rather than nowhere.
        Assert.Empty(Inbox());
        Assert.Equal([Phone], collector.SaidTimesAreUnreadable);

        await collector.CollectAsync();
        Assert.Equal([Phone], collector.SaidTimesAreUnreadable);
        Assert.Empty(Inbox());

        // A phone that answers again is forgotten, so a cable that fails later still says so.
        adb.RefuseTimes = false;
        await collector.CollectAsync();
        Assert.Empty(collector.SaidTimesAreUnreadable);
        Assert.Equal(["voice-message.m4a"], Inbox());
    });

    [Fact]
    public Task WithTheInboxOffOrWithoutAdbNothingIsAsked() => Run(async () =>
    {
        adb.Connect([Phone]);
        adb.Put("slovo.m4a", Phone, age: 4);
        adb.Put("slovo.json", Phone, age: 4);
        string? installed = null;
        var lookedForAdb = 0;
        var collector = MakeCollector(findAdb: () =>
        {
            lookedForAdb++;
            return installed;
        });

        settings.InboxEnabled = false;
        await collector.CollectAsync();
        // The switch is read first, on every pass.
        Assert.Equal(0, lookedForAdb);

        settings.InboxEnabled = true;
        await collector.CollectAsync();
        await collector.CollectAsync();
        Assert.Equal(2, lookedForAdb);
        Assert.True(collector.SaidAdbIsMissing);
        Assert.Empty(adb.Commands);
        Assert.Empty(Inbox());

        // adb installed while the app runs is found on the next pass.
        installed = FakeAdb.Executable;
        await collector.CollectAsync();
        Assert.False(collector.SaidAdbIsMissing);
        Assert.Equal(["slovo.json", "slovo.m4a"], Inbox());
    });

    // MARK: - Polling

    [Fact]
    public Task StartCollectsAtOnceThenAfterEachPassUntilStopped() => Run(async () =>
    {
        adb.Connect([Phone]);
        var clock = new ManualClock();
        var collector = MakeCollector(scheduler: clock);

        collector.Start();
        Assert.True(collector.IsRunning);
        await WaitUntil(() => clock.PendingTimers == 1);
        Assert.Equal(1, adb.DeviceListings);

        adb.Put("slovo.m4a", Phone, age: 4);
        adb.Put("slovo.json", Phone, age: 4);
        clock.Advance(PhoneClipCollector.PollSeconds - 1);
        await Task.Delay(50);
        Assert.Equal(1, adb.DeviceListings);
        clock.Advance(1);
        await WaitUntil(() => clock.PendingTimers == 1 && Inbox().Count == 2);
        Assert.Equal(["slovo.json", "slovo.m4a"], Inbox());

        // The switch is read on every pass.
        settings.InboxEnabled = false;
        clock.Advance(PhoneClipCollector.PollSeconds);
        await WaitUntil(() => clock.PendingTimers == 1);
        Assert.Equal(2, adb.DeviceListings);

        collector.Stop();
        Assert.False(collector.IsRunning);
        Assert.Equal(0, clock.PendingTimers);
        settings.InboxEnabled = true;
        clock.Advance(60);
        await Task.Delay(100);
        Assert.Equal(2, adb.DeviceListings);
    });

    [Fact]
    public Task StoppingKillsTheCommandInFlight() => Run(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawStop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = MakeCollector(runner: (_, _, cancellation) =>
        {
            started.TrySetResult();
            sawStop.TrySetResult(cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            return CommandResult.NotRun;
        });

        collector.Start();
        await started.Task;
        collector.Dispose();

        Assert.True(await sawStop.Task);
        Assert.False(collector.IsRunning);
    });

    [Fact]
    public Task CommandsRunOneAtATimeWhoeverSendsThem() => Run(async () =>
    {
        var active = 0;
        var mostActive = 0;
        CommandResult Slow(string executable, IReadOnlyList<string> arguments, CancellationToken cancellation)
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref mostActive, now);
            Thread.Sleep(20);
            Interlocked.Decrement(ref active);
            return arguments is ["devices"] ? new CommandResult(0, $"List of devices attached\n{Phone}\tdevice\n") : new CommandResult(0, "");
        }

        await Task.WhenAll(MakeCollector(runner: Slow).CollectAsync(), MakeCollector(runner: Slow).CollectAsync(),
                           MakeCollector(runner: Slow).CollectAsync());

        Assert.Equal(1, Volatile.Read(ref mostActive));
    });

    // MARK: - Helpers

    PhoneClipCollector MakeCollector(Func<string?>? findAdb = null, IScheduler? scheduler = null,
                                     PhoneClipCollector.CommandRunner? runner = null) =>
        new(folder.Paths, settings, mainThread, scheduler ?? new ManualClock(), findAdb ?? (() => FakeAdb.Executable), runner ?? adb.Run);

    /// <summary>Every file in the inbox, hidden ones included, so a partial copy left behind shows.</summary>
    List<string> Inbox() =>
        Directory.GetFiles(InboxFolder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToList();

    static void InterlockedMax(ref int target, int value)
    {
        for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Stands in for adb and the phones on its cable. The folder on each phone is a folder on
    /// this computer, and each command the collector sends is answered the way a phone answers
    /// it — including stripping the quotes the phone's own shell would strip, since adb joins the
    /// arguments of <c>shell</c> into one line without quoting anything itself. Anything the
    /// collector should never send fails and is kept in <see cref="Unexpected"/>.
    /// </summary>
    sealed class FakeAdb(string root)
    {
        public const string Executable = "/fake/platform-tools/adb";
        const string Folder = PhoneClipCollector.PhoneFolder;

        readonly Lock gate = new();
        readonly List<string> devices = [];
        readonly List<IReadOnlyList<string>> commands = [];
        readonly List<string> pulls = [];
        readonly List<string> pullTargets = [];
        readonly List<string> deleted = [];
        readonly List<string> unexpected = [];

        /// <summary>Files the phone hands over no more, the way a cable pulled at the wrong moment would.</summary>
        public HashSet<string> RefusePull { get; } = [];
        /// <summary>Files the phone deletes no more, the way a read-only volume would.</summary>
        public HashSet<string> RefuseDelete { get; } = [];
        /// <summary>Files adb copies part of and then reports as failed.</summary>
        public HashSet<string> FailAfterCopy { get; } = [];
        /// <summary>Files adb reports as copied while writing nothing.</summary>
        public HashSet<string> EmptyPull { get; } = [];
        /// <summary>A phone whose <c>stat</c> does not take the flags the collector sends: it answers with its own clock and says nothing about any file.</summary>
        public bool RefuseTimes { get; set; }

        public IReadOnlyList<IReadOnlyList<string>> Commands => Locked(() => commands.ToList());
        public int DeviceListings => Locked(() => commands.Count(command => command is ["devices"]));
        /// <summary>The files pulls asked the phone for, in order, whether or not they were handed over.</summary>
        public IReadOnlyList<string> Pulls => Locked(() => pulls.ToList());
        /// <summary>The name of the local file each pull wrote into, in order.</summary>
        public IReadOnlyList<string> PullTargets => Locked(() => pullTargets.ToList());
        /// <summary>What the phone deleted, in order.</summary>
        public IReadOnlyList<string> Deleted => Locked(() => deleted.ToList());
        public IReadOnlyList<string> Unexpected => Locked(() => unexpected.ToList());

        /// <summary>The phones adb reports, each with a folder of its own.</summary>
        public void Connect(IEnumerable<string> serials, IEnumerable<string>? unauthorized = null)
        {
            lock (gate)
            {
                devices.Clear();
                foreach (var serial in serials)
                {
                    devices.Add($"{serial}\tdevice");
                    Directory.CreateDirectory(Path.Combine(root, serial));
                }
                devices.AddRange((unauthorized ?? []).Select(serial => $"{serial}\tunauthorized"));
            }
        }

        /// <summary>A file in a phone's folder, as old as the phone would say it is.</summary>
        public void Put(string name, string serial, double age)
        {
            File.WriteAllText(Path.Combine(root, serial, name), "clip");
            SetAge(name, serial, age);
        }

        public void SetAge(string name, string serial, double age) =>
            File.SetLastWriteTimeUtc(Path.Combine(root, serial, name), DateTime.UtcNow.AddSeconds(-age));

        public IReadOnlyList<string> OnPhone(string serial) =>
            Directory.GetFiles(Path.Combine(root, serial)).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToList();

        public CommandResult Run(string executable, IReadOnlyList<string> arguments, CancellationToken cancellation)
        {
            lock (gate)
            {
                commands.Add(arguments.ToList());
            }
            if (executable != Executable)
            {
                return Refuse(arguments);
            }
            return arguments switch
            {
                ["devices"] => new CommandResult(0, Locked(() => "List of devices attached\n" + string.Concat(devices.Select(line => line + "\n")) + "\n")),
                ["-s", var serial, "shell", "ls", "-1", Folder] => List(serial),
                ["-s", var serial, "shell", "date", "+%s", ";", "stat", "-c", "'%Y %n'", ..] => Times(serial, arguments.Skip(9)),
                ["-s", var serial, "shell", "rm", "-f", var path] => Delete(serial, path, arguments),
                ["-s", var serial, "pull", var remote, var local] => Pull(serial, remote, local, arguments),
                _ => Refuse(arguments),
            };
        }

        CommandResult List(string serial)
        {
            var phone = Path.Combine(root, serial);
            if (!Directory.Exists(phone))
            {
                return new CommandResult(1, $"ls: {Folder}: No such file or directory\n");
            }
            return new CommandResult(0, string.Concat(OnPhone(serial).Select(name => name + "\n")));
        }

        CommandResult Times(string serial, IEnumerable<string> quotedPaths)
        {
            var output = new StringBuilder();
            output.Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Append('\n');
            if (RefuseTimes)
            {
                output.Append("stat: unknown option -- 'c'\n");
                return new CommandResult(1, output.ToString());
            }
            foreach (var quoted in quotedPaths)
            {
                if (NameIn(Unquoted(quoted)) is not { } name)
                {
                    return Refuse([quoted]);
                }
                var file = Path.Combine(root, serial, name);
                if (!File.Exists(file))
                {
                    output.Append($"stat: '{Folder}/{name}': No such file or directory\n");
                    continue;
                }
                output.Append(new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds()).Append($" {Folder}/{name}\n");
            }
            return new CommandResult(0, output.ToString());
        }

        CommandResult Delete(string serial, string quoted, IReadOnlyList<string> arguments)
        {
            if (NameIn(Unquoted(quoted)) is not { } name)
            {
                return Refuse(arguments);
            }
            if (RefuseDelete.Contains(name))
            {
                return new CommandResult(1, $"rm: {Folder}/{name}: Read-only file system\n");
            }
            File.Delete(Path.Combine(root, serial, name));
            lock (gate)
            {
                deleted.Add(name);
            }
            return new CommandResult(0, "");
        }

        /// <summary>A pull names the file on the phone as it is: no shell reads it, so it carries no quotes.</summary>
        CommandResult Pull(string serial, string remote, string local, IReadOnlyList<string> arguments)
        {
            if (NameIn(remote) is not { } name)
            {
                return Refuse(arguments);
            }
            lock (gate)
            {
                pulls.Add(name);
            }
            var file = Path.Combine(root, serial, name);
            if (!File.Exists(file))
            {
                return new CommandResult(1, $"adb: error: failed to stat remote object '{remote}': No such file or directory\n");
            }
            if (RefusePull.Contains(name))
            {
                return new CommandResult(1, "adb: error: failed to copy\n");
            }
            lock (gate)
            {
                pullTargets.Add(Path.GetFileName(local));
            }
            if (EmptyPull.Contains(name))
            {
                File.WriteAllBytes(local, []);
                return new CommandResult(0, $"{remote}: 1 file pulled, 0 skipped.\n");
            }
            if (FailAfterCopy.Contains(name))
            {
                File.WriteAllText(local, "cl");
                return new CommandResult(1, "adb: error: failed to read copy response: Connection reset by peer\n");
            }
            File.Copy(file, local, overwrite: true);
            return new CommandResult(0, $"{remote}: 1 file pulled, 0 skipped.\n");
        }

        CommandResult Refuse(IReadOnlyList<string> arguments)
        {
            lock (gate)
            {
                unexpected.Add(string.Join(' ', arguments));
            }
            return new CommandResult(1, "adb: usage: unknown command\n");
        }

        /// <summary>What the phone's shell makes of a path in single quotes; null for one without them.</summary>
        static string? Unquoted(string word) =>
            word.Length >= 2 && word[0] == '\'' && word[^1] == '\'' ? word[1..^1] : null;

        /// <summary>The name of a file in the phone's folder; null for any other path.</summary>
        static string? NameIn(string? path) =>
            path is not null && path.StartsWith(Folder + "/", StringComparison.Ordinal) && path.IndexOf('/', Folder.Length + 1) < 0
                ? path[(Folder.Length + 1)..]
                : null;

        T Locked<T>(Func<T> read)
        {
            lock (gate)
            {
                return read();
            }
        }
    }
}
