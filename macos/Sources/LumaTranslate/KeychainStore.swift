import Foundation
import Security

enum KeychainStore {
    private static let service = "com.luma.translate.macos.ai"

    static func read(provider: AIProvider) -> String {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: provider.rawValue,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data,
              let value = String(data: data, encoding: .utf8)
        else { return "" }
        return value.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func save(_ value: String, provider: AIProvider) throws {
        let key = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if key.isEmpty {
            try delete(provider: provider)
            return
        }
        guard let data = key.data(using: .utf8) else {
            throw LumaError.message("API 密钥编码失败。 / The API key could not be encoded.")
        }
        let identity: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: provider.rawValue
        ]
        let status = SecItemUpdate(
            identity as CFDictionary,
            [kSecValueData as String: data] as CFDictionary
        )
        if status == errSecSuccess { return }
        guard status == errSecItemNotFound else {
            throw LumaError.message("无法更新钥匙串中的 API 密钥（\(status)）。 / Could not update Keychain.")
        }
        var add = identity
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        let addStatus = SecItemAdd(add as CFDictionary, nil)
        guard addStatus == errSecSuccess else {
            throw LumaError.message("无法把 API 密钥存入钥匙串（\(addStatus)）。 / Could not save to Keychain.")
        }
    }

    static func delete(provider: AIProvider) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: provider.rawValue
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw LumaError.message("无法从钥匙串删除 API 密钥（\(status)）。 / Could not delete from Keychain.")
        }
    }
}

@MainActor
final class CredentialVault {
    private var sessionKeys: [AIProvider: String] = [:]

    func key(for provider: AIProvider) -> String {
        if let session = sessionKeys[provider], !session.isEmpty { return session }
        if let environment = ProcessInfo.processInfo.environment[provider.environmentKeyName],
           !environment.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return environment.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return KeychainStore.read(provider: provider)
    }

    func save(_ key: String, provider: AIProvider, persist: Bool) throws {
        let normalized = key.trimmingCharacters(in: .whitespacesAndNewlines)
        sessionKeys[provider] = normalized
        if persist {
            try KeychainStore.save(normalized, provider: provider)
        } else {
            try KeychainStore.delete(provider: provider)
        }
    }

    func clear(provider: AIProvider) throws {
        sessionKeys[provider] = nil
        try KeychainStore.delete(provider: provider)
    }

    func hasSavedOrEnvironmentKey(for provider: AIProvider) -> Bool {
        !key(for: provider).isEmpty
    }
}
