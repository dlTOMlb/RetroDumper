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

# dotnet を探す。
#
# **PATH に無いことがある。**
# 公式の導入台本 (dot.net/v1/dotnet-install.sh) は ~/.dotnet へ入れるが、
# PATH は書き換えない。その状態で ./build-mac.sh と打つと
#   ./build-mac.sh: line NN: dotnet: command not found
# で止まる。よく使う置き場所を順に見る。
if ! command -v dotnet > /dev/null 2>&1; then
    for candidate in "$HOME/.dotnet" "/usr/local/share/dotnet" "/opt/homebrew/share/dotnet"; do
        if [ -x "$candidate/dotnet" ]; then
            PATH="$candidate:$PATH"
            export PATH
            break
        fi
    done
fi

if ! command -v dotnet > /dev/null 2>&1; then
    echo "dotnet が見つかりません。" >&2
    echo "~/.dotnet か /usr/local/share/dotnet に入れるか、PATH を通してください。" >&2
    exit 1
fi

targets=("${@:-osx-arm64 osx-x64}")
read -r -a targets <<< "${targets[*]}"

notarized=""
staple_failed=""

# 版は Directory.Build.props の 1 か所で決まっている。
# **ここで読み直さないと Info.plist だけ古い値が残る。**
# 「今動かしているのがどのビルドか」を取り違えないための値なので、
# 取れなければ黙って進めずに止める。
version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props | head -1)
if [ -z "$version" ]; then
    echo "Directory.Build.props から版を読めません。" >&2
    exit 1
fi
echo "版: $version"

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
  <key>CFBundleShortVersionString</key> <string>${version}</string>
  <key>CFBundleVersion</key>         <string>${version}</string>
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
    # Developer ID Application の証明書がキーチェーンにあればそれを使い、
    # 無ければ ad-hoc（「-」）にする。
    # ad-hoc では公証を受けられないので、受け取った人が手で Gatekeeper を
    # 通すことになる。
    signed_with=""
    if command -v codesign > /dev/null 2>&1; then
        # 証明書が 1 枚も無いと grep が失敗する。
        # この台本は set -e で走っているので、|| true を付けないとここで止まる。
        identity=$(security find-identity -v -p codesigning 2>/dev/null \
                   | grep "Developer ID Application" | head -1 \
                   | sed -E 's/.*"(.*)"/\1/' || true)

        if [ -n "$identity" ]; then
            # **Hardened Runtime には entitlements が要る。**
            # 付けないと JIT が実行可能メモリを確保できず、.NET は起動しない。
            #
            #   allow-jit / allow-unsigned-executable-memory
            #     JIT が生成したコードを実行するために要る。
            #   disable-library-validation
            #     単一ファイルの実行ファイルは起動時に ~/.net/... へ中身を
            #     展開し、そこから dylib を読む。展開されたものは本体と
            #     同じ署名を持たないので、これが無いと読み込みが拒否される。
            #
            # **app.entitlements にコメントを書いてはいけない。**
            # codesign が使う AMFIUnserializeXML はコメントを解釈せず、
            #   Failed to parse entitlements: AMFIUnserializeXML: syntax error
            # で落ちる。説明はここに置いてある。
            # 失敗したら理由が見えるように、出力は捨てない。
            codesign --force --options runtime --timestamp \
                     --entitlements src/RetroDumper.Ui/app.entitlements \
                     --sign "$identity" "$app"
            signed_with="$identity"
        else
            codesign --force --sign - "$app" > /dev/null 2>&1
            signed_with="ad-hoc"
        fi

        if codesign -v "$app" > /dev/null 2>&1; then
            echo "  署名しました ($signed_with)"
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

    # 公証。NOTARY_PROFILE に資格情報の名前を入れておくと実行する。
    #
    #   xcrun notarytool store-credentials retrodumper \
    #     --apple-id "<Apple ID>" --team-id "<チーム ID>"
    #   NOTARY_PROFILE=retrodumper ./build-mac.sh
    #
    # ad-hoc 署名では通らない。Developer ID で署名されている必要がある。
    if [ -n "${NOTARY_PROFILE:-}" ] && [ -n "$zip" ] && [ "$signed_with" != "ad-hoc" ]; then
        echo "  公証に出します（数分かかります）…"
        if xcrun notarytool submit "$zip" --keychain-profile "$NOTARY_PROFILE" --wait; then
            # チケットを .app に貼ると、受け取った人はネットに繋がなくても開ける。
            #
            # **受理された直後は貼れないことがある。**
            # Apple 側でチケットを取り出せるようになるまで少し遅れるためで、
            #   The staple and validate action failed! Error 73.
            # になる。cdhash は一致していて、アプリ側の問題ではない。
            # 実測では、少し待ってから試し直すと貼れた。
            stapled=""
            for attempt in 1 2 3 4 5; do
                if xcrun stapler staple "$app" > /dev/null 2>&1; then
                    stapled=yes
                    echo "  チケットを添付しました（$attempt 回目）"
                    break
                fi
                if [ "$attempt" -lt 5 ]; then
                    echo "  チケットがまだ取れません。30 秒待って試します（$attempt/5）"
                    sleep 30
                fi
            done

            if [ -n "$stapled" ]; then
                notarized=yes
                # 貼ったので書庫を作り直す
                rm -f "$zip"
                ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
            else
                # **ここで成功したことにしてはいけない。**
                # 以前は失敗しても先へ進み、貼れていない .app から書庫を作り、
                # 末尾に「公証済みです。そのまま開けます」と出していた。
                staple_failed=yes
                echo "  警告: チケットを添付できませんでした（$rid）" >&2
                echo "         公証は通っているのでオンラインなら開けるが、" >&2
                echo "         オフラインの相手では弾かれる。" >&2
                echo "         後から貼り直せる:" >&2
                echo "           xcrun stapler staple $app" >&2
                echo "           ditto -c -k --sequesterRsrc --keepParent $app $zip" >&2
            fi
        else
            echo "  警告: 公証に通りませんでした（$rid）" >&2
        fi
    elif [ -n "${NOTARY_PROFILE:-}" ] && [ "$signed_with" = "ad-hoc" ]; then
        echo "  公証は飛ばします（ad-hoc 署名では通らない）"
    fi

    printf '  %s\n' "$out/RetroDumper"
    printf '  %s\n' "$app"
    [ -n "$zip" ] && printf '  %s\n' "$zip"
done

if [ -n "$staple_failed" ]; then
cat << 'NOTE'

--- 配布について ---

**チケットを添付できなかったものがあります。上の警告を見てください。**
公証は通っているのでオンラインなら開けますが、
オフラインの相手では弾かれます。貼り直してから配ってください。

NOTE
elif [ -n "$notarized" ]; then
cat << 'NOTE'

--- 配布について ---

公証済みです。受け取った人はそのまま開けます。
Gatekeeper を手で通す操作は要りません。

配る相手の Mac に合わせて選んでください。

  dist-avalonia/RetroDumper-osx-arm64.zip   Apple シリコン
  dist-avalonia/RetroDumper-osx-x64.zip     Intel Mac

NOTE
else
cat << 'NOTE'

--- 配布について ---

**公証を受けていません。**
受け取った人は初回に Gatekeeper で止められます。
macOS 15 以降は右クリック「開く」で回避できないので、
システム設定 → プライバシーとセキュリティ → 「このまま開く」か、
次を実行してもらってください。

  xattr -dr com.apple.quarantine RetroDumper.app

公証するには、Developer ID の証明書と資格情報を用意して
NOTARY_PROFILE を付けて走らせます。

  xcrun notarytool store-credentials <名前> --apple-id <ID> --team-id <チーム>
  NOTARY_PROFILE=<名前> ./build-mac.sh

配る相手の Mac に合わせて選んでください。

  dist-avalonia/RetroDumper-osx-arm64.zip   Apple シリコン
  dist-avalonia/RetroDumper-osx-x64.zip     Intel Mac

NOTE
fi

cat << 'NOTE'
--- 実機で試すとき ---

**画面より先に通信層を確かめること。**

macOS ではアダプタのシリアルポートが生えません。
USB のバルク転送で繋ぎます。詳しくは README-dev.md にあります。

NOTE
