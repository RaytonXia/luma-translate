# Windows → macOS port map

This is a source-level port, not a wrapper around the original executable. The Windows binary cannot run natively on macOS because its UI and system integrations are Windows-only.

| Windows implementation | macOS implementation |
| --- | --- |
| .NET Framework 4.x / WinForms | Swift 5.9 / SwiftUI + AppKit |
| `SetWindowsHookEx(WH_MOUSE_LL)` | Quartz `CGEvent.tapCreate` |
| Windows Media OCR | Apple Vision `VNRecognizeTextRequest` |
| GDI `CopyFromScreen` | Quartz window-server capture, memory only |
| Windows DPAPI | Keychain Services |
| Windows Media / System Speech | `AVSpeechSynthesizer` |
| `NotifyIcon` | SwiftUI `MenuBarExtra` |
| Windows x64 executable | Universal 2 app (`arm64` + `x86_64`) |

The right-click state machine intentionally delays a normal single right-click until the system double-click interval expires. If no second right-click arrives, Luma posts a marked synthetic right-click pair back to the event stream. Marked events bypass Luma, preventing recursion. Modifier-assisted right-clicks are never intercepted.

The app needs two user-controlled macOS permissions:

1. **Accessibility** — required for an active global mouse event tap and for replaying an ordinary single right-click.
2. **Screen Recording** — required to capture the small OCR region. Screenshots stay in memory and are released after Vision finishes.

No App Sandbox entitlement is enabled because a sandboxed app cannot provide this global gesture workflow. Developer ID signing and Apple notarization are therefore recommended before public distribution.
