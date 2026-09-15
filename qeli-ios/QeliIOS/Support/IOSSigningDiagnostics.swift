import Foundation

/// Verifies the effective shared-container capabilities of the running app.
///
/// An unsigned archive can be re-signed by a generic sideloading tool and still launch, while
/// lacking every capability that makes it a VPN. Checking the source `.entitlements` file is
/// insufficient: the public APIs below probe what iOS actually authorizes for this process.
enum IOSSigningDiagnostics {
    enum Requirement: String, CaseIterable, Equatable {
        case packetTunnel
        case appGroup
        case keychainGroup
    }

    struct Entitlements {
        var networkExtensions: [String]
        var appGroups: [String]
        var keychainGroups: [String]
    }

    static func missingRequirements(
        in entitlements: Entitlements,
        expectedAppGroup: String,
        expectedKeychainGroup: String?
    ) -> [Requirement] {
        var missing: [Requirement] = []
        if !entitlements.networkExtensions.contains("packet-tunnel-provider") {
            missing.append(.packetTunnel)
        }
        if expectedAppGroup.isEmpty || !entitlements.appGroups.contains(expectedAppGroup) {
            missing.append(.appGroup)
        }
        guard let expectedKeychainGroup, !expectedKeychainGroup.isEmpty,
              entitlements.keychainGroups.contains(expectedKeychainGroup) else {
            missing.append(.keychainGroup)
            return missing
        }
        return missing
    }

    static func missingRequirements() -> [Requirement] {
        // Packet Tunnel Providers cannot run in the simulator. Simulator builds are retained
        // for Swift compilation and unit tests, so provisioning is intentionally not a gate.
        #if targetEnvironment(simulator)
        return []
        #else
        var missing: [Requirement] = []
        let appGroup = AppConstants.appGroupIdentifier
        if appGroup.isEmpty || appGroup.contains("$(")
            || FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: appGroup
            ) == nil {
            missing.append(.appGroup)
        }
        guard let keychainGroup = AppConstants.keychainAccessGroup,
              KeychainStore.canAccess(group: keychainGroup) else {
            missing.append(.keychainGroup)
            return missing
        }
        // NetworkExtension itself performs the packet-tunnel entitlement check when a manager
        // is saved or started. If the provider launches, that capability is already authorized.
        return missing
        #endif
    }
}
