import Foundation

private struct OfflineEntry: Sendable {
    var headword: String
    var phonetic: String
    var definition: String
    var translation: String
    var exchange: String
    var exampleEn = ""
    var exampleZh = ""
    var singaporeNote = ""
    var isCustom = false
}

final class OfflineDictionary: @unchecked Sendable {
    static let libraryVersion = "ECDICT-core-2026-07-22+SG-overlay-1-mac"

    private var entries: [String: OfflineEntry] = [:]
    private let irregularForms: [String: String] = [
        "went": "go", "gone": "go", "was": "be", "were": "be", "been": "be",
        "did": "do", "done": "do", "had": "have", "made": "make", "took": "take",
        "taken": "take", "gave": "give", "given": "give", "got": "get", "gotten": "get",
        "saw": "see", "seen": "see", "said": "say", "thought": "think", "bought": "buy",
        "brought": "bring", "children": "child", "men": "man", "women": "woman",
        "people": "person", "teeth": "tooth", "feet": "foot", "mice": "mouse"
    ]

    var entryCount: Int { entries.count }

    init(resourceURL: URL? = ResourceLocator.url(forResource: "offline_ecdict_core", withExtension: "tsv")) throws {
        guard let resourceURL else {
            throw LumaError.message("本地词库资源缺失，请重新下载完整应用。 / Offline dictionary resource is missing.")
        }
        try loadDictionary(from: resourceURL)
        addExchangeAliases()
        addSingaporeOverlay()
    }

    func hasExactEntry(_ englishText: String) -> Bool {
        let key = TextLogic.lookupKey(englishText)
        return !key.isEmpty && findEntry(for: key) != nil
    }

    func translate(_ englishText: String) throws -> TranslationResult {
        let source = TextLogic.normalizeInput(englishText)
        guard TextLogic.isEnglishInput(source) else {
            throw LumaError.message("此版本只支持英文原文译成简体中文。 / This version accepts English source text only.")
        }

        let key = TextLogic.lookupKey(source)
        if let match = findEntry(for: key) {
            return exactResult(source: source, entry: match.entry, matchKind: match.kind)
        }

        let words = TextLogic.englishWords(in: source)
        guard !words.isEmpty else {
            throw LumaError.message("没有检测到可查询的英文单词。 / No English words were found.")
        }

        var chineseLines: [String] = []
        var englishLines: [String] = []
        var unknown: [String] = []
        var seenDefinitions = Set<String>()
        var covered = 0
        var index = 0

        while index < words.count {
            var found: (entry: OfflineEntry, kind: String)?
            var foundText = ""
            var foundLength = 0
            let maximumLength = min(5, words.count - index)

            if maximumLength > 0 {
                for length in stride(from: maximumLength, through: 1, by: -1) {
                    let phrase = words[index..<(index + length)].joined(separator: " ")
                    if let candidate = findEntry(for: TextLogic.lookupKey(phrase)) {
                        found = candidate
                        foundText = phrase
                        foundLength = length
                        break
                    }
                }
            }

            guard let found else {
                let missing = words[index]
                if !unknown.contains(where: { $0.caseInsensitiveCompare(missing) == .orderedSame }) {
                    unknown.append(missing)
                }
                index += 1
                continue
            }

            covered += foundLength
            chineseLines.append("\(foundText)：\(firstLine(found.entry.translation))")
            let definition = firstLine(found.entry.definition)
            let definitionKey = definition.lowercased()
            if !definition.isEmpty, seenDefinitions.insert(definitionKey).inserted {
                englishLines.append("\(foundText): \(definition)")
            }
            index += foundLength
        }

        guard covered > 0 else {
            throw LumaError.message("本地词典没有找到这些英文词。可在设置中配置 AI 做上下文翻译。 / No offline entries were found.")
        }

        var result = TranslationResult()
        result.translation = chineseLines.joined(separator: "\n")
        result.meaningZh = "离线词典覆盖 \(covered)/\(words.count) 个英文词。"
        if !unknown.isEmpty {
            result.meaningZh += " 未收录：\(unknown.prefix(8).joined(separator: "、"))。"
        }
        result.meaningZh += " 词典无法判断完整句子的语法和语境；本次没有联网。"
        result.simpleEnglish = englishLines.isEmpty
            ? "Some words were found in the local dictionary."
            : englishLines.joined(separator: "\n")
        result.speakText = source
        result.provider = "offline"
        result.matchKind = "word_breakdown"
        result.coveredWords = covered
        result.totalWords = words.count
        return result
    }

    private func loadDictionary(from url: URL) throws {
        let raw: String
        do {
            raw = try String(contentsOf: url, encoding: .utf8)
        } catch {
            throw LumaError.message("本地词库无法读取。 / The offline dictionary could not be read.")
        }

        var lines = raw.split(separator: "\n", omittingEmptySubsequences: true).makeIterator()
        guard let header = lines.next(), header.hasPrefix("#SGFT-ECDICT-1\t") else {
            throw LumaError.message("本地词库格式不兼容。 / Offline dictionary format is incompatible.")
        }

        entries.reserveCapacity(60_000)
        while let rawLine = lines.next() {
            let line = rawLine.last == "\r" ? rawLine.dropLast() : rawLine[...]
            let fields = line.split(separator: "\t", omittingEmptySubsequences: false)
            guard fields.count == 5 else { continue }
            guard
                let headword = decodeBase64(fields[0]),
                let phonetic = decodeBase64(fields[1]),
                let definition = decodeBase64(fields[2]),
                let translation = decodeBase64(fields[3]),
                let exchange = decodeBase64(fields[4])
            else {
                throw LumaError.message("本地词库内容损坏。 / Offline dictionary data is damaged.")
            }
            let key = TextLogic.lookupKey(headword)
            if !key.isEmpty, !definition.isEmpty, !translation.isEmpty {
                entries[key] = OfflineEntry(
                    headword: headword,
                    phonetic: phonetic,
                    definition: definition,
                    translation: translation,
                    exchange: exchange
                )
            }
        }

        guard entries.count >= 10_000 else {
            throw LumaError.message("本地词库没有完整加载。 / Offline dictionary did not load completely.")
        }
    }

    private func decodeBase64(_ value: Substring) -> String? {
        guard let data = Data(base64Encoded: String(value)) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private func addExchangeAliases() {
        let snapshot = Array(entries.values)
        var aliases: [(String, OfflineEntry)] = []
        aliases.reserveCapacity(12_000)
        for entry in snapshot where !entry.exchange.isEmpty {
            for form in entry.exchange.split(separator: "/") {
                let value: Substring
                if let colon = form.firstIndex(of: ":") {
                    value = form[form.index(after: colon)...]
                } else {
                    value = form
                }
                let key = TextLogic.lookupKey(String(value))
                if !key.isEmpty, !key.contains(" "), entries[key] == nil {
                    aliases.append((key, entry))
                }
            }
        }
        for (key, entry) in aliases where entries[key] == nil {
            entries[key] = entry
        }
    }

    private func findEntry(for key: String) -> (entry: OfflineEntry, kind: String)? {
        if let entry = entries[key] { return (entry, "exact") }

        let alternate = key.contains("-")
            ? key.replacingOccurrences(of: "-", with: " ")
            : key.replacingOccurrences(of: " ", with: "-")
        if alternate != key, let entry = entries[alternate] {
            return (entry, "normalised")
        }

        guard !key.contains(" ") else { return nil }
        if let lemma = irregularForms[key], let entry = entries[lemma] {
            return (entry, "inflected")
        }
        for candidate in morphologyCandidates(for: key) {
            if let entry = entries[candidate] {
                return (entry, "inflected")
            }
        }
        return nil
    }

    private func morphologyCandidates(for word: String) -> [String] {
        var candidates: [String] = []
        if word.count > 4, word.hasSuffix("ies") {
            candidates.append(String(word.dropLast(3)) + "y")
        }
        if word.count > 4, word.hasSuffix("es") {
            candidates.append(String(word.dropLast(2)))
        }
        if word.count > 3, word.hasSuffix("s") {
            candidates.append(String(word.dropLast()))
        }
        if word.count > 5, word.hasSuffix("ing") {
            let stem = String(word.dropLast(3))
            candidates.append(stem)
            candidates.append(stem + "e")
            if stem.count > 2, stem.last == stem.dropLast().last {
                candidates.append(String(stem.dropLast()))
            }
        }
        if word.count > 4, word.hasSuffix("ed") {
            let stem = String(word.dropLast(2))
            candidates.append(stem)
            candidates.append(stem + "e")
        }
        return candidates
    }

    private func exactResult(source: String, entry: OfflineEntry, matchKind: String) -> TranslationResult {
        var result = TranslationResult()
        result.translation = entry.translation
        result.meaningZh = entry.isCustom
            ? "新加坡本地词条精确匹配；本次没有联网。"
            : (matchKind == "inflected"
                ? "已通过词形变化找到本地词典原形；本次没有联网。"
                : "本地词典精确匹配；本次没有联网。")
        result.simpleEnglish = entry.definition
        result.speakText = source
        result.exampleEn = entry.exampleEn
        result.exampleZh = entry.exampleZh
        result.singaporeNote = entry.singaporeNote
        result.provider = "offline"
        result.matchKind = matchKind
        result.phonetic = entry.phonetic
        result.partOfSpeech = partOfSpeech(for: entry)
        result.practicalUsageEn = entry.exampleEn
        result.practicalUsageZh = entry.exampleZh
        result.coveredWords = 1
        result.totalWords = 1
        return result
    }

    private func partOfSpeech(for entry: OfflineEntry) -> String {
        let source = entry.translation + "\n" + entry.definition
        let pattern = #"(?im)(?:^|[;；\r\n])\s*(n|v|vt|vi|adj|adv|prep|pron|conj|interj|aux|num|art|det|abbr)\."#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return "" }
        let range = NSRange(source.startIndex..<source.endIndex, in: source)
        var values: [String] = []
        for match in regex.matches(in: source, range: range) {
            guard let swiftRange = Range(match.range(at: 1), in: source) else { continue }
            let value = source[swiftRange].lowercased() + "."
            if !values.contains(value) { values.append(value) }
            if values.count == 3 { break }
        }
        if !values.isEmpty { return values.joined(separator: " / ") }
        return entry.headword.contains(" ") ? "phrase" : ""
    }

    private func firstLine(_ value: String) -> String {
        let line = value.components(separatedBy: .newlines).first ?? value
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.count > 220 ? String(trimmed.prefix(220)) + "…" : trimmed
    }

    private func addSingaporeOverlay() {
        let custom: [(String, String, String, String, String, String)] = [
            ("MRT", "地铁；新加坡大众捷运系统", "Singapore's Mass Rapid Transit train system.", "Take the MRT to City Hall.", "乘地铁到政府大厦站。", "新加坡常用缩写。"),
            ("HDB", "新加坡建屋发展局；组屋（按语境）", "Singapore's public housing authority, or an HDB flat.", "They live in an HDB flat.", "他们住在组屋里。", "新加坡住房常用语。"),
            ("CPF", "中央公积金", "Singapore's compulsory savings system for retirement, housing, and healthcare.", "Check your CPF balance.", "查看你的中央公积金余额。", "具体政策可能变化，请核对官方资料。"),
            ("BTO", "预购组屋", "A new HDB flat sold through Singapore's Build-To-Order scheme.", "They applied for a BTO flat.", "他们申请了预购组屋。", "新加坡住房用语。"),
            ("COE", "拥车证", "A Certificate of Entitlement needed to own a vehicle in Singapore.", "The COE price increased.", "拥车证价格上涨了。", "具体政策可能变化，请核对官方资料。"),
            ("ERP", "电子道路收费；公路电子收费", "Singapore's electronic road-pricing system.", "ERP charges apply here.", "这里收取电子道路费。", "新加坡交通用语。"),
            ("NRIC", "新加坡国民身份证", "Singapore's National Registration Identity Card.", "Do not share your NRIC number.", "不要透露你的身份证号码。", "属于敏感个人资料。"),
            ("Singpass", "新加坡政府数字身份账户", "A digital identity used to access Singapore government services.", "Log in with Singpass.", "使用 Singpass 登录。", "不要透露密码或验证码。"),
            ("PayNow", "新加坡即时转账服务", "A Singapore service for sending money instantly through a bank.", "You can pay by PayNow.", "你可以用 PayNow 付款。", "转账前核对收款人。"),
            ("EZ-Link", "易通卡；新加坡交通储值卡", "A stored-value card commonly used for public transport in Singapore.", "Top up your EZ-Link card.", "给易通卡充值。", "新加坡交通用语。"),
            ("hawker centre", "熟食中心；小贩中心", "A Singapore food centre with many affordable cooked-food stalls.", "Let's eat at the hawker centre.", "我们去熟食中心吃饭吧。", "新加坡中文通常称“熟食中心”。"),
            ("hawker center", "熟食中心；小贩中心", "The American-spelling form of hawker centre.", "Meet me at the hawker center.", "在熟食中心见我。", "新加坡通常采用英式拼写 centre。"),
            ("kopitiam", "咖啡店；传统食阁", "A traditional Southeast Asian coffee shop or local food court.", "We had breakfast at the kopitiam.", "我们在咖啡店吃了早餐。", "本地常用词。"),
            ("void deck", "组屋底层公共空间", "The open ground-floor area under many HDB blocks.", "Wait for me at the void deck.", "在组屋底层等我。", "新加坡组屋语境。"),
            ("wet market", "巴刹；传统生鲜市场", "A market that sells fresh meat, fish, vegetables, and other food.", "She buys fish at the wet market.", "她在巴刹买鱼。", "新加坡中文常说“巴刹”。"),
            ("kopi", "本地咖啡", "Singapore-style coffee, usually ordered with local terms for sugar and milk.", "One kopi, please.", "请来一杯本地咖啡。", "不同后缀表示糖和奶的搭配。"),
            ("kopi-o", "不加奶的本地咖啡（通常加糖）", "Singapore coffee without milk; it normally includes sugar unless you say kosong.", "I would like kopi-o.", "我要一杯不加奶的本地咖啡。", "点单用语。"),
            ("kopi-c", "加淡奶的本地咖啡", "Singapore coffee made with evaporated milk and usually sugar.", "She ordered kopi-c.", "她点了加淡奶的本地咖啡。", "点单用语。"),
            ("teh", "本地奶茶；茶", "The local word used when ordering Singapore-style tea.", "Two teh, please.", "请来两杯本地奶茶。", "具体配法取决于后缀。"),
            ("cai png", "菜饭；经济饭", "Rice served with a choice of cooked dishes at a local stall.", "Let's have cai png for lunch.", "午餐吃菜饭吧。", "源自方言的本地用语。"),
            ("zi char", "煮炒；点菜式中餐摊", "A local Chinese food stall serving cooked-to-order dishes for sharing.", "We ordered fish at the zi char stall.", "我们在煮炒摊点了鱼。", "也常写作 tze char。"),
            ("prata", "印度煎饼", "A flaky South Asian flatbread commonly eaten in Singapore.", "I ordered egg prata.", "我点了鸡蛋印度煎饼。", "本地餐饮用语。"),
            ("Singlish", "新加坡式英语", "Informal Singapore English influenced by several local languages.", "This sentence uses Singlish.", "这句话使用了新加坡式英语。", "正式场合通常改用标准英语。"),
            ("lah", "语气词（需结合语境）", "A Singlish particle that can add emphasis, friendliness, or insistence.", "Can lah.", "可以啦。", "没有单一固定译法，需结合语气。"),
            ("leh", "语气词（需结合语境）", "A Singlish particle often used to soften a statement or show contrast.", "Different leh.", "不一样咧。", "没有单一固定译法。"),
            ("lor", "语气词（需结合语境）", "A Singlish particle that may show resignation or that something is obvious.", "Like that lor.", "就是这样咯。", "没有单一固定译法。"),
            ("meh", "表示疑问或怀疑的语气词", "A Singlish particle used to show doubt or ask if something is really true.", "Really meh?", "真的吗？", "非正式用语。"),
            ("shiok", "很爽；非常过瘾；很好吃（依语境）", "A Singlish word for a very enjoyable or satisfying feeling.", "The food was shiok.", "这食物太好吃了。", "非正式用语。"),
            ("chope", "占位；预留座位", "In Singapore, to reserve a seat, often by leaving a small item on the table.", "Please chope a table.", "请先占一张桌子。", "非正式本地用语。"),
            ("paiseh", "不好意思；害羞；尴尬", "A local word for feeling embarrassed, shy, or sorry.", "Paiseh, I am late.", "不好意思，我迟到了。", "源自福建话的非正式用语。"),
            ("kiasu", "怕输；唯恐落后", "Very worried about losing out or missing an advantage.", "Do not be so kiasu.", "不要那么怕输。", "可带玩笑或批评意味。"),
            ("blur", "迷糊；搞不清楚", "In Singlish, confused, unaware, or slow to understand what is happening.", "I was blur this morning.", "我今天早上很迷糊。", "非正式本地用法。"),
            ("makan", "吃饭；食物", "A Malay-derived local word meaning to eat or food.", "Let's go makan.", "我们去吃饭吧。", "非正式本地用语。"),
            ("atas", "高档的；装高级的", "A local informal word for something expensive, fashionable, or high-class.", "That restaurant is very atas.", "那家餐厅很高档。", "有时带调侃意味。"),
            ("can or not", "可以吗？行不行？", "An informal Singlish way to ask whether something is possible or allowed.", "Tomorrow can or not?", "明天可以吗？", "正式英语可说 Is tomorrow possible?"),
            ("take the MRT", "乘地铁", "Travel somewhere using Singapore's MRT train system.", "Take the MRT to Orchard.", "乘地铁去乌节路。", "新加坡交通常用表达。")
        ]

        for item in custom {
            addCustom(
                headword: item.0,
                translation: item.1,
                definition: item.2,
                exampleEn: item.3,
                exampleZh: item.4,
                note: item.5
            )
        }
    }

    private func addCustom(
        headword: String,
        translation: String,
        definition: String,
        exampleEn: String,
        exampleZh: String,
        note: String
    ) {
        entries[TextLogic.lookupKey(headword)] = OfflineEntry(
            headword: headword,
            phonetic: "",
            definition: definition,
            translation: translation,
            exchange: "",
            exampleEn: exampleEn,
            exampleZh: exampleZh,
            singaporeNote: note,
            isCustom: true
        )
    }
}
