import AppKit
import SwiftUI

enum LumaPalette {
    static let ink = Color(red: 0.075, green: 0.086, blue: 0.14)
    static let violet = Color(red: 0.45, green: 0.34, blue: 1.0)
    static let cyan = Color(red: 0.29, green: 0.84, blue: 0.91)
    static let paper = Color(red: 0.97, green: 0.975, blue: 0.99)
    static let slate = Color(red: 0.38, green: 0.40, blue: 0.49)
    static let coral = Color(red: 0.95, green: 0.45, blue: 0.45)
    static let success = Color(red: 0.18, green: 0.70, blue: 0.53)
    static let orbitGradient = LinearGradient(
        colors: [cyan, Color(red: 0.35, green: 0.61, blue: 1.0), violet],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )
}

private final class LumaPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

private final class SelectionBorderView: NSView {
    override var isOpaque: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let bounds = self.bounds.insetBy(dx: 2.5, dy: 2.5)
        let path = NSBezierPath(roundedRect: bounds, xRadius: 9, yRadius: 9)
        NSColor(calibratedRed: 0.45, green: 0.34, blue: 1.0, alpha: 0.12).setFill()
        path.fill()
        path.lineWidth = 2.5
        path.setLineDash([8, 5], count: 2, phase: 0)
        NSColor(calibratedRed: 0.36, green: 0.72, blue: 1.0, alpha: 0.95).setStroke()
        path.stroke()
    }
}

@MainActor
final class SelectionOverlayController {
    private let panel: NSPanel

    init() {
        panel = NSPanel(
            contentRect: .zero,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.contentView = SelectionBorderView(frame: .zero)
    }

    func show(start: CGPoint, current: CGPoint) {
        let quartzRect = CGRect(
            x: min(start.x, current.x),
            y: min(start.y, current.y),
            width: max(4, abs(current.x - start.x)),
            height: max(4, abs(current.y - start.y))
        )
        let rect = ScreenCoordinates.appKitRect(fromQuartz: quartzRect).insetBy(dx: -4, dy: -4)
        panel.setFrame(rect, display: true)
        panel.orderFrontRegardless()
    }

    func hide() {
        panel.orderOut(nil)
    }
}

private struct CursorOrbView: View {
    let state: GestureVisualState

    var body: some View {
        ZStack {
            Circle()
                .fill(.ultraThinMaterial)
                .overlay(Circle().strokeBorder(Color.white.opacity(0.7), lineWidth: 1))
            Circle()
                .fill(
                    state == .processing
                        ? AnyShapeStyle(LumaPalette.violet)
                        : AnyShapeStyle(LumaPalette.orbitGradient)
                )
                .frame(width: state == .awaitingSecondClick ? 13 : 10, height: state == .awaitingSecondClick ? 13 : 10)
                .shadow(color: LumaPalette.cyan.opacity(0.55), radius: 5)
            if state == .selecting {
                Image(systemName: "sparkles")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.white)
            }
        }
        .padding(3)
    }
}

@MainActor
final class CursorBadgeController {
    private let panel: NSPanel
    private var timer: Timer?
    private var state: GestureVisualState = .idle
    private var temporarilyHidden = false

    init() {
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 28, height: 28),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        updateView()
    }

    func start() {
        guard timer == nil else { return }
        panel.orderFrontRegardless()
        followPointer()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            guard let self else { return }
            Task { @MainActor [self] in self.followPointer() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        panel.orderOut(nil)
    }

    func setState(_ value: GestureVisualState) {
        state = value
        updateView()
    }

    func hideForCapture() {
        temporarilyHidden = true
        panel.orderOut(nil)
    }

    func restoreAfterCapture() {
        temporarilyHidden = false
        if timer != nil { panel.orderFrontRegardless() }
    }

    private func followPointer() {
        guard !temporarilyHidden else { return }
        let point = NSEvent.mouseLocation
        panel.setFrameOrigin(CGPoint(x: point.x + 15, y: point.y - 36))
    }

    private func updateView() {
        panel.contentViewController = NSHostingController(rootView: CursorOrbView(state: state))
    }
}

private struct PopupSection: View {
    let eyebrow: String
    let text: String
    var secondary: String = ""

    var body: some View {
        if !text.isEmpty {
            VStack(alignment: .leading, spacing: 5) {
                Text(eyebrow.uppercased())
                    .font(.system(size: 10, weight: .bold, design: .rounded))
                    .tracking(1.1)
                    .foregroundStyle(LumaPalette.violet)
                Text(text)
                    .font(.system(size: 13.5, weight: .regular))
                    .foregroundStyle(.primary)
                    .textSelection(.enabled)
                if !secondary.isEmpty {
                    Text(secondary)
                        .font(.system(size: 12.5))
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

private struct QuickPopupView: View {
    let result: TranslationResult
    let isBusy: Bool
    let onSpeak: () -> Void
    let onCopy: () -> Void
    let onAI: (() -> Void)?
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 9) {
                ZStack {
                    Circle().fill(LumaPalette.orbitGradient)
                    Image(systemName: result.provider == "offline" ? "book.closed.fill" : "sparkles")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(.white)
                }
                .frame(width: 27, height: 27)

                VStack(alignment: .leading, spacing: 1) {
                    Text(result.speakText.isEmpty ? "Luma Translate" : result.speakText)
                        .font(.system(size: 15, weight: .semibold, design: .rounded))
                        .lineLimit(2)
                    HStack(spacing: 5) {
                        if !result.phonetic.isEmpty { Text("/\(result.phonetic)/") }
                        if !result.partOfSpeech.isEmpty { Text(result.partOfSpeech) }
                    }
                    .font(.system(size: 10.5, weight: .medium, design: .monospaced))
                    .foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                Button(action: onClose) {
                    Image(systemName: "xmark")
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .accessibilityLabel("关闭")
            }
            .padding(16)

            Rectangle().fill(LumaPalette.orbitGradient).frame(height: 2)

            ScrollView {
                VStack(alignment: .leading, spacing: 15) {
                    PopupSection(eyebrow: "简体中文", text: result.translation)
                    PopupSection(eyebrow: "Plain English", text: result.simpleEnglish)
                    PopupSection(eyebrow: "日常用法", text: result.practicalUsageZh, secondary: result.practicalUsageEn)
                    PopupSection(eyebrow: "例句", text: result.exampleEn, secondary: result.exampleZh)
                    if !result.singaporeNote.isEmpty {
                        PopupSection(eyebrow: "Singapore", text: result.singaporeNote)
                    }
                    if !result.meaningZh.isEmpty {
                        Text(result.meaningZh)
                            .font(.system(size: 10.5))
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Divider()
            HStack(spacing: 9) {
                Button(action: onSpeak) { Label("朗读", systemImage: "speaker.wave.2") }
                Button(action: onCopy) { Label("复制", systemImage: "doc.on.doc") }
                Spacer()
                if let onAI {
                    Button(action: onAI) {
                        if isBusy { ProgressView().controlSize(.small) }
                        else { Label("AI 上下文", systemImage: "sparkles") }
                    }
                    .disabled(isBusy)
                    .buttonStyle(.borderedProminent)
                    .tint(LumaPalette.violet)
                }
            }
            .font(.system(size: 12, weight: .medium))
            .padding(12)
        }
        .frame(width: 430, height: 500)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(Color.white.opacity(0.38), lineWidth: 1)
        )
    }
}

private struct MessagePopupView: View {
    let message: String
    let isError: Bool
    let onClose: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 11) {
            Image(systemName: isError ? "exclamationmark.triangle.fill" : "sparkles")
                .foregroundStyle(isError ? LumaPalette.coral : LumaPalette.violet)
            Text(message)
                .font(.system(size: 13))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            Button(action: onClose) { Image(systemName: "xmark") }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
        }
        .padding(15)
        .frame(width: 370)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: 15, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 15, style: .continuous)
                .strokeBorder(Color.white.opacity(0.35), lineWidth: 1)
        )
    }
}

@MainActor
final class QuickPopupController {
    private let panel: LumaPanel
    private(set) var currentResult: TranslationResult?
    private var anchor = CGPoint.zero
    private var speakAction: (() -> Void)?
    private var aiAction: (() -> Void)?
    private var isBusy = false

    init() {
        panel = LumaPanel(
            contentRect: NSRect(x: 0, y: 0, width: 430, height: 500),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isReleasedWhenClosed = false
    }

    func showResult(
        _ result: TranslationResult,
        at quartzPoint: CGPoint,
        onSpeak: @escaping () -> Void,
        onAI: (() -> Void)?
    ) {
        currentResult = result
        anchor = quartzPoint
        speakAction = onSpeak
        aiAction = onAI
        isBusy = false
        renderResult()
        placeNearAnchor(size: CGSize(width: 430, height: 500))
        panel.orderFrontRegardless()
    }

    func updateResult(_ result: TranslationResult) {
        currentResult = result
        isBusy = false
        renderResult()
    }

    func setAIBusy(_ busy: Bool) {
        isBusy = busy
        renderResult()
    }

    func showMessage(_ message: String, at quartzPoint: CGPoint, isError: Bool) {
        currentResult = nil
        anchor = quartzPoint
        let view = MessagePopupView(message: message, isError: isError) { [weak self] in self?.hide() }
        panel.contentViewController = NSHostingController(rootView: view)
        placeNearAnchor(size: CGSize(width: 370, height: 110))
        panel.orderFrontRegardless()
    }

    func hide() {
        panel.orderOut(nil)
        currentResult = nil
        isBusy = false
    }

    func contains(quartzPoint: CGPoint) -> Bool {
        guard panel.isVisible else { return false }
        return panel.frame.contains(ScreenCoordinates.appKitPoint(fromQuartz: quartzPoint))
    }

    private func renderResult() {
        guard let result = currentResult else { return }
        let view = QuickPopupView(
            result: result,
            isBusy: isBusy,
            onSpeak: { [weak self] in self?.speakAction?() },
            onCopy: { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(result.translation, forType: .string) },
            onAI: aiAction,
            onClose: { [weak self] in self?.hide() }
        )
        panel.contentViewController = NSHostingController(rootView: view)
    }

    private func placeNearAnchor(size: CGSize) {
        let point = ScreenCoordinates.appKitPoint(fromQuartz: anchor)
        let visible = ScreenCoordinates.screen(containingQuartz: anchor)?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        var origin = CGPoint(x: point.x + 18, y: point.y - size.height - 18)
        if origin.x + size.width > visible.maxX { origin.x = point.x - size.width - 18 }
        if origin.y < visible.minY { origin.y = min(point.y + 18, visible.maxY - size.height) }
        origin.x = min(max(origin.x, visible.minX + 8), visible.maxX - size.width - 8)
        origin.y = min(max(origin.y, visible.minY + 8), visible.maxY - size.height - 8)
        panel.setFrame(NSRect(origin: origin, size: size), display: true)
    }
}
