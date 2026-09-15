import XCTest
@testable import Qeli

final class MobilePacketHandoffBufferTests: XCTestCase {
    private func ipv4Packet(protocolNumber: UInt8, size: Int = 40) -> Data {
        var packet = Data(repeating: 0, count: size)
        packet[0] = 0x45
        packet[9] = protocolNumber
        return packet
    }

    private func ipv6Packet(extensionHeader: UInt8, protocolNumber: UInt8) -> Data {
        var packet = Data(repeating: 0, count: 48)
        packet[0] = 0x60
        packet[6] = extensionHeader
        packet[40] = protocolNumber
        // Hop-by-Hop/Destination/Routing use Hdr Ext Len = 0 (8 bytes). Fragment also has a
        // fixed eight-byte header, so the same fixture shape covers both forms.
        packet[41] = 0
        return packet
    }

    func testTCPOutlivesShortUDPReplayWindow() {
        var buffer = MobilePacketHandoffBuffer()
        let tcp = ipv4Packet(protocolNumber: 6)
        let udp = ipv4Packet(protocolNumber: 17)
        XCTAssertEqual(
            buffer.retain([tcp, udp], continuityKey: "same", now: 100),
            .init(retained: 2, dropped: 0)
        )
        XCTAssertEqual(buffer.drain(continuityKey: "same", now: 103), [tcp])
    }

    func testIPv6ExtensionHeadersPreserveTransportSpecificReplayWindows() {
        var buffer = MobilePacketHandoffBuffer()
        let tcpFragment = ipv6Packet(extensionHeader: 44, protocolNumber: 6)
        let udpHopByHop = ipv6Packet(extensionHeader: 0, protocolNumber: 17)
        XCTAssertEqual(
            buffer.retain([tcpFragment, udpHopByHop], continuityKey: "same", now: 100),
            .init(retained: 2, dropped: 0)
        )
        XCTAssertEqual(buffer.drain(continuityKey: "same", now: 103), [tcpFragment])
    }

    func testChangedNetworkPlanCannotReplayOldPackets() {
        var buffer = MobilePacketHandoffBuffer()
        _ = buffer.retain([ipv4Packet(protocolNumber: 6)], continuityKey: "old", now: 10)
        XCTAssertTrue(buffer.drain(continuityKey: "new", now: 11).isEmpty)
        XCTAssertEqual(buffer.count, 0)
        XCTAssertEqual(buffer.bytes, 0)
    }

    func testPacketAndByteBoundsDropOldestEntries() {
        var buffer = MobilePacketHandoffBuffer(maximumPackets: 2, maximumBytes: 90)
        let first = ipv4Packet(protocolNumber: 6, size: 40)
        let second = ipv4Packet(protocolNumber: 6, size: 40)
        let newest = ipv4Packet(protocolNumber: 6, size: 50)
        let result = buffer.retain([first, second, newest], continuityKey: "same", now: 1)
        XCTAssertEqual(result, .init(retained: 2, dropped: 1))
        XCTAssertEqual(buffer.drain(continuityKey: "same", now: 2), [second, newest])
    }
}
