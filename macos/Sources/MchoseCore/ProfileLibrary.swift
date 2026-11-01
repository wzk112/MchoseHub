import Foundation

public struct ProfileLibrary: Sendable {
  public let root: URL
  public init(root: URL) { self.root = root }
  private func directory(model: String) throws -> URL {
    _ = try KeyboardSchema.load(model: model)
    let directory = root.appendingPathComponent(model, isDirectory: true)
    try checkLinks(directory)
    return directory
  }
  private func path(model: String, name: String) throws -> URL {
    guard !name.isEmpty, name.count <= 64,
      name == name.trimmingCharacters(in: .whitespacesAndNewlines),
      !name.hasSuffix("."),
      !name.unicodeScalars.contains(where: {
        CharacterSet.controlCharacters.contains($0) || "<>:\"/\\|?*".unicodeScalars.contains($0)
      }), name != ".", name != ".."
    else { throw MchoseError.invalid("配置名称应为 1–64 字，不能包含路径符号或首尾空格。") }
    let stem = name.components(separatedBy: ".")[0].uppercased()
    guard !["CON", "PRN", "AUX", "NUL"].contains(stem),
      stem.range(of: "^(COM|LPT)[0-9]$", options: .regularExpression) == nil
    else { throw MchoseError.invalid("请使用其他配置名。") }
    let url = try directory(model: model).appendingPathComponent(
      name.precomposedStringWithCanonicalMapping + ".json")
    try checkLinks(url)
    return url
  }
  public func list(model: String) throws -> [String] {
    let dir = try directory(model: model)
    guard FileManager.default.fileExists(atPath: dir.path) else { return [] }
    return try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
      .filter { $0.pathExtension == "json" && (try? checkLinks($0)) != nil }.map {
        $0.deletingPathExtension().lastPathComponent
      }.sorted()
  }
  public func save(name: String, profile: KeyboardProfile) throws {
    let url = try path(model: profile.model, name: name)
    try FileManager.default.createDirectory(
      at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try profile.save(to: url, schema: KeyboardSchema.load(model: profile.model))
  }
  public func load(model: String, name: String) throws -> KeyboardProfile {
    try KeyboardProfile.read(
      from: path(model: model, name: name), schema: KeyboardSchema.load(model: model))
  }
  public func delete(model: String, name: String) throws {
    let destination = try path(model: model, name: name)
    try FileManager.default.removeItem(at: destination)
  }
  private func checkLinks(_ url: URL) throws {
    var current = url
    while current.path != "/" {
      if let attrs = try? FileManager.default.attributesOfItem(atPath: current.path),
        attrs[.type] as? FileAttributeType == .typeSymbolicLink
      {
        throw MchoseError.invalid("配置库路径不能含符号链接。")
      }
      if current.standardizedFileURL == root.standardizedFileURL { break }
      current.deleteLastPathComponent()
    }
  }
}
