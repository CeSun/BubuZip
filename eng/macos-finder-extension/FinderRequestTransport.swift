import AppKit
import Darwin
import FinderSync
import Foundation

enum FinderArchiveAction: Int, CaseIterable {
    case open = 1, extract, extractHere, extractFolder, test, compress, compress7z, compressZip, extractSmart

    var verb: String {
        switch self {
        case .open: return "open"
        case .extract: return "extract"
        case .extractHere: return "extract-here"
        case .extractSmart: return "extract-smart"
        case .extractFolder: return "extract-folder"
        case .test: return "test"
        case .compress: return "compress"
        case .compress7z: return "compress-7z"
        case .compressZip: return "compress-zip"
        }
    }

    var combinesSelection: Bool { self == .compress || self == .compress7z || self == .compressZip }
}

struct FinderMenuCommand: Equatable {
    let action: FinderArchiveAction
    let menuKind: Int

    // Finder copies NSMenuItem across processes. Its supported scalar tag survives;
    // representedObject and the original item instance do not.
    var tag: Int { 0xEC000 + menuKind * 0x100 + action.rawValue }

    init(action: FinderArchiveAction, menuKind: FIMenuKind) {
        self.action = action
        self.menuKind = Int(menuKind.rawValue)
    }

    init?(tag: Int) {
        let value = tag - 0xEC000
        guard value >= 0, value < 0x300,
              let action = FinderArchiveAction(rawValue: value & 0xFF) else { return nil }
        self.action = action
        self.menuKind = value >> 8
    }

    func paths(selected: [URL]?, targeted: URL?) throws -> [String] {
        let urls: [URL]
        if menuKind == Int(FIMenuKind.contextualMenuForItems.rawValue) {
            urls = selected ?? []
        } else {
            urls = targeted.map { [$0] } ?? []
        }
        guard urls.allSatisfy({ $0.isFileURL }) else {
            throw FinderRequestFailure.invalidSelection
        }
        let paths = urls.map(\.path)
        try FinderRequestTransport.validatePaths(paths)
        return paths
    }
}

enum FinderRequestFailure: LocalizedError {
    case invalidSelection, unsupportedControlCharacters, missingApplication, oversizedRequest, unsafeDirectory, writeFailed(Int32)

    var errorDescription: String? {
        switch self {
        case .invalidSelection: return "请重新选择有效的文件或文件夹，单次最多选择 256 项。"
        case .unsupportedControlCharacters: return "路径含暂不支持的控制字符。"
        case .missingApplication: return "未能定位 BubuZip.app，请重新安装完整应用包。"
        case .oversizedRequest: return "所选路径过多或过长，请减少单次选择的项目。"
        case .unsafeDirectory: return "无法安全创建访达菜单请求，请检查应用的数据目录权限。"
        case .writeFailed(let code): return "无法保存访达菜单请求（系统错误 \(code)）。"
        }
    }
}

struct FinderArchiveRequest: Codable {
    let version: Int
    let action: String
    let paths: [String]
    let createdAt: Int64
}

/// A private request document is delivered as a file-open event. App Sandbox
/// deliberately strips NSWorkspace.OpenConfiguration.arguments, so argv cannot be
/// the transport from a Finder Sync extension to its containing application.
final class FinderRequestTransport {
    static let maximumPaths = 256
    static let maximumBytes = 256 * 1024
    static let documentExtension = "bubuzip-request"
    static let bundleIdentifier = "io.bubuzip.app"

    typealias Launcher = ([URL], URL, NSWorkspace.OpenConfiguration, @escaping (Error?) -> Void) -> Void

    private let directory: URL
    private let now: () -> Date
    private let launch: Launcher

    init(directory: URL? = nil, now: @escaping () -> Date = Date.init,
         launch: @escaping Launcher = { urls, app, configuration, completion in
             NSWorkspace.shared.open(urls, withApplicationAt: app, configuration: configuration) { _, error in
                 completion(error)
             }
         }) {
        self.directory = directory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("BubuZip", isDirectory: true)
            .appendingPathComponent("FinderRequests", isDirectory: true)
        self.now = now
        self.launch = launch
    }

    static func containingApplication(extensionBundle: URL) throws -> URL {
        var candidate = extensionBundle.standardizedFileURL
        while candidate.path != "/" {
            if candidate.pathExtension.lowercased() == "app",
               Bundle(url: candidate)?.bundleIdentifier == bundleIdentifier { return candidate }
            candidate.deleteLastPathComponent()
        }
        throw FinderRequestFailure.missingApplication
    }

    func send(action: FinderArchiveAction, paths: [String], application: URL,
              onFailure: @escaping (Error) -> Void) throws {
        try Self.validatePaths(paths)
        let batches = action.combinesSelection ? [paths] : paths.map { [$0] }
        try prepareDirectory()
        cleanExpiredRequests()
        for batch in batches {
            let requestURL = try write(action: action, paths: batch)
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = true
            configuration.createsNewApplicationInstance = true
            configuration.promptsUserIfNeeded = true
            // Pass the request as a document, never as launch arguments.
            launch([requestURL], application, configuration) { error in
                guard let error else { return }
                try? FileManager.default.removeItem(at: requestURL)
                onFailure(error)
            }
        }
    }

    static func validatePaths(_ paths: [String]) throws {
        guard !paths.isEmpty, paths.count <= maximumPaths,
              paths.allSatisfy({ $0.hasPrefix("/") }) else {
            throw FinderRequestFailure.invalidSelection
        }
        // Match the main app's char.IsControl validation (Unicode category Cc).
        guard !paths.contains(where: { path in
            path.unicodeScalars.contains { $0.properties.generalCategory == .control }
        }) else { throw FinderRequestFailure.unsupportedControlCharacters }
    }

    private func prepareDirectory() throws {
        // Application Support itself belongs to the extension's private container.
        // Reject links and foreign ownership in the two directories we create/use.
        for url in [directory.deletingLastPathComponent(), directory] {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            guard attributes[.type] as? FileAttributeType == .typeDirectory,
                  (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == getuid() else {
                throw FinderRequestFailure.unsafeDirectory
            }
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
        }
    }

    private func write(action: FinderArchiveAction, paths: [String]) throws -> URL {
        let request = FinderArchiveRequest(version: 1, action: action.verb, paths: paths,
                                           createdAt: Int64(now().timeIntervalSince1970))
        let data = try JSONEncoder().encode(request)
        guard data.count <= Self.maximumBytes else { throw FinderRequestFailure.oversizedRequest }
        let identifier = UUID().uuidString.lowercased()
        let temporary = directory.appendingPathComponent(identifier + ".pending")
        let destination = directory.appendingPathComponent(identifier + "." + Self.documentExtension)
        let descriptor = Darwin.open(temporary.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, mode_t(0o600))
        guard descriptor >= 0 else { throw FinderRequestFailure.writeFailed(errno) }
        var descriptorOpen = true
        defer {
            if descriptorOpen { Darwin.close(descriptor) }
            try? FileManager.default.removeItem(at: temporary)
        }
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let written = Darwin.write(descriptor, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if written < 0 && errno == EINTR { continue }
                guard written > 0 else { throw FinderRequestFailure.writeFailed(errno) }
                offset += written
            }
        }
        guard fsync(descriptor) == 0 else { throw FinderRequestFailure.writeFailed(errno) }
        Darwin.close(descriptor)
        descriptorOpen = false
        try FileManager.default.moveItem(at: temporary, to: destination)
        return destination
    }

    private func cleanExpiredRequests() {
        let cutoff = now().addingTimeInterval(-3600)
        guard let entries = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else { return }
        for entry in entries {
            guard entry.pathExtension == Self.documentExtension,
                  UUID(uuidString: entry.deletingPathExtension().lastPathComponent)?.uuidString.lowercased()
                    == entry.deletingPathExtension().lastPathComponent,
                  let attributes = try? FileManager.default.attributesOfItem(atPath: entry.path),
                  attributes[.type] as? FileAttributeType == .typeRegular,
                  (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == getuid(),
                  let modified = attributes[.modificationDate] as? Date, modified < cutoff else { continue }
            try? FileManager.default.removeItem(at: entry)
        }
    }
}
