#if DEBUG
import Foundation
import SlovoCore

/// Realistic made-up content for rendered screenshots: Russian and English history,
/// dictionary items, snippets, notes and a meeting transcript.
@MainActor
struct ScreenFixtures {
    let now: Date
    private let calendar = Calendar.current

    let historyIDs = (0..<9).map { _ in UUID() }
    let dictionaryHoverID = UUID()
    let snippetHoverID = UUID()
    let transcribedMeetingID = UUID()
    let transcribingMeetingID = UUID()
    let recordingMeetingID = UUID()
    let modelMissingMeetingID = UUID()

    init() {
        // A fixed afternoon keeps "Today" entries in the past whenever screens are rendered.
        let today = Calendar.current.startOfDay(for: Date())
        now = Calendar.current.date(bySettingHour: 16, minute: 45, second: 0, of: today) ?? Date()
    }

    private func date(daysAgo: Int, _ hour: Int, _ minute: Int) -> Date {
        let day = calendar.date(byAdding: .day, value: -daysAgo, to: now) ?? now
        return calendar.date(bySettingHour: hour, minute: minute, second: 0, of: day) ?? day
    }

    // MARK: - Snapshots

    /// Everything filled in, permissions granted, models mostly ready.
    var full: SnapshotContent {
        var content = SnapshotContent()
        content.now = now
        content.history = history
        content.insights = insights
        content.dictionary = dictionary
        content.snippets = snippets
        content.notes = notes
        content.meetings = meetings
        content.transcriptionProgress = [transcribingMeetingID: TranscriptionProgress(
            completedSegments: 12, totalSegments: 31, phase: .transcribing, overallFraction: 0.2 + 0.8 * 12 / 31)]
        content.modelStatuses = [
            .gigaamRNNT: .ready(diskBytes: 232_800_000),
            .gigaamCTC: .downloading(progress: 0.42),
            .parakeet: .ready(diskBytes: 483_400_000),
            .appleDictation: .systemManaged(available: true),
            .whisperTurbo: .notDownloaded(downloadBytes: WhisperEngine.Model.size)
        ]
        // One engine mid-load shows its model actions switched off.
        content.engineLoadStates = [.gigaamRNNT: .ready, .parakeet: .loading]
        content.permissions = [.microphone: .granted, .accessibility: .granted, .inputMonitoring: .granted,
                               .systemAudioRecording: .unknown]
        content.microphones = [AudioInputDevice(uid: "BuiltInMicrophoneDevice", name: "MacBook Air Microphone"),
                               AudioInputDevice(uid: "AirPods-Pro-Input", name: "AirPods Pro")]
        content.appsByCategory = [
            .personal: [AppIdentity(bundleID: "ru.keepcoder.Telegram", name: "Telegram"),
                        AppIdentity(bundleID: "com.apple.MobileSMS", name: "Messages")],
            .work: [AppIdentity(bundleID: "com.tinyspeck.slackmacgap", name: "Slack"),
                    AppIdentity(bundleID: "us.zoom.xos", name: "Zoom")],
            .email: [AppIdentity(bundleID: "com.apple.mail", name: "Mail")],
            .other: [AppIdentity(bundleID: "com.apple.Notes", name: "Notes"),
                     AppIdentity(bundleID: "com.apple.Safari", name: "Safari"),
                     AppIdentity(bundleID: "com.apple.dt.Xcode", name: "Xcode")]
        ]
        content.isKeyDown = false
        content.isHotkeyRunning = true
        content.launchAtLogin = true
        content.fnUsage = .changeInputSource
        content.isWisprFlowRunning = true
        content.practiceResult = "Привет! Это моя первая диктовка в Slovo, и она работает."
        content.speakerModelStatus = .ready(diskBytes: DiarizationService.downloadBytes)
        content.mcpEnabled = true
        content.claudeRegistration = ClaudeRegistration(claudeCode: true, claudeApp: false)
        content.mcpCheck = .finished(.works(visibleMeetings: 12))
        return content
    }

    /// A fresh install: nothing dictated yet, permissions still to grant.
    var empty: SnapshotContent {
        var content = full
        content.history = []
        content.insights = .empty
        content.dictionary = []
        content.snippets = []
        content.notes = []
        content.meetings = []
        content.appsByCategory = [:]
        content.permissions = [.microphone: .granted, .accessibility: .notDetermined, .inputMonitoring: .denied]
        content.isHotkeyRunning = false
        content.practiceResult = nil
        return content
    }

    // MARK: - History

    var history: [HistoryEntry] {
        let slack = ("com.tinyspeck.slackmacgap", "Slack")
        let telegram = ("ru.keepcoder.Telegram", "Telegram")
        let mail = ("com.apple.mail", "Mail")
        let xcode = ("com.apple.dt.Xcode", "Xcode")
        let notes = ("com.apple.Notes", "Notes")
        let safari = ("com.apple.Safari", "Safari")
        return [
            entry(0, date(daysAgo: 0, 14, 32),
                  "Can we move the planetarium demo to eleven? I need to test the star map on the lobby screen.",
                  app: slack, category: .work, engine: .parakeet, mode: .english, duration: 9.4, fixes: 2, audio: true),
            entry(1, date(daysAgo: 0, 13, 5),
                  "Привет! Созвонимся вечером? Нужно выбрать фотографии для выставки",
                  app: telegram, category: .personal, engine: .gigaamRNNT, mode: .mixed, duration: 6.8, fixes: 0, audio: true),
            entry(2, date(daysAgo: 0, 11, 48),
                  "Добавить таймер для слайдов и проверить, как каталог выглядит на планшете.",
                  app: xcode, category: .other, engine: .gigaamRNNT, mode: .mixed, duration: 7.2, fixes: 2, audio: false),
            entry(3, date(daysAgo: 0, 10, 20),
                  "Hi Morgan, thanks for sending the floor plan. I marked two spots for the telescope display. Let's walk through the layout tomorrow.",
                  app: mail, category: .email, engine: .parakeet, mode: .english, duration: 14.1, fixes: 0, audio: true),
            entry(4, date(daysAgo: 1, 19, 41),
                  "Захвати, пожалуйста, цветную бумагу и фломастеры для плаката",
                  app: telegram, category: .personal, engine: .gigaamRNNT, mode: .mixed, duration: 3.9, fixes: 0, audio: true),
            entry(5, date(daysAgo: 1, 16, 2),
                  "Идеи для выставки: карта звёздного неба, карточки созвездий и стенд с вопросами.",
                  app: notes, category: .other, engine: .gigaamRNNT, mode: .mixed, duration: 8.5, fixes: 1, audio: true),
            entry(6, date(daysAgo: 1, 9, 15),
                  "Фотографии загрузились, но на стенде не видны подписи. Проверю настройки и напишу через час.",
                  app: slack, category: .work, engine: .gigaamRNNT, mode: .mixed, duration: 6.1, fixes: 1, audio: true),
            entry(7, date(daysAgo: 3, 18, 30),
                  "Добрый день! Отправляю макет афиши. Если понадобятся правки, пришлите их до пятницы.",
                  app: mail, category: .email, engine: .gigaamRNNT, mode: .mixed, duration: 7.7, fixes: 0, audio: false),
            entry(8, date(daysAgo: 3, 12, 12),
                  "best practices for Swift concurrency in AppKit apps",
                  app: safari, category: .other, engine: .parakeet, mode: .english, duration: 3.2, fixes: 0, audio: false)
        ]
    }

    private func entry(_ index: Int, _ date: Date, _ text: String, app: (String, String), category: AppCategory,
                       engine: EngineID, mode: LanguageMode, duration: TimeInterval, fixes: Int, audio: Bool) -> HistoryEntry {
        HistoryEntry(id: historyIDs[index], date: date, text: text, rawText: text, engineID: engine, languageMode: mode,
                     appBundleID: app.0, appName: app.1, category: category, duration: duration,
                     wordCount: HistoryEntry.countWords(in: text), fixesCount: fixes,
                     audioFileName: audio ? WAVFile.relativePath(for: historyIDs[index], date: date) : nil)
    }

    // MARK: - Insights

    var insights: Insights {
        // A deterministic generator gives a believable calendar: quieter weekends, a few
        // days off, and an unbroken run over the last week.
        var seed: UInt64 = 0x5A17_0B0E
        func next() -> Double {
            seed = seed &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return Double(seed >> 33) / Double(UInt32.max)
        }
        let dayCount = 140
        let breaks: Set<Int> = [3, 4, 17, 18, 19, 40, 41, 55, 76, 77, 90, 104, 105, 120, 133]
        var daily: [DailyWordCount] = []
        for offset in 0..<dayCount {
            let daysAgo = dayCount - 1 - offset
            let day = calendar.startOfDay(for: calendar.date(byAdding: .day, value: -daysAgo, to: now) ?? now)
            let weekday = calendar.component(.weekday, from: day)
            let isWeekend = weekday == 1 || weekday == 7
            let ramp = 0.45 + 0.55 * Double(offset) / Double(dayCount)
            var words = Int((isWeekend ? 60 : 180) * ramp + next() * (isWeekend ? 90 : 320) * ramp)
            if breaks.contains(offset) || daysAgo == 6 {
                words = 0
            }
            daily.append(DailyWordCount(day: day, words: words))
        }
        var longest = 0
        var run = 0
        for day in daily {
            run = day.words > 0 ? run + 1 : 0
            longest = max(longest, run)
        }
        let current = daily.reversed().prefix { $0.words > 0 }.count
        let total = daily.reduce(0) { $0 + $1.words }
        let shares: [(AppCategory, Double)] = [(.personal, 0.32), (.work, 0.41), (.email, 0.17), (.other, 0.10)]
        return Insights(
            totalWords: total,
            dictationCount: total / 23,
            wordsPerMinute: 148,
            fixesMade: 213,
            currentStreak: current,
            longestStreak: longest,
            dailyWords: daily,
            categoryShares: shares.map { CategoryShare(category: $0.0, words: Int(Double(total) * $0.1), fraction: $0.1) },
            topApps: [
                AppUsage(app: AppIdentity(bundleID: "com.tinyspeck.slackmacgap", name: "Slack"), words: 9_870, dictations: 412),
                AppUsage(app: AppIdentity(bundleID: "ru.keepcoder.Telegram", name: "Telegram"), words: 7_450, dictations: 388),
                AppUsage(app: AppIdentity(bundleID: "com.apple.mail", name: "Mail"), words: 4_380, dictations: 96),
                AppUsage(app: AppIdentity(bundleID: "com.apple.dt.Xcode", name: "Xcode"), words: 2_910, dictations: 131),
                AppUsage(app: AppIdentity(bundleID: "com.apple.Notes", name: "Notes"), words: 1_640, dictations: 58)
            ]
        )
    }

    // MARK: - Dictionary, snippets, notes

    var dictionary: [DictionaryItem] {
        let terms = ["Kubernetes", "GitHub Actions", "Postgres", "Docker", "UserDefaults", "SwiftUI", "TestFlight",
                     "Parakeet", "GigaAM", "Figma"]
        var items: [DictionaryItem] = []
        for (index, term) in terms.enumerated() {
            items.append(DictionaryItem(id: index == 1 ? dictionaryHoverID : UUID(), kind: .term, text: term,
                                        createdAt: date(daysAgo: index * 2, 12, 0)))
        }
        let replacements: [(String, String)] = [("гитхаб", "GitHub"), ("эпл", "Apple"), ("пиар", "PR"), ("слак", "Slack")]
        for (index, pair) in replacements.enumerated() {
            items.append(DictionaryItem(kind: .replacement, text: pair.0, replacement: pair.1,
                                        createdAt: date(daysAgo: index * 3 + 1, 15, 0)))
        }
        return items
    }

    var snippets: [Snippet] {
        [
            Snippet(id: snippetHoverID, trigger: "мой адрес", expansion: "г. Примерск, ул. Примерная, д. 1",
                    createdAt: date(daysAgo: 20, 10, 0), updatedAt: date(daysAgo: 2, 10, 0)),
            Snippet(trigger: "calendar link", expansion: "https://cal.example.com/demo/30min",
                    createdAt: date(daysAgo: 18, 10, 0), updatedAt: date(daysAgo: 4, 10, 0)),
            Snippet(trigger: "подпись", expansion: "С уважением,\nКоманда проекта",
                    createdAt: date(daysAgo: 30, 10, 0), updatedAt: date(daysAgo: 6, 10, 0)),
            Snippet(trigger: "status update",
                    expansion: "Yesterday: printed the exhibit labels. Today: testing the interactive map.",
                    createdAt: date(daysAgo: 9, 10, 0), updatedAt: date(daysAgo: 8, 10, 0)),
            Snippet(trigger: "ссылка на созвон", expansion: "https://meet.example.com/planetarium",
                    createdAt: date(daysAgo: 40, 10, 0), updatedAt: date(daysAgo: 12, 10, 0))
        ]
    }

    var notes: [Note] {
        [
            Note(text: "План выставки «Звёздный маршрут»\n— распечатать карточки\n— проверить проектор\n— согласовать время экскурсии",
                 createdAt: date(daysAgo: 2, 9, 0), updatedAt: date(daysAgo: 0, 15, 10)),
            Note(text: "Planetarium exhibit\nTest the projector at ten. Move the printed maps to the entrance before noon.",
                 createdAt: date(daysAgo: 3, 9, 0), updatedAt: date(daysAgo: 1, 18, 20)),
            Note(text: "Вопросы к встрече\nГде поставить телескоп и кто принесёт запасные лампы?",
                 createdAt: date(daysAgo: 4, 9, 0), updatedAt: date(daysAgo: 3, 11, 5)),
            Note(text: "", createdAt: date(daysAgo: 5, 9, 0), updatedAt: date(daysAgo: 5, 9, 0))
        ]
    }

    // MARK: - Meetings

    /// An in-person meeting separated into three voices: «Я», a speaker renamed «Участник 2»
    /// and «Спикер 3», with two important moments.
    var coworkSpeakers: [MeetingSpeaker] {
        [MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0, isMe: true),
         MeetingSpeaker(id: "s2", track: .microphone, number: 2, displayName: "Участник 2", colorIndex: 1),
         MeetingSpeaker(id: "s3", track: .microphone, number: 3, colorIndex: 2)]
    }

    var cowork: MeetingRecord {
        let lines: [(TimeInterval, TimeInterval, String, String)] = [
            (4, 9, "s1", "Всем привет! Давайте сверим план выставки «Звёздный маршрут»."),
            (10, 24, "s2", "Я принесла макет карты созвездий. Хочу попробовать его у входа."),
            (25, 31, "s3", "А афишу успеем напечатать к четвергу?"),
            (33, 47, "s1", "Успеваем, если сегодня выберем иллюстрации. Возьмёшь проверку подписей?"),
            (48, 51, "s2", "Да, возьму."),
            (54, 75, "s3", "Тогда я подготовлю карточки для экскурсии и проверю запасной проектор."),
            (758, 777, "s2", "Давайте к пятнице повесим карту и проведём пробную экскурсию."),
            (779, 788, "s1", "Договорились. Дедлайн — пятница, обед."),
            (790, 806, "s3", "Могу помочь со светом, если закончу с проектором."),
            (808, 812, "s2", "Отлично, спасибо!")
        ]
        let segments = lines.map { TranscriptSegment(start: $0.0, end: $0.1, speaker: .me, text: $0.3, speakerID: $0.2) }
        let highlights = [
            MeetingHighlight(time: 34, source: .transcript, createdAt: date(daysAgo: 0, 12, 10)),
            MeetingHighlight(time: 762, note: "срок пробной экскурсии", source: .recording, createdAt: date(daysAgo: 0, 11, 13))
        ]
        return MeetingRecord(id: transcribedMeetingID, title: "Подготовка выставки «Звёздный маршрут»", createdAt: date(daysAgo: 0, 11, 0),
                             duration: 3_852, status: .transcribed, hasSystemAudio: false, engineID: .parakeet,
                             segments: segments, speakers: coworkSpeakers, highlights: highlights,
                             diarization: MeetingDiarization(state: .applied, speakersPerTrack: ["microphone": 3],
                                                             model: DiarizationService.modelIdentifier,
                                                             configuration: DiarizationTuning.standard.description,
                                                             processedAt: date(daysAgo: 0, 12, 5)))
    }

    var meetings: [MeetingRecord] {
        let transcript: [TranscriptSegment] = [
            TranscriptSegment(start: 4, end: 10, speaker: .me, text: "Всем привет! Давайте проверим программу экскурсии."),
            TranscriptSegment(start: 11, end: 25, speaker: .others,
                              text: "Макет готов. Осталось проверить, читаются ли звёздные карты в зале."),
            TranscriptSegment(start: 27, end: 33, speaker: .me, text: "Отлично. Где будут лежать карточки для посетителей?"),
            TranscriptSegment(start: 35, end: 58, speaker: .others,
                              text: "У входа. Поставим рядом корзину для карандашей и листы с заданиями."),
            TranscriptSegment(start: 62, end: 69, speaker: .me, text: "Тогда встречаемся в четверг. Подготовишь список материалов?"),
            TranscriptSegment(start: 70, end: 80, speaker: .others, text: "Подготовлю к среде и отправлю в общий чат."),
            TranscriptSegment(start: 84, end: 90, speaker: .me, text: "И последний вопрос: успеем проверить проектор до экскурсии?"),
            TranscriptSegment(start: 91, end: 104, speaker: .others, text: "Да, его привезут утром. Проверим вместе со звуком.")
        ]
        let interview: [TranscriptSegment] = [
            TranscriptSegment(start: 2, end: 9, speaker: .me, text: "Расскажите, пожалуйста, о своём последнем проекте."),
            TranscriptSegment(start: 10, end: 40, speaker: .others, text: "Я разработал интерактивный каталог книг для вымышленной городской библиотеки.")
        ]
        return [
            cowork,
            MeetingRecord(id: modelMissingMeetingID, title: "Созвон о программе экскурсии", createdAt: date(daysAgo: 0, 9, 30),
                          duration: 2_292, status: .transcribed, hasSystemAudio: true, engineID: .parakeet, segments: transcript,
                          diarization: MeetingDiarization(state: .modelMissing, processedAt: date(daysAgo: 0, 10, 10))),
            MeetingRecord(id: transcribingMeetingID, title: "Exhibit team check-in", createdAt: date(daysAgo: 1, 16, 30),
                          duration: 3_160, status: .transcribing, hasSystemAudio: true, engineID: .parakeet),
            MeetingRecord(title: "Интервью с кандидатом", createdAt: date(daysAgo: 3, 12, 0), duration: 2_705,
                          status: .transcribed, hasSystemAudio: true, engineID: .gigaamCTC, segments: interview, formatVersion: 1),
            MeetingRecord(title: "Call with Morgan", createdAt: date(daysAgo: 5, 10, 15), duration: 750,
                          status: .recorded, hasSystemAudio: false)
        ]
    }

    /// The list while a new meeting records.
    var recordingMeetings: [MeetingRecord] {
        let marks = [MeetingHighlight(time: 212, source: .recording, createdAt: date(daysAgo: 0, 16, 36)),
                     MeetingHighlight(time: 640, source: .recording, createdAt: date(daysAgo: 0, 16, 43))]
        return [MeetingRecord(id: recordingMeetingID, title: "Репетиция экскурсии", createdAt: date(daysAgo: 0, 16, 32),
                              duration: 0, status: .recording, hasSystemAudio: true, highlights: marks)] + meetings
    }
}
#endif
