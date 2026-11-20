using System.Text;

namespace DaymanDesk.Core;

/// <summary>Turns printed text into one comparable form: base Arabic letters, Latin digits, lower-case Latin.</summary>
public static class TextNormalizer
{
    public static string Normalize(string s, bool lower = true)
    {
        // NFKC folds Arabic presentation forms (CP864 prints those) to base letters and the
        // lam-alef ligatures to two letters.
        string k;
        try { k = s.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException)
        {
            // Some code pages decode bytes to non-characters that ICU refuses; drop them.
            var clean = new StringBuilder(s.Length);
            foreach (char ch in s) if (!char.IsSurrogate(ch) && ch != '\uFFFE' && ch != '\uFFFF' && !(ch >= '\uFDD0' && ch <= '\uFDEF')) clean.Append(ch);
            try { k = clean.ToString().Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { k = clean.ToString(); }
        }
        var sb = new StringBuilder(k.Length);
        foreach (char ch in k)
        {
            char c = ch;
            if (c >= '٠' && c <= '٩') c = (char)('0' + (c - '٠'));
            else if (c >= '۰' && c <= '۹') c = (char)('0' + (c - '۰'));
            else if (c == '٫') c = '.';          // Arabic decimal separator
            else if (c == '٬' || c == '،') c = ','; // Arabic thousands separator, Arabic comma
            else if (c == '٪') c = '%';
            else if (c == 'أ' || c == 'إ' || c == 'آ' || c == 'ٱ') c = 'ا'; // alef forms
            else if (c == 'ى') c = 'ي';      // alef maqsura -> yeh
            else if (c == 'ة') c = 'ه';      // teh marbuta -> heh
            else if (c == 'ـ') continue;          // tatweel
            else if (c >= 'ً' && c <= 'ْ') continue; // harakat
            else if (c == ' ') c = ' ';
            else if (lower) c = char.ToLowerInvariant(c);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Reverses a string by UTF-16 unit (enough for Arabic and Latin; no surrogates expected on receipts).</summary>
    public static string Reverse(string s)
    {
        var a = s.ToCharArray();
        Array.Reverse(a);
        return new string(a);
    }

    public static bool IsArabicLetter(char c) =>
        (c >= 'ء' && c <= 'ي') || (c >= 'ﹰ' && c <= 'ﻼ') || (c >= 'ﭐ' && c <= '﷿');
}
