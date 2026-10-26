using System.Globalization;
using System.Text;
using TunerFish.Models;

namespace TunerFish.Services;

/// <summary>
/// How artists and albums are filed: by Jellyfin's sort name (which already ignores leading
/// articles, and which you can change in Jellyfin's metadata editor). Display names never change.
/// <para>
/// The plain-letter rule: for sorting and for the A–Z jump bar only, accented letters count as
/// their plain letter (É → E, ß → ss). Then symbols sort before digits, and digits before letters,
/// position by position, so every jump-bar group is one unbroken block of the list. Non-Latin
/// scripts aren't supported: they count as symbols and land in the "#" group.
/// </para>
/// </summary>
public static class SortOrder
{
    /// <summary>The jump-bar group for everything that doesn't start with a letter A–Z.</summary>
    public const string OtherGroup = "#";

    /// <summary>The jump-bar groups in bar order: "#", then "A" to "Z".</summary>
    public static IReadOnlyList<string> Groups { get; } =
        new[] { OtherGroup }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString())).ToArray();

    public static IComparer<string> Comparer { get; } = new PlainLetterComparer();

    /// <summary>The key an item files under: Jellyfin's sort name, or its name if the server sent none.</summary>
    public static string KeyFor(BaseItem item) =>
        string.IsNullOrWhiteSpace(item.SortName) ? item.Name.Trim() : item.SortName.Trim();

    /// <summary>The jump-bar group of a sort key: its first plain letter in capitals, or "#".</summary>
    public static string GroupOf(string key)
    {
        var plain = PlainLetters(key);
        if (plain.Length == 0) return OtherGroup;
        var first = char.ToUpperInvariant(plain[0]);
        return first is >= 'A' and <= 'Z' ? first.ToString() : OtherGroup;
    }

    /// <summary>
    /// The text with accents removed (É → E, ñ → n) and the letters that don't decompose spelled out
    /// (ß → ss, Æ → AE, Ø → O, ...). Used only for sorting and grouping, never shown.
    /// </summary>
    public static string PlainLetters(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch
            {
                'ß' => "ss",
                'Æ' => "AE", 'æ' => "ae",
                'Œ' => "OE", 'œ' => "oe",
                'Ø' => "O", 'ø' => "o",
                'Ł' => "L", 'ł' => "l",
                'Đ' or 'Ð' => "D", 'đ' or 'ð' => "d",
                'Þ' => "TH", 'þ' => "th",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Compares plain-letter keys character by character: symbols (including anything non-Latin),
    /// then digits 0–9, then letters A–Z; within a class by character code, ignoring case.
    /// A key that's the start of another sorts first. Explicit, so the Android app can match it.
    /// </summary>
    private sealed class PlainLetterComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var a = PlainLetters(x);
            var b = PlainLetters(y);
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var ca = char.ToUpperInvariant(a[i]);
                var cb = char.ToUpperInvariant(b[i]);
                if (ca == cb) continue;
                var byClass = ClassOf(ca).CompareTo(ClassOf(cb));
                return byClass != 0 ? byClass : ca.CompareTo(cb);
            }
            return a.Length.CompareTo(b.Length);
        }

        private static int ClassOf(char upper) => upper switch
        {
            >= 'A' and <= 'Z' => 2,
            >= '0' and <= '9' => 1,
            _ => 0,
        };
    }
}
