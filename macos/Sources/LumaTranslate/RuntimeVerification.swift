import AppKit
import Foundation

/// Exercises the installed app bundle in a real Aqua session without changing privacy permissions.
@MainActor
enum RuntimeVerification {
    static var outputDirectory: URL? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--verify-runtime"), args.indices.contains(i + 1) else { return nil }
        return URL(fileURLWithPath: args[i + 1], isDirectory: true)
    }

    static func run(to directory: URL) async {
        var checks: [String] = []
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let model = AppModel.shared
            for _ in 0..<300 {
                if model.isDictionaryReady, model.controlCenterWindow != nil { break }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            guard model.isDictionaryReady, model.dictionaryEntryCount > 47_000 else {
                throw LumaError.message("Bundled dictionary failed to load")
            }
            checks.append("Bundled dictionary loaded: \(model.dictionaryEntryCount)")
            model.inputText = "significant"
            model.translateOffline()
            for _ in 0..<100 {
                if model.currentResult != nil, !model.isWorking { break }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            guard let result = model.currentResult, !result.translation.isEmpty,
                  !result.simpleEnglish.isEmpty, !result.phonetic.isEmpty else {
                throw LumaError.message("Offline UI translation missing required dictionary fields")
            }
            checks.append("Main-window offline translation, IPA, English definitions")
            guard let window = model.controlCenterWindow, let view = window.contentView else {
                throw LumaError.message("Control center window was not created")
            }
            window.makeKeyAndOrderFront(nil)
            try await Task.sleep(nanoseconds: 500_000_000)
            for (name, size) in [("main", NSSize(width: 900, height: 720)), ("minimum", NSSize(width: 820, height: 650))] {
                window.setContentSize(size)
                try await Task.sleep(nanoseconds: 300_000_000)
                view.layoutSubtreeIfNeeded()
                guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
                    throw LumaError.message("Could not render app window")
                }
                view.cacheDisplay(in: view.bounds, to: bitmap)
                guard let data = bitmap.representation(using: .png, properties: [:]) else {
                    throw LumaError.message("Could not encode app screenshot")
                }
                try data.write(to: directory.appendingPathComponent("\(name).png"))
            }
            window.close()
            model.showMainWindow()
            guard window.isVisible else { throw LumaError.message("Closed main window did not reopen") }
            checks.append("Native UI rendered at default/minimum size; close and reopen")

            let image = NSImage(size: NSSize(width: 640, height: 120))
            image.lockFocus()
            NSColor.white.setFill()
            NSRect(x: 0, y: 0, width: 640, height: 120).fill()
            ("statistical significance" as NSString).draw(at: NSPoint(x: 20, y: 38), withAttributes: [
                .font: NSFont.systemFont(ofSize: 34), .foregroundColor: NSColor.black
            ])
            image.unlockFocus()
            guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
                throw LumaError.message("Could not generate OCR fixture")
            }
            let recognized = try ScreenOCRService.selectionText(in: cgImage)
            guard recognized.lowercased().contains("statistical significance") else {
                throw LumaError.message("Production Vision OCR returned: \(recognized)")
            }
            checks.append("Production Vision OCR on rendered English bitmap: \(recognized)")
            let report: [String: Any] = [
                "passed": true, "os": ProcessInfo.processInfo.operatingSystemVersionString,
                "checks": checks, "bundle": Bundle.main.bundleIdentifier ?? "",
                "notes": "Global mouse gestures and user-granted screen capture permissions need an interactive user session. No cloud API request was made."
            ]
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
                .write(to: directory.appendingPathComponent("runtime.json"))
            print("LUMA_RUNTIME_PASS \(checks)")
            exit(0)
        } catch {
            let message = "LUMA_RUNTIME_FAIL: \(error)"
            try? message.write(to: directory.appendingPathComponent("failure.txt"), atomically: true, encoding: .utf8)
            print(message)
            exit(1)
        }
    }
}
