import Foundation

/// A bounded generation-to-generation uplink handoff. Only packets which the native core did
/// not accept are retained. TCP is safe to replay as an IP retransmission; stale UDP must be
/// discarded quickly because delayed voice/game datagrams are worse than loss.
struct MobilePacketHandoffBuffer {
    struct Retention: Equatable {
        var retained = 0
        var dropped = 0
    }

    private struct Entry {
        let serial: UInt64
        let packet: Data
        let continuityKey: String
        let expiresAt: TimeInterval
    }

    private var entries: [Entry] = []
    private var byteCount = 0
    private var nextSerial: UInt64 = 0
    let maximumPackets: Int
    let maximumBytes: Int

    init(maximumPackets: Int = 256, maximumBytes: Int = 512 * 1_024) {
        precondition(maximumPackets > 0 && maximumBytes > 0)
        self.maximumPackets = maximumPackets
        self.maximumBytes = maximumBytes
    }

    var count: Int { entries.count }
    var bytes: Int { byteCount }

    mutating func retain(
        _ packets: [Data], continuityKey: String, now: TimeInterval = Date().timeIntervalSince1970
    ) -> Retention {
        var result = Retention()
        var inserted = Set<UInt64>()
        purgeExpired(now: now, dropped: &result.dropped)
        for packet in packets {
            guard !packet.isEmpty, packet.count <= maximumBytes else {
                result.dropped += 1
                continue
            }
            let serial = nextSerial
            nextSerial &+= 1
            entries.append(Entry(
                serial: serial,
                packet: packet,
                continuityKey: continuityKey,
                expiresAt: now + Self.timeToLive(for: packet)
            ))
            inserted.insert(serial)
            byteCount += packet.count
            while entries.count > maximumPackets || byteCount > maximumBytes {
                let removed = entries.removeFirst()
                byteCount -= removed.packet.count
                result.dropped += 1
            }
        }
        result.retained = entries.lazy.filter { inserted.contains($0.serial) }.count
        return result
    }

    /// Drain only packets belonging to the newly authenticated equivalent plan. Entries for a
    /// different address/route/DNS plan are unsafe to replay and are discarded here.
    mutating func drain(
        continuityKey: String, now: TimeInterval = Date().timeIntervalSince1970
    ) -> [Data] {
        var result: [Data] = []
        result.reserveCapacity(entries.count)
        for entry in entries where entry.expiresAt > now && entry.continuityKey == continuityKey {
            result.append(entry.packet)
        }
        entries.removeAll(keepingCapacity: true)
        byteCount = 0
        return result
    }

    mutating func removeAll() {
        entries.removeAll(keepingCapacity: false)
        byteCount = 0
    }

    private mutating func purgeExpired(now: TimeInterval, dropped: inout Int) {
        guard entries.contains(where: { $0.expiresAt <= now }) else { return }
        let oldCount = entries.count
        entries.removeAll { $0.expiresAt <= now }
        byteCount = entries.reduce(0) { $0 + $1.packet.count }
        dropped += oldCount - entries.count
    }

    private static func timeToLive(for packet: Data) -> TimeInterval {
        switch transportProtocol(for: packet) {
        case 6: return 120 // TCP: retain one pending retransmission across a longer outage.
        case 17: return 2 // UDP: never emit a long-delayed real-time/application datagram.
        default: return 5
        }
    }

    /// Resolve the IPv6 upper-layer protocol through bounded extension-header traversal.
    /// Fragment headers still carry the original Next Header even for non-first fragments, which
    /// is sufficient for replay lifetime classification without parsing transport ports.
    private static func transportProtocol(for packet: Data) -> UInt8? {
        guard let first = packet.first else { return nil }
        switch first >> 4 {
        case 4 where packet.count > 9:
            return packet[packet.startIndex + 9]
        case 6 where packet.count >= 40:
            var next = packet[packet.startIndex + 6]
            var offset = 40
            for _ in 0..<8 {
                switch next {
                case 0, 43, 60, 135, 139, 140: // Hop-by-Hop, Routing, Destination, mobility.
                    guard packet.count >= offset + 2 else { return nil }
                    next = packet[packet.startIndex + offset]
                    let length = (Int(packet[packet.startIndex + offset + 1]) + 1) * 8
                    guard length >= 8, packet.count >= offset + length else { return nil }
                    offset += length
                case 44: // Fragment.
                    guard packet.count >= offset + 8 else { return nil }
                    next = packet[packet.startIndex + offset]
                    offset += 8
                case 51: // Authentication Header: length is in 32-bit words minus two.
                    guard packet.count >= offset + 2 else { return nil }
                    next = packet[packet.startIndex + offset]
                    let length = (Int(packet[packet.startIndex + offset + 1]) + 2) * 4
                    guard length >= 8, packet.count >= offset + length else { return nil }
                    offset += length
                default:
                    return next
                }
            }
            return nil
        default:
            return nil
        }
    }
}
