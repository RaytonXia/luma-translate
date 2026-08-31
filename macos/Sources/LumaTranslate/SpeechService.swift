import AVFoundation
import Foundation

struct SpeechVoice: Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let language: String
    let quality: String

    var displayName: String {
        quality.isEmpty ? "\(name) · \(language)" : "\(name) · \(language) · \(quality)"
    }
}

@MainActor
final class SpeechService: NSObject, AVSpeechSynthesizerDelegate {
    private let synthesizer = AVSpeechSynthesizer()
    private(set) var voices: [SpeechVoice] = []
    var onSpeakingChanged: ((Bool) -> Void)?

    override init() {
        super.init()
        synthesizer.delegate = self
        voices = AVSpeechSynthesisVoice.speechVoices()
            .filter { $0.language.lowercased().hasPrefix("en") }
            .map {
                let quality: String
                switch $0.quality {
                case .enhanced: quality = "Enhanced"
                case .premium: quality = "Premium"
                default: quality = ""
                }
                return SpeechVoice(id: $0.identifier, name: $0.name, language: $0.language, quality: quality)
            }
            .sorted {
                if $0.quality != $1.quality { return $0.quality > $1.quality }
                return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
            }
    }

    func speak(_ text: String, voiceID: String?) {
        let value = TextLogic.speechText(text)
        guard !value.isEmpty else { return }
        synthesizer.stopSpeaking(at: .immediate)
        let utterance = AVSpeechUtterance(string: value)
        if let voiceID, !voiceID.isEmpty, let voice = AVSpeechSynthesisVoice(identifier: voiceID) {
            utterance.voice = voice
        } else {
            utterance.voice = preferredDefaultVoice()
        }
        utterance.rate = 0.47
        utterance.pitchMultiplier = 1.0
        utterance.volume = 1.0
        onSpeakingChanged?(true)
        synthesizer.speak(utterance)
    }

    func stop() {
        synthesizer.stopSpeaking(at: .immediate)
        onSpeakingChanged?(false)
    }

    private func preferredDefaultVoice() -> AVSpeechSynthesisVoice? {
        let all = AVSpeechSynthesisVoice.speechVoices().filter { $0.language.lowercased().hasPrefix("en") }
        return all.first(where: { $0.quality == .premium })
            ?? all.first(where: { $0.quality == .enhanced })
            ?? AVSpeechSynthesisVoice(language: "en-US")
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        Task { @MainActor [weak self] in self?.onSpeakingChanged?(false) }
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
        Task { @MainActor [weak self] in self?.onSpeakingChanged?(false) }
    }
}
