import AppKit
import SwiftUI

private struct GlassCard<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        content
            .padding(18)
            .background(.thinMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .strokeBorder(Color.white.opacity(0.28), lineWidth: 1)
            )
    }
}

private struct LumaLogoView: View {
    var size: CGFloat = 40

    var body: some View {
        Group {
            if let url = ResourceLocator.url(forResource: "luma-logo-icon", withExtension: "png"),
               let image = NSImage(contentsOf: url) {
                Image(nsImage: image).resizable().scaledToFit()
            } else {
                Image(systemName: "character.book.closed.fill")
                    .resizable().scaledToFit().foregroundStyle(LumaPalette.orbitGradient)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

private struct OrbitalStatusView: View {
    let state: GestureVisualState
    let enabled: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(minimumInterval: reduceMotion ? 2 : 1.0 / 30.0, paused: !enabled || reduceMotion)) { context in
            let phase = context.date.timeIntervalSinceReferenceDate
            ZStack {
                Circle()
                    .stroke(LumaPalette.cyan.opacity(0.16), lineWidth: 1)
                    .frame(width: 88, height: 88)
                Circle()
                    .stroke(LumaPalette.violet.opacity(0.13), style: StrokeStyle(lineWidth: 1, dash: [4, 6]))
                    .frame(width: 116, height: 116)
                    .rotationEffect(.degrees(reduceMotion ? 0 : phase * 10))
                Circle()
                    .fill(enabled ? LumaPalette.orbitGradient : LinearGradient(colors: [.gray.opacity(0.45)], startPoint: .top, endPoint: .bottom))
                    .frame(width: state == .processing ? 47 : 42, height: state == .processing ? 47 : 42)
                    .shadow(color: enabled ? LumaPalette.cyan.opacity(0.42) : .clear, radius: 12)
                    .overlay {
                        Image(systemName: state == .selecting ? "selection.pin.in.out" : "cursorarrow.rays")
                            .font(.system(size: 18, weight: .semibold))
                            .foregroundStyle(.white)
                    }
                if enabled {
                    Circle()
                        .fill(LumaPalette.cyan)
                        .frame(width: 9, height: 9)
                        .offset(x: 44)
                        .rotationEffect(.degrees(reduceMotion ? 0 : phase * 48))
                    Circle()
                        .fill(LumaPalette.violet)
                        .frame(width: 7, height: 7)
                        .offset(x: -58)
                        .rotationEffect(.degrees(reduceMotion ? 180 : -phase * 31))
                }
            }
            .frame(width: 132, height: 132)
        }
    }
}

private struct PermissionRow: View {
    let title: String
    let detail: String
    let granted: Bool
    let action: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: granted ? "checkmark.circle.fill" : "circle.dashed")
                .foregroundStyle(granted ? LumaPalette.success : LumaPalette.coral)
                .font(.system(size: 16, weight: .semibold))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 12.5, weight: .semibold, design: .rounded))
                Text(detail).font(.system(size: 10.5)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            if !granted {
                Button("允许", action: action)
                    .controlSize(.small)
            }
        }
    }
}

private struct GestureRail: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(spacing: 7) {
                OrbitalStatusView(state: model.gestureVisualState, enabled: model.gestureEnabled)
                Text(model.gestureEnabled ? model.gestureVisualState.label : "手势已暂停")
                    .font(.system(size: 15, weight: .bold, design: .rounded))
                Text(model.gestureEnabled ? "Luma 在鼠标旁待命" : "主窗口查询仍可使用")
                    .font(.system(size: 10.5, weight: .medium, design: .monospaced))
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)

            Button(action: model.toggleGestureMode) {
                Label(model.gestureEnabled ? "暂停全局手势" : "开启全局手势", systemImage: model.gestureEnabled ? "pause.fill" : "play.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(model.gestureEnabled ? LumaPalette.slate : LumaPalette.violet)
            .controlSize(.large)

            Divider()

            VStack(alignment: .leading, spacing: 13) {
                Text("两项本机权限")
                    .font(.system(size: 11, weight: .bold, design: .rounded))
                    .tracking(0.5)
                    .foregroundStyle(.secondary)
                PermissionRow(
                    title: "辅助功能",
                    detail: "识别全局右键手势",
                    granted: model.accessibilityGranted,
                    action: model.requestAccessibilityPermission
                )
                PermissionRow(
                    title: "屏幕录制",
                    detail: "只截取 OCR 所需区域",
                    granted: model.screenCaptureGranted,
                    action: model.requestScreenCapturePermission
                )
            }

            Divider()

            VStack(alignment: .leading, spacing: 11) {
                GestureHint(symbol: "computermouse.fill", title: "右键双击", detail: "鼠标所在单词 · 离线点译")
                GestureHint(symbol: "selection.pin.in.out", title: "长按后拖拽", detail: "框选完整句子 · 可选 AI")
            }

            Spacer(minLength: 0)

            Label("截图不保存，也不上传", systemImage: "lock.shield.fill")
                .font(.system(size: 10.5, weight: .semibold))
                .foregroundStyle(LumaPalette.success)
        }
        .padding(20)
        .frame(width: 238)
        .background(LumaPalette.ink.opacity(0.96))
        .foregroundStyle(.white)
    }
}

private struct GestureHint: View {
    let symbol: String
    let title: String
    let detail: String

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: symbol)
                .frame(width: 19)
                .foregroundStyle(LumaPalette.cyan)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 12, weight: .semibold, design: .rounded))
                Text(detail).font(.system(size: 10.5)).foregroundStyle(.white.opacity(0.58))
            }
        }
    }
}

private struct ResultCard: View {
    let result: TranslationResult
    @EnvironmentObject private var model: AppModel

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(result.provider == "offline" ? "OFFLINE DICTIONARY" : result.provider.uppercased())
                            .font(.system(size: 10, weight: .bold, design: .rounded))
                            .tracking(1.1)
                            .foregroundStyle(result.provider == "offline" ? LumaPalette.success : LumaPalette.violet)
                        HStack(spacing: 8) {
                            if !result.partOfSpeech.isEmpty {
                                Text(result.partOfSpeech)
                                    .font(.system(size: 11, weight: .medium, design: .monospaced))
                                    .foregroundStyle(.secondary)
                            }
                            if !result.phonetic.isEmpty {
                                Text("/\(result.phonetic)/")
                                    .font(.system(size: 11, design: .monospaced))
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                    Spacer()
                    Button(action: model.speakCurrent) { Image(systemName: "speaker.wave.2") }
                        .help("朗读英文")
                    Button(action: model.copyCurrentTranslation) { Image(systemName: "doc.on.doc") }
                        .help("复制中文")
                }

                Text(result.translation)
                    .font(.system(size: 23, weight: .semibold, design: .rounded))
                    .foregroundStyle(.primary)
                    .textSelection(.enabled)

                if !result.simpleEnglish.isEmpty {
                    ResultSection(title: "PLAIN ENGLISH", primary: result.simpleEnglish)
                }
                if !result.practicalUsageZh.isEmpty || !result.practicalUsageEn.isEmpty {
                    ResultSection(title: "日常用法", primary: result.practicalUsageZh, secondary: result.practicalUsageEn)
                }
                if !result.exampleEn.isEmpty {
                    ResultSection(title: "例句", primary: result.exampleEn, secondary: result.exampleZh)
                }
                if !result.singaporeNote.isEmpty {
                    ResultSection(title: "SINGAPORE", primary: result.singaporeNote)
                }

                Text(result.meaningZh)
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
            }
        }
    }
}

private struct ResultSection: View {
    let title: String
    let primary: String
    var secondary = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.system(size: 9.5, weight: .bold, design: .rounded))
                .tracking(1)
                .foregroundStyle(LumaPalette.violet)
            Text(primary).font(.system(size: 13.5)).textSelection(.enabled)
            if !secondary.isEmpty {
                Text(secondary).font(.system(size: 12.5)).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct ControlCenterView: View {
    @EnvironmentObject private var model: AppModel
    @FocusState private var inputFocused: Bool

    var body: some View {
        HStack(spacing: 0) {
            GestureRail()
                .environmentObject(model)

            ZStack {
                LumaPalette.paper.opacity(0.72).ignoresSafeArea()
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        header
                        inputCard
                        if let result = model.currentResult {
                            ResultCard(result: result).environmentObject(model)
                        } else {
                            emptyState
                        }
                        statusFooter
                    }
                    .padding(24)
                    .frame(maxWidth: 720)
                    .frame(maxWidth: .infinity)
                }
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var header: some View {
        HStack(spacing: 12) {
            LumaLogoView(size: 46)
            VStack(alignment: .leading, spacing: 2) {
                Text("Luma Translate")
                    .font(.system(size: 25, weight: .bold, design: .rounded))
                    .foregroundStyle(LumaPalette.ink)
                Text("英文停在眼前，中文就出现在手边。")
                    .font(.system(size: 12.5))
                    .foregroundStyle(LumaPalette.slate)
            }
            Spacer()
            Button {
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            } label: {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.bordered)
            .help("设置")
        }
    }

    private var inputCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text("英文原文")
                        .font(.system(size: 11, weight: .bold, design: .rounded))
                        .tracking(0.7)
                    Spacer()
                    Text("\(model.inputText.count) / \(TextLogic.maxInputCharacters)")
                        .font(.system(size: 10, design: .monospaced))
                        .foregroundStyle(model.inputText.count > TextLogic.maxInputCharacters ? LumaPalette.coral : .secondary)
                }
                TextEditor(text: $model.inputText)
                    .font(.system(size: 15))
                    .scrollContentBackground(.hidden)
                    .focused($inputFocused)
                    .frame(minHeight: 84, maxHeight: 135)
                    .padding(9)
                    .background(Color.white.opacity(0.58))
                    .clipShape(RoundedRectangle(cornerRadius: 11, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .strokeBorder(Color.primary.opacity(0.08), lineWidth: 1)
                    )
                    .accessibilityLabel("英文原文")

                HStack(spacing: 10) {
                    Button(action: model.translateClipboardOffline) {
                        Label("粘贴", systemImage: "doc.on.clipboard")
                    }
                    Button(action: model.translateOffline) {
                        Label("离线查词", systemImage: "book.closed")
                    }
                    .keyboardShortcut(.return, modifiers: [.command])
                    .disabled(!model.isDictionaryReady || model.isWorking)
                    Spacer()
                    Picker("AI", selection: $model.provider) {
                        ForEach(AIProvider.allCases) { provider in
                            Text(provider.displayName).tag(provider)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 105)
                    Button(action: model.translateWithAI) {
                        Label("AI 上下文", systemImage: "sparkles")
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(LumaPalette.violet)
                    .disabled(model.isWorking)
                }
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "text.magnifyingglass")
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(LumaPalette.orbitGradient)
            Text("从一个英文单词开始")
                .font(.system(size: 16, weight: .semibold, design: .rounded))
            Text("在这里输入，或开启左侧手势后直接在任意应用中右键双击。")
                .font(.system(size: 12.5))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, minHeight: 180)
    }

    private var statusFooter: some View {
        HStack(spacing: 8) {
            if model.isWorking { ProgressView().controlSize(.small) }
            else {
                Circle()
                    .fill(model.errorMessage.isEmpty ? LumaPalette.success : LumaPalette.coral)
                    .frame(width: 7, height: 7)
            }
            Text(model.statusMessage)
                .font(.system(size: 10.5, weight: .medium, design: .monospaced))
                .foregroundStyle(model.errorMessage.isEmpty ? LumaPalette.slate : LumaPalette.coral)
                .lineLimit(2)
            Spacer()
            if model.isDictionaryReady {
                Text("\(model.dictionaryEntryCount.formatted()) entries")
                    .font(.system(size: 9.5, design: .monospaced))
                    .foregroundStyle(.secondary)
            }
        }
    }
}

struct APIKeySheet: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                LumaLogoView(size: 38)
                VStack(alignment: .leading, spacing: 2) {
                    Text("配置 AI 翻译")
                        .font(.system(size: 20, weight: .bold, design: .rounded))
                    Text("点译默认离线；只有你主动使用 AI 时才会联网。")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                }
            }

            Picker("服务", selection: $model.provider) {
                ForEach(AIProvider.allCases) { Text($0.displayName).tag($0) }
            }
            .pickerStyle(.segmented)

            VStack(alignment: .leading, spacing: 6) {
                Text("模型").font(.system(size: 11, weight: .semibold))
                TextField("模型名称", text: Binding(
                    get: { model.currentModel },
                    set: { model.setCurrentModel($0) }
                ))
                .textFieldStyle(.roundedBorder)
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("API Key").font(.system(size: 11, weight: .semibold))
                SecureField("输入 \(model.provider.displayName) API Key", text: $model.apiKeyDraft)
                    .textFieldStyle(.roundedBorder)
                Text("目标主机固定为 \(model.provider.host)，应用不会跟随重定向转发密钥。")
                    .font(.system(size: 10.5)).foregroundStyle(.secondary)
            }

            Toggle("保存到 macOS 钥匙串", isOn: $model.rememberAPIKey)
            if !model.errorMessage.isEmpty {
                Text(model.errorMessage).font(.system(size: 11)).foregroundStyle(LumaPalette.coral)
            }

            HStack {
                Button("移除已保存密钥", role: .destructive) { model.clearAPIKey() }
                Spacer()
                Button("取消") { model.cancelCloudConsent(); dismiss() }
                Button("保存并继续") { model.saveAPIKey() }
                    .buttonStyle(.borderedProminent)
                    .tint(LumaPalette.violet)
                    .disabled(model.apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(24)
        .frame(width: 500)
    }
}

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        TabView {
            Form {
                Picker("AI 服务", selection: $model.provider) {
                    ForEach(AIProvider.allCases) { Text($0.displayName).tag($0) }
                }
                TextField("模型", text: Binding(
                    get: { model.currentModel },
                    set: { model.setCurrentModel($0) }
                ))
                HStack {
                    Text(model.providerHasKey ? "密钥已配置" : "尚未配置密钥")
                    Spacer()
                    Button("管理密钥") {
                        model.apiKeyDraft = model.credentials.key(for: model.provider)
                        model.showAPIKeySheet = true
                        model.showMainWindow()
                    }
                }
                Text("AI 翻译是可选功能。离线词典和 Vision OCR 不会调用网络。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(22)
            .tabItem { Label("AI", systemImage: "sparkles") }

            Form {
                Picker("英语声音", selection: $model.selectedVoiceID) {
                    Text("系统推荐").tag("")
                    ForEach(model.voices) { voice in
                        Text(voice.displayName).tag(voice.id)
                    }
                }
                Button("试听") { model.speak("Luma Translate is ready.") }
                Text("朗读完全使用 macOS 本机语音。可在系统设置的辅助功能或语音设置中下载更多声音。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(22)
            .tabItem { Label("朗读", systemImage: "speaker.wave.2") }

            VStack(alignment: .leading, spacing: 16) {
                Label("截图只在内存中用于 Vision OCR，完成后立即释放。", systemImage: "rectangle.dashed.badge.record")
                Label("离线点译不联网；词典包含 47,149 条 ECDICT 核心记录。", systemImage: "book.closed")
                Label("API Key 使用 macOS 钥匙串保存。", systemImage: "key.fill")
                Label("通用二进制：Apple Silicon arm64 + Intel x86_64。", systemImage: "cpu")
                Spacer()
                Text("Luma Translate for macOS · MIT License")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(28)
            .tabItem { Label("隐私", systemImage: "lock.shield") }
        }
    }
}

struct MenuBarContent: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        Button("打开 Luma Translate") { model.showMainWindow() }
        Button(model.gestureEnabled ? "暂停全局手势" : "开启全局手势") { model.toggleGestureMode() }
        Divider()
        Text(model.gestureEnabled ? "右键双击：离线点译" : "手势已暂停")
        Text("AI：\(model.provider.displayName)")
        Divider()
        Button("退出") { model.quit() }
    }
}
