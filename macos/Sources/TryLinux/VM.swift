// Builds the Apple Virtualization configuration for a system and runs it in its own window.
import AppKit
import Virtualization

enum VMBuilder {
    static func configuration(_ c: VMConfig, installer: Bool) throws -> VZVirtualMachineConfiguration {
        let conf = VZVirtualMachineConfiguration()
        let res = c.resources()
        conf.cpuCount = res.cores
        conf.memorySize = UInt64(res.ramGB) << 30

        let platform = VZGenericPlatformConfiguration()
        platform.machineIdentifier = try machineIdentifier(c)
        conf.platform = platform

        let boot = VZEFIBootLoader()
        boot.variableStore = try efiStore(c)
        conf.bootLoader = boot

        var storage: [VZStorageDeviceConfiguration] = [
            VZVirtioBlockDeviceConfiguration(attachment: try VZDiskImageStorageDeviceAttachment(url: c.diskURL, readOnly: false))
        ]
        if installer {
            // The installer ISO appears as a USB stick; the empty disk can't boot, so EFI starts from it.
            let iso = try VZDiskImageStorageDeviceAttachment(url: URL(fileURLWithPath: c.iso), readOnly: true)
            storage.append(VZUSBMassStorageDeviceConfiguration(attachment: iso))
        }
        conf.storageDevices = storage

        let net = VZVirtioNetworkDeviceConfiguration()
        net.attachment = VZNATNetworkDeviceAttachment()
        if let mac = VZMACAddress(string: c.macAddress) { net.macAddress = mac }
        conf.networkDevices = [net]

        let gpu = VZVirtioGraphicsDeviceConfiguration()
        gpu.scanouts = [VZVirtioGraphicsScanoutConfiguration(widthInPixels: c.width, heightInPixels: c.height)]
        conf.graphicsDevices = [gpu]
        conf.keyboards = [VZUSBKeyboardConfiguration()]
        conf.pointingDevices = [VZUSBScreenCoordinatePointingDeviceConfiguration()]
        conf.entropyDevices = [VZVirtioEntropyDeviceConfiguration()]
        conf.memoryBalloonDevices = [VZVirtioTraditionalMemoryBalloonDeviceConfiguration()]

        if c.sound {
            let sound = VZVirtioSoundDeviceConfiguration()
            let out = VZVirtioSoundDeviceOutputStreamConfiguration()
            out.sink = VZHostAudioOutputStreamSink()
            var streams: [VZVirtioSoundDeviceStreamConfiguration] = [out]
            if c.mic {
                let input = VZVirtioSoundDeviceInputStreamConfiguration()
                input.source = VZHostAudioInputStreamSource()
                streams.append(input)
            }
            sound.streams = streams
            conf.audioDevices = [sound]
        }

        var shares: [VZDirectorySharingDeviceConfiguration] = []
        if !c.sharedFolder.isEmpty, FileManager.default.fileExists(atPath: c.sharedFolder) {
            let fs = VZVirtioFileSystemDeviceConfiguration(tag: "host")
            fs.share = VZSingleDirectoryShare(directory: VZSharedDirectory(url: URL(fileURLWithPath: c.sharedFolder), readOnly: !c.sharedWrite))
            shares.append(fs)
        }
        if c.rosetta, VZLinuxRosettaDirectoryShare.availability == .installed {
            let fs = VZVirtioFileSystemDeviceConfiguration(tag: "rosetta")
            fs.share = try VZLinuxRosettaDirectoryShare()
            shares.append(fs)
        }
        conf.directorySharingDevices = shares

        if c.clipboard {
            // The SPICE agent port lets spice-vdagent in Linux share the clipboard with macOS.
            let console = VZVirtioConsoleDeviceConfiguration()
            let port = VZVirtioConsolePortConfiguration()
            port.name = VZSpiceAgentPortAttachment.spiceAgentPortName
            let agent = VZSpiceAgentPortAttachment()
            agent.sharesClipboard = true
            port.attachment = agent
            console.ports[0] = port
            conf.consoleDevices = [console]
        }

        try conf.validate()
        return conf
    }

    static func machineIdentifier(_ c: VMConfig) throws -> VZGenericMachineIdentifier {
        if let data = try? Data(contentsOf: c.machineIdURL), let id = VZGenericMachineIdentifier(dataRepresentation: data) { return id }
        let id = VZGenericMachineIdentifier()
        try id.dataRepresentation.write(to: c.machineIdURL)
        return id
    }

    static func efiStore(_ c: VMConfig) throws -> VZEFIVariableStore {
        if FileManager.default.fileExists(atPath: c.efiURL.path) { return VZEFIVariableStore(url: c.efiURL) }
        return try VZEFIVariableStore(creatingVariableStoreAt: c.efiURL)
    }
}

final class VMSession: NSObject, ObservableObject, VZVirtualMachineDelegate, NSWindowDelegate {
    let config: VMConfig
    let installer: Bool
    private(set) var started = Date()
    private let vm: VZVirtualMachine
    private var window: NSWindow?
    private var finished = false
    var onStop: ((VMSession, Error?) -> Void)?
    var onChange: (() -> Void)?          // state shown on Virtual Machines changed
    var onWindowClosed: (() -> Void)?    // the window was closed; the launcher shows the system's status

    // Opened from Console while off: the window is open, but the machine hasn't been started.
    private(set) var held = false
    private(set) var hidden = false
    private(set) var paused = false
    // Power menu: Restart asks Linux to shut down, then starts it again (if it stops within a few
    // minutes, so a cancelled prompt doesn't turn a later shutdown into a restart). Reset stops it
    // at once and starts it again. Force stop and closing a console end it quietly.
    var restartAsked = Date.distantPast
    var resetAsked = false
    var quiet = false

    init(config: VMConfig, installer: Bool) throws {
        var c = config
        if c.macAddress.isEmpty {
            c.macAddress = VZMACAddress.randomLocallyAdministered().string
            try c.save()
        }
        self.config = c
        self.installer = installer
        vm = VZVirtualMachine(configuration: try VMBuilder.configuration(c, installer: installer))
        super.init()
        vm.delegate = self
    }

    func start(_ completion: @escaping (Error?) -> Void) {
        started = Date()   // the installer check counts from here
        vm.start { result in
            switch result {
            case .success: completion(nil)
            case .failure(let error): completion(error)
            }
        }
    }

    // A window for a system that's off. Start (or Power on) starts it in this window.
    func showConsole() {
        held = true
        showWindow(fullscreen: false)
        window?.title = config.displayName + " (off)"
    }

    func powerOn(_ completion: @escaping (Error?) -> Void) {
        start { [weak self] error in
            DispatchQueue.main.async {
                guard let self else { return }
                if error == nil {
                    self.held = false
                    self.window?.title = self.config.displayName
                    self.onChange?()
                }
                completion(error)
            }
        }
    }

    func showWindow(fullscreen: Bool? = nil) {
        let view = VZVirtualMachineView()
        view.virtualMachine = vm
        view.capturesSystemKeys = true
        if #available(macOS 14.0, *) { view.automaticallyReconfiguresDisplay = true }
        let visible = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let w = min(CGFloat(config.width) / 1.5, visible.width * 0.9)
        let h = min(w * CGFloat(config.height) / CGFloat(config.width), visible.height * 0.9)
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: w, height: h),
                           styleMask: [.titled, .closable, .miniaturizable, .resizable],
                           backing: .buffered, defer: false)
        win.title = config.displayName
        win.contentView = view
        win.isReleasedWhenClosed = false
        win.delegate = self
        win.collectionBehavior.insert(.fullScreenPrimary)
        win.center()
        window = win
        win.makeKeyAndOrderFront(nil)
        win.makeFirstResponder(view)
        NSApp.activate(ignoringOtherApps: true)
        if fullscreen ?? config.fullscreen { win.toggleFullScreen(nil) }
    }

    func bringToFront() {
        if hidden { hidden = false; onChange?() }
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    // Asks Linux to power off, like pressing a power button. A paused system is resumed first,
    // since it can't answer while paused.
    func requestShutdown() {
        if paused { setPaused(false) { [weak self] in self?.requestShutdown() }; return }
        if vm.canRequestStop { try? vm.requestStop() }
    }

    func setPaused(_ pause: Bool, then done: (() -> Void)? = nil) {
        let handler: (Result<Void, Error>) -> Void = { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                if case .success = result { self.paused = pause; self.onChange?() }
                done?()
            }
        }
        if pause, vm.canPause { vm.pause(completionHandler: handler) }
        else if !pause, vm.canResume { vm.resume(completionHandler: handler) }
        else { done?() }
    }

    func forceStop() {
        if held { quiet = true; finish(nil); return }   // never started: just close the window
        guard vm.canStop else { return }
        vm.stop { [weak self] error in self?.finish(error) }
    }

    // Closing the window leaves the system running; Virtual Machines shows it and Console brings it back.
    // A console that was never powered on just closes.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if held { forceStop(); return false }
        if sender.styleMask.contains(.fullScreen) { sender.toggleFullScreen(nil) }
        sender.orderOut(nil)
        hidden = true
        onChange?()
        onWindowClosed?()
        return false
    }

    func guestDidStop(_ virtualMachine: VZVirtualMachine) { finish(nil) }

    func virtualMachine(_ virtualMachine: VZVirtualMachine, didStopWithError error: Error) { finish(error) }

    private func finish(_ error: Error?) {
        DispatchQueue.main.async {
            guard !self.finished else { return }
            self.finished = true
            self.window?.delegate = nil
            self.window?.close()
            self.window = nil
            self.onStop?(self, error)
        }
    }
}
