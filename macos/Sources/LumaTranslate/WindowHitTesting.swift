import AppKit
import CoreGraphics

enum WindowHitTesting {
    // CGWindowList is ordered front to back. A covered Luma window must never
    // prevent lookup in the document that is actually under the pointer.
    static func isOwnInteractiveWindow(at point: CGPoint, windows: [[String: Any]],
                                       ownPID: Int32, interactiveNumbers: Set<Int>) -> Bool {
        for info in windows {
            guard let bounds = info[kCGWindowBounds as String] as? [String: Any],
                  let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary),
                  rect.contains(point),
                  ((info[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1) > 0,
                  let pid = info[kCGWindowOwnerPID as String] as? NSNumber else { continue }
            let number = (info[kCGWindowNumber as String] as? NSNumber)?.intValue ?? -1
            if pid.int32Value == ownPID {
                if interactiveNumbers.contains(number) { return true }
                continue // Non-interactive badge and selection overlay.
            }
            return false
        }
        return false
    }
}
