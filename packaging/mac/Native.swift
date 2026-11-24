import AppKit
import Carbon
import Security

func fail(_ text: String) -> Never {
    FileHandle.standardError.write((text + "\n").data(using: .utf8)!)
    exit(1)
}

let arguments = CommandLine.arguments
guard arguments.count >= 2 else { fail("缺少系统操作。") }
switch arguments[1] {
case "--services-update":
    NSUpdateDynamicServices()
case "--icons":
    struct FileIcon: Codable { let Name: String; let Path: String; let Icon: String }
    var result: [FileIcon] = []
    for path in arguments.dropFirst(2) where FileManager.default.fileExists(atPath: path) {
        let icon = NSWorkspace.shared.icon(forFile: path)
        icon.size = NSSize(width: 64, height: 64)
        guard let tiff = icon.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff), let png = bitmap.representation(using: .png, properties: [:]) else { continue }
        result.append(FileIcon(Name: FileManager.default.displayName(atPath: path), Path: path, Icon: png.base64EncodedString()))
    }
    let data = try JSONEncoder().encode(result)
    print(String(data: data, encoding: .utf8)!)
case "--hotkey":
    let application = NSApplication.shared
    application.setActivationPolicy(.prohibited)
    guard arguments.count == 4, let key = UInt32(arguments[2]), let modifiers = UInt32(arguments[3]) else { fail("快捷键无效。") }
    var kind = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    let installed = InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
        print("SHOW"); fflush(stdout); return noErr
    }, 1, &kind, nil, nil)
    guard installed == noErr else { fail("无法监听全局快捷键，请从菜单栏打开应用。") }
    var ref: EventHotKeyRef?
    let status = RegisterEventHotKey(key, modifiers, EventHotKeyID(signature: 0x45414B50, id: 1), GetApplicationEventTarget(), 0, &ref)
    guard status == noErr else { fail("快捷键已被占用，请在偏好设置中更换。") }
    application.run()
case "--scan":
    struct AppEntry: Codable { let Name: String; let Path: String; let Icon: String }
    let fm = FileManager.default
    let roots = ["/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities", NSHomeDirectory() + "/Applications"]
    var paths = Set<String>(); var results: [AppEntry] = []
    for root in roots {
        guard let urls = try? fm.contentsOfDirectory(at: URL(fileURLWithPath: root), includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else { continue }
        for url in urls where url.pathExtension.lowercased() == "app" {
            guard paths.insert(url.path).inserted else { continue }
            let bundle = Bundle(url: url)
            let name = bundle?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String ?? bundle?.object(forInfoDictionaryKey: "CFBundleName") as? String ?? url.deletingPathExtension().lastPathComponent
            let icon = NSWorkspace.shared.icon(forFile: url.path)
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 64, pixelsHigh: 64, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            icon.draw(in: NSRect(x: 0, y: 0, width: 64, height: 64)); NSGraphicsContext.restoreGraphicsState()
            results.append(AppEntry(Name: name, Path: url.path, Icon: rep.representation(using: .png, properties: [:])?.base64EncodedString() ?? ""))
        }
    }
    let data = try JSONEncoder().encode(results.sorted { $0.Name.localizedStandardCompare($1.Name) == .orderedAscending })
    FileHandle.standardOutput.write(data)
case "--copy-file":
    guard arguments.count == 3, FileManager.default.fileExists(atPath: arguments[2]) else { fail("找不到此文件，可能已移动或删除。") }
    NSPasteboard.general.clearContents()
    guard NSPasteboard.general.writeObjects([NSURL(fileURLWithPath: arguments[2])]) else { fail("暂时无法访问剪贴板，请重试。") }
case "--key":
    let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "io.kangxls.EasyAppKeeper", kSecAttrAccount as String: "agenda-master-key"]
    var find = query; find[kSecReturnData as String] = true; find[kSecMatchLimit as String] = kSecMatchLimitOne
    var result: CFTypeRef?
    let status = SecItemCopyMatching(find as CFDictionary, &result)
    if status == errSecSuccess, let data = result as? Data { print(data.base64EncodedString()) }
    else if status == errSecItemNotFound {
        var bytes = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { fail("无法生成配对凭据保护密钥。") }
        let data = Data(bytes); var add = query; add[kSecValueData as String] = data
        let added = SecItemAdd(add as CFDictionary, nil)
        if added == errSecDuplicateItem {
            guard SecItemCopyMatching(find as CFDictionary, &result) == errSecSuccess, let existing = result as? Data else { fail("无法读取钥匙串配对凭据。") }
            print(existing.base64EncodedString())
        } else {
            guard added == errSecSuccess else { fail("无法将配对凭据写入钥匙串，请解锁登录钥匙串后重试。") }
            print(data.base64EncodedString())
        }
    } else { fail("无法读取钥匙串配对凭据，请解锁登录钥匙串后重试。") }
case "--login":
    guard arguments.count == 3 else { fail("登录启动选项无效。") }
    let fm = FileManager.default
    let directory = URL(fileURLWithPath: NSHomeDirectory() + "/Library/LaunchAgents", isDirectory: true)
    let file = directory.appendingPathComponent("io.kangxls.EasyAppKeeper.plist")
    if arguments[2] == "status" { print(fm.fileExists(atPath: file.path) ? "true" : "false") }
    else if arguments[2] == "disable" { if fm.fileExists(atPath: file.path) { try fm.removeItem(at: file) } }
    else if arguments[2] == "enable" {
        let executable = URL(fileURLWithPath: arguments[0]).standardizedFileURL
        let app = executable.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        guard app.pathExtension == "app" else { fail("请将 EasyAppKeeper.app 放入应用程序目录后启用登录启动。") }
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let plist: [String: Any] = ["Label": "io.kangxls.EasyAppKeeper", "ProgramArguments": ["/usr/bin/open", "-g", "-a", app.path, "--args", "--background"], "RunAtLoad": true]
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: file, options: .atomic)
    } else { fail("登录启动选项无效。") }
default: fail("未知系统操作。")
}
