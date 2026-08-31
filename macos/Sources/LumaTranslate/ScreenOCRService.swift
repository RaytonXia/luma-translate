import AppKit
import CoreGraphics
import Foundation
import Vision

struct OCRWordHit: Sendable {
    let word: String
    let line: String
    let bounds: CGRect
    let anchor: CGPoint
}

enum ScreenCoordinates {
    static func appKitPoint(fromQuartz point: CGPoint) -> CGPoint {
        for screen in NSScreen.screens {
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
                continue
            }
            let displayBounds = CGDisplayBounds(CGDirectDisplayID(number.uint32Value))
            if displayBounds.contains(point) {
                return CGPoint(
                    x: screen.frame.minX + point.x - displayBounds.minX,
                    y: screen.frame.maxY - (point.y - displayBounds.minY)
                )
            }
        }
        let mainHeight = NSScreen.screens.first?.frame.height ?? 0
        return CGPoint(x: point.x, y: mainHeight - point.y)
    }

    static func appKitRect(fromQuartz rect: CGRect) -> CGRect {
        let topLeft = appKitPoint(fromQuartz: CGPoint(x: rect.minX, y: rect.minY))
        let bottomRight = appKitPoint(fromQuartz: CGPoint(x: rect.maxX, y: rect.maxY))
        return CGRect(
            x: min(topLeft.x, bottomRight.x),
            y: min(topLeft.y, bottomRight.y),
            width: abs(bottomRight.x - topLeft.x),
            height: abs(bottomRight.y - topLeft.y)
        )
    }

    static func screen(containingQuartz point: CGPoint) -> NSScreen? {
        for screen in NSScreen.screens {
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
                continue
            }
            if CGDisplayBounds(CGDirectDisplayID(number.uint32Value)).contains(point) {
                return screen
            }
        }
        return NSScreen.main
    }
}

final class ScreenOCRService: @unchecked Sendable {
    static var hasScreenCapturePermission: Bool {
        CGPreflightScreenCaptureAccess()
    }

    @discardableResult
    static func requestScreenCapturePermission() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    func recognizeNearestWord(at point: CGPoint) async throws -> OCRWordHit {
        try await Task.detached(priority: .userInitiated) {
            let region = try Self.captureRegion(around: point)
            let image = try Self.capture(region: region)
            return try Self.nearestWord(in: image, region: region, click: point)
        }.value
    }

    func recognizeSelection(in selection: CGRect) async throws -> String {
        try await Task.detached(priority: .userInitiated) {
            let standardized = selection.standardized
            guard standardized.width >= 12, standardized.height >= 8 else {
                throw LumaError.message("框选区域太小，请长按右键后拖过完整句子。 / The selection is too small.")
            }
            let image = try Self.capture(region: standardized)
            return try Self.selectionText(in: image)
        }.value
    }

    private static func captureRegion(around point: CGPoint) throws -> CGRect {
        var displayID = CGMainDisplayID()
        var count: UInt32 = 0
        let status = CGGetDisplaysWithPoint(point, 1, &displayID, &count)
        guard status == .success, count > 0 else {
            throw LumaError.message("无法确定鼠标所在屏幕。 / Could not locate the display.")
        }
        let bounds = CGDisplayBounds(displayID)
        let desired = CGRect(x: point.x - 340, y: point.y - 125, width: 680, height: 250)
        let clipped = desired.intersection(bounds)
        guard !clipped.isNull, clipped.width > 20, clipped.height > 20 else {
            throw LumaError.message("鼠标附近没有可截取的屏幕区域。 / No capturable area near the pointer.")
        }
        return clipped
    }

    private static func capture(region: CGRect) throws -> CGImage {
        guard hasScreenCapturePermission else {
            throw LumaError.message("需要“屏幕录制”权限才能在本机做 OCR。请到系统设置 → 隐私与安全性 → 屏幕录制中允许 Luma Translate。")
        }
        guard let image = CGWindowListCreateImage(
            region,
            .optionOnScreenOnly,
            kCGNullWindowID,
            [.bestResolution, .boundsIgnoreFraming]
        ) else {
            throw LumaError.message("屏幕截图失败。若刚刚授权，请退出并重新打开 Luma Translate。 / Screen capture failed.")
        }
        return image
    }

    private static func makeRequest() -> VNRecognizeTextRequest {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.recognitionLanguages = ["en-US"]
        request.usesLanguageCorrection = true
        request.minimumTextHeight = 0.012
        return request
    }

    private static func observations(in image: CGImage) throws -> [VNRecognizedTextObservation] {
        let request = makeRequest()
        let handler = VNImageRequestHandler(cgImage: image, orientation: .up, options: [:])
        do {
            try handler.perform([request])
        } catch {
            throw LumaError.message("本地 Vision OCR 失败：\(error.localizedDescription)")
        }
        return request.results ?? []
    }

    private static func nearestWord(in image: CGImage, region: CGRect, click: CGPoint) throws -> OCRWordHit {
        let observations = try observations(in: image)
        let pixelWidth = CGFloat(image.width)
        let pixelHeight = CGFloat(image.height)
        let scaleX = pixelWidth / region.width
        let scaleY = pixelHeight / region.height
        let clickInVisionPixels = CGPoint(
            x: (click.x - region.minX) * scaleX,
            y: pixelHeight - ((click.y - region.minY) * scaleY)
        )
        let regex = try NSRegularExpression(
            pattern: #"[A-Za-zÀ-ÖØ-öø-ÿ]+(?:['’\-][A-Za-zÀ-ÖØ-öø-ÿ]+)*"#
        )

        var best: (word: String, line: String, pixelRect: CGRect, distance: CGFloat)?
        for observation in observations {
            guard let candidate = observation.topCandidates(1).first, candidate.confidence >= 0.20 else { continue }
            let line = candidate.string
            let fullRange = NSRange(line.startIndex..<line.endIndex, in: line)
            for match in regex.matches(in: line, range: fullRange) {
                guard let range = Range(match.range, in: line) else { continue }
                let box: VNRectangleObservation
                do {
                    guard let value = try candidate.boundingBox(for: range) else { continue }
                    box = value
                } catch {
                    continue
                }
                let pixelRect = VNImageRectForNormalizedRect(
                    box.boundingBox,
                    Int(pixelWidth),
                    Int(pixelHeight)
                )
                let distance = distance(from: clickInVisionPixels, to: pixelRect)
                if best == nil || distance < best!.distance {
                    best = (String(line[range]), line, pixelRect, distance)
                }
            }
        }

        guard let best, best.distance <= max(120, pixelHeight * 0.42) else {
            throw LumaError.message("鼠标附近没有识别到英文单词。请把指针放到单词中央再试。 / No English word was found nearby.")
        }
        let globalBounds = CGRect(
            x: region.minX + best.pixelRect.minX / scaleX,
            y: region.minY + (pixelHeight - best.pixelRect.maxY) / scaleY,
            width: best.pixelRect.width / scaleX,
            height: best.pixelRect.height / scaleY
        )
        return OCRWordHit(
            word: TextLogic.lookupKey(best.word),
            line: best.line,
            bounds: globalBounds,
            anchor: click
        )
    }

    private static func selectionText(in image: CGImage) throws -> String {
        let observations = try observations(in: image)
        let lines: [(text: String, box: CGRect)] = observations.compactMap { observation in
            guard let candidate = observation.topCandidates(1).first,
                  candidate.confidence >= 0.18,
                  !TextLogic.englishWords(in: candidate.string).isEmpty
            else { return nil }
            return (candidate.string, observation.boundingBox)
        }
        .sorted { lhs, rhs in
            let verticalDifference = abs(lhs.box.midY - rhs.box.midY)
            if verticalDifference > 0.025 { return lhs.box.midY > rhs.box.midY }
            return lhs.box.minX < rhs.box.minX
        }

        let text = TextLogic.normalizedSelection(lines.map { $0.text }.joined(separator: "\n"))
        guard !text.isEmpty else {
            throw LumaError.message("框选区域没有识别到英文。 / No English text was recognized in the selection.")
        }
        guard TextLogic.isEnglishInput(text) else {
            throw LumaError.message("框选内容不像英文，请重新框选。 / The selected text does not appear to be English.")
        }
        guard text.count <= TextLogic.maxInputCharacters else {
            throw LumaError.message("框选文字超过 3000 个字符，请缩小范围。 / The selection exceeds 3000 characters.")
        }
        return text
    }

    private static func distance(from point: CGPoint, to rect: CGRect) -> CGFloat {
        let dx = max(max(rect.minX - point.x, 0), point.x - rect.maxX)
        let dy = max(max(rect.minY - point.y, 0), point.y - rect.maxY)
        return hypot(dx, dy)
    }
}
