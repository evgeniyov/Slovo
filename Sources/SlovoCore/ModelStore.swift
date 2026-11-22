import Foundation

/// Where downloaded speech models live. The bench and the app share this folder,
/// so a model is downloaded once.
public enum ModelStore {
    public static var root: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return support.appendingPathComponent("Slovo/Models", isDirectory: true)
    }
}
