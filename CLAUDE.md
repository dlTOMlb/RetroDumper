# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

レトロフリーク カートリッジアダプタ (RFCA) を PC に USB で直結し、カセットから ROM とセーブを吸い出す Windows アプリ。
**文書・コメント・コミットメッセージはすべて日本語**（README.md / README-dev.md がその調子の基準）。

## コマンド

```bash
dotnet build RetroDumper.sln -c Release -warnaserror   # 警告 0 を保っている。増やさない
dotnet test                                            # 401 件。実機不要
dotnet run --project src/RetroDumper.App -c Release
```

絞り込んで動かす（**テスト名の大半が日本語**なので、クラス名で絞るのが楽）:

```bash
dotnet test --filter "FullyQualifiedName~WriteProtectionTests"
```

配布用の単一 EXE（指定は csproj にあるので引数は要らない）:

```bash
dotnet publish src/RetroDumper.App -c Release -o dist
```

`tools/RfcaLab` は**ソリューションに入っていない**ので別に建てる:

```bash
dotnet build tools/RfcaLab -c Release
```

## 構成

| | |
|---|---|
| `src/RetroDumper.Core/` | 吸い出しロジック。`net9.0`、実機に依存しない |
| `src/RetroDumper.App/` | WPF アプリ。`net9.0-windows`、**ビルドに Windows が要る** |
| `tools/RfcaLab/` | 実機を直接叩く CLI（下記） |
| `tests/` | 擬似カートリッジによる検証 |

### 中心にある 2 つの抽象

**`IRfcaLink`**（`Transport/`）がアダプタとの通信。吸い出しロジックはすべてこれに対して書かれているので、`tests/Fake*Cartridge.cs` を差し替えれば実機なしで検証できる。**新しい機種を足すときは、まず擬似カートリッジを書く。**

**`ICartridgeDumper`**（`Dumping/DumpTypes.cs`）が機種ごとの実装。`Dumping/DumperRegistry.cs` に並べると画面と自動判別に載る。`Identify` → `Dump` の 2 段。

### 機種ごとの差は Transport に集約されている

- `RfcaOpcode.cs` — opcode 表
- `RfcaLink.InitializeSlot` — **スロット初期化は機種ごとに違う**。GBA は `0x04(0)→0x05`、SFC は `0x2F` のみ、PC エンジンは `0x05` のみ、他は `0x04(1)→0x05`。**GB に `0x2F` を送ると拒否され、全 `0xFF` になる**
- `CartridgeKind.cs` — 状態応答の種別コード

プロトコルの詳細（フレーム形式、機種別のアドレス変換、容量判定）は **[README-dev.md](README-dev.md)** にある。**推測で書く前にそこを読む。**

## 書き込みに関する決まり

カセットの内容を壊しうるので、層が分かれている。**まとめて緩めない。**

| 層 | 役割 |
|---|---|
| `IRfcaLink.AllowWrites` | 既定で false。ROM 吸い出しには一切不要 |
| `IRfcaLink.AllowSaveWrites` | セーブ領域だけの別の許可 |
| `SaveMemory.IsSaveWrite` | **番地の門。**セーブ領域の外へは通さない |
| `MapperRegister.IsBankRegister` | バンク切り替えは揮発性なので `AllowWrites` の対象外 |
| `SaveSupport.IsVerified` | **画面に出す機種。**実機で確かめたものだけ |

`SaveSupport` と `SaveMemory` は**別の守り**。画面から外した機種でも、`SaveMemory` は緩めない（RfcaLab からの実機実験の経路が要る）。

**GBA の ROM へは書かない。**セーブ領域のみ 2026-09-24 に解禁された。

## 実機で確かめるとき

`tools/RfcaLab` が実機を画面なしで叩く CLI。**挙動調査はまずこれ。**GUI を操作してもらうと往復が増え、1 回ごとにセーブを危険にさらす。

```bash
dotnet run --project tools/RfcaLab -c Release -- survey COM3
```

**既定は読み出しのみ。**書き込む操作は名前で区別してある（`erase` / `wsram` / `sramprobe` など）。コマンド一覧は README-dev.md。

### 繰り返し踏んでいる失敗

- **一様なデータで確かめようとしない。**同じ値で埋まったセーブは、書けたのか何もしていないのか区別できない。折り返し検出も全周期で「折り返す」と出る。位置で変わる模様を使う。テストデータも `i * 31` だけだと 256 バイト周期になる
- **全 `0xFF` や読むたび違う値が出たら、まず挿し直す。**接触不良でも種別コードだけは正しく返る。コマンドを疑う前に物理を疑う
- **切り分けは「その処理を外して通るか」で行う。**GBA フラッシュが USB から落ちた原因は、チャンク幅でもタイムアウトでもなく直前の ID 読み出しだった
- **トレース出力を `grep -v` で捨てない。**WARN が同じ接頭辞で出る。GB の全 `0xFF` はログに答えが出ていた
- **プロトコルで詰まったら推測せず逆コンパイルする。**`D:\Emu\RetroFreakDumper\RetroFreakDumper.exe` が動く実装。`ilspycmd -o <dir> -p <exe>`（`DOTNET_ROLL_FORWARD=LatestMajor` が要る）

## 文書

`README.md` は使う人向け、`README-dev.md` は手を入れる人向け。**実機で確認したことと、していないことを必ず書き分ける。**「実装済み・実機未確認」を「対応」と書かない。

Markdown は**一文一行**で、行末に半角スペース 2 つ（強制改行）。
**`**…です。**次の文` と書かない。**閉じる `**` の前が句点だと閉じ記号として認識されず、`**` がそのまま表示される。`**…です**。次の文` とする。
GitHub のリリースノートと Issue は改行がそのまま反映されるので、そちらでは 2 スペースは不要。

## アプリが作るファイル

**保存先を選んだファイルしか作らない。**診断や測定の結果は画面のログに出すだけで、EXE の横には置かない。セーブの控えもメモリにだけ持つ。この方針は 2026-09-26 に決めたもので、`ProbeJournal` にファイルを開かない経路がある。

---

`~/.gemini/settings.json` が見つかりました。取り込めるものがあるか調べるには `/import` と返信してください（対象を一覧します）。適用は `/import --yes=<digest>` です。このコマンドがこの画面で使えない場合は、ターミナルで `claude import` を実行してください。
