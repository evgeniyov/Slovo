import Combine
import CoreAudio
import Foundation

/// Core Audio input devices.
public enum AudioDevices {
    /// Every device with at least one input channel, sorted by name.
    public static func inputDevices() -> [AudioInputDevice] {
        inputDeviceIDs()
            .compactMap(device(for:))
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// The system default input device.
    public static func defaultInputDevice() -> AudioInputDevice? {
        defaultInputDeviceID().flatMap(device(for:))
    }

    /// The device with this UID when it is connected.
    public static func device(uid: String) -> AudioInputDevice? {
        inputDevices().first { $0.uid == uid }
    }

    /// The Core Audio id of the connected input device with this UID.
    static func deviceID(uid: String) -> AudioDeviceID? {
        inputDeviceIDs().first { stringProperty(kAudioDevicePropertyDeviceUID, of: $0) == uid }
    }

    static func defaultInputDeviceID() -> AudioDeviceID? {
        var address = globalAddress(kAudioHardwarePropertyDefaultInputDevice)
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &deviceID)
        guard status == noErr, deviceID != AudioDeviceID(kAudioObjectUnknown) else { return nil }
        return deviceID
    }

    static func globalAddress(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }

    /// Visible devices with input channels. Hidden ones include the private aggregate
    /// devices that process taps create.
    private static func inputDeviceIDs() -> [AudioDeviceID] {
        allDeviceIDs().filter { !isHidden($0) && inputChannelCount(of: $0) > 0 }
    }

    private static func device(for id: AudioDeviceID) -> AudioInputDevice? {
        guard let uid = stringProperty(kAudioDevicePropertyDeviceUID, of: id) else { return nil }
        return AudioInputDevice(uid: uid, name: stringProperty(kAudioObjectPropertyName, of: id) ?? uid)
    }

    private static func allDeviceIDs() -> [AudioDeviceID] {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var address = globalAddress(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        guard count > 0 else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        // A device can disappear between the two calls.
        return Array(ids.prefix(Int(size) / MemoryLayout<AudioDeviceID>.size))
    }

    private static func isHidden(_ id: AudioDeviceID) -> Bool {
        var address = globalAddress(kAudioDevicePropertyIsHidden)
        guard AudioObjectHasProperty(id, &address) else { return false }
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr else { return false }
        return value != 0
    }

    private static func inputChannelCount(of id: AudioDeviceID) -> Int {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration,
                                                 mScope: kAudioObjectPropertyScopeInput,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return 0 }
        let buffers = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return buffers.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    private static func stringProperty(_ selector: AudioObjectPropertySelector, of id: AudioObjectID) -> String? {
        var address = globalAddress(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) { pointer in
            AudioObjectGetPropertyData(id, &address, 0, nil, &size, pointer)
        }
        guard status == noErr, let value else { return nil }
        // Core Audio hands out a retained string for these properties.
        return value.takeRetainedValue() as String
    }
}

/// Publishes the device list and the default device, updating when devices come and go.
@MainActor
public final class AudioDeviceObserver: ObservableObject {
    @Published public private(set) var devices: [AudioInputDevice] = []
    @Published public private(set) var defaultDeviceUID: String?

    private static let selectors = [kAudioHardwarePropertyDevices, kAudioHardwarePropertyDefaultInputDevice]
    private let log = SlovoLog.logger("AudioDevices")
    private var listener: AudioObjectPropertyListenerBlock?

    public init() {}

    /// Installs Core Audio property listeners and reads the current list.
    public func start() {
        refresh()
        guard listener == nil else { return }
        let block = Self.makeListener { [weak self] in
            self?.refresh()
        }
        for selector in Self.selectors {
            var address = AudioDevices.globalAddress(selector)
            let status = AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address,
                                                             DispatchQueue.main, block)
            if status != noErr {
                log.error("Could not watch audio devices: OSStatus \(status)")
            }
        }
        listener = block
    }

    public func stop() {
        guard let listener else { return }
        for selector in Self.selectors {
            var address = AudioDevices.globalAddress(selector)
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address,
                                                   DispatchQueue.main, listener)
        }
        self.listener = nil
    }

    public func refresh() {
        let current = AudioDevices.inputDevices()
        if current != devices {
            devices = current
        }
        let uid = AudioDevices.defaultInputDevice()?.uid
        if uid != defaultDeviceUID {
            defaultDeviceUID = uid
        }
    }

    /// Built outside the main actor because Core Audio owns the block; it runs on the
    /// main queue passed at registration.
    nonisolated private static func makeListener(_ onChange: @escaping @MainActor () -> Void) -> AudioObjectPropertyListenerBlock {
        { _, _ in
            MainActor.assumeIsolated {
                onChange()
            }
        }
    }
}
