import XCTest
@testable import LanClipCore

/// Подставной `HealthProbing`, считающий обращения по каждому адресу — тесты
/// резолвера проверяют не только итоговый результат, но и то, что кеш не даёт
/// резолверу ходить в сеть повторно, пока не сброшен.
private final class FakeProber: HealthProbing, @unchecked Sendable {
    private let lock = NSLock()
    private var alive: [String: Bool]
    private(set) var callCounts: [String: Int] = [:]

    init(alive: [String: Bool]) {
        self.alive = alive
    }

    var totalCalls: Int {
        lock.lock()
        defer { lock.unlock() }
        return callCounts.values.reduce(0, +)
    }

    func setAlive(_ isAlive: Bool, for host: String) {
        lock.lock()
        alive[host] = isAlive
        lock.unlock()
    }

    func probe(host: String, port: Int, token: String, timeout: TimeInterval) -> Bool {
        lock.lock()
        callCounts[host, default: 0] += 1
        let result = alive[host] ?? false
        lock.unlock()
        return result
    }
}

final class PeerResolverTests: XCTestCase {
    private func makeConfig(peers: [String]) -> Config {
        Config(token: "test-token", peers: peers)
    }

    func testResolveReturnsFirstLiveAddress() {
        let prober = FakeProber(alive: ["10.0.0.1": true, "10.0.0.2": true])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1", "10.0.0.2"]), prober: prober)

        XCTAssertEqual(resolver.resolve(), "10.0.0.1")
    }

    func testResolveSkipsDeadAddresses() {
        let prober = FakeProber(alive: ["10.0.0.1": false, "10.0.0.2": true])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1", "10.0.0.2"]), prober: prober)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")
    }

    func testResolveReturnsNilWhenNoneAlive() {
        let prober = FakeProber(alive: ["10.0.0.1": false, "10.0.0.2": false])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1", "10.0.0.2"]), prober: prober)

        XCTAssertNil(resolver.resolve())
    }

    func testRepeatedResolveDoesNotProbeNetworkAgain() {
        let prober = FakeProber(alive: ["10.0.0.1": true, "10.0.0.2": true])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1", "10.0.0.2"]), prober: prober)

        XCTAssertEqual(resolver.resolve(), "10.0.0.1")
        let callsAfterFirst = prober.totalCalls
        XCTAssertEqual(resolver.resolve(), "10.0.0.1")
        XCTAssertEqual(resolver.resolve(), "10.0.0.1")

        XCTAssertEqual(prober.totalCalls, callsAfterFirst, "кеш должен предотвращать повторные обращения к сети")
    }

    func testInvalidateCausesReprobe() {
        let prober = FakeProber(alive: ["10.0.0.1": true])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1"]), prober: prober)

        XCTAssertEqual(resolver.resolve(), "10.0.0.1")
        let callsBeforeInvalidate = prober.callCounts["10.0.0.1"]

        resolver.invalidate()
        XCTAssertEqual(resolver.resolve(), "10.0.0.1")

        XCTAssertEqual(prober.callCounts["10.0.0.1"], (callsBeforeInvalidate ?? 0) + 1,
                        "после invalidate() резолвер обязан снова опросить сеть")
    }

    func testAfterInvalidateFormerLiveAddressThatDiedIsSkippedForNextAlive() {
        let prober = FakeProber(alive: ["10.0.0.1": true, "10.0.0.2": true])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1", "10.0.0.2"]), prober: prober)

        XCTAssertEqual(resolver.resolve(), "10.0.0.1")

        prober.setAlive(false, for: "10.0.0.1")
        resolver.invalidate()

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")
    }

    func testInvalidateRestartsProbingFromTheBeginningOfTheList() {
        // После invalidate() перебор обязан идти с начала списка peers, а не
        // продолжаться с адреса, на котором резолвер остановился в прошлый раз.
        let prober = FakeProber(alive: ["10.0.0.1": false, "10.0.0.2": true])
        let resolver = PeerResolver(config: makeConfig(peers: ["10.0.0.1", "10.0.0.2"]), prober: prober)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")
        XCTAssertEqual(prober.callCounts["10.0.0.1"], 1)

        resolver.invalidate()
        prober.setAlive(true, for: "10.0.0.1")

        XCTAssertEqual(resolver.resolve(), "10.0.0.1")
        XCTAssertEqual(prober.callCounts["10.0.0.1"], 2, "перебор после invalidate() должен снова начаться с 10.0.0.1")
    }

    func testResolveWithEmptyPeersReturnsNilWithoutProbing() {
        let prober = FakeProber(alive: [:])
        let resolver = PeerResolver(config: makeConfig(peers: []), prober: prober)

        XCTAssertNil(resolver.resolve())
        XCTAssertEqual(prober.totalCalls, 0)
    }
}
