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
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

    # 実行ファイル 1 個で動く。
    #
    # 以前は Avalonia のネイティブライブラリ
    # (libSkiaSharp / libHarfBuzzSharp / libAvaloniaNative) が
    # 単一ファイルの外に出ていたため、まとめて中へ入れていた。
    # csproj に IncludeAllContentForSelfExtract を入れたので、
    # 今はこれらも実行ファイルへ埋め込まれ、発行先には出てこない。
    cp "$out/RetroDumper" "$app/Contents/MacOS/"
    chmod +x "$app/Contents/MacOS/RetroDumper" 2>/dev/null || true

    # アイコン。無くても動くが、Finder と Dock で汎用アイコンになる。
    # src/RetroDumper.App/app.ico から作った icns で、元が 256px までなので
    # 512 と 1024 は拡大したものが入っている。作り直し方は README-dev.md にある。
    icon_key=""
    if [ -f src/RetroDumper.Ui/app.icns ]; then
        cp src/RetroDumper.Ui/app.icns "$app/Contents/Resources/"
        icon_key='  <key>CFBundleIconFile</key>        <string>app</string>'
    else
        echo "  警告: src/RetroDumper.Ui/app.icns が無いのでアイコンなしで作ります"
    fi

    cat > "$app/Contents/Info.plist" << PLIST
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
${icon_key}
  <key>NSHighResolutionCapable</key> <true/>
  <key>LSMinimumSystemVersion</key>  <string>11.0</string>
</dict>
</plist>
PLIST

    # **組み立てたあとに署名し直す。**
    #
    # .NET が付ける ad-hoc 署名は実行ファイル単体に対するもので、
    # Contents/Resources を持たない前提になっている。
    # アイコンを入れたままだと codesign -v が
    #   code has no resources but signature indicates they must be present
    # を返し、バンドルとしては署名が壊れた状態になる。
    # 起動はできるが、配布物としては直しておく。
    #
    # 「-」は ad-hoc 署名。配布用の証明書は持っていないので公証は受けられない。
    if command -v codesign > /dev/null 2>&1; then
        codesign --force --sign - "$app" > /dev/null 2>&1
        if codesign -v "$app" > /dev/null 2>&1; then
            echo "  署名し直しました (ad-hoc)"
        else
            echo "  警告: 署名の検証に通りませんでした"
        fi
    fi

    # 配布用の書庫。
    #
    # **.app はフォルダなので、そのままでは渡せない。**
    # ditto は実行権限とバンドルの構造を保ったまま固める。
    # zip -r でも大抵は通るが、環境によって実行ビットが落ちる。
    zip="dist-avalonia/RetroDumper-$rid.zip"
    if command -v ditto > /dev/null 2>&1; then
        rm -f "$zip"
        ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
    else
        echo "  ditto が無いので書庫は作りません（Mac で走らせてください）"
        zip=""
    fi

    printf '  %s\n' "$out/RetroDumper"
    printf '  %s\n' "$app"
    [ -n "$zip" ] && printf '  %s\n' "$zip"
done

cat << 'NOTE'

--- Mac で動かすとき ---

署名していないので、初回は Gatekeeper に止められます。

  chmod +x RetroDumper.app/Contents/MacOS/RetroDumper
  xattr -dr com.apple.quarantine RetroDumper.app

配る相手の Mac に合わせて選んでください。

  dist-avalonia/RetroDumper-osx-arm64.zip   Apple シリコン
  dist-avalonia/RetroDumper-osx-x64.zip     Intel Mac

**署名していないので、受け取った人も同じ手順が要ります。**
macOS 15 以降は右クリック「開く」で回避できません。
システム設定 → プライバシーとセキュリティ → 「このまま開く」か、
上の xattr を実行してもらってください。

シリアルポートの名前は COM3 ではありません。

  ls /dev/tty.usbmodem*

**画面より先に通信層を確かめること。**

  dotnet run --project tools/RfcaLab -c Release -- survey /dev/tty.usbmodem○○○
NOTE
