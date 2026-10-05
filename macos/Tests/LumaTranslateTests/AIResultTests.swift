import XCTest
@testable import LumaTranslate

final class AIResultTests: XCTestCase {
    private var fixture: [String: Any] {
        ["translation_zh": "1. 解释。\n2. 占（比例）。", "part_of_speech": "phrasal verb",
         "phonetic": "AmE /əˈkaʊnt fɔːr/", "explanation_en": "1. Explain.\n2. Constitute a share.",
         "practical_usage_en": "Used in research reports.", "practical_usage_zh": "用于研究报告。",
         "example_en": "1. The model accounts for the pattern.\n2. They account for half the sample.",
         "example_zh": "1. 模型解释了该模式。\n2. 他们占样本的一半。",
         "academic_notes": "根据宾语区分解释和占比。", "coverage_note": "示例内容；义项不保证穷尽。"]
    }
    func testFullResultPreservesSensesAndAcademicFields() throws {
        let client = AITranslationClient()
        for provider in AIProvider.allCases {
            let result = try client.fullResult(fixture, source: "account for", provider: provider)
            XCTAssertTrue(result.translation.contains("2."))
            XCTAssertTrue(result.exampleEn.contains("2."))
            XCTAssertTrue(result.phonetic.contains("kaʊnt"))
            XCTAssertFalse(result.academicNotes.isEmpty)
            XCTAssertFalse(result.coverageNote.isEmpty)
            XCTAssertEqual(result.speakText, "account for")
        }
    }
    func testMissingRequiredFieldIsRejected() {
        var values = fixture
        values.removeValue(forKey: "phonetic")
        XCTAssertThrowsError(try AITranslationClient().fullResult(values, source: "account for", provider: .deepseek))
        values = fixture
        values["academic_notes"] = ["wrong JSON type"]
        XCTAssertThrowsError(try AITranslationClient().fullResult(values, source: "account for", provider: .gemini))
    }
}
