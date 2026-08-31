import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        AppModel.shared.start()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationShouldHandleReopen(
        _ sender: NSApplication,
        hasVisibleWindows flag: Bool
    ) -> Bool {
        AppModel.shared.showMainWindow()
        return true
    }
}

@main
@MainActor
struct LumaTranslateApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel.shared

    var body: some Scene {
        WindowGroup("Luma Translate") {
            ControlCenterView()
                .environmentObject(model)
                .frame(minWidth: 780, minHeight: 650)
                .onAppear { model.start() }
                .alert("允许发送英文文字？", isPresented: $model.showCloudConsent) {
                    Button("取消", role: .cancel) { model.cancelCloudConsent() }
                    Button("允许并继续") { model.approveCloudConsent() }
                } message: {
                    Text(model.consentDescription)
                }
                .sheet(isPresented: $model.showAPIKeySheet) {
                    APIKeySheet()
                        .environmentObject(model)
                }
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 880, height: 720)
        .commands {
            CommandGroup(after: .pasteboard) {
                Button("翻译剪贴板") { model.translateClipboardOffline() }
                    .keyboardShortcut("v", modifiers: [.command, .shift])
            }
        }

        MenuBarExtra {
            MenuBarContent()
                .environmentObject(model)
        } label: {
            Image(systemName: model.gestureEnabled ? "character.book.closed.fill" : "character.book.closed")
                .accessibilityLabel("Luma Translate")
        }
        .menuBarExtraStyle(.menu)

        Settings {
            SettingsView()
                .environmentObject(model)
                .frame(width: 560, height: 520)
        }
    }
}
