import Foundation
import XCTest
@testable import SlovoCore

/// What `PhoneClipCollector` reads and refuses: the device list, the folder listing that
/// pairs a clip with its sidecar, the ages the phone reports for the clips that came
/// without one, names that must not reach a shell, where adb is looked for, and the process
/// runner itself. The collecting tests put a stand-in for adb at the end of the cable, so
/// what a phone would do — answer, hand over a file, refuse to delete one — is a folder on
/// this Mac and a script that reads it.
@MainActor
final class InboxPhoneClipsTests: XCTestCase {
    private var temporary: URL!
    private var directories: AppDirectories!
    private var settings: AppSettings!
    private var phone: URL!
    private var suiteName = ""
    private var defaults: UserDefaults!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoPhoneTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
        suiteName = "slovo.tests.phone.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)
        settings = AppSettings(defaults: defaults)
        settings.inboxEnabled = true
        phone = temporary.appendingPathComponent("Phone", isDirectory: true)
        try FileManager.default.createDirectory(at: phone, withIntermediateDirectories: true)
        try writeFakeAdb()
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Reading what adb printed

    func testOnlyPhonesThatAnsweredAreTakenFromTheDeviceList() {
        let output = """
        List of devices attached
        TEST-USB-A\tdevice
        emulator-5554\toffline
        TEST-UNAUTHORIZED\tunauthorized
        TEST-USB-B           device product:demo model:Demo_Phone transport_id:3

        """
        XCTAssertEqual(PhoneClipCollector.authorizedSerials(in: output), ["TEST-USB-A", "TEST-USB-B"])
        XCTAssertEqual(PhoneClipCollector.authorizedSerials(in: "List of devices attached\n\n"), [])
        XCTAssertEqual(PhoneClipCollector.authorizedSerials(in: "* daemon started successfully"), [])
        XCTAssertEqual(PhoneClipCollector.authorizedSerials(in: ""), [])
    }

    func testAListingPairsEachClipWithItsSidecar() {
        let listing = """
        slovo-20260916-131002.m4a\r
        slovo-20260916-125458.m4a\r
        slovo-20260916-125458.json\r
        stray-metadata.json\r
        notes.txt\r
        """
        XCTAssertEqual(PhoneClipCollector.clips(in: listing), [
            PhoneClipCollector.PhoneClip(audio: "slovo-20260916-125458.m4a", sidecar: "slovo-20260916-125458.json"),
            PhoneClipCollector.PhoneClip(audio: "slovo-20260916-131002.m4a", sidecar: nil)
        ])
        // What `ls` prints when the folder is not there at all.
        XCTAssertEqual(PhoneClipCollector.clips(in: "ls: /sdcard/Download/Slovo: No such file or directory"), [])
        XCTAssertEqual(PhoneClipCollector.clips(in: ""), [])
    }

    func testNamesThatCouldLeaveTheFolderOrTheQuotesAreRefused() {
        XCTAssertTrue(PhoneClipCollector.isSafeName("slovo-20260916-125458.m4a"))
        XCTAssertTrue(PhoneClipCollector.isSafeName("Запись 1.m4a"))
        for name in ["", ".", "..", "../escape.m4a", "folder/clip.m4a", "it's here.m4a", "back\\slash.m4a",
                     "tick`.m4a", "$HOME.m4a", "quote\".m4a", "line\nbreak.m4a"] {
            XCTAssertFalse(PhoneClipCollector.isSafeName(name), name)
        }
        XCTAssertEqual(PhoneClipCollector.clips(in: "../escape.m4a\nit's here.m4a"), [])
    }

    func testTheNameMediaStoreGivesAClipItIsStillWritingIsRefused() {
        // MediaStore parks a pending row under `.pending-<n>-<name>`, which ends in .m4a and
        // holds nothing else a name check would object to. Refusing the leading dot is what
        // keeps it out — not `ls` happening to hide dotfiles.
        XCTAssertFalse(PhoneClipCollector.isSafeName(".pending-42-slovo-20260916-125458.m4a"))
        XCTAssertFalse(PhoneClipCollector.isSafeName(".slovo-20260916-125458.json"))
        XCTAssertEqual(PhoneClipCollector.clips(in: ".pending-42-slovo-20260916-125458.m4a\nslovo.m4a"),
                       [PhoneClipCollector.PhoneClip(audio: "slovo.m4a", sidecar: nil)])
    }

    func testTheClockAndTheAgesAreReadFromWhatThePhonePrinted() {
        let output = """
        1758013200\r
        1758013100 /sdcard/Download/Slovo/slovo-20260916-125458.m4a\r
        1758012000 /sdcard/Download/Slovo/Запись 1.m4a\r
        stat: '/sdcard/Download/Slovo/gone.m4a': No such file or directory\r
        """
        let times = PhoneClipCollector.phoneTimes(in: output)
        XCTAssertEqual(times.now, Date(timeIntervalSince1970: 1_758_013_200))
        XCTAssertEqual(times.modified["slovo-20260916-125458.m4a"], Date(timeIntervalSince1970: 1_758_013_100))
        // A name can hold spaces, so the line is split once and the rest is a path.
        XCTAssertEqual(times.modified["Запись 1.m4a"], Date(timeIntervalSince1970: 1_758_012_000))
        XCTAssertNil(times.modified["gone.m4a"])

        XCTAssertEqual(PhoneClipCollector.phoneTimes(in: ""), PhoneClipCollector.PhoneTimes())
        XCTAssertNil(PhoneClipCollector.phoneTimes(in: "stat: unknown option -c").now)
    }

    func testAClipWithoutASidecarIsTakenOnlyOnceItHasStoppedChanging() {
        let clips = [PhoneClipCollector.PhoneClip(audio: "paired.m4a", sidecar: "paired.json"),
                     PhoneClipCollector.PhoneClip(audio: "fresh.m4a", sidecar: nil),
                     PhoneClipCollector.PhoneClip(audio: "settled.m4a", sidecar: nil),
                     PhoneClipCollector.PhoneClip(audio: "undated.m4a", sidecar: nil)]
        let now = Date(timeIntervalSince1970: 1_758_013_200)
        let times = PhoneClipCollector.PhoneTimes(now: now, modified: [
            "fresh.m4a": now.addingTimeInterval(-PhoneClipCollector.sidecarGraceSeconds + 1),
            "settled.m4a": now.addingTimeInterval(-PhoneClipCollector.sidecarGraceSeconds)
        ])
        // A clip that came with its sidecar is finished by the rule the phone follows; one
        // without waits, and one the phone would not date is not guessed at.
        XCTAssertEqual(PhoneClipCollector.collectable(clips, times: times).map(\.audio),
                       ["paired.m4a", "settled.m4a"])
        XCTAssertEqual(PhoneClipCollector.collectable(clips, times: PhoneClipCollector.PhoneTimes()).map(\.audio),
                       ["paired.m4a"])
    }

    func testOneClipMissingFromTheAnswerIsNotAPhoneThatCannotBeRead() {
        let asked = ["settled.m4a", "gone.m4a"]
        let now = Date(timeIntervalSince1970: 1_758_013_200)
        // stat naming a file that has just gone still leaves an answer to compare against.
        XCTAssertTrue(PhoneClipCollector.timesAreReadable(
            PhoneClipCollector.PhoneTimes(now: now, modified: ["settled.m4a": now]), asked: asked))
        // A clock with nothing dated, a date for a file with no clock to compare it to, and
        // an answer holding neither: none of them can say how old a clip is.
        XCTAssertFalse(PhoneClipCollector.timesAreReadable(PhoneClipCollector.PhoneTimes(now: now), asked: asked))
        XCTAssertFalse(PhoneClipCollector.timesAreReadable(
            PhoneClipCollector.PhoneTimes(now: nil, modified: ["settled.m4a": now]), asked: asked))
        XCTAssertFalse(PhoneClipCollector.timesAreReadable(PhoneClipCollector.PhoneTimes(), asked: asked))
    }

    func testAPhoneThatCannotDateItsClipsIsSaidOnceAndNotEveryPoll() async throws {
        try connect(["TEST-USB-A"])
        // Old enough to be taken, if only the phone would say how old it is.
        try put("voice-message.m4a", on: "TEST-USB-A", age: 3_600)
        try refuseTimes(true)
        let collector = makeCollector()

        await collector.collect()

        // The clip stays where it is, and the reason is now in the log rather than nowhere.
        XCTAssertEqual(inbox(), [])
        XCTAssertEqual(collector.saidTimesAreUnreadable, ["TEST-USB-A"])

        await collector.collect()
        XCTAssertEqual(collector.saidTimesAreUnreadable, ["TEST-USB-A"], "said once, not once per poll")
        XCTAssertEqual(inbox(), [])

        // A phone that answers again is forgotten, so a cable that fails later still says so.
        try refuseTimes(false)
        await collector.collect()
        XCTAssertEqual(collector.saidTimesAreUnreadable, [])
        XCTAssertEqual(inbox(), ["voice-message.m4a"])
    }

    func testAClipInTheInboxOrAlreadyImportedCountsAsAlreadyHere() throws {
        let inbox = directories.inboxFolder
        XCTAssertFalse(PhoneClipCollector.isAlreadyHere("slovo-20260916-125458.m4a", inbox: inbox))

        try Data("clip".utf8).write(to: inbox.appendingPathComponent("waiting.m4a"))
        let imported = inbox.appendingPathComponent(InboxImporter.importedFolderName, isDirectory: true)
        try FileManager.default.createDirectory(at: imported, withIntermediateDirectories: true)
        try Data("clip".utf8).write(to: imported.appendingPathComponent("slovo-20260916-125458.m4a"))

        XCTAssertTrue(PhoneClipCollector.isAlreadyHere("waiting.m4a", inbox: inbox))
        // The importer moved it on; without this the clip would arrive a second time.
        XCTAssertTrue(PhoneClipCollector.isAlreadyHere("slovo-20260916-125458.m4a", inbox: inbox))
    }

    func testAdbIsFoundOnThePathWhenTheUsualPlacesHaveNone() throws {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoAdbTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let adb = folder.appendingPathComponent("adb")
        try Data("#!/bin/sh\n".utf8).write(to: adb)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: adb.path)

        XCTAssertEqual(PhoneClipCollector.adbURL(defaultPaths: [], environment: ["PATH": folder.path])?.path, adb.path)
        // A known place wins over PATH, and nothing at all is the ordinary answer on a Mac
        // that never installed adb.
        XCTAssertEqual(PhoneClipCollector.adbURL(defaultPaths: [adb.path], environment: [:])?.path, adb.path)
        XCTAssertNil(PhoneClipCollector.adbURL(defaultPaths: ["/nowhere/adb"], environment: ["PATH": ""]))
        XCTAssertNil(PhoneClipCollector.adbURL(defaultPaths: [], environment: ["PATH": folder.path + "/missing"]))
    }

    func testRunningACommandReturnsWhatItPrintedAndSaysWhenItCouldNotStart() async {
        let listed = await PhoneClipCollector.run(URL(fileURLWithPath: "/bin/echo"), ["TEST-USB-A\tdevice"])
        XCTAssertTrue(listed.succeeded)
        XCTAssertEqual(PhoneClipCollector.authorizedSerials(in: listed.output), ["TEST-USB-A"])

        let missing = await PhoneClipCollector.run(URL(fileURLWithPath: "/nowhere/adb"), ["devices"])
        XCTAssertFalse(missing.succeeded)
        XCTAssertEqual(missing.output, "")

        let failed = await PhoneClipCollector.run(URL(fileURLWithPath: "/bin/sh"), ["-c", "exit 3"])
        XCTAssertFalse(failed.succeeded)
    }

    // MARK: - Collecting over the cable

    func testEveryConnectedPhoneIsCollectedAndAClipMidPublishIsLeftBehind() async throws {
        try connect(["TEST-USB-A", "TEST-USB-B"], unauthorized: ["TEST-UNAUTHORIZED"])
        try put("slovo-20260916-125458.m4a", on: "TEST-USB-A", age: 4)
        try put("slovo-20260916-125458.json", on: "TEST-USB-A", age: 4)
        // Audio published seconds ago with no sidecar beside it: the phone may still be
        // writing the moments that belong to it.
        try put("slovo-20260916-131002.m4a", on: "TEST-USB-A", age: 4)
        // Audio some other app left in the folder long ago, which is nobody's meeting.
        try put("voice-message.m4a", on: "TEST-USB-A", age: 3_600)
        try put("slovo-20260916-140000.m4a", on: "TEST-USB-B", age: 30)
        try put("slovo-20260916-140000.json", on: "TEST-USB-B", age: 30)

        await makeCollector().collect()

        XCTAssertEqual(inbox(), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a",
                                 "slovo-20260916-140000.json", "slovo-20260916-140000.m4a",
                                 "voice-message.m4a"])
        XCTAssertEqual(onPhone("TEST-USB-A"), ["slovo-20260916-131002.m4a"])
        // The second phone was collected too, not left waiting for the first to be unplugged.
        XCTAssertEqual(onPhone("TEST-USB-B"), [])

        // Once the grace period has passed the clip is taken as the plain note it looks like.
        try setAge(of: "slovo-20260916-131002.m4a", on: "TEST-USB-A",
                   to: PhoneClipCollector.sidecarGraceSeconds + 5)
        await makeCollector().collect()
        XCTAssertTrue(inbox().contains("slovo-20260916-131002.m4a"))
        XCTAssertEqual(onPhone("TEST-USB-A"), [])
    }

    func testNothingIsDeletedFromThePhoneUntilBothFilesHaveArrived() async throws {
        try connect(["TEST-USB-A"])
        try put("slovo-20260916-125458.m4a", on: "TEST-USB-A", age: 4)
        try put("slovo-20260916-125458.json", on: "TEST-USB-A", age: 4)
        try refuse(toPull: ["slovo-20260916-125458.json"])

        await makeCollector().collect()

        // The sidecar could not be copied, so the audio was never even asked for.
        XCTAssertEqual(inbox(), [])
        XCTAssertEqual(onPhone("TEST-USB-A"), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"])

        // The other way round: the sidecar arrives and its audio does not. It leaves the
        // inbox again rather than waiting there for audio that is still on the phone.
        try refuse(toPull: ["slovo-20260916-125458.m4a"])
        await makeCollector().collect()
        XCTAssertEqual(inbox(), [])
        XCTAssertEqual(onPhone("TEST-USB-A"), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"])
    }

    func testAClipThePhoneRefusedToDeleteDoesNotArriveTwice() async throws {
        try connect(["TEST-USB-A"])
        try put("slovo-20260916-125458.m4a", on: "TEST-USB-A", age: 4)
        try put("slovo-20260916-125458.json", on: "TEST-USB-A", age: 4)
        try refuse(toDelete: ["slovo-20260916-125458.m4a"])

        await makeCollector().collect()

        XCTAssertEqual(inbox(), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"])
        // The audio would not go, so the sidecar stays with it: while the pair is whole, the
        // next pass sees a clip with its moments rather than audio waiting out a grace period.
        XCTAssertEqual(onPhone("TEST-USB-A"), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"])

        // The importer has since turned it into a meeting and moved both files on.
        let imported = directories.inboxFolder
            .appendingPathComponent(InboxImporter.importedFolderName, isDirectory: true)
        try FileManager.default.createDirectory(at: imported, withIntermediateDirectories: true)
        for name in inbox() {
            try FileManager.default.moveItem(at: directories.inboxFolder.appendingPathComponent(name),
                                             to: imported.appendingPathComponent(name))
        }

        await makeCollector().collect()

        // One clip, one meeting: the copy still on the phone is recognised as already here.
        XCTAssertEqual(inbox(), [])
        XCTAssertEqual(onPhone("TEST-USB-A"), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"])
    }

    /// adb writes under a hidden name the importer never takes, and the file gets its own
    /// name only once it arrived whole, so a pull that stalls is never imported half copied.
    func testAClipIsCopiedUnderAHiddenNameAndRenamedOnceWhole() async throws {
        try connect(["TEST-USB-A"])
        try put("slovo-20260916-125458.m4a", on: "TEST-USB-A", age: 4)
        try put("slovo-20260916-125458.json", on: "TEST-USB-A", age: 4)
        // A sidecar an earlier pass left behind is replaced rather than blocking the rename.
        try FileManager.default.createDirectory(at: directories.inboxFolder, withIntermediateDirectories: true)
        try Data("stale".utf8).write(to: directories.inboxFolder.appendingPathComponent("slovo-20260916-125458.json"))

        await makeCollector().collect()

        let targets = try String(contentsOf: phone.appendingPathComponent("pull-targets"), encoding: .utf8)
            .split(separator: "\n").map(String.init)
        XCTAssertEqual(targets, [PhoneClipCollector.partialName(for: "slovo-20260916-125458.json"),
                                 PhoneClipCollector.partialName(for: "slovo-20260916-125458.m4a")])
        XCTAssertTrue(targets.allSatisfy { $0.hasPrefix(".") }, "the importer skips hidden files")
        XCTAssertEqual(inbox(), ["slovo-20260916-125458.json", "slovo-20260916-125458.m4a"], "nothing partial is left")
        let sidecar = try String(contentsOf: directories.inboxFolder.appendingPathComponent("slovo-20260916-125458.json"),
                                 encoding: .utf8)
        XCTAssertEqual(sidecar, "clip", "the fresh copy replaced the stale one")
    }

    func testANameTooLongToCopyUnderItsHiddenNameIsRefused() {
        let longest = String(repeating: "a", count: 255 - PhoneClipCollector.partialPrefix.utf8.count - 4) + ".m4a"
        XCTAssertTrue(PhoneClipCollector.isSafeName(longest))
        XCTAssertFalse(PhoneClipCollector.isSafeName("a" + longest))
    }

    // MARK: - The phone at the end of the cable

    private func makeCollector() -> PhoneClipCollector {
        PhoneClipCollector(directories: directories, settings: settings, adbPaths: [],
                           environment: ["PATH": temporary.appendingPathComponent("bin").path])
    }

    /// The phones adb reports, each with a folder of its own.
    private func connect(_ serials: [String], unauthorized: [String] = []) throws {
        let lines = ["List of devices attached"]
            + serials.map { "\($0)\tdevice" }
            + unauthorized.map { "\($0)\tunauthorized" }
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: phone.appendingPathComponent("devices"))
        for serial in serials {
            try FileManager.default.createDirectory(at: phone.appendingPathComponent(serial, isDirectory: true),
                                                    withIntermediateDirectories: true)
        }
    }

    /// A file in a phone's folder, as old as the phone would say it is.
    private func put(_ name: String, on serial: String, age: TimeInterval) throws {
        let url = phone.appendingPathComponent(serial).appendingPathComponent(name)
        try Data("clip".utf8).write(to: url)
        try setAge(of: name, on: serial, to: age)
    }

    private func setAge(of name: String, on serial: String, to age: TimeInterval) throws {
        let url = phone.appendingPathComponent(serial).appendingPathComponent(name)
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -age)],
                                              ofItemAtPath: url.path)
    }

    /// Files the fake phone hands over or deletes no more, the way a cable pulled at the
    /// wrong moment or a read-only volume would.
    private func refuse(toPull names: [String]) throws {
        try Data(names.joined(separator: "\n").utf8).write(to: phone.appendingPathComponent("refuse-pull"))
    }

    /// A phone whose `stat` does not take the flags the collector sends: it answers with its
    /// own clock and says nothing about any file.
    private func refuseTimes(_ refuse: Bool) throws {
        let marker = phone.appendingPathComponent("refuse-times")
        if refuse {
            try Data().write(to: marker)
        } else {
            try FileManager.default.removeItem(at: marker)
        }
    }

    private func refuse(toDelete names: [String]) throws {
        try Data(names.joined(separator: "\n").utf8).write(to: phone.appendingPathComponent("refuse-rm"))
    }

    private func inbox() -> [String] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: directories.inboxFolder.path)) ?? []
        return names.filter { $0 != InboxImporter.importedFolderName }.sorted()
    }

    private func onPhone(_ serial: String) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: phone.appendingPathComponent(serial).path)) ?? []).sorted()
    }

    /// Stands in for adb. The folder on the phone is a folder on this Mac, and each command
    /// the collector sends is answered the way a phone answers it — including stripping the
    /// quotes the phone's own shell would strip, since adb joins the arguments into one line
    /// without quoting anything itself.
    private func writeFakeAdb() throws {
        let bin = temporary.appendingPathComponent("bin", isDirectory: true)
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let script = #"""
        #!/bin/sh
        root="\#(phone.path)"
        folder="/sdcard/Download/Slovo"

        unquote() {
            value=${1#\'}
            value=${value%\'}
            printf '%s' "$value"
        }

        if [ "$1" = "devices" ]; then
            cat "$root/devices"
            exit 0
        fi
        serial="$2"
        action="$3"
        shift 3
        case "$action" in
            pull)
                name=$(basename "$(unquote "$1")")
                [ -f "$root/$serial/$name" ] || exit 1
                if grep -qxF "$name" "$root/refuse-pull" 2>/dev/null; then
                    echo "adb: error: failed to copy" >&2
                    exit 1
                fi
                printf '%s\n' "$(basename "$2")" >> "$root/pull-targets"
                cp "$root/$serial/$name" "$2" || exit 1
                exit 0
                ;;
            shell)
                case "$1" in
                    ls)
                        ls -1 "$root/$serial" 2>/dev/null
                        exit 0
                        ;;
                    date)
                        date +%s
                        if [ -f "$root/refuse-times" ]; then
                            echo "stat: unknown option -- 'c'" >&2
                            exit 1
                        fi
                        shift 6
                        for path in "$@"; do
                            name=$(basename "$(unquote "$path")")
                            [ -e "$root/$serial/$name" ] || continue
                            printf '%s %s/%s\n' "$(stat -f %m "$root/$serial/$name")" "$folder" "$name"
                        done
                        exit 0
                        ;;
                    rm)
                        name=$(basename "$(unquote "$3")")
                        if grep -qxF "$name" "$root/refuse-rm" 2>/dev/null; then
                            echo "rm: $name: Read-only file system" >&2
                            exit 1
                        fi
                        rm -f "$root/$serial/$name"
                        exit 0
                        ;;
                esac
                ;;
        esac
        exit 1

        """#
        let adb = bin.appendingPathComponent("adb")
        try Data(script.utf8).write(to: adb)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: adb.path)
    }
}
