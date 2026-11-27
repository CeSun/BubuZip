import AppKit
import Darwin
import FinderSync
import Foundation

@main
struct FinderRequestTransportTests {
    static var assertions = 0

    static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
        assertions += 1
        if try !condition() { throw TestFailure(message: message) }
    }

    static func expectFailure(_ message: String, _ operation: () throws -> Void) throws {
        assertions += 1
        do { try operation() } catch { return }
        throw TestFailure(message: message)
    }

    static func main() throws {
        let files = FileManager.default
        let root = files.temporaryDirectory.appendingPathComponent("BubuZip.FinderTests-" + UUID().uuidString, isDirectory: true)
        try files.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? files.removeItem(at: root) }

        let selected = [root.appendingPathComponent("中文 📦 空格 & $(literal).txt"), root.appendingPathComponent("quote \"item\".zip")]
        let folder = root.appendingPathComponent("target", isDirectory: true)
        let kinds: [FIMenuKind] = [.contextualMenuForItems, .contextualMenuForContainer, .contextualMenuForSidebar]
        var tags = Set<Int>()
        for kind in kinds {
            for action in FinderArchiveAction.allCases {
                let command = FinderMenuCommand(action: action, menuKind: kind)
                // Model the copy Finder sends back: tag survives, representedObject is absent.
                let copiedItem = NSMenuItem(title: "copied by Finder", action: nil, keyEquivalent: "")
                copiedItem.tag = command.tag
                copiedItem.representedObject = nil
                try expect(FinderMenuCommand(tag: copiedItem.tag) == command, "Command must survive Finder's menu copy.")
                try expect(tags.insert(command.tag).inserted, "Every context/action needs a distinct tag.")
                let paths = try command.paths(selected: selected, targeted: folder)
                try expect(paths == (kind == .contextualMenuForItems ? selected.map(\.path) : [folder.path]),
                           "Action must use the correct Finder selection context.")
            }
        }
        try expect(FinderMenuCommand(tag: 0) == nil && FinderMenuCommand(tag: -1) == nil, "Unknown tags must fail closed.")
        // One past the largest known action keeps this probe unknown as actions are added.
        let unknownAction = 0xEC000 + (FinderArchiveAction.allCases.map(\.rawValue).max() ?? 0) + 1
        try expect(FinderMenuCommand(tag: unknownAction) == nil && FinderMenuCommand(tag: 0xEC301) == nil,
                   "Unknown action/context tags must fail closed.")
        let open = FinderMenuCommand(action: .open, menuKind: .contextualMenuForItems)
        try expectFailure("Missing selection must not launch anything.") { _ = try open.paths(selected: nil, targeted: folder) }
        try expectFailure("Non-file URLs must not become shell paths.") { _ = try open.paths(selected: [URL(string: "https://example.com")!], targeted: nil) }

        let app = root.appendingPathComponent("含 空格的 BubuZip.app", isDirectory: true)
        let contents = app.appendingPathComponent("Contents", isDirectory: true)
        let plugin = contents.appendingPathComponent("PlugIns/BubuZipFinder.appex", isDirectory: true)
        try files.createDirectory(at: plugin, withIntermediateDirectories: true)
        let info: [String: Any] = ["CFBundleIdentifier": FinderRequestTransport.bundleIdentifier, "CFBundlePackageType": "APPL"]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            .write(to: contents.appendingPathComponent("Info.plist"))
        try expect(try FinderRequestTransport.containingApplication(extensionBundle: plugin) == app,
                   "The class's extension bundle must resolve to the correct containing app.")
        try expectFailure("Unrelated bundles must not resolve to arbitrary apps.") {
            _ = try FinderRequestTransport.containingApplication(extensionBundle: root)
        }

        let requests = root.appendingPathComponent("Application Support/BubuZip/FinderRequests", isDirectory: true)
        let date = Date()
        var delivered: [URL] = []
        let transport = FinderRequestTransport(directory: requests, now: { date }, launch: { urls, target, configuration, completion in
            delivered.append(contentsOf: urls)
            do {
                try expect(target == app, "Launch must target the containing app URL.")
                try expect(configuration.arguments.isEmpty, "Sandboxed extension must never rely on argv.")
                try expect(configuration.createsNewApplicationInstance && configuration.activates,
                           "Every command must create and activate an operation instance.")
            } catch { fatalError(error.localizedDescription) }
            completion(nil)
        })
        var launchFailed = false
        try transport.send(action: .compressZip, paths: selected.map(\.path), application: app) { _ in launchFailed = true }
        try expect(!launchFailed && delivered.count == 1, "Compression combines the complete selection into one document.")
        let document = delivered[0]
        try expect(document.pathExtension == FinderRequestTransport.documentExtension, "Transport needs its own document extension.")
        try expect(UUID(uuidString: document.deletingPathExtension().lastPathComponent)?.uuidString.lowercased()
                   == document.deletingPathExtension().lastPathComponent, "Request names must use canonical lower-case UUIDs.")
        let attributes = try files.attributesOfItem(atPath: document.path)
        let directoryAttributes = try files.attributesOfItem(atPath: requests.path)
        try expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600, "Request contents must be private.")
        try expect((directoryAttributes[.posixPermissions] as? NSNumber)?.intValue == 0o700, "Request directory must be private.")
        let data = try Data(contentsOf: document)
        let decoded = try JSONDecoder().decode(FinderArchiveRequest.self, from: data)
        try expect(decoded.version == 1 && decoded.action == "compress-zip" && decoded.createdAt == Int64(date.timeIntervalSince1970),
                   "Request must use the agreed version/action/Unix-time contract.")
        try expect(decoded.paths == selected.map(\.path), "Unicode, spaces, quotes and shell characters must remain literal.")
        let object = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        try expect(Set(object.keys) == ["version", "action", "paths", "createdAt"], "Wire request must contain only the agreed fields.")
        try expect(!files.fileExists(atPath: document.deletingPathExtension().appendingPathExtension("pending").path),
                   "Atomic publication must not leave a pending file.")

        delivered.removeAll()
        try transport.send(action: .extract, paths: selected.map(\.path), application: app) { _ in launchFailed = true }
        try expect(delivered.count == 2, "Non-compression actions get one request per selected archive.")
        for (index, url) in delivered.enumerated() {
            let entry = try JSONDecoder().decode(FinderArchiveRequest.self, from: Data(contentsOf: url))
            try expect(entry.paths == [selected[index].path], "Each archive request must preserve its own selection.")
        }

        var failedURL: URL?
        var sawFailure = false
        let failure = FinderRequestTransport(directory: requests, now: { date }, launch: { urls, _, _, completion in
            failedURL = urls.first
            completion(TestFailure(message: "simulated launch refusal"))
        })
        try failure.send(action: .compress, paths: [selected[0].path], application: app) { _ in sawFailure = true }
        try expect(sawFailure && failedURL != nil && !files.fileExists(atPath: failedURL!.path),
                   "A failed launch must report the error and remove its unpublished request.")

        let stale = requests.appendingPathComponent(UUID().uuidString.lowercased() + "." + FinderRequestTransport.documentExtension)
        try Data("stale".utf8).write(to: stale)
        try files.setAttributes([.posixPermissions: 0o600, .modificationDate: date.addingTimeInterval(-7200)], ofItemAtPath: stale.path)
        let unrelated = requests.appendingPathComponent("keep.txt")
        try Data("unrelated".utf8).write(to: unrelated)
        try files.setAttributes([.modificationDate: date.addingTimeInterval(-7200)], ofItemAtPath: unrelated.path)
        let link = requests.appendingPathComponent(UUID().uuidString.lowercased() + "." + FinderRequestTransport.documentExtension)
        try files.createSymbolicLink(at: link, withDestinationURL: unrelated)
        try transport.send(action: .compress, paths: [selected[0].path], application: app) { _ in launchFailed = true }
        try expect(!files.fileExists(atPath: stale.path), "Only old, owned request documents should be cleaned.")
        try expect(files.fileExists(atPath: unrelated.path) && (try? files.destinationOfSymbolicLink(atPath: link.path)) != nil,
                   "Cleanup must preserve unrelated files and symbolic links.")
        try expect(files.fileExists(atPath: document.path), "Recent pending documents must remain available to the main app.")

        let beforeRejected = try files.contentsOfDirectory(atPath: requests.path).count
        try expectFailure("Excessive path counts must be rejected.") {
            try transport.send(action: .compress, paths: Array(repeating: "/tmp/a", count: 257), application: app) { _ in }
        }
        try expectFailure("Relative paths must be rejected.") {
            try transport.send(action: .compress, paths: ["relative.txt"], application: app) { _ in }
        }
        try expectFailure("Null characters must be rejected.") {
            try transport.send(action: .compress, paths: ["/tmp/a\0b"], application: app) { _ in }
        }
        for control in ["\n", "\t", "\u{0085}"] {
            try expectFailure("Control characters must be rejected consistently with the main app.") {
                try transport.send(action: .compress, paths: ["/tmp/a" + control + "b"], application: app) { _ in }
            }
        }
        try expectFailure("Oversized request documents must be rejected.") {
            try transport.send(action: .compress, paths: Array(repeating: "/" + String(repeating: "x", count: 2048), count: 256), application: app) { _ in }
        }
        try expect(try files.contentsOfDirectory(atPath: requests.path).count == beforeRejected,
                   "Rejected input must not leave request or temporary files.")

        print("PASS: \(assertions) Finder dispatch/request checks (no Finder GUI or system registration).")
    }
}

private struct TestFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
