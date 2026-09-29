#!/usr/bin/env bash
#
# Mac 向けに Avalonia 版を発行する。
#
# Windows からでも作れる（.NET のクロス発行）。
# ただし **実行権限は Windows 側で付けられない**ので、
# Mac で chmod +x するか、この台本を Mac で走らせること。
#
#   ./build-mac.sh              両方（arm64 と x64）
#   ./build-mac.sh osx-arm64    Apple Silicon だけ
#   ./build-mac.sh osx-x64      Intel Mac だけ
#
set -euo pipefail

cd "$(dirname "$0")"

targets=("${@:-osx-arm64 osx-x64}")
read -r -a targets <<< "${targets[*]}"

for rid in "${targets[@]}"; do
    echo "=== $rid ==="

    dotnet publish src/RetroDumper.Ui -c Release -r "$rid" --nologo -v quiet

    out="dist-avalonia/$rid"
    app="$out/RetroDumper.app"

    # 実行権限。Windows 上では無視されるが、Mac で走らせれば効く。
    chmod +x "$out/RetroDumper" 2>/dev/null || true

    # Finder から開けるように .app の体裁を整える。
    # 中身は同じ実行ファイルで、置き場所と Info.plist が違うだけ。
    rm -rf "$app"
    mkdir -p "$app/Contents/MacOS"

    # **実行ファイルだけでは動かない。**
    # 単一ファイルにしても Avalonia のネイティブライブラリ
    # (libSkiaSharp / libHarfBuzzSharp / libAvaloniaNative) は
    # 外に出るので、まとめて中へ入れる。
    cp "$out/RetroDumper" "$app/Contents/MacOS/"
    cp "$out"/*.dylib "$app/Contents/MacOS/" 2>/dev/null || true
    chmod +x "$app/Contents/MacOS/RetroDumper" 2>/dev/null || true

    cat > "$app/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>            <string>RetroDumper</string>
  <key>CFBundleDisplayName</key>     <string>RetroDumper</string>
  <key>CFBundleIdentifier</key>      <string>io.github.dltomlb.retrodumper</string>
  <key>CFBundleExecutable</key>      <string>RetroDumper</string>
  <key>CFBundlePackageType</key>     <string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key> <string>6.0</string>
  <key>NSHighResolutionCapable</key> <true/>
  <key>LSMinimumSystemVersion</key>  <string>11.0</string>
</dict>
</plist>
PLIST

    printf '  %s\n' "$out/RetroDumper"
    printf '  %s\n' "$app"
done

cat << 'NOTE'

--- Mac で動かすとき ---

署名していないので、初回は Gatekeeper に止められます。

  chmod +x RetroDumper.app/Contents/MacOS/RetroDumper
  xattr -dr com.apple.quarantine RetroDumper.app

シリアルポートの名前は COM3 ではありません。

  ls /dev/tty.usbmodem*

**画面より先に通信層を確かめること。**

  dotnet run --project tools/RfcaLab -c Release -- survey /dev/tty.usbmodem○○○
NOTE
