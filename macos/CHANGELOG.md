# Changelog

## 1.0.0-mac

- Rebuilt the Windows-only WinForms application as a native macOS SwiftUI/AppKit app.
- Added a true Universal 2 release pipeline containing `arm64` and `x86_64` slices.
- Replaced Windows OCR with on-device Apple Vision text recognition.
- Replaced the low-level Windows mouse hook with a permission-aware Quartz event tap.
- Replaced DPAPI storage with macOS Keychain Services.
- Replaced Windows speech synthesis with on-device `AVSpeechSynthesizer` voices.
- Preserved the 47,149-record ECDICT core, Singapore vocabulary overlay, DeepSeek, and Gemini modes.
- Added menu-bar controls, macOS permission onboarding, a selection overlay, and a pointer-adjacent translation panel.
