import XCTest
@testable import LumaTranslate

final class OfflineDictionaryTests: XCTestCase {
    private var dictionary: OfflineDictionary!

    override func setUpWithError() throws {
        dictionary = try OfflineDictionary()
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
}
