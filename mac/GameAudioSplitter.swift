// Game Audio Splitter (macOS) - plays one app's sound (or all system audio) on extra output devices.
// Uses Core Audio process taps (macOS 14.4+): no virtual driver, and the original playback on your
// main output is never touched (taps are created "unmuted").
// Build: ./build.sh  (needs only the Xcode Command Line Tools)

import SwiftUI
import AppKit
import CoreAudio
import AudioToolbox
import UniformTypeIdentifiers

// MARK: - Core Audio helpers

let kUnknownObject = AudioObjectID(kAudioObjectUnknown)
let kSystemObject = AudioObjectID(kAudioObjectSystemObject)

struct CAError: Error {
    let status: OSStatus

    /// OSStatus as a four-char code when printable ('!dev'), otherwise the number.
    var text: String {
        let u = UInt32(bitPattern: status)
        let bytes = [24, 16, 8, 0].map { UInt8((u >> UInt32($0)) & 0xFF) }
        if bytes.allSatisfy({ $0 >= 32 && $0 < 127 }), let s = String(bytes: bytes, encoding: .ascii) {
            return "'\(s)'"
        }
        return "\(status)"
    }
}

enum CA {
    static func addr(_ selector: AudioObjectPropertySelector,
                     _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    static func check(_ status: OSStatus) throws {
        if status != noErr { throw CAError(status: status) }
    }

    static func ids(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> [AudioObjectID] {
        var a = addr(selector)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(object, &a, 0, nil, &size) == noErr, size > 0 else { return [] }
        var out = [AudioObjectID](repeating: kUnknownObject, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(object, &a, 0, nil, &size, &out) == noErr else { return [] }
        return out
    }

    static func string(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var a = addr(selector)
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var value: Unmanaged<CFString>? = nil
        let status = AudioObjectGetPropertyData(object, &a, 0, nil, &size, &value)
        guard status == noErr, let unwrapped = value else { return nil }
        return unwrapped.takeRetainedValue() as String
    }

    static func scalar<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ fallback: T,
                          scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> T {
        var a = addr(selector, scope)
        var value = fallback
        var size = UInt32(MemoryLayout<T>.size)
        let status = withUnsafeMutablePointer(to: &value) { ptr in
            AudioObjectGetPropertyData(object, &a, 0, nil, &size, ptr)
        }
        return status == noErr ? value : fallback
    }

    static func hasOutputStreams(_ device: AudioObjectID) -> Bool {
        var a = addr(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(device, &a, 0, nil, &size) == noErr && size > 0
    }

    /// Core Audio's process object for a PID, or kUnknownObject if that process has never used audio.
    static func processObject(pid: pid_t) -> AudioObjectID {
        var a = addr(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var qualifier = pid
        var object = kUnknownObject
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(kSystemObject, &a, UInt32(MemoryLayout<pid_t>.size), &qualifier, &size, &object)
        return status == noErr ? object : kUnknownObject
    }

    /// The aggregate also exposes the target device's own inputs (e.g. a headset mic). Tell the HAL our
    /// IOProc only uses the last input stream (the tap), so it never opens the mic - which would show the
    /// mic indicator and drop Bluetooth headphones into low-quality call mode.
    /// AudioHardwareIOProcStreamUsage layout: void* mIOProc @0, UInt32 mNumberStreams @8, UInt32 mStreamIsOn[] @12.
    static func useOnlyLastInputStream(_ device: AudioObjectID, _ proc: AudioDeviceIOProcID) {
        var a = addr(kAudioDevicePropertyIOProcStreamUsage, kAudioObjectPropertyScopeInput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &a, 0, nil, &size) == noErr, size >= 16 else { return }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: 8)
        defer { raw.deallocate() }
        raw.initializeMemory(as: UInt8.self, repeating: 0, count: Int(size))
        raw.storeBytes(of: unsafeBitCast(proc, to: UnsafeMutableRawPointer.self), as: UnsafeMutableRawPointer.self)
        guard AudioObjectGetPropertyData(device, &a, 0, nil, &size, raw) == noErr else { return }
        let count = Int(raw.load(fromByteOffset: 8, as: UInt32.self))
        guard count > 1, 12 + 4 * count <= Int(size) else { return }
        for i in 0..<count {
            raw.storeBytes(of: UInt32(i == count - 1 ? 1 : 0), toByteOffset: 12 + 4 * i, as: UInt32.self)
        }
        _ = AudioObjectSetPropertyData(device, &a, 0, nil, size, raw)
    }
}

// MARK: - Tap session (one per target device)

/// One process tap + one private aggregate device (target = clock master, tap drift-compensated into it).
/// The IO block runs on Core Audio's real-time thread and touches only raw pointers allocated here.
final class TapSession {
    static let maxFrames = 8192
    static let aggregatePrefix = "com.gameaudiosplitter.tap."

    let deviceUID: String
    let deviceName: String
    private var tapID = kUnknownObject
    private var aggregateID = kUnknownObject
    private var procID: AudioDeviceIOProcID?

    // cells: [0] target gain L, [1] target gain R  (written by UI)
    //        [2] current gain L, [3] current gain R, [4] peak L, [5] peak R  (written by audio thread)
    private let cells: UnsafeMutablePointer<Float>
    private let scratch: UnsafeMutablePointer<Float>

    init(deviceUID: String, deviceName: String) {
        self.deviceUID = deviceUID
        self.deviceName = deviceName
        cells = .allocate(capacity: 6)
        cells.initialize(repeating: 0, count: 6)   // current gains start at 0 -> short fade-in, no click
        scratch = .allocate(capacity: TapSession.maxFrames * 2)
        scratch.initialize(repeating: 0, count: TapSession.maxFrames * 2)
    }

    deinit {
        stop()
        cells.deallocate()
        scratch.deallocate()
    }

    /// immediate: jump straight to the gain (no fade from silence) - used by the app-volume mixer.
    func setGains(_ gains: (l: Float, r: Float), immediate: Bool = false) {
        cells[0] = gains.l
        cells[1] = gains.r
        if immediate {
            cells[2] = gains.l
            cells[3] = gains.r
        }
    }

    var peaks: (l: Float, r: Float) { (cells[4], cells[5]) }

    /// muteOriginal false (splitter): the app keeps playing on the main output exactly as before.
    /// muteOriginal true (app volume): the app's own output is silenced while our IO runs and we replay
    /// it at a new level. .mutedWhenTapped (not .muted) means: if our IO ever stops - or this process
    /// dies, which makes coreaudiod destroy our taps and aggregates - the app's audio returns by itself.
    func start(tap description: CATapDescription, muteOriginal: Bool = false) throws {
        do {
            description.uuid = UUID()
            description.muteBehavior = muteOriginal ? .mutedWhenTapped : .unmuted
            description.isPrivate = true
            description.name = "Game Audio Splitter"
            var tap = kUnknownObject
            try CA.check(AudioHardwareCreateProcessTap(description, &tap))
            tapID = tap

            let aggregate: [String: Any] = [
                kAudioAggregateDeviceNameKey: "Game Audio Splitter - \(deviceName)",
                kAudioAggregateDeviceUIDKey: TapSession.aggregatePrefix + UUID().uuidString,
                kAudioAggregateDeviceMainSubDeviceKey: deviceUID,
                kAudioAggregateDeviceIsPrivateKey: true,
                kAudioAggregateDeviceIsStackedKey: false,
                kAudioAggregateDeviceTapAutoStartKey: true,
                kAudioAggregateDeviceSubDeviceListKey: [
                    [kAudioSubDeviceUIDKey: deviceUID]
                ],
                kAudioAggregateDeviceTapListKey: [
                    [
                        kAudioSubTapDriftCompensationKey: true,
                        kAudioSubTapUIDKey: description.uuid.uuidString
                    ]
                ]
            ]
            var device = kUnknownObject
            try CA.check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &device))
            aggregateID = device

            let rate = CA.scalar(device, kAudioDevicePropertyNominalSampleRate, Float64(48000))
            let smoothing = Float(1 - exp(-1 / (0.012 * max(rate, 8000))))   // ~12 ms gain glide
            let block = TapSession.makeIOBlock(cells: cells, scratch: scratch,
                                               maxFrames: TapSession.maxFrames, smoothing: smoothing)
            var proc: AudioDeviceIOProcID?
            // nil queue = the block runs directly on the device's real-time IO thread.
            try CA.check(AudioDeviceCreateIOProcIDWithBlock(&proc, device, nil, block))
            procID = proc
            if let proc { CA.useOnlyLastInputStream(device, proc) }
            try CA.check(AudioDeviceStart(device, procID))
        } catch {
            stop()
            throw error
        }
    }

    /// Order matters: stop IO -> destroy IOProc -> destroy aggregate -> destroy tap.
    func stop() {
        if aggregateID != kUnknownObject {
            if let proc = procID {
                _ = AudioDeviceStop(aggregateID, proc)
                _ = AudioDeviceDestroyIOProcID(aggregateID, proc)
            }
            _ = AudioHardwareDestroyAggregateDevice(aggregateID)
            aggregateID = kUnknownObject
        }
        procID = nil
        if tapID != kUnknownObject {
            _ = AudioHardwareDestroyProcessTap(tapID)
            tapID = kUnknownObject
        }
    }

    /// Real-time safe: no allocation, no locks, no Objective-C/Swift object access.
    private static func makeIOBlock(cells: UnsafeMutablePointer<Float>, scratch: UnsafeMutablePointer<Float>,
                                    maxFrames: Int, smoothing k: Float) -> AudioDeviceIOBlock {
        return { _, inInputData, _, outOutputData, _ in
            let input = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inInputData))
            let output = UnsafeMutableAudioBufferListPointer(outOutputData)
            let bufL = scratch
            let bufR = scratch + maxFrames
            var frames = 0

            // The tap's stream comes after any input streams of the target device itself, so the tap is
            // the LAST input buffer that carries data (disabled mic streams arrive with mData == nil).
            var idx = input.count - 1
            while idx >= 0 && (input[idx].mData == nil || input[idx].mDataByteSize == 0) { idx -= 1 }

            if idx >= 0, let raw = input[idx].mData {
                let ch = max(Int(input[idx].mNumberChannels), 1)
                let src = raw.assumingMemoryBound(to: Float.self)
                frames = min(Int(input[idx].mDataByteSize) / (4 * ch), maxFrames)
                var srcL = src
                var srcR = src
                var step = ch
                if ch >= 2 {
                    srcR = src + 1                                   // interleaved stereo
                } else if idx >= 1, let rawL = input[idx - 1].mData, input[idx - 1].mNumberChannels == 1 {
                    srcL = rawL.assumingMemoryBound(to: Float.self)  // non-interleaved L / R pair
                    step = 1
                }

                let targetL = cells[0], targetR = cells[1]
                var gl = cells[2], gr = cells[3]
                var pl = cells[4] * 0.85, pr = cells[5] * 0.85      // meter decay per buffer
                if gl == targetL && gr == targetR {
                    if gl == 1 && gr == 1 {
                        for f in 0..<frames {                        // settled at unity: plain copy
                            let a = srcL[f * step], b = srcR[f * step]
                            bufL[f] = a; bufR[f] = b
                            pl = max(pl, abs(a)); pr = max(pr, abs(b))
                        }
                    } else {
                        for f in 0..<frames {
                            let a = srcL[f * step] * gl, b = srcR[f * step] * gr
                            bufL[f] = a; bufR[f] = b
                            pl = max(pl, abs(a)); pr = max(pr, abs(b))
                        }
                    }
                } else {
                    for f in 0..<frames {                            // one-pole glide, no zipper noise
                        gl += (targetL - gl) * k
                        gr += (targetR - gr) * k
                        let a = srcL[f * step] * gl, b = srcR[f * step] * gr
                        bufL[f] = a; bufR[f] = b
                        pl = max(pl, abs(a)); pr = max(pr, abs(b))
                    }
                    if abs(gl - targetL) < 0.0005 { gl = targetL }
                    if abs(gr - targetR) < 0.0005 { gr = targetR }
                }
                cells[2] = gl; cells[3] = gr
                cells[4] = pl; cells[5] = pr
            } else {
                cells[4] *= 0.85; cells[5] *= 0.85
            }

            // Scatter L/R into the target's layout: interleaved (1 buffer, N ch) or N mono buffers.
            var totalChannels = 0
            for b in output { totalChannels += Int(b.mNumberChannels) }
            var base = 0
            for b in output {
                let ch = max(Int(b.mNumberChannels), 1)
                defer { base += ch }
                guard let raw = b.mData else { continue }
                let out = raw.assumingMemoryBound(to: Float.self)
                let outFrames = Int(b.mDataByteSize) / (4 * ch)
                let n = min(frames, outFrames)
                for c in 0..<ch {
                    let global = base + c
                    var f = 0
                    if totalChannels == 1 {
                        while f < n { out[f * ch + c] = (bufL[f] + bufR[f]) * 0.5; f += 1 }
                    } else if global == 0 {
                        while f < n { out[f * ch + c] = bufL[f]; f += 1 }
                    } else if global == 1 {
                        while f < n { out[f * ch + c] = bufR[f]; f += 1 }
                    }
                    while f < outFrames { out[f * ch + c] = 0; f += 1 }
                }
            }
        }
    }
}

// MARK: - Device controls (each device's own macOS volume / balance / mute)

// Four-char codes of kAudioHardwareServiceDeviceProperty_VirtualMainVolume ('vmvc') and
// ..._VirtualMainBalance ('vmbl'), the same controls as the menu-bar volume and Sound settings.
// Spelled as codes so the build doesn't depend on the SDK's Main/Master symbol names.
let kVirtualMainVolume: AudioObjectPropertySelector = 0x766D_7663
let kVirtualMainBalance: AudioObjectPropertySelector = 0x766D_626C

struct SystemControls: Equatable {
    var hasVolume = false         // settable on the device itself
    var hasBalance = false
    var hasMute = false
    var volume: Float = 1         // 0...1
    var balance: Float = 0.5      // 0 = left, 0.5 = centre, 1 = right
    var muted = false
}

extension CA {
    static func has(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> Bool {
        var a = addr(selector, kAudioObjectPropertyScopeOutput)
        return AudioObjectHasProperty(object, &a)
    }

    static func isSettable(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> Bool {
        var a = addr(selector, kAudioObjectPropertyScopeOutput)
        guard AudioObjectHasProperty(object, &a) else { return false }
        var settable: DarwinBoolean = false
        return AudioObjectIsPropertySettable(object, &a, &settable) == noErr && settable.boolValue
    }

    static func setOutput<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ value: T) {
        var a = addr(selector, kAudioObjectPropertyScopeOutput)
        // Callers pass plain values (Float32 / UInt32); copy them as raw bytes.
        withUnsafeBytes(of: value) { raw in
            _ = AudioObjectSetPropertyData(object, &a, 0, nil, UInt32(raw.count), raw.baseAddress!)
        }
    }

    static func controls(_ device: AudioObjectID) -> SystemControls {
        let out = kAudioObjectPropertyScopeOutput
        var c = SystemControls()
        c.hasVolume = isSettable(device, kVirtualMainVolume)
        c.hasBalance = isSettable(device, kVirtualMainBalance)
        c.hasMute = isSettable(device, kAudioDevicePropertyMute)
        if has(device, kVirtualMainVolume) { c.volume = scalar(device, kVirtualMainVolume, Float32(1), scope: out) }
        if has(device, kVirtualMainBalance) { c.balance = scalar(device, kVirtualMainBalance, Float32(0.5), scope: out) }
        if has(device, kAudioDevicePropertyMute) { c.muted = scalar(device, kAudioDevicePropertyMute, UInt32(0), scope: out) != 0 }
        return c
    }

    static let controlSelectors = [kVirtualMainVolume, kVirtualMainBalance, kAudioDevicePropertyMute]
}

/// C-function listener (not a block) so it can be removed reliably: removal matches on (proc, clientData).
/// Core Audio calls it on its own thread; hop to main before touching the model.
private let controlListenerProc: AudioObjectPropertyListenerProc = { objectID, _, _, _ in
    DispatchQueue.main.async { AppModel.shared.deviceControlsChanged(objectID) }
    return noErr
}

/// A process started or stopped playing (kAudioProcessPropertyIsRunningOutput). Same C-proc pattern.
private let processOutputListenerProc: AudioObjectPropertyListenerProc = { _, _, _, _ in
    DispatchQueue.main.async { AppModel.shared.processOutputChanged() }
    return noErr
}

// MARK: - App volume types

enum MainTab: String { case splitter, mixer }

struct AppVolumeSetting: Codable, Equatable {
    var volume: Double = 100      // 0...100
    var muted = false
    var name: String? = nil       // remembered for the "Saved" list

    var isAdjusted: Bool { muted || volume < 100 }

    /// Squared slider position: 50% is about -12 dB, which sounds roughly "half as loud".
    var gain: Float { muted ? 0 : Float((volume / 100) * (volume / 100)) }

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        volume = try c.decodeIfPresent(Double.self, forKey: .volume) ?? 100
        muted = try c.decodeIfPresent(Bool.self, forKey: .muted) ?? false
        name = try c.decodeIfPresent(String.self, forKey: .name)
    }
}

/// One row in the mixer: an app plus all its audio processes (helpers merged in).
struct MixerApp: Identifiable, Equatable {
    let id: String                // bundle identifier of the owning app
    let name: String
    let icon: NSImage
    let playing: Bool
    let objects: [AudioObjectID]  // Core Audio process objects, sorted
}

// MARK: - Model

struct OutputDevice: Identifiable, Equatable {
    let id: String            // device UID
    let objectID: AudioObjectID
    let name: String
    let isDefault: Bool
    let transport: UInt32
    let connected: Bool

    var symbol: String {
        switch transport {
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: return "headphones"
        case kAudioDeviceTransportTypeUSB: return "cable.connector"
        case kAudioDeviceTransportTypeHDMI, kAudioDeviceTransportTypeDisplayPort: return "display"
        case kAudioDeviceTransportTypeAirPlay: return "airplayaudio"
        case kAudioDeviceTransportTypeVirtual: return "waveform"
        default: return "hifispeaker"
        }
    }
}

struct AppEntry: Identifiable {
    let id: String            // bundle identifier
    let name: String
    let icon: NSImage
    let running: Bool
}

struct DeviceSettings: Codable, Equatable {
    var enabled = false
    var name: String? = nil       // remembered so an unplugged device still shows by name
    var followMain = false
    // In-app fallbacks: used only when the device has no settable volume / balance / mute of its own.
    var volume: Double = 100      // 0...100
    var balance: Double = 0       // -100 (left) ... +100 (right)
    var muted = false

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        name = try c.decodeIfPresent(String.self, forKey: .name)
        followMain = try c.decodeIfPresent(Bool.self, forKey: .followMain) ?? false
        volume = try c.decodeIfPresent(Double.self, forKey: .volume) ?? 100
        balance = try c.decodeIfPresent(Double.self, forKey: .balance) ?? 0
        muted = try c.decodeIfPresent(Bool.self, forKey: .muted) ?? false
    }
}

enum SourceMode: String { case app, system }

enum Phase: Equatable {
    case idle
    case waiting(String)
    case live(Int)
    case problem(String)
}

final class Meters: ObservableObject {
    struct Level: Equatable { var l: Float = 0; var r: Float = 0 }
    @Published var levels: [String: Level] = [:]

    /// Peak (linear) -> 0...1 on a -60 dB...0 dB scale.
    static func scale(_ peak: Float) -> Float {
        guard peak > 0.001 else { return 0 }
        return max(0, min(1, 1 + 20 * log10(peak) / 60))
    }
}

final class AppModel: ObservableObject {
    static let shared = AppModel()

    private enum Key {
        static let mode = "mode", app = "appBundleID", appName = "appName"
        static let autoStart = "autoStart", devices = "devices"
        static let tab = "tab", appVolumes = "appVolumes"
    }

    @Published var mode: SourceMode { didSet { scheduleSave(); if running { reconcile() } } }
    @Published var selectedAppID: String {
        didSet {
            if let a = apps.first(where: { $0.id == selectedAppID }) { selectedAppName = a.name }
            scheduleSave()
            DispatchQueue.main.async { self.refreshApps() }
            if running { reconcile() }
        }
    }
    @Published var autoStart: Bool { didSet { scheduleSave() } }
    @Published private(set) var apps: [AppEntry] = []
    @Published private(set) var devices: [OutputDevice] = []
    @Published private(set) var deviceSettings: [String: DeviceSettings]
    @Published private(set) var controls: [String: SystemControls] = [:]
    @Published private(set) var running = false
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var targetStatus: [String: String] = [:]

    // App volume (mixer)
    @Published var tab: MainTab {
        didSet {
            scheduleSave()
            if tab == .mixer { refreshMixer() } else { syncProcessListeners() }
        }
    }
    @Published private(set) var mixerApps: [MixerApp] = []          // every app with audio processes
    @Published private(set) var appVolumes: [String: AppVolumeSetting]
    @Published private(set) var mixerStatus: [String: String] = [:]
    private var appSessions: [String: (key: String, session: TapSession)] = [:]
    private var listenedProcesses = Set<AudioObjectID>()
    private var iconCache: [String: NSImage] = [:]
    private var mixerWork: DispatchWorkItem?
    let meters = Meters()

    private var selectedAppName: String
    private var sessions: [String: TapSession] = [:]
    private var sourceKey = ""
    private var listenedDevices = Set<AudioObjectID>()
    private var deviceWork: DispatchWorkItem?
    private var processWork: DispatchWorkItem?
    private var saveWork: DispatchWorkItem?
    private var meterTimer: Timer?
    private var windowVisible = true
    private weak var window: NSWindow?
    private var occlusionObserver: NSObjectProtocol?

    private init() {
        let d = UserDefaults.standard
        _mode = Published(initialValue: SourceMode(rawValue: d.string(forKey: Key.mode) ?? "") ?? .app)
        _selectedAppID = Published(initialValue: d.string(forKey: Key.app) ?? "")
        _autoStart = Published(initialValue: d.bool(forKey: Key.autoStart))
        var stored: [String: DeviceSettings] = [:]
        if let data = d.data(forKey: Key.devices),
           let decoded = try? JSONDecoder().decode([String: DeviceSettings].self, from: data) {
            stored = decoded
        }
        _deviceSettings = Published(initialValue: stored)
        _tab = Published(initialValue: MainTab(rawValue: d.string(forKey: Key.tab) ?? "") ?? .splitter)
        var volumes: [String: AppVolumeSetting] = [:]
        if let data = d.data(forKey: Key.appVolumes),
           let decoded = try? JSONDecoder().decode([String: AppVolumeSetting].self, from: data) {
            volumes = decoded
        }
        _appVolumes = Published(initialValue: volumes)
        selectedAppName = d.string(forKey: Key.appName) ?? ""

        refreshApps()
        refreshDevices()
        installListeners()
        if mixerActive { refreshMixer() }   // re-applies saved app volumes right away
        if autoStart { DispatchQueue.main.async { self.start() } }
    }

    // MARK: Derived state for the UI

    var canStart: Bool {
        let anyOutput = devices.contains { $0.connected && settings(for: $0.id).enabled }
        return anyOutput && (mode == .system || !selectedAppID.isEmpty)
    }

    var startHint: String? {
        if mode == .app && selectedAppID.isEmpty { return "Choose an app to copy." }
        if !canStart { return "Turn on at least one output." }
        return nil
    }

    func settings(for uid: String) -> DeviceSettings { deviceSettings[uid] ?? DeviceSettings() }
    func systemControls(for uid: String) -> SystemControls? { controls[uid] }
    func isLive(_ uid: String) -> Bool { sessions[uid] != nil }
    private func device(_ uid: String) -> OutputDevice? { devices.first { $0.id == uid && $0.connected } }
    private var mainDevice: OutputDevice? { devices.first { $0.isDefault } }

    var selectedAppDisplayName: String {
        apps.first { $0.id == selectedAppID }?.name ?? (selectedAppName.isEmpty ? selectedAppID : selectedAppName)
    }

    // MARK: Bindings - volume / balance / mute go to the device's own system controls when it has them

    func enabledBinding(_ uid: String) -> Binding<Bool> {
        Binding(get: { self.settings(for: uid).enabled },
                set: { v in
                    let name = self.device(uid)?.name
                    withAnimation(.easeOut(duration: 0.2)) {
                        self.update(uid) { s in
                            s.enabled = v
                            if let name { s.name = name }
                        }
                    }
                })
    }

    func followMainBinding(_ uid: String) -> Binding<Bool> {
        Binding(get: { self.settings(for: uid).followMain },
                set: { v in self.update(uid) { $0.followMain = v } })
    }

    func volumeBinding(_ uid: String) -> Binding<Double> {
        Binding(get: {
            if let c = self.controls[uid], c.hasVolume { return Double(c.volume * 100) }
            return self.settings(for: uid).volume
        }, set: { value in
            let v = min(100, max(0, value.rounded()))
            if let dev = self.device(uid), var c = self.controls[uid], c.hasVolume {
                c.volume = Float(v / 100)
                self.controls[uid] = c                       // the listener confirms with the real value
                CA.setOutput(dev.objectID, kVirtualMainVolume, Float32(c.volume))
            } else {
                self.update(uid) { $0.volume = v }
            }
        })
    }

    /// Centre detent: anything within ±4 snaps to the middle.
    func balanceBinding(_ uid: String) -> Binding<Double> {
        Binding(get: {
            if let c = self.controls[uid], c.hasBalance { return Double((c.balance - 0.5) * 200) }
            return self.settings(for: uid).balance
        }, set: { value in
            let v = abs(value) < 4 ? 0 : min(100, max(-100, value.rounded()))
            self.setBalance(uid, v)
        })
    }

    func resetBalance(_ uid: String) { setBalance(uid, 0) }

    private func setBalance(_ uid: String, _ v: Double) {
        if let dev = device(uid), var c = controls[uid], c.hasBalance {
            c.balance = Float(v / 200 + 0.5)
            controls[uid] = c
            CA.setOutput(dev.objectID, kVirtualMainBalance, Float32(c.balance))
        } else {
            update(uid) { $0.balance = v }
        }
    }

    func isMuted(_ uid: String) -> Bool {
        if let c = controls[uid], c.hasMute { return c.muted }
        return settings(for: uid).muted
    }

    func toggleMute(_ uid: String) {
        if let dev = device(uid), var c = controls[uid], c.hasMute {
            c.muted.toggle()
            controls[uid] = c
            CA.setOutput(dev.objectID, kAudioDevicePropertyMute, UInt32(c.muted ? 1 : 0))
        } else {
            update(uid) { $0.muted.toggle() }
        }
    }

    private func update(_ uid: String, _ change: (inout DeviceSettings) -> Void) {
        var s = settings(for: uid)
        let wasEnabled = s.enabled
        change(&s)
        deviceSettings[uid] = s
        sessions[uid]?.setGains(gains(for: uid))      // live, no restart
        scheduleSave()
        if running && s.enabled != wasEnabled { reconcile() }
        if !s.enabled { pruneDisconnected() }
    }

    /// Gain applied inside the IO block. 1.0 (plain copy) when the device handles volume/balance/mute itself.
    private func gains(for uid: String) -> (l: Float, r: Float) {
        let s = settings(for: uid)
        let c = controls[uid] ?? SystemControls()
        var volume: Float = c.hasVolume ? 1 : Float(s.volume / 100)
        if !c.hasMute && s.muted { volume = 0 }
        let balance: Float = c.hasBalance ? 0 : Float(s.balance / 100)
        if s.followMain, let main = mainDevice, main.id != uid, let m = controls[main.id] {
            volume *= m.muted ? 0 : m.volume
        }
        return (volume * min(1, 1 - balance), volume * min(1, 1 + balance))
    }

    private func applyGains() {
        for (uid, session) in sessions { session.setGains(gains(for: uid)) }
    }

    /// A device's volume / balance / mute changed (menu bar, volume keys, Sound settings, or us).
    /// Only reads back into the UI - never writes - so there is no feedback loop.
    func deviceControlsChanged(_ object: AudioObjectID) {
        guard let dev = devices.first(where: { $0.objectID == object && $0.connected }) else { return }
        let fresh = CA.controls(object)
        if controls[dev.id] != fresh { controls[dev.id] = fresh }
        applyGains()                                   // also covers "follow main volume"
    }

    // MARK: App volume (mixer)
    // Only apps set below 100% or muted are touched: their output is muted by a tap (.mutedWhenTapped)
    // and replayed on the current default output at the new level. Apps at 100% are never tapped.

    /// True when the mixer has work to do; otherwise process-list changes are ignored for it.
    private var mixerActive: Bool { tab == .mixer || appVolumes.values.contains { $0.isAdjusted } }

    var mixerPlaying: [MixerApp] { mixerApps.filter { $0.playing } }

    /// Adjusted apps that aren't playing right now (so they can still be reset).
    var mixerSaved: [MixerApp] {
        let playing = Set(mixerPlaying.map { $0.id })
        return appVolumes.filter { $0.value.isAdjusted && !playing.contains($0.key) }
            .map { id, s in MixerApp(id: id, name: s.name ?? id, icon: appIcon(id, url: nil), playing: false, objects: []) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    var hasAdjustedApps: Bool { appVolumes.values.contains { $0.isAdjusted } }

    func appVolume(_ id: String) -> AppVolumeSetting { appVolumes[id] ?? AppVolumeSetting() }

    func appVolumeBinding(_ app: MixerApp) -> Binding<Double> {
        Binding(get: { self.appVolume(app.id).volume },
                set: { v in self.updateApp(app) { $0.volume = min(100, max(0, v.rounded())) } })
    }

    func toggleAppMute(_ app: MixerApp) { updateApp(app) { $0.muted.toggle() } }
    func resetApp(_ app: MixerApp) { updateApp(app) { $0 = AppVolumeSetting() } }

    func resetAllApps() {
        withAnimation(.easeOut(duration: 0.2)) { appVolumes = [:] }
        mixerStatus = [:]
        scheduleSave()
        reconcileMixer()
    }

    private func updateApp(_ app: MixerApp, _ change: (inout AppVolumeSetting) -> Void) {
        var s = appVolume(app.id)
        change(&s)
        s.name = app.name
        if s.isAdjusted { appVolumes[app.id] = s } else { appVolumes[app.id] = nil }
        scheduleSave()
        if let current = appSessions[app.id], s.isAdjusted {
            current.session.setGains((s.gain, s.gain))   // live, no rebuild
        } else {
            reconcileMixer()                              // crossing 100% creates / removes the tap
        }
    }

    func processOutputChanged() {
        debounce(\.mixerWork, 0.3) { $0.refreshMixer() }
    }

    /// Groups Core Audio process objects by owning app (helpers like com.google.Chrome.helper merge
    /// into Chrome), then brings the taps in line. Called on changes only - never on a timer.
    private func refreshMixer() {
        let me = getpid()
        let myBundle = Bundle.main.bundleIdentifier
        let regular = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular && $0.bundleIdentifier != nil }
        var objects: [String: [AudioObjectID]] = [:]
        var names: [String: String] = [:]
        var urls: [String: URL] = [:]
        var playing = Set<String>()
        var allObjects: [AudioObjectID] = []

        for object in CA.ids(kSystemObject, kAudioHardwarePropertyProcessObjectList) {
            let pid = CA.scalar(object, kAudioProcessPropertyPID, pid_t(-1))
            guard pid > 0, pid != me else { continue }                       // never tap ourselves
            allObjects.append(object)
            let bundle = CA.string(object, kAudioProcessPropertyBundleID) ?? ""
            let owner = regular.first { $0.processIdentifier == pid }
                ?? (bundle.isEmpty ? nil : regular.first { bundle.hasPrefix(($0.bundleIdentifier ?? "") + ".") })
            let id: String
            if let owner, let ownerID = owner.bundleIdentifier {
                id = ownerID
                names[id] = owner.localizedName ?? ownerID
                if let url = owner.bundleURL { urls[id] = url }
            } else if !bundle.isEmpty {
                id = bundle
                if names[id] == nil { names[id] = AppModel.friendlyName(bundle: bundle, pid: pid) }
                if urls[id] == nil, let url = NSRunningApplication(processIdentifier: pid)?.bundleURL { urls[id] = url }
            } else {
                continue                                                      // nameless daemons
            }
            guard id != myBundle else { continue }
            objects[id, default: []].append(object)
            if CA.scalar(object, kAudioProcessPropertyIsRunningOutput, UInt32(0)) != 0 { playing.insert(id) }
        }

        var list: [MixerApp] = []
        for (id, objs) in objects {
            let name = names[id] ?? id
            list.append(MixerApp(id: id, name: name, icon: appIcon(id, url: urls[id]),
                                 playing: playing.contains(id), objects: objs.sorted()))
        }
        list.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        if list != mixerApps {
            withAnimation(.easeOut(duration: 0.2)) { mixerApps = list }
        }
        syncProcessListeners(allObjects)
        reconcileMixer()
    }

    private func reconcileMixer() {
        guard let main = devices.first(where: { $0.isDefault && $0.connected }) else {
            teardownMixer()
            return
        }
        var keep = Set<String>()
        var status: [String: String] = [:]
        for app in mixerApps {
            let s = appVolume(app.id)
            guard s.isAdjusted, !app.objects.isEmpty else { continue }
            keep.insert(app.id)
            // Rebuild when the main output or the app's process list changes (e.g. a new browser tab).
            let key = main.id + "|" + app.objects.map { String($0) }.joined(separator: ",")
            if let current = appSessions[app.id], current.key == key {
                current.session.setGains((s.gain, s.gain))
                continue
            }
            appSessions[app.id]?.session.stop()
            appSessions[app.id] = nil
            let session = TapSession(deviceUID: main.id, deviceName: main.name)
            session.setGains((s.gain, s.gain), immediate: true)
            do {
                try session.start(tap: CATapDescription(stereoMixdownOfProcesses: app.objects), muteOriginal: true)
                appSessions[app.id] = (key, session)
            } catch let e as CAError {
                status[app.id] = "Couldn't change this app (\(e.text))"
            } catch {
                status[app.id] = "Couldn't change this app"
            }
        }
        for id in Array(appSessions.keys) where !keep.contains(id) {
            appSessions[id]?.session.stop()
            appSessions[id] = nil
        }
        if status != mixerStatus { mixerStatus = status }
    }

    private func teardownMixer() {
        for entry in appSessions.values { entry.session.stop() }
        appSessions.removeAll()
    }

    /// Per-process "is playing" listeners exist only while the App volume tab is open.
    private func syncProcessListeners(_ objects: [AudioObjectID] = []) {
        let wanted = tab == .mixer ? Set(objects) : Set<AudioObjectID>()
        for object in listenedProcesses.subtracting(wanted) { removeProcessListener(object) }
        for object in wanted.subtracting(listenedProcesses) {
            var a = CA.addr(kAudioProcessPropertyIsRunningOutput)
            _ = AudioObjectAddPropertyListener(object, &a, processOutputListenerProc, nil)
        }
        listenedProcesses = wanted
    }

    private func removeProcessListener(_ object: AudioObjectID) {
        var a = CA.addr(kAudioProcessPropertyIsRunningOutput)
        _ = AudioObjectRemovePropertyListener(object, &a, processOutputListenerProc, nil)
    }

    private func appIcon(_ id: String, url: URL?) -> NSImage {
        if let cached = iconCache[id] { return cached }
        let resolved = url ?? NSWorkspace.shared.urlForApplication(withBundleIdentifier: id)
        let image = AppModel.icon(resolved, size: 20)
        iconCache[id] = image
        return image
    }

    private static func friendlyName(bundle: String, pid: pid_t) -> String {
        if bundle.hasPrefix("com.apple.WebKit") { return "Safari & web views" }
        if let name = NSRunningApplication(processIdentifier: pid)?.localizedName, !name.isEmpty { return name }
        return bundle.components(separatedBy: ".").last ?? bundle
    }

    // MARK: Start / stop

    func toggleRunning() { running ? stop() : start() }

    func start() {
        guard !running else { return }
        running = true
        reconcile()
    }

    func stop() {
        running = false
        teardownAll()
        phase = .idle
        targetStatus = [:]
        updateMeterTimer()
    }

    func shutdown() {
        stop()
        for object in listenedDevices { removeControlListeners(object) }
        listenedDevices.removeAll()
        teardownMixer()                              // apps' own audio returns as each tap goes away
        for object in listenedProcesses { removeProcessListener(object) }
        listenedProcesses.removeAll()
        saveWork?.cancel()
        saveNow()
    }

    private func teardownAll() {
        for s in sessions.values { s.stop() }
        sessions.removeAll()
        sourceKey = ""
    }

    /// Brings the running taps in line with the current source, device list and switches.
    /// Called on start and whenever anything relevant changes - never on a timer.
    private func reconcile() {
        guard running else { return }

        var key = ""
        var makeTap: (() -> CATapDescription)? = nil
        var waitingFor: String? = nil
        var problem: String? = nil

        switch mode {
        case .system:
            let me = CA.processObject(pid: getpid())
            if me == kUnknownObject {
                problem = "Preparing…"   // our own process object appears once we touch audio; retried on change
            } else {
                key = "system:\(me)"
                makeTap = { CATapDescription(stereoGlobalTapButExcludeProcesses: [me]) }   // exclude self: no feedback
            }
        case .app:
            let objects = processObjects(forBundleID: selectedAppID)
            if objects.isEmpty {
                waitingFor = selectedAppDisplayName
            } else {
                key = "app:" + objects.map { String($0) }.joined(separator: ",")
                makeTap = { CATapDescription(stereoMixdownOfProcesses: objects) }
            }
        }

        let enabled = deviceSettings.filter { $0.value.enabled }.map { $0.key }
        let present = Dictionary(devices.filter { $0.connected }.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })

        guard let tapFactory = makeTap else {
            teardownAll()
            if let waitingFor {
                phase = Phase.waiting(waitingFor)
            } else {
                phase = Phase.problem(problem ?? "Problem")
            }
            var status: [String: String] = [:]
            for uid in enabled { status[uid] = present[uid] == nil ? "Not connected" : "Waiting for source" }
            targetStatus = status
            updateMeterTimer()
            return
        }

        if key != sourceKey {
            teardownAll()
            sourceKey = key
        }
        for uid in Array(sessions.keys) where !enabled.contains(uid) || present[uid] == nil {
            sessions[uid]?.stop()
            sessions[uid] = nil
        }

        var status: [String: String] = [:]
        for uid in enabled {
            guard let device = present[uid] else { status[uid] = "Not connected"; continue }
            if sessions[uid] == nil {
                let session = TapSession(deviceUID: uid, deviceName: device.name)
                session.setGains(gains(for: uid))
                do {
                    try session.start(tap: tapFactory())
                    sessions[uid] = session
                } catch let e as CAError {
                    status[uid] = "Failed: \(e.text)"
                    continue
                } catch {
                    status[uid] = "Failed"
                    continue
                }
            }
            status[uid] = "Playing"
        }
        targetStatus = status
        if sessions.isEmpty {
            phase = .problem(enabled.isEmpty ? "No outputs on" : "No output could start")
        } else {
            phase = .live(sessions.count)
        }
        updateMeterTimer()
    }

    /// The app's own processes plus helpers whose bundle ID extends it (e.g. com.google.Chrome.helper).
    private func processObjects(forBundleID bundleID: String) -> [AudioObjectID] {
        guard !bundleID.isEmpty else { return [] }
        let pids = Set(NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).map { $0.processIdentifier })
        guard !pids.isEmpty else { return [] }
        var result: [AudioObjectID] = []
        for object in CA.ids(kSystemObject, kAudioHardwarePropertyProcessObjectList) {
            let pid = CA.scalar(object, kAudioProcessPropertyPID, pid_t(-1))
            if pids.contains(pid) {
                result.append(object)
            } else if let b = CA.string(object, kAudioProcessPropertyBundleID), b.hasPrefix(bundleID + ".") {
                result.append(object)
            }
        }
        return result.sorted()
    }

    // MARK: Lists

    private func refreshApps() {
        let me = Bundle.main.bundleIdentifier
        var seen = Set<String>()
        var list: [AppEntry] = []
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            guard let id = app.bundleIdentifier, id != me, !seen.contains(id) else { continue }
            seen.insert(id)
            list.append(AppEntry(id: id, name: app.localizedName ?? id, icon: AppModel.icon(app.bundleURL), running: true))
        }
        list.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        if !selectedAppID.isEmpty && !seen.contains(selectedAppID) {   // keep the saved app pinned
            let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: selectedAppID)
            let name = selectedAppName.isEmpty ? selectedAppID : selectedAppName
            list.insert(AppEntry(id: selectedAppID, name: name, icon: AppModel.icon(url), running: false), at: 0)
        }
        apps = list
    }

    private static func icon(_ url: URL?, size: CGFloat = 16) -> NSImage {
        let base = url.map { NSWorkspace.shared.icon(forFile: $0.path) } ?? NSWorkspace.shared.icon(for: .applicationBundle)
        let image = (base.copy() as? NSImage) ?? base
        image.size = NSSize(width: size, height: size)
        return image
    }

    private func refreshDevices() {
        let defaultOut = CA.scalar(kSystemObject, kAudioHardwarePropertyDefaultOutputDevice, kUnknownObject)
        var list: [OutputDevice] = []
        var freshControls: [String: SystemControls] = [:]
        for id in CA.ids(kSystemObject, kAudioHardwarePropertyDevices) {
            guard CA.hasOutputStreams(id),
                  let uid = CA.string(id, kAudioDevicePropertyDeviceUID),
                  !uid.hasPrefix(TapSession.aggregatePrefix) else { continue }
            let name = CA.string(id, kAudioObjectPropertyName) ?? "Unknown device"
            list.append(OutputDevice(id: uid, objectID: id, name: name, isDefault: id == defaultOut,
                                     transport: CA.scalar(id, kAudioDevicePropertyTransportType, UInt32(0)),
                                     connected: true))
            freshControls[uid] = CA.controls(id)
            if var s = deviceSettings[uid], s.name != name {   // keep the remembered name current
                s.name = name
                deviceSettings[uid] = s
            }
        }
        list.sort {
            if $0.isDefault != $1.isDefault { return $0.isDefault }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        // Outputs that are switched on but unplugged stay visible (greyed out) so nothing jumps around.
        let connectedIDs = Set(list.map { $0.id })
        for (uid, s) in deviceSettings.sorted(by: { $0.key < $1.key }) where s.enabled && !connectedIDs.contains(uid) {
            list.append(OutputDevice(id: uid, objectID: kUnknownObject, name: s.name ?? "Unplugged device",
                                     isDefault: false, transport: 0, connected: false))
        }

        // Follow each connected device's own volume / balance / mute; drop listeners for devices that left.
        let objects = Set(list.filter { $0.connected }.map { $0.objectID })
        for object in listenedDevices.subtracting(objects) { removeControlListeners(object) }
        for object in objects.subtracting(listenedDevices) { addControlListeners(object) }
        listenedDevices = objects

        if list != devices { devices = list }
        if freshControls != controls { controls = freshControls }
        applyGains()
        if running { reconcile() }
        reconcileMixer()   // default output changed or left: app-volume taps move to the new main output
    }

    private func pruneDisconnected() {
        let pruned = devices.filter { $0.connected || settings(for: $0.id).enabled }
        if pruned != devices { devices = pruned }
    }

    private func addControlListeners(_ object: AudioObjectID) {
        for selector in CA.controlSelectors {
            var a = CA.addr(selector, kAudioObjectPropertyScopeOutput)
            if AudioObjectHasProperty(object, &a) {
                _ = AudioObjectAddPropertyListener(object, &a, controlListenerProc, nil)
            }
        }
    }

    private func removeControlListeners(_ object: AudioObjectID) {
        for selector in CA.controlSelectors {
            var a = CA.addr(selector, kAudioObjectPropertyScopeOutput)
            _ = AudioObjectRemovePropertyListener(object, &a, controlListenerProc, nil)   // harmless if gone
        }
    }

    // MARK: Change notifications (no polling anywhere)

    private func installListeners() {
        var devicesAddr = CA.addr(kAudioHardwarePropertyDevices)
        _ = AudioObjectAddPropertyListenerBlock(kSystemObject, &devicesAddr, DispatchQueue.main) { [weak self] _, _ in
            self?.debounce(\.deviceWork, 0.5) { $0.refreshDevices() }
        }
        // Default output changed: "Main output" label and "Follow main volume" source move with it.
        var defaultAddr = CA.addr(kAudioHardwarePropertyDefaultOutputDevice)
        _ = AudioObjectAddPropertyListenerBlock(kSystemObject, &defaultAddr, DispatchQueue.main) { [weak self] _, _ in
            self?.debounce(\.deviceWork, 0.2) { $0.refreshDevices() }
        }
        // Fires when any process starts/stops using audio: lets "Waiting for <app>" resolve by itself.
        var processAddr = CA.addr(kAudioHardwarePropertyProcessObjectList)
        _ = AudioObjectAddPropertyListenerBlock(kSystemObject, &processAddr, DispatchQueue.main) { [weak self] _, _ in
            guard let self else { return }
            if self.running { self.debounce(\.processWork, 0.3) { $0.reconcile() } }
            if self.mixerActive { self.debounce(\.mixerWork, 0.3) { $0.refreshMixer() } }
        }

        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification] {
            center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                guard let self else { return }
                self.refreshApps()
                if self.running && self.mode == .app { self.reconcile() }
                if self.mixerActive { self.debounce(\.mixerWork, 0.3) { $0.refreshMixer() } }
            }
        }
    }

    private func debounce(_ slot: ReferenceWritableKeyPath<AppModel, DispatchWorkItem?>, _ delay: Double,
                          _ action: @escaping (AppModel) -> Void) {
        self[keyPath: slot]?.cancel()
        let work = DispatchWorkItem { [weak self] in if let self { action(self) } }
        self[keyPath: slot] = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    // MARK: Meters (20 Hz, only while running and the window is visible)

    func attach(window: NSWindow) {
        guard self.window !== window else { return }
        self.window = window
        window.isMovableByWindowBackground = true
        window.titlebarAppearsTransparent = true
        if let o = occlusionObserver { NotificationCenter.default.removeObserver(o) }
        occlusionObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.didChangeOcclusionStateNotification, object: window, queue: .main) { [weak self] note in
                guard let self, let w = note.object as? NSWindow else { return }
                self.windowVisible = w.occlusionState.contains(.visible)
                self.updateMeterTimer()
            }
        windowVisible = window.occlusionState.contains(.visible)
        updateMeterTimer()
    }

    private func updateMeterTimer() {
        let wanted = running && windowVisible && !sessions.isEmpty
        if wanted, meterTimer == nil {
            let timer = Timer(timeInterval: 0.05, repeats: true) { [weak self] _ in self?.tickMeters() }
            RunLoop.main.add(timer, forMode: .common)   // keeps meters moving while a slider is dragged
            meterTimer = timer
        } else if !wanted, let timer = meterTimer {
            timer.invalidate()
            meterTimer = nil
            if !meters.levels.isEmpty { meters.levels = [:] }
        }
    }

    private func tickMeters() {
        var levels: [String: Meters.Level] = [:]
        for (uid, s) in sessions {
            let p = s.peaks
            levels[uid] = Meters.Level(l: Meters.scale(p.l), r: Meters.scale(p.r))
        }
        if levels != meters.levels { meters.levels = levels }
    }

    // MARK: Persistence

    private func scheduleSave() {
        saveWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.saveNow() }
        saveWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    private func saveNow() {
        let d = UserDefaults.standard
        d.set(mode.rawValue, forKey: Key.mode)
        d.set(selectedAppID, forKey: Key.app)
        d.set(selectedAppName, forKey: Key.appName)
        d.set(autoStart, forKey: Key.autoStart)
        if let data = try? JSONEncoder().encode(deviceSettings) { d.set(data, forKey: Key.devices) }
        d.set(tab.rawValue, forKey: Key.tab)
        if let data = try? JSONEncoder().encode(appVolumes) { d.set(data, forKey: Key.appVolumes) }
    }
}

// MARK: - App

@main
struct GameAudioSplitterApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    /// Never taller than the screen's usable area (menu bar and Dock excluded).
    static var defaultHeight: CGFloat {
        let available = NSScreen.main?.visibleFrame.height ?? 720
        return max(480, min(660, available - 60))
    }

    var body: some Scene {
        Window("Game Audio Splitter", id: "main") {
            ContentView(model: AppModel.shared)
                .frame(minWidth: 380, minHeight: 480)
        }
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentMinSize)
        .defaultSize(width: 420, height: GameAudioSplitterApp.defaultHeight)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { AppModel.shared.shutdown() }
}

// MARK: - Views
// Layout rule: no fixed widths on text. Names truncate with a tooltip; label/value rows are
// HStack + Spacer with the label given priority; only the outputs scroll, Start/Stop stays pinned.

struct ContentView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.top, 36)
                .padding(.horizontal, 20)
                .padding(.bottom, 14)

            if model.tab == .splitter {
                splitterBody
                    .transition(.opacity)
            } else {
                MixerView(model: model)
                    .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.2), value: model.tab)
        .background(GlassBackground().ignoresSafeArea())
        .background(WindowAccessor { model.attach(window: $0) })
    }

    private var splitterBody: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    sourceCard
                    VStack(alignment: .leading, spacing: 4) {
                        SectionTitle("Also play it on")
                        Text("Volume, balance and mute are each device's own system settings, so they always match the system.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(.top, 6)
                    if model.devices.isEmpty {
                        Text("No output devices found.")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    ForEach(model.devices) { device in
                        DeviceCard(model: model, meters: model.meters, device: device)
                    }
                    DisclosureGroup("Advanced") {
                        Toggle("Start automatically when opened", isOn: $model.autoStart)
                            .toggleStyle(.switch)
                            .controlSize(.small)
                            .padding(.top, 6)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .padding(.top, 4)
                }
                .padding(.horizontal, 20)
                .padding(.bottom, 12)
            }

            footer
                .padding(.horizontal, 20)
                .padding(.top, 8)
                .padding(.bottom, 20)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 12) {
                Text("Game Audio Splitter")
                    .font(.title3.weight(.semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .layoutPriority(1)
                Spacer(minLength: 0)
                StatusPill(phase: model.phase)
            }
            Picker("View", selection: $model.tab) {
                Text("Splitter").tag(MainTab.splitter)
                Text("App volume").tag(MainTab.mixer)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
        }
    }

    private var sourceCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                SectionTitle("Source")
                Picker("Source", selection: $model.mode) {
                    Text("One app").tag(SourceMode.app)
                    Text("All system audio").tag(SourceMode.system)
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                if model.mode == .app {
                    Picker("App", selection: $model.selectedAppID) {
                        if model.selectedAppID.isEmpty { Text("Choose an app…").tag("") }
                        ForEach(model.apps) { app in
                            Label {
                                Text(app.running ? app.name : "\(app.name) (not running)")
                            } icon: {
                                Image(nsImage: app.icon)
                            }
                            .tag(app.id)
                        }
                    }
                    .labelsHidden()
                    .help(model.selectedAppDisplayName)
                    Text("Only this app is copied. Everything else stays on your main output.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text("Everything you hear is copied, except this app.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var footer: some View {
        VStack(spacing: 8) {
            if !model.running, let hint = model.startHint {
                Text(hint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button(action: model.toggleRunning) {
                Text(model.running ? "Stop" : "Start")
                    .font(.body.weight(.semibold))
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .tint(model.running ? Color(nsColor: .systemGray) : Color.accentColor)
            .keyboardShortcut(.defaultAction)
            .disabled(!model.running && !model.canStart)
            .animation(.easeOut(duration: 0.2), value: model.running)

            Signature().padding(.top, 4)
        }
    }
}

/// BlackWolf wolf-head mark, drawn on a 12x12 grid: two ears, angular jaw, eye notches (even-odd).
struct WolfMark: Shape {
    func path(in r: CGRect) -> Path {
        let s = min(r.width, r.height) / 12
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: r.minX + x * s, y: r.minY + y * s) }
        var path = Path()
        path.addLines([p(0, 0), p(4.2, 3.6), p(7.8, 3.6), p(12, 0), p(11.2, 7.2), p(6, 12), p(0.8, 7.2)])
        path.closeSubpath()
        path.addLines([p(3.2, 5.6), p(5.2, 6.6), p(3.6, 7.2)])
        path.closeSubpath()
        path.addLines([p(8.8, 5.6), p(6.8, 6.6), p(8.4, 7.2)])
        path.closeSubpath()
        return path
    }
}

/// Hover flag for Signature. A plain ObservableObject, not @State: in newer SDKs (seen with the
/// macOS 27 SDK) @State is a macro whose plugin is not in the Command Line Tools, so it won't build.
final class HoverState: ObservableObject {
    @Published var on = false
}

/// BlackWolf signature under the Start button: quiet until hovered.
struct Signature: View {
    @StateObject private var hover = HoverState()

    var body: some View {
        HStack(spacing: 8) {
            Rectangle().fill(.tertiary).frame(width: 34, height: 1)
            WolfMark()
                .fill(Color.accentColor, style: FillStyle(eoFill: true))
                .frame(width: 11, height: 11)
            Text("BlackWolf")
                .font(.system(size: 11, weight: .semibold))
                .tracking(1)
                .foregroundStyle(.secondary)
            Rectangle().fill(.tertiary).frame(width: 34, height: 1)
        }
        .opacity(hover.on ? 1 : 0.55)
        .animation(.easeOut(duration: 0.2), value: hover.on)
        .onHover { hover.on = $0 }
        .help("Made by BlackWolf")
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Made by BlackWolf")
    }
}

/// App volume tab: one card per app that is playing, plus "Saved" for adjusted apps that are quiet.
struct MixerView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        let playing = model.mixerPlaying
        let saved = model.mixerSaved
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("Changes what you hear on your main output. Copies made by the Splitter keep their own levels.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .layoutPriority(1)
                    Spacer(minLength: 8)
                    if model.hasAdjustedApps {
                        Button("Reset all") { model.resetAllApps() }
                            .buttonStyle(.borderless)
                            .font(.caption)
                            .fixedSize()
                    }
                }

                if playing.isEmpty {
                    Text("Apps appear here while they play sound.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 4)
                }
                ForEach(playing) { app in
                    AppVolumeRow(model: model, app: app)
                        .transition(.opacity.combined(with: .move(edge: .top)))
                }

                if !saved.isEmpty {
                    SectionTitle("Saved").padding(.top, 6)
                    ForEach(saved) { app in
                        AppVolumeRow(model: model, app: app)
                            .transition(.opacity)
                    }
                }
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 20)
            .animation(.easeOut(duration: 0.2), value: playing.map { $0.id })
            .animation(.easeOut(duration: 0.2), value: saved.map { $0.id })
        }
    }
}

struct AppVolumeRow: View {
    @ObservedObject var model: AppModel
    let app: MixerApp

    var body: some View {
        let s = model.appVolume(app.id)
        Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Image(nsImage: app.icon)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .frame(width: 20, height: 20)
                    Text(app.name)
                        .font(.subheadline.weight(.medium))
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .help(app.name)
                        .layoutPriority(1)
                    Spacer(minLength: 8)
                    Text(s.muted ? "Muted" : "\(Int(s.volume))%")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                HStack(spacing: 8) {
                    Button { model.toggleAppMute(app) } label: {
                        Image(systemName: s.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                            .foregroundStyle(s.muted ? Color.orange : Color.secondary)
                    }
                    .buttonStyle(.borderless)
                    .help(s.muted ? "Unmute" : "Mute")
                    Slider(value: model.appVolumeBinding(app), in: 0...100)
                        .controlSize(.small)
                        .help("Double-click or Option-click to reset to 100%")
                        .simultaneousGesture(TapGesture(count: 2).onEnded { model.resetApp(app) })
                        .simultaneousGesture(TapGesture().onEnded {
                            if NSEvent.modifierFlags.contains(.option) { model.resetApp(app) }
                        })
                }
                if let problem = model.mixerStatus[app.id] {
                    Text(problem)
                        .font(.caption)
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }
}

struct DeviceCard: View {
    @ObservedObject var model: AppModel
    let meters: Meters
    let device: OutputDevice

    var body: some View {
        let s = model.settings(for: device.id)
        let c = model.systemControls(for: device.id)
        let muted = model.isMuted(device.id)
        let volume = model.volumeBinding(device.id)
        let balance = model.balanceBinding(device.id)
        Card {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 10) {
                    Image(systemName: device.symbol)
                        .font(.system(size: 14))
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(device.name)
                            .font(.subheadline.weight(.medium))
                            .lineLimit(1)
                            .truncationMode(.tail)
                            .help(device.name)
                        if !subtitle(s).isEmpty {
                            Text(subtitle(s))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.tail)
                                .help(subtitle(s))
                        }
                    }
                    .layoutPriority(1)
                    Spacer(minLength: 8)
                    Toggle("Use \(device.name)", isOn: model.enabledBinding(device.id))
                        .toggleStyle(.switch)
                        .controlSize(.small)
                        .labelsHidden()
                        .fixedSize()
                }

                if s.enabled {
                    VStack(alignment: .leading, spacing: 12) {
                        if device.isDefault {
                            Text("This is your main output. You'll hear it twice.")
                                .font(.caption)
                                .foregroundStyle(.orange)
                                .fixedSize(horizontal: false, vertical: true)
                        }

                        VStack(alignment: .leading, spacing: 4) {
                            HStack(spacing: 8) {
                                Button { model.toggleMute(device.id) } label: {
                                    Image(systemName: muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                                        .foregroundStyle(muted ? Color.orange : Color.secondary)
                                }
                                .buttonStyle(.borderless)
                                .help(muted ? "Unmute" : "Mute")
                                Text("Device volume")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                    .layoutPriority(1)
                                Spacer(minLength: 8)
                                Text("\(Int(volume.wrappedValue.rounded()))%")
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                            Slider(value: volume, in: 0...100)
                                .controlSize(.small)
                        }

                        VStack(alignment: .leading, spacing: 4) {
                            HStack(spacing: 8) {
                                Text("Balance")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                    .layoutPriority(1)
                                Spacer(minLength: 8)
                                Text(DeviceCard.balanceText(balance.wrappedValue))
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                    .onTapGesture(count: 2) { model.resetBalance(device.id) }
                                    .help("Double-click to centre")
                            }
                            Slider(value: balance, in: -100...100)
                                .controlSize(.small)
                                .simultaneousGesture(TapGesture(count: 2).onEnded { model.resetBalance(device.id) })
                        }

                        if device.connected, let note = DeviceCard.fallbackNote(c) {
                            Text(note)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }

                        if !device.isDefault {
                            Toggle(isOn: model.followMainBinding(device.id)) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("Follow main volume")
                                        .font(.caption)
                                    Text("Gets louder and quieter with your main output's volume keys.")
                                        .font(.caption2)
                                        .foregroundStyle(.secondary)
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .toggleStyle(.switch)
                            .controlSize(.mini)
                        }

                        if model.running && model.isLive(device.id) {
                            MeterView(meters: meters, uid: device.id)
                                .transition(.opacity)
                        }
                    }
                    .disabled(!device.connected)
                    .opacity(device.connected ? 1 : 0.5)
                    .transition(.opacity.combined(with: .move(edge: .top)))
                }
            }
        }
        .animation(.easeOut(duration: 0.2), value: s.enabled)
    }

    private func subtitle(_ s: DeviceSettings) -> String {
        var parts: [String] = []
        if device.isDefault { parts.append("Main output") }
        if !device.connected {
            parts.append("Not connected")
        } else if model.running, s.enabled, let st = model.targetStatus[device.id] {
            parts.append(st)
        }
        return parts.joined(separator: " · ")
    }

    static func fallbackNote(_ c: SystemControls?) -> String? {
        guard let c else { return nil }
        if !c.hasVolume { return "This device has no volume control of its own; set here." }
        if !c.hasBalance { return "This device has no balance control of its own; set here." }
        return nil
    }

    static func balanceText(_ b: Double) -> String {
        let v = Int(b.rounded())
        if v == 0 { return "Centre" }
        return v < 0 ? "L \(-v)" : "R \(v)"
    }
}

struct MeterView: View {
    @ObservedObject var meters: Meters
    let uid: String

    var body: some View {
        let level = meters.levels[uid] ?? Meters.Level()
        VStack(spacing: 3) {
            bar(level.l)
            bar(level.r)
        }
        .accessibilityHidden(true)
    }

    private func bar(_ value: Float) -> some View {
        Capsule()
            .fill(.quaternary)
            .frame(height: 3)
            .overlay(alignment: .leading) {
                Capsule()
                    .fill(Color.accentColor)
                    .scaleEffect(x: CGFloat(value), y: 1, anchor: .leading)
            }
    }
}

struct StatusPill: View {
    let phase: Phase

    var body: some View {
        let text = describe.text
        let color = describe.color
        HStack(spacing: 6) {
            Circle().fill(color).frame(width: 6, height: 6)
            Text(text)
                .font(.caption.weight(.medium))
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .foregroundStyle(color)
        .padding(.horizontal, 10)
        .padding(.vertical, 4)
        .background(Capsule().fill(color.opacity(0.14)))
        .help(text)
        .animation(.easeOut(duration: 0.2), value: text)
    }

    private var describe: (text: String, color: Color) {
        switch phase {
        case .idle: return ("Idle", Color.secondary)
        case .waiting(let name): return ("Waiting for \(name)…", Color.orange)
        case .live(let count): return ("Live · \(count) output\(count == 1 ? "" : "s")", Color.accentColor)
        case .problem(let message): return (message, Color.orange)
        }
    }
}

struct SectionTitle: View {
    let title: String
    init(_ title: String) { self.title = title }

    var body: some View {
        Text(title)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.secondary)
            .lineLimit(1)
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(HierarchicalShapeStyle.quaternary.opacity(0.6))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5)
            )
    }
}

// MARK: - AppKit bridges

/// Behind-window blur: the desktop shows through softly, follows light/dark automatically.
struct GlassBackground: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .hudWindow
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }

    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}

struct WindowAccessor: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = WindowAccessorView()
        view.onWindow = onWindow
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}

final class WindowAccessorView: NSView {
    var onWindow: ((NSWindow) -> Void)?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let window { onWindow?(window) }
    }
}
