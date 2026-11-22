import XCTest
@testable import SlovoCore

final class TextTransliteratorTests: XCTestCase {
    func testSpecExamples() {
        XCTAssertEqual(Transliterator.latin("кубернетес"), "kubernetes")
        XCTAssertEqual(Transliterator.compactLatin("гитхаб экшенс"), "githabekshens")
        XCTAssertEqual(Transliterator.latin("юзер дефолтс"), "yuzer defolts")
    }

    func testLatinDigitsAndPunctuationPassThroughLowercased() {
        XCTAssertEqual(Transliterator.latin("Swift 6, Ёжик!"), "swift 6, yozhik!")
    }

    func testSoftAndHardSignsVanish() {
        XCTAssertEqual(Transliterator.latin("модель"), "model")
        XCTAssertEqual(Transliterator.latin("щука, объект, чай, цвет, ы"), "shchuka, obekt, chay, tsvet, y")
    }

    func testCompactLatinKeepsOnlyLettersAndDigits() {
        XCTAssertEqual(Transliterator.compactLatin("Package.swift"), "packageswift")
        XCTAssertEqual(Transliterator.compactLatin("Café №5"), "cafe5")
    }

    func testSpellingKeyBringsRussianAndEnglishSpellingsTogether() {
        XCTAssertEqual(SpellingKey.make(["докер"]), SpellingKey.make(["Docker"]))
        XCTAssertEqual(SpellingKey.make(["постгресс"]), SpellingKey.make(["Postgres"]))
        XCTAssertEqual(SpellingKey.make(["опшн"]), SpellingKey.make(["Option"]))
        XCTAssertEqual(SpellingKey.make(["виспер"]), SpellingKey.make(["Whisper"]))
        XCTAssertEqual(key(["GitHub", "Actions"]), "githubakshns")
        XCTAssertEqual(key(["Xcode"]), "ekskode")
        XCTAssertEqual(key(["Джисон"]), "jison")
    }

    func testVoicingPairsCostHalfInConsonantDistance() {
        XCTAssertEqual(SpellingKey.consonantDistance(Array("slnd".utf8), Array("slnt".utf8)), 0.5)
        XCTAssertEqual(SpellingKey.consonantDistance(Array("vrs".utf8), Array("vrsl".utf8)), 1)
    }

    private func key(_ words: [String]) -> String {
        String(decoding: SpellingKey.make(words), as: UTF8.self)
    }
}
