using System.Buffers.Binary;
using System.IO.Ports;

namespace RetroDumper.Core.Transport;

/// <summary>
/// レトロフリーク カートリッジアダプタ (RFCA) との仮想 COM ポート通信。
///
/// フレーム形式（すべてリトルエンディアン）:
///   リクエスト(20B): [u32 opcode][u32 0x08][u32 size][u32 addr][u32 size]
///   リード ACK (8B): [u32 0x00000000][u32 size]
///   ライト ACK (8B): 00 00 00 00 00 00 00 00
///   状態要求(12B)  : [u32 0x06][u32 0x00][u32 0x04]
///   状態応答(12B)  : byte[8] がカートリッジ種別
///
/// リードの癖: ACK を受け取ってもデータ本体はすぐに来ない。
/// 状態要求を「催促」として送ると、データ本体 size バイト＋状態応答 12 バイトが続けて返る。
/// これは実機（レトロフリーク本体）の挙動とは異なるが、PC 側からはこの手順でしか読めない。
/// </summary>
public sealed class RfcaLink : IRfcaLink, IDisposable
{
    public const int BaudRate = 115200;

    /// <summary>1 リクエストあたりの推奨転送サイズ。dumpfreak と同じ 1KB。</summary>
    public const int DefaultChunkSize = 1024;

    private static readonly byte[] StatusRequest =
    [
        0x06, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00,
    ];

    private readonly SerialPort _port;
    private readonly object _gate = new();
    private bool _disposed;

    public string PortName { get; }

    /// <summary>
    /// 書き込みを許可するか。**既定は false（書き込み禁止）**。
    ///
    /// 吸い出しだけが目的なら、この値は false のままでよい。
    /// SFC (SA-1 含む) / メガドライブ / GBA の ROM 読み出しは
    /// 書き込みを一切必要としない。
    ///
    /// true にする必要があるのは次の場合だけ:
    ///   ・ゲームボーイ … MBC のバンク切り替え
    ///   ・マークIII / GG … マッパーのバンク切り替え
    ///   ・SA-1 / S-DD1 で 4MB を超える ROM … MMC の貼り替え
    /// </summary>
    public bool AllowWrites { get; set; }

    /// <summary>
    /// リード要求の肯定応答を待つ時間。
    ///
    /// opcode 総当たりでは未実装の番号が大量に空振りするので、
    /// 探索中だけ短くして全体の所要時間を抑える。
    /// </summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>通信内容のトレース出力先。解析・不具合調査用。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// 直前の読み出しで、データ本体のあとに返ってきた状態応答 12 バイト。
    ///
    /// これまでエラー時にしか見ておらず、中身を捨てていた。
    /// 読み出しが「受理されたのに 0xFF しか返らない」ときの理由が
    /// ここに入っている可能性があるため、生のまま残す。
    /// </summary>
    public byte[] LastTrailingStatus { get; private set; } = [];

    public RfcaLink(string portName)
    {
        PortName = portName;
        _port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 250,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();

        // ポートを開くと DTR/RTS が立つ。CDC デバイスによってはこれが
        // リセット扱いになるため、落ち着くまで待ってから話しかける。
        // 直後に状態要求を投げると無応答になることがある。
        Thread.Sleep(SettleMilliseconds);
        DrainInput();
    }

    /// <summary>ポートを開いてからコマンドを送り始めるまでの待ち時間。</summary>
    public const int SettleMilliseconds = 300;

    public static string[] EnumeratePorts() => SerialPort.GetPortNames();

    /// <summary>
    /// アダプタがまだ USB バスに存在するか。
    ///
    /// ポートが開いていても、デバイスが物理的に消えていれば列挙から外れる。
    /// 探索中はコマンドを送るたびにこれを確かめ、消えた時点で即座に止める。
    /// 止めないと以降の全 opcode が「応答なし」として記録され、
    /// どのコマンドが原因だったのか分からなくなる。
    /// </summary>
    public bool IsStillEnumerated
    {
        get
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Any(p => string.Equals(p, PortName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>消えていれば <see cref="RfcaDisconnectedException"/> を投げる。</summary>
    public void EnsureAlive(uint? lastOpcode = null)
    {
        if (!IsStillEnumerated)
            throw new RfcaDisconnectedException(PortName, lastOpcode);
    }

    /// <summary>
    /// 全シリアルポートに状態要求を投げ、RFCA らしい応答を返したものを探す。
    ///
    /// PC に複数の COM ポートがあると、どれが RFCA なのかは見た目で分からない。
    /// 正規の 12 バイト応答を返すかどうかで判定する。
    /// </summary>
    public static string? FindAdapterPort(Action<string>? log = null)
    {
        foreach (string port in EnumeratePorts().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            log?.Invoke($"{port} を確認中…");

            try
            {
                using var candidate = new RfcaLink(port);
                var status = candidate.GetStatusWithRetry();

                if (status.HasResponse)
                {
                    log?.Invoke($"{port}: RFCA の応答を確認 ({status})");
                    return port;
                }

                log?.Invoke($"{port}: 応答なし");
            }
            catch (Exception ex)
            {
                log?.Invoke($"{port}: 開けません ({ex.Message})");
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 状態要求
    // ------------------------------------------------------------------

    /// <summary>状態要求を送り、挿入中カートリッジの種別を得る。</summary>
    public RfcaStatus GetStatus()
    {
        lock (_gate)
        {
            DrainInput();
            _port.Write(StatusRequest, 0, StatusRequest.Length);
            Trace?.Invoke("TX status");

            var buf = new byte[12];
            int got = TryReadExact(buf, 0, buf.Length, TimeSpan.FromMilliseconds(500));
            if (got == 0)
                return new RfcaStatus([]);
            if (got < 12)
                return new RfcaStatus(buf[..got]);

            Trace?.Invoke($"RX status {Convert.ToHexString(buf)}");
            return new RfcaStatus(buf);
        }
    }

    /// <summary>
    /// 応答が返るまで状態要求を数回試す。
    ///
    /// 接続直後の 1 回目は取りこぼすことがある。dumpfreak はメニューを
    /// 描き直すたびに状態要求を出しているので、事実上リトライしていた。
    /// </summary>
    public RfcaStatus GetStatusWithRetry(int attempts = 3)
    {
        RfcaStatus last = default;

        for (int i = 0; i < attempts; i++)
        {
            last = GetStatus();
            if (last.HasResponse) return last;

            Trace?.Invoke($"状態要求 {i + 1} 回目に応答なし。再試行します");
            Thread.Sleep(150);
        }

        return last;
    }

    public CartridgeKind DetectCartridge() => GetStatusWithRetry().Kind;

    // ------------------------------------------------------------------
    // 制御コマンド
    // ------------------------------------------------------------------

    /// <summary>
    /// データ本体を伴わない制御コマンドを送る。
    ///
    /// リクエストの 5 つ目のフィールド（リード／ライトでは size の再掲）に
    /// <paramref name="parameter"/> を入れる。dumpfreak の 0x2F コマンドが
    /// この形で、size=0 / addr=0 / param=1 を送っている。
    /// </summary>
    /// <param name="headerField">
    /// フレーム 2 つ目のフィールド。全スロット共通で 8。
    ///
    /// 「GBA だけ 0x00 でないと受け付けない」と書いていた時期があるが誤り。
    /// RetroFreakDumper も全コマンドで 8 を入れている。
    /// 引数として残してあるのは、調査で値を振れるようにするため。
    /// </param>
    public byte[] SendControl(
        uint opcode, uint address = 0, uint size = 0, uint parameter = 0,
        uint headerField = 0x08)
    {
        lock (_gate)
        {
            DrainInput();

            var req = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), headerField);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), size);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(12), address);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(16), parameter);
            _port.Write(req, 0, req.Length);
            Trace?.Invoke($"TX control op=0x{opcode:X2} hdr=0x{headerField:X2} " +
                          $"addr=0x{address:X6} size={size} param={parameter}");

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, TimeSpan.FromMilliseconds(800));
            if (got == 0)
                throw new RfcaTimeoutException($"制御コマンド 0x{opcode:X2} に応答がありません");

            Trace?.Invoke($"RX control {Convert.ToHexString(ack.AsSpan(0, got))}");

            // ステータスを検証していなかったため、拒否されていても
            // 「送信完了」と報告してしまっていた。
            if (got >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(ack) == 0xFFFFFFFF)
                Trace?.Invoke($"WARN: 制御コマンド 0x{opcode:X2} は拒否されました");

            return ack[..got];
        }
    }

    /// <summary>
    /// スロットをウェイクアップさせる。dumpfreak の 2F コマンドと同じ手順
    /// （送信後 0.5 秒待つ）。
    ///
    /// これを送るまでカートリッジはデータバスを駆動せず、
    /// どのアドレスを読んでも 0xFF しか返らない。実機で確認済み。
    /// </summary>
    public byte[] WakeSlot(uint? parameter = null)
    {
        uint param = parameter ?? ResolveWakeParameter();
        var kind = (CartridgeKind)(byte)param;

        // RetroFreakDumper.exe の逆コンパイルで判明した正式な初期化手順。
        // スロットごとに送るものが違う。
        byte[] ack = kind switch
        {
            CartridgeKind.GameBoyAdvance => InitGbaSlot(),
            CartridgeKind.SuperFamicom => InitSnesSlot(),
            _ => InitGenericSlot(param),
        };

        _awake = true;
        SettleAfterWake(kind);
        return ack;
    }

    /// <summary>
    /// GBA スロットの初期化。
    ///
    ///   Command04(0) → Command05() → 200ms 待つ
    ///
    /// **0x2F は送らない。** GBA スロットには不要で、送っても拒否される。
    /// 以前これを「GBA が拒否するので別の手段があるはず」と誤解し、
    /// 存在しない初期化コマンドを長く探していた。
    /// </summary>
    private byte[] InitGbaSlot()
    {
        Trace?.Invoke("GBA スロットを初期化します (0x04 param=0 → 0x05)");

        var ack = SendSlotSelect(0);
        SendSlotCommit();
        Thread.Sleep(200);

        return ack;
    }

    /// <summary>
    /// SFC スロットの初期化。
    ///
    ///   Command04(1) → Command2F(1) → Command05() ×2
    ///
    /// 0x2F だけでも読めていたが、正式な手順はこちら。
    /// </summary>
    private byte[] InitSnesSlot()
    {
        // 0x2F だけを送る。**実機で動作実績があるのはこの手順**。
        //
        // RetroFreakDumper の正規手順は 0x04(1) → 0x2F(1) → 0x05 ×2 だが、
        // それに合わせたところカービィ3 を認識しなくなった（2026-09-23 実機）。
        // 本家は初期化後に SnesRead(0xC000, 0x4000) で 16KB を捨て読みし、
        // ヘッダ判定に失敗したら最大 5 回やり直す作りになっており、
        // 0x04 / 0x05 だけを真似ても同じにはならない。
        //
        // 動いているものを、動く根拠のない「正しさ」で置き換えない。
        Trace?.Invoke("SFC スロットをウェイクアップします (0x2F param=1)");

        var ack = SendControl(RfcaOpcode.SlotWakeup, address: 0, size: 0, parameter: 1);
        Thread.Sleep(WakeSettleMilliseconds);

        return ack;
    }

    private byte[] InitGenericSlot(uint param)
    {
        // SFC と同じく 0x2F だけ。未検証の手順を混ぜない。
        Trace?.Invoke($"スロットをウェイクアップします (0x2F param={param})");

        var ack = SendControl(RfcaOpcode.SlotWakeup, address: 0, size: 0, parameter: param);
        Thread.Sleep(WakeSettleMilliseconds);

        return ack;
    }

    /// <summary>
    /// スロット選択 (0x04)。**16 バイトフレーム**で、リード要求の 20 バイトとは違う。
    /// [u32 0x04][u32 4][u32 0][u32 param]、応答 8 バイト。
    /// </summary>
    public byte[] SendSlotSelect(byte param)
    {
        lock (_gate)
        {
            DrainInput();

            var req = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), RfcaOpcode.SlotSelect);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(12), param);

            _port.Write(req, 0, req.Length);
            Trace?.Invoke($"TX 0x04 param={param}");

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, TimeSpan.FromMilliseconds(800));

            if (got == 0)
                throw new RfcaTimeoutException("スロット選択 (0x04) に応答がありません");

            Trace?.Invoke($"RX 0x04 {Convert.ToHexString(ack.AsSpan(0, got))}");
            return ack[..got];
        }
    }

    /// <summary>
    /// スロット確定 (0x05)。**12 バイトフレーム**。
    /// [u32 0x05][u32 0][u32 0]、応答 8 バイト。
    /// </summary>
    public byte[] SendSlotCommit()
    {
        lock (_gate)
        {
            DrainInput();

            var req = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), RfcaOpcode.SlotCommit);

            _port.Write(req, 0, req.Length);
            Trace?.Invoke("TX 0x05");

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, TimeSpan.FromMilliseconds(800));

            if (got == 0)
                throw new RfcaTimeoutException("スロット確定 (0x05) に応答がありません");

            Trace?.Invoke($"RX 0x05 {Convert.ToHexString(ack.AsSpan(0, got))}");
            return ack[..got];
        }
    }

    /// <summary>
    /// 吸い出し終了時の後始末。SFC は 0x2F(0) も送る。
    /// </summary>
    public void ReleaseSlot(CartridgeKind kind)
    {
        try
        {
            if (kind == CartridgeKind.SuperFamicom)
                SendControl(RfcaOpcode.SlotWakeup, address: 0, size: 0, parameter: 0);

            SendSlotSelect(0);
            SendSlotCommit();
            _awake = false;
        }
        catch (RfcaException ex)
        {
            Trace?.Invoke($"スロットの解放に失敗: {ex.Message}");
        }
    }


    /// <summary>ウェイクアップ後にバスが安定するまでの待ち時間。</summary>
    public const int WakeSettleMilliseconds = 500;

    /// <summary>
    /// ウェイクアップ直後の捨て読み。
    ///
    /// 1 回目の読み出しはデータが化けることがある
    /// （SFC で $00:FFC0 の 'H' が 0x00 になる事例、
    ///  GBA でヘッダ全体が化ける事例を実機で確認）。
    ///
    /// 重要なのは **そのスロットで実際に通る opcode を使う** こと。
    /// 以前は常に SFC の 0x07 を使っていたため、GBA では拒否されて
    /// 捨て読みとして成立せず、最初の本番読み出しが化けていた。
    /// </summary>
    private void SettleAfterWake(CartridgeKind kind)
    {
        (uint Opcode, uint Address, int Size, uint HeaderField)? probe = kind switch
        {
            CartridgeKind.SuperFamicom => (RfcaOpcode.SnesRead, 0x00FFC0u, 64, 0x08u),
            CartridgeKind.MegaDrive => (RfcaOpcode.MegaDriveRead, 0x000000u, 64, 0x08u),
            CartridgeKind.MarkIIIOrGameGear => (RfcaOpcode.SmsRead, 0x000000u, 64, 0x08u),
            CartridgeKind.GameBoyAdvance when RfcaOpcode.GbaRead is uint gba
                => (gba, 0x000000u, RfcaOpcode.GbaBlockSize, RfcaOpcode.RequestHeaderField),
            _ => null,
        };

        if (probe is not var (opcode, address, size, headerField)) return;

        // 2 回読む。1 回目が化けても 2 回目以降が安定するのが実機の挙動。
        for (int i = 0; i < 2; i++)
        {
            try
            {
                Read(opcode, address, size, headerField);
            }
            catch (RfcaException ex)
            {
                Trace?.Invoke($"捨て読み {i + 1} 回目に失敗（無視して続行）: {ex.Message}");
                return;
            }
        }

        Trace?.Invoke($"{kind.ToDisplayName()} 用の捨て読みを完了しました");
    }

    private bool _awake;

    /// <summary>
    /// ウェイクアップコマンドに渡す値を固定する。null なら自動。
    ///
    /// dumpfreak は SFC で 1 を送っていた。SFC の種別コードも 0x01 なので、
    /// この値が「有効化フラグ」なのか「スロット番号」なのかは区別がついていない。
    /// 自動のときは種別コードを使い、それで読めなければ 1 を試す。
    /// </summary>
    public uint? WakeParameter { get; set; }

    private uint ResolveWakeParameter()
    {
        if (WakeParameter is uint fixedValue) return fixedValue;

        // 種別コードがスロット番号を兼ねている可能性に賭ける。
        // SFC なら 0x01 になり、実機で動作確認済みの値と一致する。
        var kind = GetStatus().Kind;
        return kind.IsConnected() ? (uint)(byte)kind : 1u;
    }

    /// <summary>
    /// ウェイクアップをやり直す。パラメータを変えて試したいときに使う。
    /// </summary>
    public void ReWake(uint parameter)
    {
        _awake = false;
        WakeSlot(parameter);
    }

    /// <summary>
    /// まだウェイクアップしていなければ実行する。
    /// 読み書きの前に必ず通るので、呼び出し側が意識する必要はない。
    /// </summary>
    public void EnsureAwake(bool force = false)
    {
        if (!AutoWake)
        {
            // 切り分け中は何も送らない。「素の状態で読めるか」を見るため。
            _awake = true;
            return;
        }

        if (_awake && !force) return;

        Trace?.Invoke("スロットをウェイクアップします (opcode 0x2F)");
        WakeSlot();
    }

    /// <summary>
    /// 読み書きの前に自動でウェイクアップするか。既定は true。
    ///
    /// false にすると <see cref="EnsureAwake"/> は何も送らない。
    /// 「追加のコマンドを一切送らずに読めるか」を確かめる切り分けで使う。
    /// 本番の吸い出しでは true のままにすること。
    /// </summary>
    public bool AutoWake { get; set; } = true;

    /// <summary>
    /// ウェイクアップ済みの状態を破棄し、次の読み書きでやり直させる。
    ///
    /// 0x2F の param は「どのスロットを使うか」の指定なので、
    /// カートリッジを差し替えたら選び直さないと、前のスロットを
    /// 向いたまま読もうとして失敗する。
    /// 種別を取得し直すタイミングで必ず呼ぶこと。
    /// </summary>
    public void InvalidateWake()
    {
        if (_awake) Trace?.Invoke("ウェイクアップ状態を破棄しました。次の読み出しで選び直します");
        _awake = false;
    }

    // ------------------------------------------------------------------
    // リード
    // ------------------------------------------------------------------

    /// <summary>
    /// 指定 opcode のバスから <paramref name="address"/> を先頭に
    /// <paramref name="size"/> バイト読み出す。
    /// </summary>
    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buf = new byte[size];
        Read(opcode, address, buf, headerField);
        return buf;
    }

    /// <summary>リード結果を呼び出し側のバッファに直接書き込むオーバーロード。</summary>
    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (destination.Length == 0) return;

        // ウェイクアップ前はカートリッジがバスを駆動しないため 0xFF しか返らない。
        // 読み出しの前に必ず一度だけ済ませておく。
        EnsureAwake();

        lock (_gate)
        {
            DrainInput();

            uint size = (uint)destination.Length;
            SendRequest(opcode, address, size, headerField);

            // 1. リード ACK: [00 00 00 00][size]
            Span<byte> ack = stackalloc byte[8];
            ReadExact(ack, AckTimeout,
                $"opcode 0x{opcode:X2} のリード要求に応答がありません");

            uint ackStatus = BinaryPrimitives.ReadUInt32LittleEndian(ack);
            uint ackSize = BinaryPrimitives.ReadUInt32LittleEndian(ack[4..]);
            if (ackStatus != 0 || ackSize != size)
            {
                throw new RfcaNakException(
                    $"opcode 0x{opcode:X2} addr 0x{address:X6} が肯定応答を返しませんでした " +
                    $"(応答: {Convert.ToHexString(ack)})",
                    ack.ToArray());
            }

            // 2. データ本体の催促として状態要求を送る
            _port.Write(StatusRequest, 0, StatusRequest.Length);

            // 3. データ本体
            ReadExact(destination, EstimateTransferTimeout(destination.Length),
                $"addr 0x{address:X6} のデータ本体を受信できませんでした");

            // 4. 催促で送った状態要求への応答
            Span<byte> trailing = stackalloc byte[12];
            int got = TryReadExact(trailing, TimeSpan.FromMilliseconds(500));
            LastTrailingStatus = trailing[..Math.Max(got, 0)].ToArray();

            if (got == 12)
            {
                var status = new RfcaStatus(trailing.ToArray());
                if (status.IsFault)
                    Trace?.Invoke($"WARN: addr 0x{address:X6} 読み出し後の状態応答がエラーを示しています");
            }
            else if (got > 0)
            {
                Trace?.Invoke($"WARN: 末尾状態応答が {got} バイトしか来ませんでした");
            }

            // 応答の長さが機種によって違う可能性があるので、
            // 残っているものは必ず捨ててから次のリクエストに移る。
            // ここを条件付きにしていると、取りこぼしが次の読み出しの
            // 先頭に紛れ込んでデータが 1 バイトずれる。
            DrainInput();
        }
    }

    // ------------------------------------------------------------------
    // 探索用の柔軟なリード
    // ------------------------------------------------------------------

    /// <summary>リード要求に対するアダプタの返事。</summary>
    public enum ReadAckKind
    {
        /// <summary>応答なし。</summary>
        Silent,

        /// <summary>ステータス 0 ＝ 受理。データが続く。</summary>
        Accepted,

        /// <summary>ステータス 0xFFFFFFFF ＝ 汎用エラー。データは続かない。</summary>
        Rejected,

        /// <summary>8 バイトのゼロ ＝ ライト要求として受理された。</summary>
        WriteAck,

        /// <summary>解釈できない応答。</summary>
        Unknown,
    }

    public readonly record struct FlexibleReadResult(
        ReadAckKind Kind, uint AckStatus, uint AckSize, byte[] Data, byte[] RawAck);

    /// <summary>
    /// opcode 探索用のリード。
    ///
    /// 通常の <see cref="Read(uint, uint, Span{byte})"/> は「要求したサイズと
    /// 同じサイズで肯定応答が返ること」を成功条件にしている。探索中はそれが
    /// きつすぎて、「受理したが別のサイズを返す」応答を取りこぼす。
    /// ここではステータスだけを見て受理と判断し、アダプタが申告したサイズ分を読む。
    ///
    /// 受理された場合はデータ本体まで読み切るので、通信の同期は保たれる。
    /// </summary>
    public FlexibleReadResult TryReadFlexible(
        uint opcode, uint address, uint requestedSize, uint headerField = 0x08)
    {
        EnsureAwake();

        lock (_gate)
        {
            DrainInput();
            SendRequest(opcode, address, requestedSize, headerField);

            var ack = new byte[8];
            int got = TryReadExact(ack, 0, ack.Length, AckTimeout);

            if (got == 0)
                return new FlexibleReadResult(ReadAckKind.Silent, 0, 0, [], []);

            if (got < 8)
                return new FlexibleReadResult(ReadAckKind.Unknown, 0, 0, [], ack[..got]);

            uint ackStatus = BinaryPrimitives.ReadUInt32LittleEndian(ack);
            uint ackSize = BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4));

            if (ackStatus == 0xFFFFFFFF)
                return new FlexibleReadResult(ReadAckKind.Rejected, ackStatus, ackSize, [], ack);

            if (ackStatus == 0 && ackSize == 0)
                return new FlexibleReadResult(ReadAckKind.WriteAck, ackStatus, ackSize, [], ack);

            if (ackStatus != 0)
                return new FlexibleReadResult(ReadAckKind.Unknown, ackStatus, ackSize, [], ack);

            // 受理された。催促を送ってデータ本体を受け取る。
            // 読み切らないと次のコマンドとずれるので、必ず最後まで処理する。
            _port.Write(StatusRequest, 0, StatusRequest.Length);

            int length = (int)Math.Min(ackSize, 64 * 1024);
            var data = new byte[length];
            int read = TryReadExact(data, 0, length, EstimateTransferTimeout(length));

            Span<byte> trailing = stackalloc byte[12];
            TryReadExact(trailing, TimeSpan.FromMilliseconds(400));
            DrainInput();

            return new FlexibleReadResult(
                ReadAckKind.Accepted, ackStatus, ackSize, data[..read], ack);
        }
    }

    // ------------------------------------------------------------------
    // ライト
    // ------------------------------------------------------------------

    /// <summary>
    /// 指定 opcode のバスの <paramref name="address"/> へ書き込む。
    /// マッパー（SA-1 Super MMC、GB の MBC、SMS のバンクレジスタ）の制御に使う。
    /// </summary>
    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        // 書き込み保護。要求フレームを組み立てる前に弾くので、
        // 禁止されている間はシリアルポートに 1 バイトも出ない。
        if (!AllowWrites)
            throw new RfcaWriteBlockedException(
                $"書き込み保護が有効です。opcode 0x{opcode:X2} アドレス 0x{address:X8} への " +
                $"{data.Length} バイトの書き込みを実行せずに中止しました。");

        EnsureAwake();

        lock (_gate)
        {
            DrainInput();

            SendRequest(opcode, address, (uint)data.Length);

            // ライト要求 ACK。実機は 8 バイトのゼロを返すが、
            // 異なる応答でも処理を続行する（dumpfreak と同じ挙動）。
            Span<byte> ack = stackalloc byte[8];
            int got = TryReadExact(ack, TimeSpan.FromMilliseconds(600));
            if (got == 0)
                throw new RfcaTimeoutException(
                    $"opcode 0x{opcode:X2} のライト要求に応答がありません");
            if (got == 8 && !IsAllZero(ack))
                Trace?.Invoke($"WARN: ライト要求 ACK が非ゼロ {Convert.ToHexString(ack)}");

            // データ本体送信
            _port.Write(data.ToArray(), 0, data.Length);

            Span<byte> ack2 = stackalloc byte[8];
            int got2 = TryReadExact(ack2, TimeSpan.FromMilliseconds(600));
            if (got2 == 0)
                throw new RfcaTimeoutException(
                    $"addr 0x{address:X6} へのデータ書き込みに応答がありません");
            if (got2 == 8 && !IsAllZero(ack2))
                Trace?.Invoke($"WARN: ライトデータ ACK が非ゼロ {Convert.ToHexString(ack2)}");
        }
    }

    /// <summary>1 バイト書き込み。マッパーレジスタ操作の定番。</summary>
    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    /// <summary>
    /// ライト要求として受理されてしまったリクエストを完了させ、通信の同期を取り戻す。
    /// opcode 探索中、リードのつもりで送った要求がライト系だったときに使う。
    ///
    /// アダプタは要求したバイト数が届くまで次のコマンドを受け付けないため、
    /// <paramref name="filler"/> を <paramref name="size"/> バイト送って手順を終わらせる。
    /// これは実際にカートリッジへの書き込みになりうる。
    /// </summary>
    public void CompletePendingWrite(int size, byte filler = 0xFF)
    {
        if (!AllowWrites)
            throw new RfcaWriteBlockedException(
                $"書き込み保護が有効です。保留中のライト手順を完了させる {size} バイトの " +
                "送信を中止しました。アダプタを抜き差しして同期を取り直してください。");

        lock (_gate)
        {
            var data = new byte[size];
            Array.Fill(data, filler);
            _port.Write(data, 0, data.Length);
            Trace?.Invoke($"TX filler {size} バイト (0x{filler:X2}) でライト手順を完了");

            Span<byte> ack = stackalloc byte[8];
            TryReadExact(ack, TimeSpan.FromMilliseconds(600));
            DrainInput();
        }
    }

    // ------------------------------------------------------------------
    // 内部処理
    // ------------------------------------------------------------------

    /// <summary>
    /// リード／ライト要求の 20 バイトフレームを組んで送る。
    ///
    /// 2 つ目のフィールド (<paramref name="headerField"/>) の意味は不明。
    /// dumpfreak の観測範囲ではリード・ライトとも常に 8 で、状態要求だけ 0 だった。
    /// 「これ以降のパラメータ長 - 4」と辻褄は合う。GBA のように
    /// 別のパラメータ構成を要求するコマンドがあるかもしれないので、
    /// 探索時に変えられるようにしてある。
    /// </summary>
    private void SendRequest(uint opcode, uint address, uint size, uint headerField = 0x08)
    {
        var req = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), headerField);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(12), address);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(16), size);
        _port.Write(req, 0, req.Length);
        Trace?.Invoke($"TX op=0x{opcode:X2} addr=0x{address:X6} size={size}");
    }

    /// <summary>転送サイズに見合ったタイムアウト。115200bps ≒ 11.5KB/s。</summary>
    private static TimeSpan EstimateTransferTimeout(int bytes)
        => TimeSpan.FromMilliseconds(600 + bytes * 1000.0 / 11000.0);

    private void ReadExact(Span<byte> buffer, TimeSpan timeout, string errorMessage)
    {
        int got = TryReadExact(buffer, timeout);
        if (got != buffer.Length)
            throw new RfcaTimeoutException($"{errorMessage} ({got}/{buffer.Length} バイト受信)");
    }

    private int TryReadExact(Span<byte> buffer, TimeSpan timeout)
    {
        var rented = new byte[buffer.Length];
        int got = TryReadExact(rented, 0, buffer.Length, timeout);
        rented.AsSpan(0, got).CopyTo(buffer);
        return got;
    }

    /// <summary>
    /// 要求バイト数が揃うまで読む。デッドラインを過ぎたら読めた分だけ返す。
    /// SerialPort.Read は要求より少ない値を返しうるのでループが要る。
    /// </summary>
    private int TryReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        int total = 0;
        var deadline = DateTime.UtcNow + timeout;

        while (total < count)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            _port.ReadTimeout = Math.Max(1, (int)remaining.TotalMilliseconds);
            try
            {
                int n = _port.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        return total;
    }

    private void DrainInput()
    {
        try
        {
            if (_port.BytesToRead > 0)
            {
                Trace?.Invoke($"DRAIN {_port.BytesToRead} バイトの未処理受信データを破棄");
                _port.DiscardInBuffer();
            }
            _port.DiscardOutBuffer();
        }
        catch (InvalidOperationException)
        {
            // ポートが閉じられた。呼び出し側の Dispose 競合。
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            if (b != 0) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 終了時にスロット解放 (0x04(0) → 0x05) を送る実装を試したが、
        // SFC の初期化変更と併せて入れたところ認識不良を起こしたため外してある。
        // 解放しなくても次回接続時のウェイクアップで問題なく読めている。

        try { _port.Close(); } catch { /* 切断済み */ }
        _port.Dispose();
    }
}
