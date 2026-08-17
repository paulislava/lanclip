import Foundation

public func isPrivateAddress(_ address: String) -> Bool {
    let bare = address.split(separator: "%").first.map(String.init) ?? address
    if bare.isEmpty { return false }

    if bare.contains(":") {
        let lowered = bare.lowercased()
        return lowered == "::1" || lowered.hasPrefix("fe80:") || lowered.hasPrefix("fd")
    }

    let octets = bare.split(separator: ".").compactMap { Int($0) }
    guard octets.count == 4, octets.allSatisfy({ (0...255).contains($0) }) else { return false }

    if octets[0] == 127 || octets[0] == 10 { return true }
    if octets[0] == 192 && octets[1] == 168 { return true }
    if octets[0] == 172 && (16...31).contains(octets[1]) { return true }
    if octets[0] == 169 && octets[1] == 254 { return true }
    return false
}
