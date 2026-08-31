import XCTest
@testable import LumaTranslate

final class TextLogicTests: XCTestCase {
    func testLookupNormalization() {
        XCTAssertEqual(TextLogic.lookupKey("  Hawker   Centre! "), "hawker centre")
        XCTAssertEqual(TextLogic.lookupKey("RIGHT—CLICK"), "right-click")
        XCTAssertEqual(TextLogic.lookupKey("Don’t"), "don't")
    }

    func testEnglishDetectionRejectsChineseOnly() {
        XCTAssertTrue(TextLogic.isEnglishInput("Please take the MRT to City Hall."))
        XCTAssertFalse(TextLogic.isEnglishInput("请乘地铁到政府大厦站"))
        XCTAssertFalse(TextLogic.isEnglishInput("1234 !?"))
    }

    func testSelectionNormalizationJoinsHyphenatedLine() {
        let value = TextLogic.normalizedSelection("This is inter-\nnational English.\nNext line.")
        XCTAssertEqual(value, "This is international English. Next line.")
    }

    func testWordExtractionKeepsApostrophesAndHyphens() {
        XCTAssertEqual(
            TextLogic.englishWords(in: "Don't re-enter."),
            ["Don't", "re-enter"]
        )
    }
}
