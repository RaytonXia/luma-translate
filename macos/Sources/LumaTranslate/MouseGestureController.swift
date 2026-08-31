import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

final class MouseGestureController {
    var onPointGesture: ((CGPoint) -> Void)?
    var onSelectionBegan: ((CGPoint) -> Void)?
    var onSelectionChanged: ((CGPoint, CGPoint) -> Void)?
    var onSelectionFinished: ((CGRect, CGPoint) -> Void)?
    var onVisualStateChanged: ((GestureVisualState) -> Void)?
    var onLeftMouseDown: (() -> Void)?
    var shouldHandlePoint: ((CGPoint) -> Bool)?

    private enum State: Equatable {
        case idle
        case pendingDown
        case awaitingSecondClick
        case doubleClickDown
        case selecting
    }

    private static let replayMarker: Int64 = 0x4C_55_4D_41
    private let longPressSeconds = 0.42
    private let doubleClickDistance: CGFloat = 7

    private var state: State = .idle
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var longPressWorkItem: DispatchWorkItem?
    private var replayWorkItem: DispatchWorkItem?
    private var firstDownPoint = CGPoint.zero
    private var firstDownTime: TimeInterval = 0
    private var currentPoint = CGPoint.zero

    private(set) var isEnabled = false

    static func isAccessibilityTrusted(prompt: Bool) -> Bool {
        let promptKey = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        return AXIsProcessTrustedWithOptions([promptKey: prompt] as CFDictionary)
    }

    func start() throws {
        guard !isEnabled else { return }
        guard Self.isAccessibilityTrusted(prompt: false) else {
            throw LumaError.message("需要“辅助功能”权限来识别右键手势。请在系统设置 → 隐私与安全性 → 辅助功能中允许 Luma Translate。")
        }

        let eventTypes: [CGEventType] = [
            .rightMouseDown, .rightMouseUp, .rightMouseDragged,
            .leftMouseDown
        ]
        let mask = eventTypes.reduce(CGEventMask(0)) {
            $0 | (CGEventMask(1) << $1.rawValue)
        }
        let pointer = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: Self.eventCallback,
            userInfo: pointer
        ) else {
            throw LumaError.message("无法启动全局鼠标监听。请确认辅助功能权限后重新打开应用。 / Could not start the mouse event tap.")
        }
        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            CFMachPortInvalidate(tap)
            throw LumaError.message("无法创建鼠标监听运行循环。 / Could not create the event-tap run loop.")
        }

        eventTap = tap
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        isEnabled = true
        setVisualState(.idle)
    }

    func stop() {
        if state == .pendingDown || state == .awaitingSecondClick {
            replayPendingRightClick()
        }
        cancelTimers()
        state = .idle
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let tap = eventTap {
            CFMachPortInvalidate(tap)
        }
        runLoopSource = nil
        eventTap = nil
        isEnabled = false
        setVisualState(.idle)
    }

    deinit {
        stop()
    }

    private static let eventCallback: CGEventTapCallBack = { _, type, event, userInfo in
        guard let userInfo else { return Unmanaged.passUnretained(event) }
        let controller = Unmanaged<MouseGestureController>.fromOpaque(userInfo).takeUnretainedValue()
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = controller.eventTap { CGEvent.tapEnable(tap: tap, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == MouseGestureController.replayMarker {
            return Unmanaged.passUnretained(event)
        }
        let suppress = controller.handle(type: type, event: event)
        return suppress ? nil : Unmanaged.passUnretained(event)
    }

    private func handle(type: CGEventType, event: CGEvent) -> Bool {
        switch type {
        case .leftMouseDown:
            onLeftMouseDown?()
            return false
        case .rightMouseDown:
            return handleRightDown(event)
        case .rightMouseUp:
            return handleRightUp(event)
        case .rightMouseDragged:
            return handleRightDrag(event)
        default:
            return false
        }
    }

    private func handleRightDown(_ event: CGEvent) -> Bool {
        let point = event.location
        let modifierMask: CGEventFlags = [.maskShift, .maskControl, .maskAlternate, .maskCommand]
        guard event.flags.intersection(modifierMask).isEmpty,
              shouldHandlePoint?(point) ?? true
        else { return false }

        let now = ProcessInfo.processInfo.systemUptime
        if state == .awaitingSecondClick,
           now - firstDownTime <= NSEvent.doubleClickInterval + 0.12,
           hypot(point.x - firstDownPoint.x, point.y - firstDownPoint.y) <= doubleClickDistance {
            replayWorkItem?.cancel()
            replayWorkItem = nil
            state = .doubleClickDown
            setVisualState(.processing)
            onPointGesture?(point)
            return true
        }

        if state == .awaitingSecondClick {
            replayPendingRightClick()
        }
        beginFirstRightDown(at: point, time: now)
        return true
    }

    private func handleRightUp(_ event: CGEvent) -> Bool {
        currentPoint = event.location
        switch state {
        case .pendingDown:
            longPressWorkItem?.cancel()
            longPressWorkItem = nil
            state = .awaitingSecondClick
            setVisualState(.awaitingSecondClick)
            scheduleSingleClickReplay()
            return true
        case .doubleClickDown:
            state = .idle
            setVisualState(.processing)
            return true
        case .selecting:
            let selection = CGRect(
                x: min(firstDownPoint.x, currentPoint.x),
                y: min(firstDownPoint.y, currentPoint.y),
                width: abs(currentPoint.x - firstDownPoint.x),
                height: abs(currentPoint.y - firstDownPoint.y)
            )
            state = .idle
            setVisualState(.processing)
            onSelectionFinished?(selection, currentPoint)
            return true
        case .awaitingSecondClick:
            return true
        case .idle:
            return false
        }
    }

    private func handleRightDrag(_ event: CGEvent) -> Bool {
        currentPoint = event.location
        switch state {
        case .pendingDown:
            return true
        case .selecting:
            onSelectionChanged?(firstDownPoint, currentPoint)
            return true
        case .doubleClickDown, .awaitingSecondClick:
            return true
        case .idle:
            return false
        }
    }

    private func beginFirstRightDown(at point: CGPoint, time: TimeInterval) {
        cancelTimers()
        firstDownPoint = point
        currentPoint = point
        firstDownTime = time
        state = .pendingDown

        let workItem = DispatchWorkItem { [weak self] in
            guard let self, self.state == .pendingDown else { return }
            self.state = .selecting
            self.setVisualState(.selecting)
            self.onSelectionBegan?(self.firstDownPoint)
        }
        longPressWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + longPressSeconds, execute: workItem)
    }

    private func scheduleSingleClickReplay() {
        replayWorkItem?.cancel()
        let workItem = DispatchWorkItem { [weak self] in
            guard let self, self.state == .awaitingSecondClick else { return }
            self.replayPendingRightClick()
            self.state = .idle
            self.setVisualState(.idle)
        }
        replayWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + NSEvent.doubleClickInterval, execute: workItem)
    }

    private func replayPendingRightClick() {
        replayWorkItem?.cancel()
        replayWorkItem = nil
        guard let source = CGEventSource(stateID: .combinedSessionState),
              let down = CGEvent(
                mouseEventSource: source,
                mouseType: .rightMouseDown,
                mouseCursorPosition: firstDownPoint,
                mouseButton: .right
              ),
              let up = CGEvent(
                mouseEventSource: source,
                mouseType: .rightMouseUp,
                mouseCursorPosition: firstDownPoint,
                mouseButton: .right
              )
        else { return }
        down.setIntegerValueField(.eventSourceUserData, value: Self.replayMarker)
        up.setIntegerValueField(.eventSourceUserData, value: Self.replayMarker)
        down.setIntegerValueField(.mouseEventClickState, value: 1)
        up.setIntegerValueField(.mouseEventClickState, value: 1)
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }

    private func cancelTimers() {
        longPressWorkItem?.cancel()
        replayWorkItem?.cancel()
        longPressWorkItem = nil
        replayWorkItem = nil
    }

    private func setVisualState(_ state: GestureVisualState) {
        onVisualStateChanged?(state)
    }
}
