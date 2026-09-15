import Foundation

/// Negotiated, display-safe runtime facts for the active session.
///
/// Mirror of the Android `PushedFacts`. Only knowable after the handshake, so it travels on
/// `TunnelSnapshot` rather than being derived from the profile. It includes both server-pushed
/// settings and capability-negotiated transport modes. Two deliberate limits:
///
/// * `routes` is CAPPED at ``routeSample`` entries with `routeCount` carrying the real total.
///   A server may advertise an arbitrarily long list — an operator pushing a country-sized
///   prefix set is a normal thing to do — and the detail sheet renders a row per entry.
///   Capping where the snapshot is built keeps both bounded instead of hoping the list stays
///   short.
/// * the session token is NOT here and must never be: it is the credential that authorises a
///   bonded stream to join this session.
///
/// It lives in its own file, apart from the rest of the protection model, because
/// `TunnelSnapshot` carries it and the WIDGET target compiles `TunnelSnapshot` from an
/// explicit file list. `ProtectionSummary` next door is built from a `VPNConfig`, so keeping
/// them together would drag the whole config model — and whatever it in turn reaches for —
/// into an extension that needs none of it.
struct PushedFacts: Codable, Equatable, Sendable {
    /// How many pushed routes the UI ever holds or renders.
    static let routeSample = 6

    var routes: [String] = []
    var routeCount = 0

    /// How many of ``routeCount`` survived into `includedRoutes`, or `-1` before the settings
    /// are built.
    ///
    /// The card used to show ``routeCount`` — the number the server SENT — as though it were
    /// the number in force. Those differ whenever a pushed CIDR is malformed: the `compactMap`
    /// that turns them into `NEIPv4Route` drops it without a word, and the tunnel comes up
    /// carrying less than the card claims. That is the one direction a protection card must
    /// never be wrong in.
    ///
    /// `-1` is deliberately not `0`: "not built yet" and "none installed" look identical
    /// otherwise, and the first is a normal moment during connect while the second is a fault.
    /// Mirrors the Android `PushedFacts.routesInstalled`.
    var routesInstalled = -1
    var multipathAdaptive = false
    var paddingEnabled = false
    var paddingMin = 0
    var paddingMax = 0
    var heartbeatEnabled = false
    var heartbeatIntervalMilliseconds = 0
    var shapingEnabled = false
    var familyMode: String?
    var carrierAddress: String?
    var recordizerMode: String?
    var recordizerPolicy: String?
    var roamingMode: String?
    var roamingPolicy: String?
}
