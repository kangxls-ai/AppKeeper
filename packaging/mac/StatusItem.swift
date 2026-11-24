import AppKit

typealias StatusCallback = @convention(c) (Int32) -> Void
private var statusController: StatusController?

private final class StatusController: NSObject {
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    let menu = NSMenu()
    let callback: StatusCallback
    init(callback: @escaping StatusCallback, icon: String) {
        self.callback = callback
        super.init()
        if let button = item.button {
            let image = NSImage(contentsOfFile: icon)
            image?.size = NSSize(width: 18, height: 18)
            button.image = image
            button.imageScaling = .scaleProportionallyDown
            button.toolTip = "EasyAppKeeper · 点击打开快捷应用，右键显示菜单"
            button.target = self
            button.action = #selector(clicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        for (name, action) in [("打开快捷应用", #selector(showHome(_:))), ("偏好设置…", #selector(preferences(_:))), ("退出 EasyAppKeeper", #selector(quit(_:)))] {
            let entry = NSMenuItem(title: name, action: action, keyEquivalent: "")
            entry.target = self
            menu.addItem(entry)
        }
    }
    @objc func clicked(_ sender: Any?) {
        let event = NSApplication.shared.currentEvent
        if event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true {
            item.popUpMenu(menu)
        } else { showHome(sender) }
    }
    @objc func showHome(_ sender: Any?) { callback(1); NSApplication.shared.activate(ignoringOtherApps: true) }
    @objc func preferences(_ sender: Any?) { callback(2); NSApplication.shared.activate(ignoringOtherApps: true) }
    @objc func quit(_ sender: Any?) { callback(3) }
    func dispose() { NSStatusBar.system.removeStatusItem(item) }
}

@_cdecl("EasyAppKeeperStatusCreate")
public func createStatus(_ callback: @escaping @convention(c) (Int32) -> Void, _ icon: UnsafePointer<CChar>) {
    statusController?.dispose()
    statusController = StatusController(callback: callback, icon: String(cString: icon))
}
@_cdecl("EasyAppKeeperStatusDispose")
public func disposeStatus() { statusController?.dispose(); statusController = nil }

// Exercise the native target/action route in the isolated UI smoke run.
@_cdecl("EasyAppKeeperStatusTestClick")
public func testStatusClick() -> Int32 {
    guard let controller = statusController, let button = controller.item.button, let action = button.action,
          controller.menu.items.count == 3, controller.menu.items.allSatisfy({ $0.target != nil && $0.action != nil }) else { return 0 }
    return NSApplication.shared.sendAction(action, to: button.target, from: button) ? 1 : 0
}
@_cdecl("EasyAppKeeperTestDockReopen")
public func testDockReopen() -> Int32 {
    let app = NSApplication.shared
    return app.delegate?.applicationShouldHandleReopen?(app, hasVisibleWindows: app.windows.contains(where: { $0.isVisible })) == nil ? 0 : 1
}
// Calls only this application's termination delegate; never shuts down the computer.
@_cdecl("EasyAppKeeperTestTermination")
public func testTermination() -> Int32 {
    let app = NSApplication.shared
    guard let reply = app.delegate?.applicationShouldTerminate?(app) else { return -1 }
    return Int32(reply.rawValue)
}
