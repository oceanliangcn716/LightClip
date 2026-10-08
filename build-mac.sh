#!/bin/zsh
set -euo pipefail
cd "${0:A:h}"
app="dist/轻剪.app"
# Keep Mach-O deployment target and bundle metadata consistent.
export MACOSX_DEPLOYMENT_TARGET=14.0
# Remove local build-directory names from dependency panic locations.
release_remap="--remap-path-prefix=$PWD=."
export CARGO_ENCODED_RUSTFLAGS="${CARGO_ENCODED_RUSTFLAGS:+$CARGO_ENCODED_RUSTFLAGS$'\x1f'}$release_remap"
architecture="$(uname -m)"
if [[ "$architecture" != arm64 && "$architecture" != x86_64 ]]; then
  print -u2 "Unsupported Mac build architecture"; exit 1
fi
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp assets/LightClip.icns "$app/Contents/Resources/LightClip.icns"
cp THIRD_PARTY_NOTICES.txt "$app/Contents/Resources/THIRD_PARTY_NOTICES.txt"
if [[ -x .tools/cargo/bin/cargo ]]; then
  CARGO_HOME="$PWD/.tools/cargo" RUSTUP_HOME="$PWD/.tools/rustup" .tools/cargo/bin/cargo build --release --locked --manifest-path pairing/Cargo.toml
else
  cargo build --release --locked --manifest-path pairing/Cargo.toml
fi
xcrun swiftc -target "$architecture-apple-macosx14.0" -file-prefix-map "$PWD=." -swift-version 5 -O mac/Wire.swift mac/Transport.swift mac/StreamWire.swift mac/TransferFiles.swift mac/StreamTransport.swift mac/Clipboard.swift mac/Discovery.swift mac/Pairing.swift mac/Group.swift mac/App.swift mac/SelfTest.swift mac/GroupTest.swift mac/StreamTest.swift mac/main.swift -import-objc-header pairing/include/LightClipPairing.h pairing/target/release/liblightclip_pairing.a -o "$app/Contents/MacOS/LightClip" -framework AppKit -framework Network -framework CryptoKit -framework Security -framework ImageIO -framework ServiceManagement
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>LightClip</string>
<key>CFBundleIdentifier</key><string>cn.oceanliang.lightclip</string>
<key>CFBundleName</key><string>轻剪</string>
<key>CFBundleDisplayName</key><string>轻剪</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.3.0</string>
<key>CFBundleVersion</key><string>4</string>
<key>CFBundleIconFile</key><string>LightClip.icns</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSLocalNetworkUsageDescription</key><string>发现同一局域网的轻剪设备，并与已配对的群组加密同步剪贴板。</string>
</dict></plist>
PLIST
xcrun strip -S "$app/Contents/MacOS/LightClip"
codesign --force --sign - "$app"
"$app/Contents/MacOS/LightClip" --self-test
