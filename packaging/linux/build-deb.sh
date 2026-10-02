#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="${VERSION:-1.0.4}"
RUNTIME="${1:-linux-x64}"
ARCH="amd64"
if [[ "$RUNTIME" == "linux-arm64" ]]; then ARCH="arm64"; fi

"$ROOT/scripts/publish-linux.sh" "$RUNTIME"
PUBLISH="$ROOT/artifacts/linux/$RUNTIME"
STAGE="$ROOT/artifacts/deb/facehub-management_${VERSION}_${ARCH}"
OUTPUT="$ROOT/artifacts/packages"

rm -rf "$STAGE"
mkdir -p "$STAGE/DEBIAN" \
         "$STAGE/opt/facehub-management/desktop" \
         "$STAGE/opt/facehub-management/agent" \
         "$STAGE/opt/facehub-management/smoketest" \
         "$STAGE/etc/facehub-management" \
         "$STAGE/etc/systemd/system" \
         "$STAGE/usr/share/applications" \
		 "$STAGE/usr/share/icons/hicolor/256x256/apps" \
         "$STAGE/var/log/facehub-management"

cp -a "$PUBLISH/desktop/." "$STAGE/opt/facehub-management/desktop/"
cp -a "$PUBLISH/agent/." "$STAGE/opt/facehub-management/agent/"
# cp -a "$PUBLISH/smoketest/." "$STAGE/opt/facehub-management/smoketest/"
cp "$ROOT/config/agentsettings.json" "$STAGE/etc/facehub-management/agentsettings.json"
cp "$ROOT/config/desktop-diagnostics.json" "$STAGE/etc/facehub-management/desktop-diagnostics.json"
cp "$ROOT/packaging/linux/facehub-management-agent.service" "$STAGE/etc/systemd/system/"
cp "$ROOT/packaging/linux/facehub-management.desktop" "$STAGE/usr/share/applications/"
cp "$ROOT/src/FaceHubManagement.Desktop/Assets/FaceHub.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/facehub-management.png"

cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: facehub-management
Version: $VERSION
Section: admin
Priority: optional
Architecture: $ARCH
Maintainer: FaceHub
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3, libicu74, ca-certificates, tzdata, libx11-6, libice6, libsm6, libfontconfig1, fonts-dejavu-core, ufw, iptables, conntrack
Description: FaceHub desktop service management and resource monitoring application
 Cross-platform Avalonia desktop UI with a privileged local agent for Nginx,
 FaceHub, CPU, RAM, NVIDIA GPU and VRAM monitoring.
CONTROL

cat > "$STAGE/DEBIAN/conffiles" <<'CONFFILES'
/etc/facehub-management/agentsettings.json
/etc/facehub-management/desktop-diagnostics.json
CONFFILES

cat > "$STAGE/DEBIAN/postinst" <<'POSTINST'
#!/usr/bin/env bash
set -e

if ! getent group facehub-management >/dev/null 2>&1; then
    groupadd --system facehub-management
fi

chown root:facehub-management /var/log/facehub-management
chmod 0770 /var/log/facehub-management

if [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]]; then
    usermod -a -G facehub-management "$SUDO_USER" || true
    echo "Added $SUDO_USER to facehub-management. Log out and back in before launching the desktop app."
fi

systemctl daemon-reload
systemctl enable facehub-management-agent.service >/dev/null
systemctl restart facehub-management-agent.service
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
POSTINST

cat > "$STAGE/DEBIAN/prerm" <<'PRERM'
#!/usr/bin/env bash
set -e
if [[ "${1:-}" == "remove" || "${1:-}" == "deconfigure" ]]; then
    systemctl stop facehub-management-agent.service >/dev/null 2>&1 || true
    systemctl disable facehub-management-agent.service >/dev/null 2>&1 || true
fi
exit 0
PRERM

cat > "$STAGE/DEBIAN/postrm" <<'POSTRM'
#!/usr/bin/env bash
set -e
systemctl daemon-reload >/dev/null 2>&1 || true
exit 0
POSTRM

chmod 0755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm" "$STAGE/DEBIAN/postrm"
chmod 0755 "$STAGE/opt/facehub-management/desktop/FaceHubManagement.Desktop" \
           "$STAGE/opt/facehub-management/agent/FaceHubManagement.Agent"

mkdir -p "$OUTPUT"
dpkg-deb --root-owner-group --build "$STAGE" "$OUTPUT/facehub-management_${VERSION}_${ARCH}.deb"
echo "Package created: $OUTPUT/facehub-management_${VERSION}_${ARCH}.deb"
