namespace RetroDumper.Ui;

/// <summary>
/// カートリッジ情報の 1 行。
///
/// KeyValuePair のままでも値は同じだが、XAML の x:DataType に
/// 総称型を書けない。名前の付いた型にしておくと、
/// バインディングがビルド時に検証される。
/// </summary>
public sealed record DetailRow(string Key, string Value);
