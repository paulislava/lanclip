import Foundation

public func isPrivateAddress(_ address: String) -> Bool {
    let bare = address.split(separator: "%").first.map(String.init) ?? address
    if bare.isEmpty { return false }

    if bare.contains(":") {
        let lowered = bare.lowercased()
        if lowered == "::1" { return true }

        // Extract first hex group: fe80::1 -> "fe80", ::1 -> ""
        let firstGroup = lowered.split(separator: ":").first.map(String.init) ?? ""
        if firstGroup.isEmpty { return true }  // :: prefix covers loopback and link-local

        // Parse first hex group to check ranges
        if let first = Int(firstGroup, radix: 16) {
            // Link-local: fe80::/10 (fe80 to febf)
            if first >= 0xfe80 && first <= 0xfebf { return true }
            // ULA: fc00::/7 (fc00 to fdff)
            if first >= 0xfc00 && first <= 0xfdff { return true }
        }
        return false
    }

    let octets = bare.split(separator: ".").compactMap { Int($0) }
    guard octets.count == 4, octets.allSatisfy({ (0...255).contains($0) }) else { return false }

    if octets[0] == 127 || octets[0] == 10 { return true }
    if octets[0] == 192 && octets[1] == 168 { return true }
    if octets[0] == 172 && (16...31).contains(octets[1]) { return true }
    if octets[0] == 169 && octets[1] == 254 { return true }
    return false
}
