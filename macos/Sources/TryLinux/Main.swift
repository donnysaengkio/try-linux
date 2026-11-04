// Try Linux for Mac: installs and runs Linux distributions in a VM on Apple Silicon,
// using Apple's Virtualization framework.
import AppKit
import SwiftUI

enum AppInfo {
    static let version = "1.0.0"
}

@main
enum Main {
    static func main() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--write-iconset"), i + 1 < args.count {
            Icons.writeIconset(to: URL(fileURLWithPath: args[i + 1]))
            return
        }
        if let i = args.firstIndex(of: "--self-test"), i + 1 < args.count {
            SelfTest.start(outputDirectory: URL(fileURLWithPath: args[i + 1]))
            return
        }
        TryLinuxApp.main()
    }
}

struct TryLinuxApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = AppModel.shared

    var body: some Scene {
        Window("Try Linux", id: "main") {
            RootView()
                .environmentObject(model)
                .frame(minWidth: 900, minHeight: 700)
                .background(Theme.bg)
                .preferredColorScheme(.dark)
        }
        .windowResizability(.contentMinSize)
        .commands { CommandGroup(replacing: .newItem) {} }

        MenuBarExtra("Try Linux", systemImage: "terminal") {
            MenuBarContent().environmentObject(model)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        DispatchQueue.main.async {
            Installer.offerAtLaunch()
            AppModel.shared.checkForUpdatesInBackground()
        }
    }

    // An update downloaded while systems were running is put in place as Try Linux quits.
    func applicationWillTerminate(_ notification: Notification) {
        if let app = AppModel.shared.stagedUpdate { try? Updater.apply(app) }
    }

    // Closing the launcher window keeps running systems (and the menu bar icon) alive.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    // Linux runs inside this app, so quitting has to shut running systems down first.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let sessions = Array(AppModel.shared.running.values)
        if sessions.isEmpty { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = "Shut down running systems?"
        alert.informativeText = sessions.map(\.config.displayName).joined(separator: ", ") +
            " will be shut down. Save your work in Linux first."
        alert.addButton(withTitle: "Shut Down and Quit")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return .terminateCancel }
        for s in sessions { s.requestShutdown() }
        // Give each guest up to 20 seconds to power off, then stop what's left.
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) {
            for s in AppModel.shared.running.values { s.forceStop() }
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { sender.reply(toApplicationShouldTerminate: true) }
        }
        AppModel.shared.onAllStopped = { sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
}

struct MenuBarContent: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if model.running.isEmpty {
            Text("No systems running")
        } else {
            ForEach(Array(model.running.values), id: \.config.id) { s in
                Menu(s.config.displayName) {
                    Button("Show window") { s.bringToFront() }
                    if !s.config.sharedFolder.isEmpty {
                        Button("Open shared folder") { NSWorkspace.shared.open(URL(fileURLWithPath: s.config.sharedFolder)) }
                    }
                    Button("Shut down") { s.requestShutdown() }
                    Button("Force stop") { model.confirmForceStop(s) }
                }
            }
        }
        Divider()
        Button("Open Try Linux") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Quit Try Linux") { NSApp.terminate(nil) }
    }
}
