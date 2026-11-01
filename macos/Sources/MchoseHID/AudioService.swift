import Foundation
import MchoseCore

public struct AudioResult: Sendable {
  public let structured: Bool
  public let state: AudioProtocol.LegacyState?
  public let reports: [Data]
}
public enum AudioAction: Sendable {
  case read
  case lighting(
    mode: UInt8, brightness: UInt8, cycle: UInt8, index: UInt8, r: UInt8, g: UInt8, b: UInt8)
  case eq(UInt8)
  case voice(Bool)
  case restore
  case raw(id: UInt8, payload: Data)
}
public final class AudioService: Sendable {
  private let queue = DispatchQueue(label: "com.mchosehub.audio", qos: .userInitiated)
  public init() {}
  public func perform(deviceID: UInt64, action: AudioAction, token: CancellationToken) async throws
    -> AudioResult
  {
    try await withCheckedThrowingContinuation { continuation in
      queue.async {
        continuation.resume(
          with: Result {
            try token.check()
            guard let device = try MacHID.scan().first(where: { $0.id == deviceID }), device.isAudio
            else { throw MchoseError.invalid("未确认音频配置接口。") }
            let transport = try MacFeatureTransport(
              registryID: deviceID, checkCancellation: token.check)
            var reports: [Data] = []
            func receive(seconds: Double, prefix: UInt8? = nil, reportID: UInt8 = 0xBC) throws
              -> Data?
            {
              let end = ProcessInfo.processInfo.systemUptime + seconds
              while ProcessInfo.processInfo.systemUptime < end {
                try token.check()
                if let report = try transport.nextNativePacket(timeout: 0.2), report.id == reportID
                {
                  reports.append(report.data)
                  if prefix == nil || report.data.first == prefix { return report.data }
                }
              }
              return nil
            }
            if case .raw(let id, let payload) = action {
              try transport.sendOutput(reportID: id, payload: payload)
              _ = try receive(seconds: 1.5, reportID: id)
              return AudioResult(structured: false, state: nil, reports: reports)
            }
            transport.clearInbox()
            try transport.sendOutput(
              reportID: 0xBC, payload: AudioProtocol.frame(type: Data([0, 1]), command: 0))
            let structured =
              try receive(seconds: 1.5, prefix: 0xAA).flatMap(AudioProtocol.parse)?.valid == true
            if !structured {
              transport.clearInbox()
              try transport.sendOutput(reportID: 0xBC, payload: AudioProtocol.legacy([0x11, 0]))
              guard try receive(seconds: 1.5, prefix: 0x11) != nil else {
                throw MchoseError.invalid("音频设备未响应已知状态请求。")
              }
            }
            func send(_ legacy: [UInt8], command: UInt8, payload: [UInt8]) throws {
              try token.check()
              let bytes =
                try structured
                ? AudioProtocol.frame(type: Data([0, 2]), command: command, payload: Data(payload))
                : AudioProtocol.legacy(legacy)
              try transport.sendOutput(reportID: 0xBC, payload: bytes)
              Thread.sleep(forTimeInterval: 0.08)
            }
            switch action {
            case .lighting(let mode, let brightness, let cycle, let index, let r, let g, let b):
              try send(
                [0x21, 3, mode, brightness, cycle, 0], command: 3,
                payload: [mode, brightness, cycle, 0])
              try send([0x22, index, 1, g, r, b], command: 14, payload: [index, 1, g, r, b])
              try send([0x21, 6, mode], command: 7, payload: [mode])
              try send([0x21, 5, 3, 1], command: 5, payload: [3, 1])
            case .eq(let value):
              try transport.sendOutput(
                reportID: 0xBC, payload: AudioProtocol.legacy([0x21, 1, value]))
            case .voice(let on):
              try transport.sendOutput(
                reportID: 0xBC, payload: AudioProtocol.legacy([0x21, 2, on ? 1 : 0]))
            case .restore:
              try transport.sendOutput(reportID: 0xBC, payload: AudioProtocol.legacy([0x21, 7, 1]))
            case .read, .raw: break
            }
            if case .read = action {
              // The initial read is already current.
            } else {
              transport.clearInbox()
              let request =
                try structured
                ? AudioProtocol.frame(type: Data([0, 1]), command: 0)
                : AudioProtocol.legacy([0x11, 0])
              try transport.sendOutput(reportID: 0xBC, payload: request)
              guard try receive(seconds: 1.5, prefix: structured ? 0xAA : 0x11) != nil else {
                throw MchoseError.invalid("指令已发送，但设备未响应状态回读。请重新读取确认结果。")
              }
            }
            let state =
              try !structured
              ? reports.last.flatMap { try AudioProtocol.LegacyState(data: $0) } : nil
            return AudioResult(structured: structured, state: state, reports: reports)
          })
      }
    }
  }
}
