import SwiftUI

enum QeliTheme {
    // Keep the application surfaces identical to the Android palette instead of letting
    // UIKit substitute its grouped-list greys. System sheets and permission prompts remain
    // native, but the Qeli-owned screens now use one cross-platform visual language.
    static let background = adaptive(light: 0xF4F7FC, dark: 0x0F1018)
    static let surface = adaptive(light: 0xFFFFFF, dark: 0x1A1B2A)
    static let surfaceVariant = adaptive(light: 0xEEF2FA, dark: 0x15182A)
    static let outline = adaptive(light: 0xE1E7F2, dark: 0x2B2E45)

    static let primary = adaptive(light: 0x2B7DD9, dark: 0x5AA8FF)
    static let primaryContainer = adaptive(light: 0xE3F0FC, dark: 0x1B2E50)
    static let secondary = adaptive(light: 0x14B2A2, dark: 0x21C7B8)

    static let textPrimary = adaptive(light: 0x1A2233, dark: 0xECEDF6)
    static let textSecondary = adaptive(light: 0x6B7A90, dark: 0x9AA3BD)
    static let textHint = adaptive(light: 0xA6B0C2, dark: 0x5B6481)

    static let connected = adaptive(light: 0x10B95C, dark: 0x19E07C)
    static let connecting = adaptive(light: 0xF0A911, dark: 0xFFB300)
    static let disconnected = adaptive(light: 0xA6B0C2, dark: 0x5B6481)
    static let error = adaptive(light: 0xE5484D, dark: 0xFF6B6B)

    private static func adaptive(light: UInt32, dark: UInt32) -> Color {
        Color(uiColor: UIColor { traits in
            rgb(traits.userInterfaceStyle == .dark ? dark : light)
        })
    }

    private static func rgb(_ value: UInt32) -> UIColor {
        UIColor(
            red: CGFloat((value >> 16) & 0xff) / 255,
            green: CGFloat((value >> 8) & 0xff) / 255,
            blue: CGFloat(value & 0xff) / 255,
            alpha: 1
        )
    }
}

struct QeliCard: ViewModifier {
    var padding: CGFloat = 16

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(QeliTheme.surface, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .stroke(QeliTheme.outline, lineWidth: 1)
            }
    }
}

extension View {
    func qeliCard(padding: CGFloat = 16) -> some View { modifier(QeliCard(padding: padding)) }
}

struct QeliOutlinedActionButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(QeliTheme.primary)
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .frame(maxWidth: .infinity, minHeight: 44)
            .padding(.horizontal, 8)
            .background(
                configuration.isPressed ? QeliTheme.primary.opacity(0.08) : Color.clear,
                in: RoundedRectangle(cornerRadius: 12, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .stroke(QeliTheme.outline, lineWidth: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

struct QeliTextActionButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(QeliTheme.connected)
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .frame(minHeight: 44)
            .padding(.horizontal, 10)
            .background(
                configuration.isPressed ? QeliTheme.connected.opacity(0.08) : Color.clear,
                in: RoundedRectangle(cornerRadius: 12, style: .continuous)
            )
            .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

struct QeliLogo: View {
    var size: CGFloat = 44

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .fill(LinearGradient(colors: [QeliTheme.primary, QeliTheme.secondary], startPoint: .topLeading, endPoint: .bottomTrailing))
            Text("Q")
                .font(.system(size: size * 0.56, weight: .black, design: .rounded))
                .foregroundStyle(.white)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

