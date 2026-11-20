import Cocoa
import Darwin
import FinderSync

/// Finder supplies the selection; the application performs every archive operation.
@objc(EclipseArchiveFinderSync)
final class EclipseArchiveFinderSync: FIFinderSync {
    private static let archiveExtensions: Set<String> = [
        "zip", "zipx", "7z", "rar", "tar", "gz", "gzip", "tgz", "bz2", "bzip2",
        "tbz", "tbz2", "xz", "txz", "lz", "lzip", "tlz", "zst", "zstd", "tzst",
        "z", "taz", "arc", "arj", "ace"
    ]

    override init() {
        super.init()
        // NSHomeDirectory() in an extension can refer to its sandbox container.
        let home = getpwuid(getuid()).map { String(cString: $0.pointee.pw_dir) } ?? NSHomeDirectory()
        FIFinderSyncController.default().directoryURLs = [
            URL(fileURLWithPath: home, isDirectory: true),
            URL(fileURLWithPath: "/Volumes", isDirectory: true)
        ]
    }

    override func menu(for menuKind: FIMenuKind) -> NSMenu {
        let menu = NSMenu(title: "")
        let controller = FIFinderSyncController.default()
        let urls: [URL]
        switch menuKind {
        case .contextualMenuForItems:
            urls = controller.selectedItemURLs() ?? []
        case .contextualMenuForContainer, .contextualMenuForSidebar:
            urls = controller.targetedURL().map { [$0] } ?? []
        default:
            return menu
        }
        guard !urls.isEmpty, urls.allSatisfy({ $0.isFileURL }) else { return menu }

        let actions = NSMenu(title: "EclipseArchive")
        let allArchives = urls.allSatisfy {
            Self.archiveExtensions.contains($0.pathExtension.lowercased()) &&
                (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == false
        }
        if allArchives {
            add("打开压缩包", verb: "open", urls: urls, to: actions)
            add("提取文件…", verb: "extract", urls: urls, to: actions)
            add("提取到此处", verb: "extract-here", urls: urls, to: actions)
            let name = urls.count == 1 ? archiveBaseName(urls[0]) : "各自同名文件夹"
            add("提取到“\(name)/”", verb: "extract-folder", urls: urls, to: actions)
            add("测试压缩包", verb: "test", urls: urls, to: actions)
            actions.addItem(.separator())
        }
        add("添加到压缩包…", verb: "compress", urls: urls, to: actions)
        // The app chooses a collision-free output name from the selected file or folder.
        add("压缩为 7z", verb: "compress-7z", urls: urls, to: actions)
        add("压缩为 ZIP", verb: "compress-zip", urls: urls, to: actions)

        let parent = NSMenuItem(title: "EclipseArchive", action: nil, keyEquivalent: "")
        parent.submenu = actions
        menu.addItem(parent)
        return menu
    }

    private func archiveBaseName(_ url: URL) -> String {
        var name = url.deletingPathExtension().lastPathComponent
        if name.lowercased().hasSuffix(".tar") { name = String(name.dropLast(4)) }
        return name
    }

    private func add(_ title: String, verb: String, urls: [URL], to menu: NSMenu) {
        let item = NSMenuItem(title: title, action: #selector(invoke(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = Request(verb: verb, paths: urls.map(\.path))
        menu.addItem(item)
    }

    @objc private func invoke(_ item: NSMenuItem) {
        guard let request = item.representedObject as? Request else { return }
        // .app/Contents/PlugIns/EclipseArchiveFinder.appex -> enclosing .app.
        let application = Bundle.main.bundleURL.deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        guard application.pathExtension.lowercased() == "app" else { return }
        let batches = request.verb.hasPrefix("compress") ? [request.paths] : request.paths.map { [$0] }
        for paths in batches {
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = true
            // Each archive gets its own operation window, including when the app is running.
            // Argument arrays preserve spaces, quotes and non-ASCII names without a shell.
            configuration.createsNewApplicationInstance = true
            configuration.arguments = ["--shell", request.verb, "--"] + paths
            NSWorkspace.shared.openApplication(at: application, configuration: configuration) { _, error in
                if let error { NSLog("EclipseArchive Finder action failed: %@", error.localizedDescription) }
            }
        }
    }

    private final class Request: NSObject {
        let verb: String
        let paths: [String]
        init(verb: String, paths: [String]) { self.verb = verb; self.paths = paths }
    }
}
