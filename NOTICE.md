# 同梱物と名称について

[LICENSE](LICENSE) の MIT License が適用されるのは、**本プロジェクトのソースコード**です。
以下はその対象外です。

## 同梱しているデータベース

`src/RetroDumper.Core/Database/Embedded/` にある 11 本の `.dat.gz` は、
他の企画が作成した成果物です。
本プロジェクトに著作権はなく、**MIT を付ける権限もありません**。
それぞれの出典の条件に従います。

| 出典 | ファイル |
|---|---|
| [No-Intro](https://no-intro.org/) | `Nintendo - Game Boy Advance (20260919-175115).dat.gz`<br>`Nintendo - Nintendo Entertainment System (Headerless) (20260923-001145).dat.gz` |
| [libretro-database](https://github.com/libretro/libretro-database) | 残りの 9 本（SFC / GB / GBC / MD / SMS / GG / PCE / SuperGrafx / スーファミターボ） |

吸い出した ROM が正規ダンプと同一かを照合するために使っています。
新しい DAT に差し替えたいときは、EXE と同じ場所に `DataBase` フォルダを作って
`*.dat` を置いてください。
同梱分より優先されます。

## 名称について

Retro Freak（レトロフリーク）の名称および製品は、各権利者に帰属します。
**本プロジェクトは公式のものではなく、権利者による承認も支援も受けていません。**

各ゲーム機の名称、ソフトの題名も同様に各権利者に帰属します。
本文中でそれらに言及しているのは、対応状況や実機で確認した内容を示すためです。
