import Foundation

struct TranslationResult: Equatable, Sendable {
    var direction = "en_to_zh"
    var sourceLanguage = "English"
    var translation = ""
    var meaningZh = ""
    var simpleEnglish = ""
    var speakText = ""
    var exampleEn = ""
    var exampleZh = ""
    var singaporeNote = ""
    var provider = "offline"
    var matchKind = ""
    var phonetic = ""
    var partOfSpeech = ""
    var practicalUsageEn = ""
    var practicalUsageZh = ""
    var coveredWords = 0
    var totalWords = 0

    static func sentence(source: String, translation: String, provider: AIProvider) -> TranslationResult {
        TranslationResult(
            translation: translation,
            meaningZh: "由 \(provider.displayName) 完成上下文整句翻译；英文文字已发送到 \(provider.serviceName)。",
            speakText: source,
            provider: provider.rawValue,
            matchKind: "ai_sentence",
            partOfSpeech: "sentence"
        )
    }
}

enum LumaError: LocalizedError, Equatable {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let message): return message
        }
    }
}

enum AIProvider: String, CaseIterable, Codable, Identifiable, Sendable {
    case deepseek
    case gemini

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .deepseek: return "DeepSeek"
        case .gemini: return "Gemini"
        }
    }

    var serviceName: String {
        switch self {
        case .deepseek: return "DeepSeek"
        case .gemini: return "Google Gemini"
        }
    }

    var host: String {
        switch self {
        case .deepseek: return "api.deepseek.com"
        case .gemini: return "generativelanguage.googleapis.com"
        }
    }

    var endpoint: URL {
        switch self {
        case .deepseek:
            return URL(string: "https://api.deepseek.com/chat/completions")!
        case .gemini:
            return URL(string: "https://generativelanguage.googleapis.com/v1beta/interactions")!
        }
    }

    var defaultModel: String {
        switch self {
        case .deepseek: return "deepseek-v4-flash"
        case .gemini: return "gemini-3.5-flash-lite"
        }
    }

    var environmentKeyName: String {
        switch self {
        case .deepseek: return "DEEPSEEK_API_KEY"
        case .gemini: return "GEMINI_API_KEY"
        }
    }
}

enum GestureVisualState: Equatable, Sendable {
    case idle
    case awaitingSecondClick
    case selecting
    case processing

    var label: String {
        switch self {
        case .idle: return "待命"
        case .awaitingSecondClick: return "再按一次右键"
        case .selecting: return "拖过要翻译的句子"
        case .processing: return "正在识别"
        }
    }
}

enum TextLogic {
    static let maxInputCharacters = 3_000

    private static let englishWordRegex = try! NSRegularExpression(
        pattern: #"[A-Za-zÀ-ÖØ-öø-ÿ]+(?:['’\-][A-Za-zÀ-ÖØ-öø-ÿ]+)*"#
    )

    static func normalizeInput(_ text: String?) -> String {
        (text ?? "")
            .replacingOccurrences(of: "\0", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isEnglishInput(_ text: String) -> Bool {
        var han = 0
        var latin = 0
        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x3400...0x4DBF, 0x4E00...0x9FFF:
                han += 1
            case 0x41...0x5A, 0x61...0x7A, 0x00C0...0x024F:
                latin += 1
            default:
                break
            }
        }
        return latin > 0 && (han == 0 || latin >= han * 3)
    }

    static func lookupKey(_ text: String) -> String {
        var value = text.precomposedStringWithCompatibilityMapping.lowercased()
        value = value
            .replacingOccurrences(of: "‘", with: "'")
            .replacingOccurrences(of: "’", with: "'")
            .replacingOccurrences(of: "–", with: "-")
            .replacingOccurrences(of: "—", with: "-")
        value = value.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        return value.trimmingCharacters(in: CharacterSet(charactersIn: " .,;:!?\"'()[]{}\n\r\t"))
    }

    static func speechText(_ text: String) -> String {
        let flattened = text
            .replacingOccurrences(of: "\r", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return String(flattened.prefix(maxInputCharacters))
    }

    static func englishWords(in text: String) -> [String] {
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return englishWordRegex.matches(in: text, range: range).compactMap { match in
            guard let swiftRange = Range(match.range, in: text) else { return nil }
            return String(text[swiftRange])
        }
    }

    static func normalizedSelection(_ text: String) -> String {
        var value = text
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        value = value.replacingOccurrences(
            of: #"([A-Za-z])\-\s*\n\s*([A-Za-z])"#,
            with: "$1$2",
            options: .regularExpression
        )
        value = value.replacingOccurrences(of: #"\s*\n\s*"#, with: " ", options: .regularExpression)
        value = value.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        return normalizeInput(value)
    }
}

enum ResourceLocator {
    static func url(forResource name: String, withExtension ext: String) -> URL? {
        if let mainURL = Bundle.main.url(forResource: name, withExtension: ext) {
            return mainURL
        }
        return Bundle.module.url(forResource: name, withExtension: ext)
    }
}
