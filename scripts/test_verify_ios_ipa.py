import plistlib
import tempfile
import unittest
import zipfile
from datetime import datetime, timedelta
from pathlib import Path

import verify_ios_ipa


class IOSIPAVerifierTests(unittest.TestCase):
    def build_ipa(
        self,
        path: Path,
        *,
        keychain_group: str = "ABCDEFGHIJ.ru.qeli.app.shared",
        include_signatures: bool = True,
        tunnel_app_group: str = "group.ru.qeli.app",
    ) -> None:
        main_root = "Payload/Qeli.app/"
        tunnel_root = main_root + "PlugIns/QeliPacketTunnel.appex/"
        widget_root = main_root + "PlugIns/QeliWidgets.appex/"
        main = {
            "CFBundleIdentifier": "ru.qeli.app",
            "QeliPacketTunnelBundleIdentifier": "ru.qeli.app.PacketTunnel",
            "QeliAppGroup": "group.ru.qeli.app",
            "QeliKeychainAccessGroup": keychain_group,
        }
        tunnel = {
            "CFBundleIdentifier": "ru.qeli.app.PacketTunnel",
            "QeliAppGroup": tunnel_app_group,
            "QeliKeychainAccessGroup": keychain_group,
            "NSExtension": {
                "NSExtensionPointIdentifier": "com.apple.networkextension.packet-tunnel"
            },
        }
        widget = {
            "CFBundleIdentifier": "ru.qeli.app.Widgets",
            "QeliAppGroup": "group.ru.qeli.app",
            "NSExtension": {"NSExtensionPointIdentifier": "com.apple.widgetkit-extension"},
        }
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(main_root + "Info.plist", plistlib.dumps(main))
            archive.writestr(tunnel_root + "Info.plist", plistlib.dumps(tunnel))
            archive.writestr(widget_root + "Info.plist", plistlib.dumps(widget))
            for root in (main_root, tunnel_root, widget_root):
                archive.writestr(root + "binary", b"binary")
                if include_signatures:
                    info = main if root == main_root else tunnel if root == tunnel_root else widget
                    bundle_id = info["CFBundleIdentifier"]
                    needs_vpn = root != widget_root
                    needs_keychain = root != widget_root
                    entitlements = {
                        "application-identifier": f"ABCDEFGHIJ.{bundle_id}",
                        "com.apple.developer.team-identifier": "ABCDEFGHIJ",
                        "com.apple.security.application-groups": ["group.ru.qeli.app"],
                    }
                    if needs_vpn:
                        entitlements["com.apple.developer.networking.networkextension"] = [
                            "packet-tunnel-provider"
                        ]
                    if needs_keychain:
                        entitlements["keychain-access-groups"] = [keychain_group]
                    profile = {
                        "ExpirationDate": datetime.now() + timedelta(days=30),
                        "TeamIdentifier": ["ABCDEFGHIJ"],
                        "ApplicationIdentifierPrefix": ["ABCDEFGHIJ"],
                        "Entitlements": entitlements,
                    }
                    archive.writestr(
                        root + "embedded.mobileprovision", plistlib.dumps(profile)
                    )
                    archive.writestr(root + "_CodeSignature/CodeResources", b"signature")

    def test_accepts_consistent_structural_contract(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "qeli.ipa"
            self.build_ipa(path)
            errors, facts = verify_ios_ipa.validate_archive(path)
            self.assertEqual(errors, [])
            self.assertEqual(facts["tunnel_id"], "ru.qeli.app.PacketTunnel")
            self.assertEqual(facts["app_group"], "group.ru.qeli.app")

    def test_rejects_unsigned_archive(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "qeli.ipa"
            self.build_ipa(path, include_signatures=False)
            errors, _ = verify_ios_ipa.validate_archive(path)
            self.assertEqual(sum("code signature is missing" in error for error in errors), 3)
            self.assertEqual(sum("embedded.mobileprovision is missing" in error for error in errors), 3)

    def test_rejects_keychain_group_without_team_prefix(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "qeli.ipa"
            self.build_ipa(path, keychain_group="ru.qeli.app.shared")
            errors, _ = verify_ios_ipa.validate_archive(path)
            self.assertIn(
                "QeliKeychainAccessGroup lacks the ten-character Apple Team/AppIdentifier prefix",
                errors,
            )

    def test_rejects_component_group_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "qeli.ipa"
            self.build_ipa(path, tunnel_app_group="group.wrong")
            errors, _ = verify_ios_ipa.validate_archive(path)
            self.assertIn("container and Packet Tunnel App Group values differ", errors)

    def test_rejects_scalar_provisioning_entitlement(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "qeli.ipa"
            self.build_ipa(path)
            rewritten = Path(directory) / "rewritten.ipa"
            with zipfile.ZipFile(path) as source, zipfile.ZipFile(rewritten, "w") as target:
                for item in source.infolist():
                    data = source.read(item.filename)
                    if item.filename.endswith("QeliPacketTunnel.appex/embedded.mobileprovision"):
                        profile = plistlib.loads(data)
                        profile["Entitlements"][
                            "com.apple.developer.networking.networkextension"
                        ] = "packet-tunnel-provider"
                        data = plistlib.dumps(profile)
                    target.writestr(item, data)
            errors, _ = verify_ios_ipa.validate_archive(rewritten)
            self.assertTrue(
                any("provisioning packet-tunnel entitlement is missing" in error for error in errors)
            )
    def test_rejects_invalid_provisioning_app_id(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "qeli.ipa"
            self.build_ipa(path)
            rewritten = Path(directory) / "rewritten.ipa"
            with zipfile.ZipFile(path) as source, zipfile.ZipFile(rewritten, "w") as target:
                for item in source.infolist():
                    data = source.read(item.filename)
                    if item.filename.endswith("QeliPacketTunnel.appex/embedded.mobileprovision"):
                        profile = plistlib.loads(data)
                        profile["Entitlements"]["application-identifier"] = "ABCDEFGHIJ.wrong"
                        data = plistlib.dumps(profile)
                    target.writestr(item, data)
            errors, _ = verify_ios_ipa.validate_archive(rewritten)
            self.assertTrue(any("provisioning App ID must be" in error for error in errors))

if __name__ == "__main__":
    unittest.main()
