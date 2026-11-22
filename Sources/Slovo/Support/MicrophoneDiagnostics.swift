import AVFoundation
import CoreAudio
import Foundation
import os
import SlovoCore

/// `Slovo --diagnose-microphone <report file>` records a few seconds through each capture
/// path the app uses and writes what arrived, so a missing permission, a wrong device and
/// a broken capture path can be told apart. Launch it through LaunchServices
/// (`open -n Slovo.app --args …`) so macOS checks Slovo's own microphone permission.
@MainActor
enum MicrophoneDiagnostics {
    static let argument = "--diagnose-microphone"

    /// Runs the diagnostics and exits when the argument is present; returns otherwise.
    static func runIfRequested() {
        let arguments = CommandLine.arguments
        guard let index = arguments.firstIndex(of: argument) else { return }
        let path = index + 1 < arguments.count
            ? arguments[index + 1]
            : NSTemporaryDirectory() + "slovo-microphone.txt"
        Task { @MainActor in
            let report = await makeReport()
            try? report.write(toFile: path, atomically: true, encoding: .utf8)
            exit(0)
        }
        RunLoop.main.run()
    }

    private static func makeReport() async -> String {
        var lines: [String] = []
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        lines.append("bundle \(Bundle.main.bundleIdentifier ?? "nil"), pid \(getpid())")
        lines.append("microphone authorization: \(describe(status))")
        if status == .notDetermined {
            let granted = await AVCaptureDevice.requestAccess(for: .audio)
            lines.append("requested access: \(granted)")
        }
        let chosen = UserDefaults.standard.string(forKey: SettingsKey.microphoneUID) ?? ""
        lines.append("chosen microphone uid: \(chosen.isEmpty ? "system default" : chosen)")
        lines.append("default input: \(AudioDevices.defaultInputDevice().map { "\($0.name) [\($0.uid)]" } ?? "none")")
        let devices = AudioDevices.inputDevices()
        for device in devices {
            lines.append("input device: \(device.name) [\(device.uid)]")
        }

        lines.append(await rawCapture(uid: nil))
        for device in devices {
            lines.append(await rawCapture(uid: device.uid))
        }
        let captureDevices = AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external],
                                                              mediaType: .audio, position: .unspecified).devices
        for device in captureDevices {
            lines.append("capture device: \(device.localizedName) [\(device.uniqueID)]")
        }
        lines.append(await sessionCapture(uid: nil))
        for device in devices {
            lines.append(await sessionCapture(uid: device.uid))
        }
        if !chosen.isEmpty {
            lines.append(await recorderCapture(uid: chosen, milliseconds: 2500))
            lines.append(await recorderCapture(uid: chosen, milliseconds: 5000))
        }
        lines.append(await recorderCapture(uid: nil, milliseconds: 2500))
        return lines.joined(separator: "\n") + "\n"
    }

    private static func describe(_ status: AVAuthorizationStatus) -> String {
        switch status {
        case .notDetermined: return "not determined"
        case .restricted: return "restricted"
        case .denied: return "denied"
        case .authorized: return "authorized"
        @unknown default: return "unknown \(status.rawValue)"
        }
    }

    /// The capture path the dictation uses, through `MicrophoneRecorder`.
    private static func recorderCapture(uid: String?, milliseconds: Int) async -> String {
        let label = "recorder[\(uid ?? "default"), \(milliseconds) ms]"
        let recorder = MicrophoneRecorder()
        do {
            try recorder.start(deviceUID: uid)
        } catch {
            return "\(label): start threw \(error.localizedDescription)"
        }
        try? await Task.sleep(for: .milliseconds(milliseconds))
        let stillRecording = recorder.isRecording
        let audio = recorder.stop()
        let peak = audio.samples.reduce(Float(0)) { max($0, abs($1)) }
        let zeros = audio.samples.reduce(0) { $0 + ($1 == 0 ? 1 : 0) }
        let seconds = Double(audio.samples.count) / audio.sampleRate
        return "\(label): recording at stop \(stillRecording), \(audio.samples.count) samples (\(String(format: "%.2f", seconds)) s), "
            + "rms \(SpeechEnergy.rms(audio.samples[...])), peak \(peak), exact zeros \(zeros), "
            + "silent by SpeechEnergy \(SpeechEnergy.isEffectivelySilent(audio))"
    }

    /// A bare `AVAudioEngine` tap with no conversion, optionally on a chosen device.
    private static func rawCapture(uid: String?) async -> String {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: measureRaw(uid: uid, seconds: 2))
            }
        }
    }

    nonisolated private static func measureRaw(uid: String?, seconds: Double) -> String {
        let engine = AVAudioEngine()
        let input = engine.inputNode
        var label = "raw[default]"
        if let uid {
            guard let deviceID = deviceID(uid: uid) else { return "raw[\(uid)]: device not found" }
            var value = deviceID
            let status: OSStatus = input.audioUnit.map {
                AudioUnitSetProperty($0, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                     &value, UInt32(MemoryLayout<AudioDeviceID>.size))
            } ?? -1
            label = "raw[\(uid), set status \(status)]"
        }
        let format = input.outputFormat(forBus: 0)
        let stats = TapStats()
        input.installTap(onBus: 0, bufferSize: 1024, format: nil) { buffer, _ in
            stats.add(buffer)
        }
        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            return "\(label): engine start failed \(error.localizedDescription)"
        }
        let changes = OSAllocatedUnfairLock(initialState: 0)
        let observer = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
        ) { _ in
            changes.withLock { $0 += 1 }
        }
        Thread.sleep(forTimeInterval: seconds)
        let runningAtEnd = engine.isRunning
        NotificationCenter.default.removeObserver(observer)
        input.removeTap(onBus: 0)
        engine.stop()
        return "\(label): node format \(format.sampleRate) Hz x\(format.channelCount), \(stats.summary), "
            + "configuration changes \(changes.withLock { $0 }), running at end \(runningAtEnd)"
    }

    /// The same capture through `AVCaptureSession`, which binds to one device directly and
    /// converts to 16 kHz mono Float32 itself.
    private static func sessionCapture(uid: String?) async -> String {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: measureSession(uid: uid, seconds: 2.5))
            }
        }
    }

    nonisolated private static func measureSession(uid: String?, seconds: Double) -> String {
        let label = "session[\(uid ?? "default")]"
        guard let device = uid.map({ AVCaptureDevice(uniqueID: $0) }) ?? AVCaptureDevice.default(for: .audio) else {
            return "\(label): no capture device with this id"
        }
        let session = AVCaptureSession()
        do {
            let input = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(input) else { return "\(label): cannot add input" }
            session.addInput(input)
        } catch {
            return "\(label): input failed \(error.localizedDescription)"
        }
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 16_000,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
            AVLinearPCMIsBigEndianKey: false
        ]
        let startedAt = ProcessInfo.processInfo.systemUptime
        let sink = SessionSink(startedAt: startedAt)
        output.setSampleBufferDelegate(sink, queue: DispatchQueue(label: "SlovoDiagnostics.session"))
        guard session.canAddOutput(output) else { return "\(label): cannot add output" }
        session.addOutput(output)
        session.startRunning()
        let startDuration = ProcessInfo.processInfo.systemUptime - startedAt
        Thread.sleep(forTimeInterval: seconds)
        session.stopRunning()
        return "\(label): device \(device.localizedName) [\(device.uniqueID)], startRunning took "
            + String(format: "%.3f s, ", startDuration) + sink.summary
    }

    nonisolated private static func deviceID(uid: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslateUIDToDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var qualifier = uid as CFString
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = withUnsafeMutablePointer(to: &qualifier) { pointer in
            AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                       UInt32(MemoryLayout<CFString>.size), pointer, &size, &deviceID)
        }
        guard status == noErr, deviceID != AudioDeviceID(kAudioObjectUnknown) else { return nil }
        return deviceID
    }
}

/// Totals of the first channel of every tap buffer, filled on the render thread.
private final class TapStats: @unchecked Sendable {
    private struct Totals {
        var buffers = 0
        var frames = 0
        var zeros = 0
        var peak: Float = 0
        var squares: Double = 0
        var firstFormat = "none"
    }

    private let totals = OSAllocatedUnfairLock(initialState: Totals())

    func add(_ buffer: AVAudioPCMBuffer) {
        let frames = Int(buffer.frameLength)
        let description = "\(buffer.format.sampleRate) Hz x\(buffer.format.channelCount) \(buffer.format.commonFormat.rawValue)"
        var zeroCount = 0
        var peakValue: Float = 0
        var squareSum: Double = 0
        if let channel = buffer.floatChannelData?[0] {
            for index in 0..<frames {
                let sample = channel[index]
                if sample == 0 { zeroCount += 1 }
                peakValue = max(peakValue, abs(sample))
                squareSum += Double(sample * sample)
            }
        }
        let zeros = zeroCount, peak = peakValue, squares = squareSum
        totals.withLock { totals in
            if totals.buffers == 0 { totals.firstFormat = description }
            totals.buffers += 1
            totals.frames += frames
            totals.zeros += zeros
            totals.peak = max(totals.peak, peak)
            totals.squares += squares
        }
    }

    var summary: String {
        totals.withLock { totals in
            let rms = totals.frames > 0 ? (totals.squares / Double(totals.frames)).squareRoot() : 0
            return "\(totals.buffers) buffers, \(totals.frames) frames, buffer format \(totals.firstFormat), "
                + "rms \(rms), peak \(totals.peak), exact zeros \(totals.zeros)"
        }
    }
}

/// Collects `AVCaptureAudioDataOutput` buffers and when the first one arrived.
private final class SessionSink: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate, @unchecked Sendable {
    private struct Totals {
        var buffers = 0
        var samples = 0
        var peak: Float = 0
        var squares: Double = 0
        var firstBufferAfter: Double = -1
        var format = "none"
    }

    private let startedAt: TimeInterval
    private let totals = OSAllocatedUnfairLock(initialState: Totals())

    init(startedAt: TimeInterval) {
        self.startedAt = startedAt
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        let arrivedAfter = ProcessInfo.processInfo.systemUptime - startedAt
        var description = "unknown"
        if let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
           let basic = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription)?.pointee {
            description = "\(basic.mSampleRate) Hz x\(basic.mChannelsPerFrame) \(basic.mBitsPerChannel) bit flags \(basic.mFormatFlags)"
        }
        var bufferList = AudioBufferList()
        var blockBuffer: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: &bufferList,
            bufferListSize: MemoryLayout<AudioBufferList>.size, blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: &blockBuffer)
        var count = 0
        var peakValue: Float = 0
        var squareSum: Double = 0
        if status == noErr, let data = bufferList.mBuffers.mData {
            count = Int(bufferList.mBuffers.mDataByteSize) / MemoryLayout<Float>.size
            let samples = data.bindMemory(to: Float.self, capacity: count)
            for index in 0..<count {
                peakValue = max(peakValue, abs(samples[index]))
                squareSum += Double(samples[index] * samples[index])
            }
        }
        let samples = count, peak = peakValue, squares = squareSum, format = description
        totals.withLock { totals in
            if totals.buffers == 0 {
                totals.firstBufferAfter = arrivedAfter
                totals.format = format
            }
            totals.buffers += 1
            totals.samples += samples
            totals.peak = max(totals.peak, peak)
            totals.squares += squares
        }
    }

    var summary: String {
        totals.withLock { totals in
            let rms = totals.samples > 0 ? (totals.squares / Double(totals.samples)).squareRoot() : 0
            return "\(totals.buffers) buffers, \(totals.samples) samples, first buffer after "
                + String(format: "%.3f s", totals.firstBufferAfter) + ", format \(totals.format), "
                + "rms \(rms), peak \(totals.peak)"
        }
    }
}
