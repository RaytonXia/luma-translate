import AppKit
import Combine
import Foundation

@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    @Published var inputText = ""
    @Published var currentResult: TranslationResult?
    @Published var statusMessage = "正在准备本地词典…"
    @Published var errorMessage = ""
    @Published var isDictionaryReady = false
    @Published var dictionaryEntryCount = 0
    @Published var isWorking = false
    @Published var gestureEnabled = false
    @Published var gestureVisualState: GestureVisualState = .idle
    @Published var accessibilityGranted = false
    @Published var screenCaptureGranted = false
    @Published var showCloudConsent = false
    @Published var showAPIKeySheet = false
    @Published var apiKeyDraft = ""
    @Published var rememberAPIKey = true
    @Published var isSpeaking = false

    @Published var provider: AIProvider {
        didSet {
            defaults.set(provider.rawValue, forKey: Keys.provider)
            apiKeyDraft = credentials.key(for: provider)
        }
    }
    @Published var deepSeekModel: String {
        didSet { defaults.set(deepSeekModel, forKey: Keys.deepSeekModel) }
    }
    @Published var geminiModel: String {
        didSet { defaults.set(geminiModel, forKey: Keys.geminiModel) }
    }
    @Published var selectedVoiceID: String {
        didSet { defaults.set(selectedVoiceID, forKey: Keys.voiceID) }
    }

    let speech = SpeechService()
    let credentials = CredentialVault()

    var voices: [SpeechVoice] { speech.voices }
    var currentModel: String { provider == .deepseek ? deepSeekModel : geminiModel }
    var providerHasKey: Bool { credentials.hasSavedOrEnvironmentKey(for: provider) }

    var consentDescription: String {
        "只有识别出的英文文字会发送到 \(provider.host)（\(provider.serviceName)）。屏幕截图始终留在本机，不会保存，也不会上传。"
    }

    private enum Keys {
        static let provider = "ai.provider"
        static let deepSeekModel = "ai.deepseek.model"
        static let geminiModel = "ai.gemini.model"
        static let voiceID = "speech.voice"
        static let wantsGesture = "gesture.wantsEnabled"
        static func consent(_ provider: AIProvider) -> String { "ai.consent.\(provider.rawValue).host" }
    }

    private struct PendingAIRequest {
        let text: String
        let sentenceOnly: Bool
        let popupAnchor: CGPoint?
    }

    private let defaults = UserDefaults.standard
    private let ocr = ScreenOCRService()
    private let aiClient = AITranslationClient()
    private let mouse = MouseGestureController()
    private let selectionOverlay = SelectionOverlayController()
    private let cursorBadge = CursorBadgeController()
    private let popup = QuickPopupController()
    private var dictionary: OfflineDictionary?
    private var pendingAIRequest: PendingAIRequest?
    private var workTask: Task<Void, Never>?
    private var aiTask: Task<Void, Never>?
    private var aiRequestVersion = 0
    private var started = false
    private var activationObserver: NSObjectProtocol?

    private init() {
        let storedDefaults = UserDefaults.standard
        let providerValue = storedDefaults.string(forKey: Keys.provider) ?? AIProvider.deepseek.rawValue
        provider = AIProvider(rawValue: providerValue) ?? .deepseek
        deepSeekModel = storedDefaults.string(forKey: Keys.deepSeekModel) ?? AIProvider.deepseek.defaultModel
        geminiModel = storedDefaults.string(forKey: Keys.geminiModel) ?? AIProvider.gemini.defaultModel
        selectedVoiceID = storedDefaults.string(forKey: Keys.voiceID) ?? ""
        apiKeyDraft = credentials.key(for: provider)

        speech.onSpeakingChanged = { [weak self] speaking in self?.isSpeaking = speaking }
        configureMouseCallbacks()
    }

    func start() {
        guard !started else { return }
        started = true
        refreshPermissions()
        loadDictionary()
        activationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.refreshPermissions()
                self?.startGestureIfPossible()
            }
        }
        startGestureIfPossible()
    }

    func refreshPermissions() {
        accessibilityGranted = MouseGestureController.isAccessibilityTrusted(prompt: false)
        screenCaptureGranted = ScreenOCRService.hasScreenCapturePermission
    }

    func requestAccessibilityPermission() {
        _ = MouseGestureController.isAccessibilityTrusted(prompt: true)
        defaults.set(true, forKey: Keys.wantsGesture)
        statusMessage = "请在系统设置中允许 Luma Translate 使用辅助功能，然后返回应用。"
        openPrivacySettings(section: "Privacy_Accessibility")
        refreshPermissions()
    }

    func requestScreenCapturePermission() {
        _ = ScreenOCRService.requestScreenCapturePermission()
        defaults.set(true, forKey: Keys.wantsGesture)
        statusMessage = "请允许屏幕录制；若授权后仍不可用，请退出并重新打开应用。"
        refreshPermissions()
        if !screenCaptureGranted { openPrivacySettings(section: "Privacy_ScreenCapture") }
    }

    func toggleGestureMode() {
        if gestureEnabled {
            defaults.set(false, forKey: Keys.wantsGesture)
            stopGestureMode()
        } else {
            defaults.set(true, forKey: Keys.wantsGesture)
            refreshPermissions()
            if !accessibilityGranted {
                requestAccessibilityPermission()
                return
            }
            if !screenCaptureGranted {
                requestScreenCapturePermission()
                return
            }
            startGestureIfPossible(force: true)
        }
    }

    func translateOffline() {
        let text = TextLogic.normalizeInput(inputText)
        guard validateInput(text) else { return }
        guard let dictionary else {
            setError("本地词典仍在加载，请稍后再试。 / The offline dictionary is still loading.")
            return
        }
        cancelCurrentWork()
        isWorking = true
        statusMessage = "正在查询本地词典…"
        workTask = Task { [weak self] in
            do {
                let result = try await Task.detached(priority: .userInitiated) {
                    try dictionary.translate(text)
                }.value
                guard !Task.isCancelled else { return }
                self?.currentResult = result
                self?.statusMessage = "离线完成 · 没有联网"
                self?.errorMessage = ""
            } catch is CancellationError {
                return
            } catch {
                self?.setError(error.localizedDescription)
            }
            self?.isWorking = false
        }
    }

    func translateWithAI() {
        let text = TextLogic.normalizeInput(inputText)
        guard validateInput(text) else { return }
        requestAI(text: text, sentenceOnly: false, popupAnchor: nil)
    }

    func translateClipboardOffline() {
        guard let value = NSPasteboard.general.string(forType: .string), !value.isEmpty else {
            setError("剪贴板里没有文字。 / The clipboard contains no text.")
            return
        }
        inputText = String(value.prefix(TextLogic.maxInputCharacters))
        translateOffline()
    }

    func approveCloudConsent() {
        defaults.set(provider.host, forKey: Keys.consent(provider))
        showCloudConsent = false
        guard let pending = pendingAIRequest else { return }
        pendingAIRequest = nil
        beginAITranslation(pending)
    }

    func cancelCloudConsent() {
        showCloudConsent = false
        pendingAIRequest = nil
        popup.setAIBusy(false)
        statusMessage = "已取消联网翻译；截图和离线词典未联网。"
    }

    func saveAPIKey() {
        do {
            try credentials.save(apiKeyDraft, provider: provider, persist: rememberAPIKey)
            showAPIKeySheet = false
            errorMessage = ""
            statusMessage = rememberAPIKey
                ? "\(provider.displayName) 密钥已安全存入 macOS 钥匙串。"
                : "\(provider.displayName) 密钥只保留到本次退出。"
            if let pending = pendingAIRequest {
                pendingAIRequest = nil
                requestAI(
                    text: pending.text,
                    sentenceOnly: pending.sentenceOnly,
                    popupAnchor: pending.popupAnchor
                )
            }
        } catch {
            setError(error.localizedDescription)
        }
    }

    func clearAPIKey() {
        do {
            try credentials.clear(provider: provider)
            apiKeyDraft = ""
            statusMessage = "已从 macOS 钥匙串移除 \(provider.displayName) 密钥。"
        } catch {
            setError(error.localizedDescription)
        }
    }

    func selectProvider(_ value: AIProvider) {
        provider = value
    }

    func setCurrentModel(_ value: String) {
        if provider == .deepseek { deepSeekModel = value }
        else { geminiModel = value }
    }

    func speakCurrent() {
        guard let result = currentResult else { return }
        speech.speak(result.speakText, voiceID: selectedVoiceID)
    }

    func speak(_ text: String) {
        speech.speak(text, voiceID: selectedVoiceID)
    }

    func stopSpeaking() {
        speech.stop()
    }

    func copyCurrentTranslation() {
        guard let translation = currentResult?.translation, !translation.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(translation, forType: .string)
        statusMessage = "已复制中文翻译。"
    }

    func showMainWindow() {
        NSApp.activate(ignoringOtherApps: true)
        if let window = NSApp.windows.first(where: { $0.canBecomeMain && $0.level == .normal }) {
            window.makeKeyAndOrderFront(nil)
        }
    }

    func quit() {
        stopGestureMode()
        NSApp.terminate(nil)
    }

    private func configureMouseCallbacks() {
        mouse.shouldHandlePoint = { [weak self] point in
            guard let self else { return false }
            return !self.isInsideOwnInteractiveWindow(quartzPoint: point)
        }
        mouse.onVisualStateChanged = { [weak self] state in
            self?.gestureVisualState = state
            self?.cursorBadge.setState(state)
        }
        mouse.onLeftMouseDown = { [weak self] in self?.popup.hide() }
        mouse.onPointGesture = { [weak self] point in self?.recognizePoint(at: point) }
        mouse.onSelectionBegan = { [weak self] point in self?.selectionOverlay.show(start: point, current: point) }
        mouse.onSelectionChanged = { [weak self] start, current in
            self?.selectionOverlay.show(start: start, current: current)
        }
        mouse.onSelectionFinished = { [weak self] rect, anchor in
            self?.recognizeSelection(rect, anchor: anchor)
        }
    }

    private func loadDictionary() {
        Task { [weak self] in
            do {
                let loaded = try await Task.detached(priority: .userInitiated) {
                    try OfflineDictionary()
                }.value
                self?.dictionary = loaded
                self?.dictionaryEntryCount = loaded.entryCount
                self?.isDictionaryReady = true
                self?.statusMessage = "本地词典已就绪 · \(loaded.entryCount.formatted()) 条 · 默认不联网"
            } catch {
                self?.setError(error.localizedDescription)
            }
        }
    }

    private func startGestureIfPossible(force: Bool = false) {
        refreshPermissions()
        let wantsGesture = force || defaults.bool(forKey: Keys.wantsGesture)
        guard wantsGesture, accessibilityGranted, screenCaptureGranted, !gestureEnabled else { return }
        do {
            try mouse.start()
            cursorBadge.start()
            gestureEnabled = true
            statusMessage = "全局手势已开启 · 右键双击点译 · 长按右键后拖拽译句"
        } catch {
            setError(error.localizedDescription)
        }
    }

    private func stopGestureMode() {
        mouse.stop()
        cursorBadge.stop()
        selectionOverlay.hide()
        popup.hide()
        gestureEnabled = false
        gestureVisualState = .idle
        statusMessage = "全局手势已暂停；主窗口离线查询仍可使用。"
    }

    private func recognizePoint(at point: CGPoint) {
        guard let dictionary else {
            popup.showMessage("本地词典仍在加载，请稍后再试。", at: point, isError: true)
            return
        }
        cancelCurrentWork()
        popup.hide()
        cursorBadge.hideForCapture()
        isWorking = true
        workTask = Task { [weak self] in
            do {
                try await Task.sleep(nanoseconds: 80_000_000)
                guard let self else { return }
                let hit = try await self.ocr.recognizeNearestWord(at: point)
                let result = try await Task.detached(priority: .userInitiated) {
                    try dictionary.translate(hit.word)
                }.value
                guard !Task.isCancelled else { return }
                self.inputText = hit.word
                self.currentResult = result
                self.statusMessage = "离线点译完成 · 截图未保存"
                self.cursorBadge.restoreAfterCapture()
                self.popup.showResult(
                    result,
                    at: hit.anchor,
                    onSpeak: { [weak self] in self?.speak(result.speakText) },
                    onAI: { [weak self] in
                        let context = TextLogic.isEnglishInput(hit.line) ? hit.line : hit.word
                        self?.requestAI(text: context, sentenceOnly: false, popupAnchor: hit.anchor)
                    }
                )
            } catch is CancellationError {
                self?.cursorBadge.restoreAfterCapture()
            } catch {
                self?.cursorBadge.restoreAfterCapture()
                self?.popup.showMessage(error.localizedDescription, at: point, isError: true)
                self?.setError(error.localizedDescription)
            }
            self?.isWorking = false
            self?.gestureVisualState = .idle
            self?.cursorBadge.setState(.idle)
        }
    }

    private func recognizeSelection(_ rect: CGRect, anchor: CGPoint) {
        cancelCurrentWork()
        selectionOverlay.hide()
        popup.hide()
        cursorBadge.hideForCapture()
        isWorking = true
        workTask = Task { [weak self] in
            do {
                try await Task.sleep(nanoseconds: 90_000_000)
                guard let self else { return }
                let text = try await self.ocr.recognizeSelection(in: rect)
                guard !Task.isCancelled else { return }
                self.cursorBadge.restoreAfterCapture()
                self.inputText = text
                self.requestAI(text: text, sentenceOnly: true, popupAnchor: anchor)
            } catch is CancellationError {
                self?.cursorBadge.restoreAfterCapture()
            } catch {
                self?.cursorBadge.restoreAfterCapture()
                self?.popup.showMessage(error.localizedDescription, at: anchor, isError: true)
                self?.setError(error.localizedDescription)
            }
            if self?.aiTask == nil { self?.isWorking = false }
            self?.gestureVisualState = .idle
            self?.cursorBadge.setState(.idle)
        }
    }

    private func requestAI(text: String, sentenceOnly: Bool, popupAnchor: CGPoint?) {
        let normalized = TextLogic.normalizeInput(text)
        guard validateInput(normalized) else {
            if let popupAnchor { popup.showMessage(errorMessage, at: popupAnchor, isError: true) }
            return
        }
        guard providerHasKey else {
            pendingAIRequest = PendingAIRequest(text: normalized, sentenceOnly: sentenceOnly, popupAnchor: popupAnchor)
            apiKeyDraft = ""
            showAPIKeySheet = true
            popup.setAIBusy(false)
            showMainWindow()
            statusMessage = "请先配置 \(provider.displayName) API 密钥。"
            return
        }
        let pending = PendingAIRequest(text: normalized, sentenceOnly: sentenceOnly, popupAnchor: popupAnchor)
        guard hasCloudConsent(for: provider) else {
            pendingAIRequest = pending
            showCloudConsent = true
            popup.setAIBusy(false)
            showMainWindow()
            return
        }
        beginAITranslation(pending)
    }

    private func beginAITranslation(_ request: PendingAIRequest) {
        aiTask?.cancel()
        aiRequestVersion += 1
        let requestVersion = aiRequestVersion
        let selectedProvider = provider
        let selectedModel = currentModel
        let key = credentials.key(for: selectedProvider)
        popup.setAIBusy(request.popupAnchor != nil)
        isWorking = true
        statusMessage = "正在通过 \(selectedProvider.displayName) 翻译…"
        aiTask = Task { [weak self] in
            do {
                guard let self else { return }
                let result = try await self.aiClient.translate(
                    provider: selectedProvider,
                    model: selectedModel,
                    apiKey: key,
                    englishText: request.text,
                    sentenceOnly: request.sentenceOnly
                )
                guard !Task.isCancelled, requestVersion == self.aiRequestVersion else { return }
                self.currentResult = result
                self.errorMessage = ""
                self.statusMessage = "\(selectedProvider.displayName) 翻译完成"
                if let anchor = request.popupAnchor {
                    if self.popup.currentResult != nil {
                        self.popup.updateResult(result)
                    } else {
                        self.popup.showResult(
                            result,
                            at: anchor,
                            onSpeak: { [weak self] in self?.speak(result.speakText) },
                            onAI: nil
                        )
                    }
                }
            } catch is CancellationError {
                return
            } catch {
                guard let self, requestVersion == self.aiRequestVersion else { return }
                self.popup.setAIBusy(false)
                if let anchor = request.popupAnchor {
                    self.popup.showMessage(error.localizedDescription, at: anchor, isError: true)
                }
                self.setError(error.localizedDescription)
            }
            if let self, requestVersion == self.aiRequestVersion {
                self.isWorking = false
                self.aiTask = nil
            }
        }
    }

    private func hasCloudConsent(for provider: AIProvider) -> Bool {
        defaults.string(forKey: Keys.consent(provider)) == provider.host
    }

    private func validateInput(_ text: String) -> Bool {
        errorMessage = ""
        guard !text.isEmpty else {
            setError("请输入或框选英文。 / Enter or select English text.")
            return false
        }
        guard text.count <= TextLogic.maxInputCharacters else {
            setError("英文不能超过 3000 个字符。 / English input cannot exceed 3000 characters.")
            return false
        }
        guard TextLogic.isEnglishInput(text) else {
            setError("目前只支持英文译简体中文。 / Only English-to-Simplified-Chinese is supported.")
            return false
        }
        return true
    }

    private func cancelCurrentWork() {
        workTask?.cancel()
        aiTask?.cancel()
        aiRequestVersion += 1
        workTask = nil
        aiTask = nil
        speech.stop()
    }

    private func setError(_ message: String) {
        errorMessage = message
        statusMessage = message
        isWorking = false
    }

    private func isInsideOwnInteractiveWindow(quartzPoint: CGPoint) -> Bool {
        let point = ScreenCoordinates.appKitPoint(fromQuartz: quartzPoint)
        return NSApp.windows.contains { window in
            window.isVisible && !window.ignoresMouseEvents && window.frame.contains(point)
        }
    }

    private func openPrivacySettings(section: String) {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(section)") else { return }
        NSWorkspace.shared.open(url)
    }
}
