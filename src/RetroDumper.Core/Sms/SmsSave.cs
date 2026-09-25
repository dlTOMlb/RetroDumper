using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Sms;

/// <summary>
/// マークIII / マスターシステム / ゲームギアのセーブ（カートリッジ RAM）の読み書き。
///
/// セーブ RAM は $8000-$BFFF に現れる。$FFFC がその制御で、
///
///   8  (0b1000) … RAM を有効にし、バンク 0 を貼る
///   12 (0b1100) … RAM を有効にし、バンク 1 を貼る
///   0           … RAM を無効に戻す
///
/// 容量は申告されないので、読めた内容から判断する。
///
///   $8000 と $A000 が同じ      → 8KB（同じものが 2 回見えている）
///   バンク 0 と 1 が同じ       → 16KB（バンク切り替えが効いていない）
///   どちらも違う               → 32KB
///
/// 触った $FFFC は必ず 0 に戻す。有効のまま放置すると、
/// 以降の操作が意図せずセーブを書き換えうる。
/// </summary>
public static class SmsSave
{
    private const uint Control = 0xFFFC;
    private const uint WindowLow = 0x8000;
    private const uint WindowHigh = 0xA000;
    private const int HalfSize = 0x2000;          // 8KB
    private const int BankSize = 0x4000;          // 16KB（$8000-$BFFF）

    private const byte EnableBank0 = 8;
    private const byte EnableBank1 = 12;
    private const byte Disable = 0;

    /// <summary>扱えるセーブの上限。</summary>
    public const int MaxSize = 32 * 1024;

    /// <summary>
    /// セーブを読み、内容から容量を判断して返す。
    /// 8KB / 16KB / 32KB のいずれかになる。
    /// </summary>
    public static byte[] Read(
        IRfcaLink link,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SetControl(link, EnableBank0);

        try
        {
            var low0 = link.Read(RfcaOpcode.SmsRead, WindowLow, HalfSize);
            var high0 = link.Read(RfcaOpcode.SmsRead, WindowHigh, HalfSize);

            progress?.Report(new DumpProgress("セーブ読み出し", HalfSize * 2, MaxSize));

            // $8000 と $A000 が同じなら、8KB が 2 回見えているだけ。
            if (low0.AsSpan().SequenceEqual(high0)) return low0;

            cancellationToken.ThrowIfCancellationRequested();

            SetControl(link, EnableBank1);

            var low1 = link.Read(RfcaOpcode.SmsRead, WindowLow, HalfSize);
            var high1 = link.Read(RfcaOpcode.SmsRead, WindowHigh, HalfSize);

            progress?.Report(new DumpProgress("セーブ読み出し", MaxSize, MaxSize));

            // バンクを変えても内容が変わらないなら 16KB。
            if (low0.AsSpan().SequenceEqual(low1) && high0.AsSpan().SequenceEqual(high1))
                return [.. low0, .. high0];

            return [.. low0, .. high0, .. low1, .. high1];
        }
        finally
        {
            SetControl(link, Disable);
        }
    }

    /// <summary>
    /// 大きさが分かっているときに、その分だけ読む。
    ///
    /// <see cref="Read"/> は内容を見比べて容量を決めるため、
    /// **全面が同じ値だと 8KB と誤る。**窓の中身が区別できないからで、
    /// 折り返して同じものが見えているのか、本当に同じものが
    /// 書かれているのかを、内容からは判別できない。
    ///
    /// 消去したあとの読み戻しがまさにそれにあたる。
    /// 書いた長さが分かっている照合では、こちらを使う。
    /// </summary>
    public static byte[] ReadExact(
        IRfcaLink link, int size,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (size <= 0 || size > MaxSize)
            throw new RfcaException(
                $"読み出す大きさが範囲外です。1 から {MaxSize} バイトのところ " +
                $"{size} バイトを指定されました。");

        var buffer = new byte[size];
        int done = 0;

        try
        {
            for (int bank = 0; done < size; bank++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SetControl(link, bank == 0 ? EnableBank0 : EnableBank1);

                for (uint window = WindowLow; window <= WindowHigh && done < size; window += HalfSize)
                {
                    int length = Math.Min(HalfSize, size - done);

                    link.Read(RfcaOpcode.SmsRead, window, buffer.AsSpan(done, length));
                    done += length;

                    progress?.Report(new DumpProgress("セーブ読み出し", done, size));
                }
            }
        }
        finally
        {
            SetControl(link, Disable);
        }

        return buffer;
    }

    /// <summary>
    /// セーブを書き、読み戻して照合する。
    ///
    /// **参照実装はここで前半を書き漏らしている。**
    /// 32KB のとき、バンク 1 に切り替えてから後半だけを書いており、
    /// 前半 16KB がどこにも書かれない。そのまま真似ると半分しか戻らないので、
    /// バンク 0 に前半、バンク 1 に後半を書く形に正してある。
    /// </summary>
    public static void Write(
        IRfcaLink link, byte[] data,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
            throw new RfcaException("書き込む内容がありません。");

        if (data.Length > MaxSize)
            throw new RfcaException(
                $"セーブが大きすぎます。扱えるのは {MaxSize} バイトまでですが、" +
                $"{data.Length} バイト渡されました。");

        SetControl(link, EnableBank0);

        try
        {
            int first = Math.Min(BankSize, data.Length);

            WriteWindow(link, data.AsSpan(0, first));
            progress?.Report(new DumpProgress("セーブ書き込み", first, data.Length));

            if (data.Length > BankSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SetControl(link, EnableBank1);
                WriteWindow(link, data.AsSpan(BankSize));

                progress?.Report(new DumpProgress("セーブ書き込み", data.Length, data.Length));
            }
        }
        finally
        {
            SetControl(link, Disable);
        }

        progress?.Report(new DumpProgress("書き込んだ内容を照合中", 0, data.Length));

        // **ここで Read を使ってはいけない。**
        // Read は内容を見比べて容量を決めるので、消去のように
        // 全面が同じ値だと 8KB と誤り、32KB 書いても照合に失敗する。
        // 書いた長さは分かっているのだから、それに合わせて読む。
        var readBack = ReadExact(link, data.Length, progress, cancellationToken);

        for (int i = 0; i < data.Length; i++)
            if (readBack[i] != data[i])
                throw new RfcaException(
                    $"照合に失敗しました。{i:X4} 番地は 0x{data[i]:X2} を書いたはずですが " +
                    $"0x{readBack[i]:X2} が読めました。" +
                    "カートリッジのセーブデータが中途半端な状態になっている可能性があります。");
    }

    /// <summary>$8000 からの窓へ書く。8KB を越える分は $A000 側へ続ける。</summary>
    private static void WriteWindow(IRfcaLink link, ReadOnlySpan<byte> data)
    {
        int low = Math.Min(HalfSize, data.Length);

        link.WriteSaveMemory(
            CartridgeKind.MarkIIIOrGameGear, RfcaOpcode.SmsWrite, WindowLow, data[..low]);

        if (data.Length > HalfSize)
            link.WriteSaveMemory(
                CartridgeKind.MarkIIIOrGameGear, RfcaOpcode.SmsWrite, WindowHigh, data[HalfSize..]);
    }

    /// <summary>$FFFC を書く。RAM の有効化とバンクの選択を兼ねている。</summary>
    private static void SetControl(IRfcaLink link, byte value)
        => link.WriteBankRegister(
            CartridgeKind.MarkIIIOrGameGear, RfcaOpcode.SmsWrite, Control, value);
}
