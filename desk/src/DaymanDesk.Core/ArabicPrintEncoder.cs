using System.Text;

namespace DaymanDesk.Core;

/// <summary>
/// Encodes a line of Arabic for a thermal printer in a given code page. Printers draw bytes left to right
/// and do not reorder, so Arabic goes out in visual order. CP864 holds only shaped glyphs (presentation
/// forms), so for CP864 the text is shaped first and each glyph falls back to the closest form CP864 has.
/// </summary>
public static class ArabicPrintEncoder
{
    // letter -> isolated form; dual-joining letters have iso, final, initial, medial at +0..+3,
    // right-joining letters only iso and final at +0..+1.
    static readonly Dictionary<char, (char iso, bool dual)> Forms = new()
    {
        ['ء'] = ('ﺀ', false), ['آ'] = ('ﺁ', false), ['أ'] = ('ﺃ', false),
        ['ؤ'] = ('ﺅ', false), ['إ'] = ('ﺇ', false), ['ئ'] = ('ﺉ', true),
        ['ا'] = ('ﺍ', false), ['ب'] = ('ﺏ', true), ['ة'] = ('ﺓ', false),
        ['ت'] = ('ﺕ', true), ['ث'] = ('ﺙ', true), ['ج'] = ('ﺝ', true),
        ['ح'] = ('ﺡ', true), ['خ'] = ('ﺥ', true), ['د'] = ('ﺩ', false),
        ['ذ'] = ('ﺫ', false), ['ر'] = ('ﺭ', false), ['ز'] = ('ﺯ', false),
        ['س'] = ('ﺱ', true), ['ش'] = ('ﺵ', true), ['ص'] = ('ﺹ', true),
        ['ض'] = ('ﺽ', true), ['ط'] = ('ﻁ', true), ['ظ'] = ('ﻅ', true),
        ['ع'] = ('ﻉ', true), ['غ'] = ('ﻍ', true), ['ف'] = ('ﻑ', true),
        ['ق'] = ('ﻕ', true), ['ك'] = ('ﻙ', true), ['ل'] = ('ﻝ', true),
        ['م'] = ('ﻡ', true), ['ن'] = ('ﻥ', true), ['ه'] = ('ﻩ', true),
        ['و'] = ('ﻭ', false), ['ى'] = ('ﻯ', false), ['ي'] = ('ﻱ', true),
    };

    static readonly Dictionary<char, char> LamAlef = new()
    {
        ['آ'] = 'ﻵ', ['أ'] = 'ﻷ', ['إ'] = 'ﻹ', ['ا'] = 'ﻻ',
    };

    static bool Joins(char c) => Forms.ContainsKey(c);
    static bool JoinsForward(char c) => Forms.TryGetValue(c, out var f) && f.dual;

    /// <summary>Contextual shaping in logical order (Unicode presentation forms B).</summary>
    public static string Shape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!Forms.TryGetValue(c, out var f)) { sb.Append(c); continue; }
            bool prevJoins = i > 0 && JoinsForward(s[i - 1]);
            if (c == 'ل' && i + 1 < s.Length && LamAlef.TryGetValue(s[i + 1], out var la))
            {
                sb.Append((char)(la + (prevJoins ? 1 : 0)));
                i++;
                continue;
            }
            bool nextJoins = f.dual && i + 1 < s.Length && Joins(s[i + 1]);
            int form = (prevJoins, nextJoins) switch
            {
                (false, false) => 0,
                (true, false) => 1,
                (false, true) => 2,
                (true, true) => 3,
            };
            sb.Append((char)(f.iso + form));
        }
        return sb.ToString();
    }

    /// <summary>Visual order for a left-to-right printer: reverse the line, keep Latin/digit runs readable.</summary>
    public static string ToVisual(string s)
    {
        var rev = TextNormalizer.Reverse(s).ToCharArray();
        int i = 0;
        while (i < rev.Length)
        {
            if (IsLtr(rev[i]))
            {
                int j = i;
                while (j + 1 < rev.Length && (IsLtr(rev[j + 1]) || (rev[j + 1] == ' ' && j + 2 < rev.Length && IsLtr(rev[j + 2])))) j++;
                Array.Reverse(rev, i, j - i + 1);
                i = j + 1;
            }
            else i++;
        }
        return new string(rev);
    }

    static bool IsLtr(char c) => (c < 0x80 && char.IsLetterOrDigit(c)) || ".,:/%-+#@_".IndexOf(c) >= 0;

    public static byte[] Encode(string text, int codePage)
    {
        if (codePage == ReceiptAnalyzer.Utf8) return Encoding.UTF8.GetBytes(text);
        // CP864 has no glyph for alef with hamza below; plain alef reads the same on a receipt.
        if (codePage == ReceiptAnalyzer.Cp864) text = text.Replace('إ', 'ا');
        string visual = ToVisual(codePage == ReceiptAnalyzer.Cp864 ? Shape(text) : text);
        if (codePage != ReceiptAnalyzer.Cp864)
            return ReceiptAnalyzer.GetDecoder(codePage).GetBytes(visual);

        var table = Cp864Table();
        var bytes = new List<byte>(visual.Length);
        foreach (char c in visual)
        {
            if (table.TryGetValue(c, out byte b)) { bytes.Add(b); continue; }
            // CP864 lacks many medial and final forms: medial -> initial, final -> isolated, initial -> isolated.
            byte? alt = null;
            foreach (var (iso, _) in Forms.Values)
            {
                int d = c - iso;
                if (d < 0 || d > 3) continue;
                char[] tries = d switch
                {
                    3 => new[] { (char)(iso + 2), (char)(iso + 1), iso },
                    2 => new[] { iso },
                    1 => new[] { iso },
                    _ => Array.Empty<char>(),
                };
                foreach (var t in tries) if (table.TryGetValue(t, out byte tb)) { alt = tb; break; }
                if (alt != null) break;
            }
            if (alt == null && c >= 'ﻵ' && c <= 'ﻼ' && table.TryGetValue((char)(c % 2 == 0 ? c - 1 : c), out byte lb)) alt = lb;
            bytes.Add(alt ?? (byte)'?');
        }
        return bytes.ToArray();
    }

    static Dictionary<char, byte>? _cp864;
    static Dictionary<char, byte> Cp864Table()
    {
        if (_cp864 != null) return _cp864;
        var enc = ReceiptAnalyzer.GetDecoder(ReceiptAnalyzer.Cp864);
        var map = new Dictionary<char, byte>();
        for (int b = 0x20; b < 0x100; b++)
        {
            string s = enc.GetString(new[] { (byte)b });
            if (s.Length == 1 && s[0] != '�' && !map.ContainsKey(s[0])) map[s[0]] = (byte)b;
        }
        return _cp864 = map;
    }
}
