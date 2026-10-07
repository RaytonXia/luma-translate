import AppKit
import Foundation

@MainActor
enum GestureRuntimeVerification {
    static func run(model: AppModel, directory: URL) async throws -> [String] {
        guard let fixturePath = ProcessInfo.processInfo.environment["LUMA_GESTURE_FIXTURE"] else {
            throw LumaError.message("External gesture fixture is required for runtime verification")
        }
        model.refreshPermissions()
        guard model.accessibilityGranted, model.screenCaptureGranted else {
            throw LumaError.message("Live gesture verification requires existing Accessibility and Screen Recording permissions; no permission was changed")
        }
        guard !model.providerHasKey else { throw LumaError.message("Gesture verification must run without a cloud API key") }
        if !model.gestureEnabled { model.toggleGestureMode() }
        guard model.gestureEnabled else { throw LumaError.message("Global event tap did not start") }
        let path=directory.appendingPathComponent("fixture.json")
        try? FileManager.default.removeItem(at:path)
        let fixture=Process()
        fixture.executableURL=URL(fileURLWithPath:fixturePath)
        fixture.arguments=[path.path]
        try fixture.run()
        defer { if fixture.isRunning { fixture.terminate() } }
        for _ in 0..<100 {
            if FileManager.default.fileExists(atPath:path.path) { break }
            try await Task.sleep(nanoseconds:100_000_000)
        }
        let points=try JSONSerialization.jsonObject(with:Data(contentsOf:path)) as! [String:[String:Double]]
        func point(_ name:String)->CGPoint { CGPoint(x:points[name]!["x"]!,y:points[name]!["y"]!) }
        func post(_ type:CGEventType,_ point:CGPoint,_ button:CGMouseButton = .right) {
            let event=CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:point,mouseButton:button)!
            event.flags=[]
            event.post(tap:.cghidEventTap)
        }
        func pause(_ ms:UInt64) async throws { try await Task.sleep(nanoseconds:ms*1_000_000) }
        func waitForText(_ text:String) async throws {
            for _ in 0..<200 {
                if model.currentResult?.speakText.lowercased()==text, !model.isWorking { return }
                try await pause(100)
            }
            throw LumaError.message("Real gesture did not produce expected text: \(text); status=\(model.statusMessage); error=\(model.errorMessage)")
        }
        func popup() throws -> NSWindow {
            guard let value=NSApp.windows.first(where:{$0.title=="Luma · 释义" && $0.isVisible}) else {
                throw LumaError.message("Gesture completed without a visible result popup")
            }
            return value
        }
        func render(_ window:NSWindow,_ name:String) throws {
            guard let view=window.contentView else { throw LumaError.message("Missing popup view") }
            view.layoutSubtreeIfNeeded()
            guard let bitmap=view.bitmapImageRepForCachingDisplay(in:view.bounds) else { throw LumaError.message("Popup render failed") }
            view.cacheDisplay(in:view.bounds,to:bitmap)
            try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent(name+".png"))
        }
        // The external fixture covers the still-open main window, exercising window occlusion.
        try await pause(400)
        model.currentResult=nil
        post(.rightMouseDown,point("word")); try await pause(60)
        post(.rightMouseUp,point("word")); try await pause(80)
        post(.rightMouseDown,point("word")); try await pause(60)
        post(.rightMouseUp,point("word"))
        try await waitForText("significant")
        let card=try popup()
        try render(card,"popup-default")
        let inside=CGPoint(x:card.frame.minX+90,y:CGDisplayBounds(CGMainDisplayID()).height-card.frame.maxY+170)
        post(.leftMouseDown,inside,.left); try await pause(60); post(.leftMouseUp,inside,.left)
        try await pause(250)
        guard card.isVisible else { throw LumaError.message("Clicking inside dismissed the popup") }
        let scroll=CGEvent(scrollWheelEvent2Source:nil,units:.pixel,wheelCount:1,wheel1:-850,wheel2:0,wheel3:0)!
        scroll.location=inside; scroll.post(tap:.cghidEventTap)
        try await pause(400)
        guard card.isVisible else { throw LumaError.message("Scrolling dismissed the popup") }
        try render(card,"popup-scrolled")
        card.setContentSize(NSSize(width:740,height:700))
        try await pause(250)
        try render(card,"popup-expanded")
        // Dismiss by clicking outside, then perform an immediate right drag with no AI key.
        post(.leftMouseDown,point("word"),.left); post(.leftMouseUp,point("word"),.left)
        try await pause(150)
        model.currentResult=nil
        post(.rightMouseDown,point("start")); try await pause(60)
        post(.rightMouseDragged,point("end")); try await pause(160)
        post(.rightMouseUp,point("end"))
        try await waitForText("confidence interval")
        let selectionCard=try popup()
        try render(selectionCard,"popup-drag")
        let checks=["Real global right-double-click -> screen capture -> Vision -> significant -> popup over a covered Luma main window",
                    "Click and scroll inside popup keep it visible; native popup default and resized rendering",
                    "Immediate right-drag without API key -> screen capture -> confidence interval -> offline popup"]
        try JSONSerialization.data(withJSONObject:["passed":true,"checks":checks],options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("gestures.json"))
        return checks
    }
}
