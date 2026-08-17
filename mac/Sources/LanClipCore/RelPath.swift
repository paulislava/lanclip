import Foundation

public enum RelPath {
    static let maxComponent = 150
    static let maxTotal = 400

    private static let illegal: Set<Character> = ["<", ">", ":", "\"", "|", "?", "*", "\0"]

    private static let reserved: Set<String> = {
        var names: Set<String> = ["con", "prn", "aux", "nul"]
        for n in 1...9 {
            names.insert("com\(n)")
            names.insert("lpt\(n)")
        }
        return names
    }()

    public static func normalize(_ raw: String) -> String? {
        let unified = raw.replacingOccurrences(of: "\\", with: "/")
        var components: [String] = []

        for piece in unified.split(separator: "/", omittingEmptySubsequences: true) {
            let component = String(piece)
            if component == "." { continue }
            if component == ".." { return nil }
            guard let safe = sanitize(component) else { return nil }
            components.append(safe)
        }

        guard !components.isEmpty else { return nil }
        let joined = components.joined(separator: "/")
        guard joined.count <= maxTotal else { return nil }
        return joined
    }

    private static func sanitize(_ component: String) -> String? {
        var cleaned = String(component.map { char in
            if illegal.contains(char) { return "_" }
            if let scalar = char.unicodeScalars.first,
               char.unicodeScalars.count == 1,
               scalar.properties.generalCategory == .control {
                return "_"
            }
            return char
        })

        while let last = cleaned.last, last == "." || last == " " {
            cleaned.removeLast()
        }

        guard !cleaned.isEmpty else { return nil }

        let base = cleaned.split(separator: ".", maxSplits: 1).first.map(String.init) ?? cleaned
        if reserved.contains(base.lowercased()) {
            cleaned += "_"
        }

        guard cleaned.count <= maxComponent else { return nil }
        return cleaned
    }
}
