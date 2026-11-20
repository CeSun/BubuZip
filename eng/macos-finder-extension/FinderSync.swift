import Cocoa
import Darwin
import FinderSync

/// Finder supplies the selection; the application performs every archive operation.
@objc(EclipseArchiveFinderSync)
final class EclipseArchiveFinderSync: FIFinderSync {
    private let transport = FinderRequestTransport()
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
            add("打开压缩包", action: .open, kind: menuKind, to: actions)
            add("提取文件…", action: .extract, kind: menuKind, to: actions)
            add("提取到此处", action: .extractHere, kind: menuKind, to: actions)
            let name = urls.count == 1 ? archiveBaseName(urls[0]) : "各自同名文件夹"
            add("提取到“\(name)/”", action: .extractFolder, kind: menuKind, to: actions)
            add("测试压缩包", action: .test, kind: menuKind, to: actions)
            actions.addItem(.separator())
        }
        add("添加到压缩包…", action: .compress, kind: menuKind, to: actions)
        // The app chooses a collision-free output name from the selected file or folder.
        add("压缩为 7z", action: .compress7z, kind: menuKind, to: actions)
        add("压缩为 ZIP", action: .compressZip, kind: menuKind, to: actions)

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

    private func add(_ title: String, action: FinderArchiveAction, kind: FIMenuKind, to menu: NSMenu) {
        let item = NSMenuItem(title: title, action: #selector(invoke(_:)), keyEquivalent: "")
        item.tag = FinderMenuCommand(action: action, menuKind: kind).tag
        menu.addItem(item)
    }

    @objc private func invoke(_ item: NSMenuItem) {
        NSLog("EclipseArchive Finder action received (tag=%ld)", item.tag)
        do {
            guard let command = FinderMenuCommand(tag: item.tag) else {
                throw FinderRequestFailure.invalidSelection
            }
            let controller = FIFinderSyncController.default()
            let paths = try command.paths(selected: controller.selectedItemURLs(), targeted: controller.targetedURL())
            let application = try FinderRequestTransport.containingApplication(
                extensionBundle: Bundle(for: EclipseArchiveFinderSync.self).bundleURL)
            try transport.send(action: command.action, paths: paths, application: application) { error in
                Self.reportFailure(error)
            }
        } catch {
            Self.reportFailure(error)
        }
    }

    private static func reportFailure(_ error: Error) {
        NSLog("EclipseArchive Finder action failed: %@", error.localizedDescription)
        DispatchQueue.main.async {
            let alert = NSAlert()
            alert.messageText = "无法执行 EclipseArchive 菜单操作"
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .warning
            alert.addButton(withTitle: "好")
            alert.window.level = .floating
            alert.runModal()
        }
    }
}
