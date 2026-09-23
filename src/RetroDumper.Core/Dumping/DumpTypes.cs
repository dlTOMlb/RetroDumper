using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Dumping;

/// <summary>吸い出し進捗。</summary>
public readonly record struct DumpProgress(string Stage, long BytesDone, long BytesTotal)
{
    public double Ratio => BytesTotal <= 0 ? 0 : (double)BytesDone / BytesTotal;
}

/// <summary>吸い出し動作の調整項目。</summary>
public sealed class DumpOptions
{
    /// <summary>1 リクエストあたりの転送サイズ。</summary>
    public int ChunkSize { get; set; } = RfcaLink.DefaultChunkSize;

    /// <summary>チャンク読み出し失敗時のリトライ回数。</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// ヘッダから求めたサイズを無視して、この値で吸い出す（バイト単位）。
    /// ヘッダが嘘をついているカセット（ウルトラコア等）用。null なら自動。
    /// </summary>
    public long? RomSizeOverride { get; set; }

    /// <summary>自動判定を無視してマッパーを固定する。null なら自動。</summary>
    public SnesMapper? SnesMapperOverride { get; set; }

    /// <summary>
    /// SA-1 / S-DD1 の MMC バンクレジスタを明示的に初期化してから読む。
    /// 電源投入直後は既定値 0,1,2,3 が入っているため通常は不要だが、
    /// 読み出し結果が壊れている場合に有効化する。
    /// </summary>
    public bool ForceMmcInit { get; set; }

    /// <summary>
    /// セーブ RAM も吸い出す。
    ///
    /// 既定はオフ。セーブ領域の配置は機種・マッパーごとに差が大きく、
    /// まだ実機で検証できていないため、当面は ROM の吸い出しに絞る。
    /// </summary>
    public bool IncludeSaveRam { get; set; }

    /// <summary>吸い出し後にヘッダのチェックサムを検証する。</summary>
    public bool VerifyChecksum { get; set; } = true;

    /// <summary>
    /// ファミコンのマッパー番号。null なら識別結果かデータベースに従う。
    ///
    /// ファミコンのカセットはマッパーを申告しないため、
    /// データベースで同定できない場合は必ず指定が要る。
    /// </summary>
    public int? NesMapperOverride { get; set; }

    /// <summary>ファミコンの PRG-ROM 容量（バイト）。</summary>
    public long? NesPrgSize { get; set; }

    /// <summary>ファミコンの CHR-ROM 容量（バイト）。0 なら CHR-RAM。</summary>
    public long? NesChrSize { get; set; }

    /// <summary>
    /// GBA スロットの ROM 先頭アドレス。
    /// 既定は GBA のシステムバス上のアドレス 0x08000000。
    /// opcode 探索でフラットな 0 番地起点だと判明した場合はそちらを設定する。
    /// </summary>
    public uint? GbaRomBase { get; set; }
}

/// <summary>カートリッジの識別結果。</summary>
public sealed class CartridgeInfo
{
    public required CartridgeKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>吸い出すべき ROM サイズ（バイト）。</summary>
    public required long RomSize { get; init; }

    /// <summary>セーブ RAM のサイズ（バイト）。0 ならセーブなし。</summary>
    public long SaveSize { get; init; }

    /// <summary>マッパー／搭載チップの説明。</summary>
    public required string Mapper { get; init; }

    /// <summary>ROM ファイルの既定拡張子（"." 込み）。</summary>
    public required string RomExtension { get; init; }

    /// <summary>ヘッダから読み取った生の情報。UI 表示用。</summary>
    public Dictionary<string, string> Details { get; } = new();

    /// <summary>ヘッダの生バイト列。</summary>
    public byte[] RawHeader { get; init; } = [];

    /// <summary>識別時に検出した注意事項。空なら問題なし。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// 識別で確定したファミコンのマッパー番号。
    ///
    /// ファミコンだけは、吸い出しに必要な情報がカセットから読めない。
    /// 識別でデータベースから引いた値を、吸い出しへ引き継ぐために持つ。
    /// </summary>
    /// <summary>
    /// セーブ装置の容量。吸い出すかどうかに関わらず、分かる値を入れる。
    ///
    /// <see cref="SaveSize"/> は「今回の吸い出しに含めるか」に左右されるため、
    /// セーブだけを読み書きする画面ではこちらを見る。
    /// </summary>
    public long SaveMemorySize { get; init; }

    /// <summary>SFC のマッパー。セーブ RAM の窓を決めるのに要る。</summary>
    public SnesMapper? SnesMapping { get; init; }

    /// <summary>GB のカートリッジ種別（ヘッダ 0x147）。MBC の判別に要る。</summary>
    public byte? GbCartridgeType { get; init; }

    public int? NesMapperNumber { get; init; }

    /// <summary>識別で確定した PRG-ROM 容量（バイト）。</summary>
    public long NesPrgSize { get; init; }

    /// <summary>識別で確定した CHR-ROM 容量（バイト）。</summary>
    public long NesChrSize { get; init; }
}

/// <summary>吸い出し結果。</summary>
public sealed class DumpResult
{
    public required CartridgeInfo Info { get; init; }
    public required byte[] Rom { get; init; }
    public byte[]? Save { get; init; }

    /// <summary>ヘッダのチェックサム検証結果。検証していない場合は null。</summary>
    public bool? ChecksumOk { get; init; }

    public string ChecksumDetail { get; init; } = "";
    public uint Crc32 { get; init; }
}

/// <summary>機種ごとの吸い出し実装。</summary>
public interface ICartridgeDumper
{
    /// <summary>UI に出す機種名。</summary>
    string Name { get; }

    /// <summary>この実装が担当する、状態応答の種別コード。</summary>
    CartridgeKind Kind { get; }

    /// <summary>
    /// 必要な opcode がすべて判明しているか。
    /// false の場合はまず <see cref="Probe.OpcodeProbe"/> で探索する必要がある。
    /// </summary>
    bool IsReady { get; }

    /// <summary>この実装が動くために足りていないものの説明。<see cref="IsReady"/> が true なら空。</summary>
    string ReadinessDetail { get; }

    /// <summary>カートリッジを識別する（ヘッダ読み出しとサイズ決定）。</summary>
    CartridgeInfo Identify(IRfcaLink link, DumpOptions options);

    /// <summary>ROM 本体とセーブ RAM を吸い出す。</summary>
    DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken);
}
