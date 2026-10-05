import XCTest
@testable import LumaTranslate

final class OfflineDictionaryTests: XCTestCase {
    private static let sharedDictionary = try! OfflineDictionary()
    private var dictionary: OfflineDictionary!

    override func setUpWithError() throws {
        dictionary = Self.sharedDictionary
    }

    func testDictionaryLoadsFullCore() {
        XCTAssertGreaterThan(dictionary.entryCount, 47_000)
    }

    func testSingaporeOverlay() throws {
        let result = try dictionary.translate("MRT")
        XCTAssertTrue(result.translation.contains("地铁"))
        XCTAssertEqual(result.provider, "offline")
        XCTAssertFalse(result.singaporeNote.isEmpty)
    }

    func testIrregularInflectionFindsLemma() throws {
        let result = try dictionary.translate("went")
        XCTAssertTrue(["exact", "inflected"].contains(result.matchKind))
        XCTAssertFalse(result.translation.isEmpty)
    }

    func testSentenceBreakdownNeverClaimsContextualTranslation() throws {
        let result = try dictionary.translate("Take the MRT home")
        XCTAssertEqual(result.provider, "offline")
        XCTAssertTrue(result.meaningZh.contains("没有联网"))
        XCTAssertGreaterThan(result.coveredWords, 0)
    }

    func testChineseInputIsRejected() {
        XCTAssertThrowsError(try dictionary.translate("你好世界"))
    }

    func testAcademicAndLanguageChunkCoverage() throws {
        XCTAssertGreaterThan(dictionary.entryCount, 750_000)
        for term in ["significant", "confidence interval", "null hypothesis", "randomized controlled trial", "heteroscedasticity", "in light of", "account for", "the early bird catches the worm"] {
            let result = try dictionary.translate(term)
            XCTAssertEqual(result.matchKind, "exact", term)
            XCTAssertFalse(result.phonetic.isEmpty, term)
            XCTAssertFalse(result.simpleEnglish.isEmpty, term)
            XCTAssertFalse(result.exampleEn.isEmpty, term)
            XCTAssertFalse(result.exampleZh.isEmpty, term)
            XCTAssertFalse(result.academicNotes.isEmpty, term)
        }
    }

    func testAcademicMistranslationsAndMissingFields() throws {
        XCTAssertFalse(try dictionary.translate("null hypothesis").translation.contains("虚假"))
        XCTAssertTrue(try dictionary.translate("significant").academicNotes.contains("不自动意味着效应大"))
        XCTAssertTrue(try dictionary.translate("control").translation.contains("对照"))
        XCTAssertTrue(try dictionary.translate("control").translation.contains("控制"))
        XCTAssertTrue(try dictionary.translate("abaca").coverageNote.contains("未收录例句"))
        XCTAssertFalse(try dictionary.translate("blur").phonetic.isEmpty)
    }
}
