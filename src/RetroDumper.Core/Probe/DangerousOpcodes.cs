namespace RetroDumper.Core.Probe;

/// <summary>
/// 送るとアダプタが応答不能になる opcode の記録。
///
/// 実測で分かったこと: 全 opcode を総当たりで撃ち込むと、
/// アダプタのファームが USB の再列挙に失敗して COM ポートごと消える。
/// 電源を入れ直す（USB を抜き差しする）まで復帰しない。
///
/// 一度やらかした opcode はファイルに残し、次回以降は送らない。
/// 記録しないと、探索のたびに同じ場所で同じようにアダプタを落とし、
/// そのたびに抜き差しを強いることになる。
/// </summary>
public static class DangerousOpcodes
{
    private static readonly object Gate = new();
    private static HashSet<uint>? _cache;

    public static string FilePath =>
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(),
            "dangerous-opcodes.txt");

    /// <summary>
    /// 既知の危険 opcode。探索から常に除外する。
    ///
    /// 0x2F は SFC のウェイクアップで必要なので除外しない。
    /// GBA では拒否されるだけで、アダプタは落ちない。
    /// </summary>
    private static readonly uint[] BuiltIn = [];

    public static IReadOnlySet<uint> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            var set = new HashSet<uint>(BuiltIn);

            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath))
                    {
                        string line = raw.Trim();
                        int hash = line.IndexOf('#');
                        if (hash >= 0) line = line[..hash].Trim();
                        if (line.Length == 0) continue;

                        if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            line = line[2..];

                        if (uint.TryParse(line,
                                System.Globalization.NumberStyles.HexNumber,
                                null, out uint value))
                            set.Add(value);
                    }
                }
            }
            catch (IOException)
            {
                // 読めなくても組み込み分だけで動かす。
            }

            _cache = set;
            return set;
        }
    }

    public static bool IsDangerous(uint opcode) => Load().Contains(opcode);

    /// <summary>アダプタを落とした opcode を記録する。</summary>
    public static void Record(uint opcode, string reason)
    {
        lock (Gate)
        {
            var set = Load() as HashSet<uint>;
            if (set is null || !set.Add(opcode)) return;

            try
            {
                bool fresh = !File.Exists(FilePath);
                using var w = new StreamWriter(FilePath, append: true);

                if (fresh)
                {
                    w.WriteLine("# 送るとアダプタが応答不能になった opcode の記録。");
                    w.WriteLine("# 探索はここに載っている番号を送りません。");
                    w.WriteLine("# 消せば再び試すようになります。");
                }

                w.WriteLine($"0x{opcode:X2}  # {DateTime.Now:yyyy-MM-dd HH:mm:ss} {reason}");
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>テスト用。キャッシュを捨てて読み直させる。</summary>
    public static void ResetCache()
    {
        lock (Gate) _cache = null;
    }
}
