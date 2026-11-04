using System.Globalization;
using System.Text;

namespace WinUse.Core.Grounding;

/// <summary>
/// Ranks candidates against an intent by text alone, for when no decision model can be asked: label, value and role
/// are compared with the intent on shared words (and character bigrams for scripts without word spaces, so a Japanese
/// intent matches a Japanese label). The score is 1 for an exact label, high for a label contained in the intent, and
/// 0 when nothing is shared.
/// </summary>
public static class LexicalRanker
{
    /// <summary>Role words an intent may use for a control type (lower case).</summary>
    static readonly Dictionary<string, string[]> RoleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Button"] = ["button", "btn", "ボタン"],
        ["SplitButton"] = ["button", "ボタン"],
        ["Edit"] = ["field", "box", "input", "textbox", "text", "edit", "入力", "欄", "フィールド", "テキスト"],
        ["Document"] = ["document", "editor", "page", "ドキュメント", "エディタ", "ページ"],
        ["ComboBox"] = ["combo", "dropdown", "select", "list", "コンボ", "ドロップダウン", "選択", "リスト"],
        ["CheckBox"] = ["checkbox", "check", "チェック", "チェックボックス"],
        ["RadioButton"] = ["radio", "option", "ラジオ", "オプション"],
        ["MenuItem"] = ["menu", "メニュー"],
        ["Menu"] = ["menu", "メニュー"],
        ["MenuBar"] = ["menu", "メニュー"],
        ["TabItem"] = ["tab", "タブ"],
        ["Tab"] = ["tab", "タブ"],
        ["ListItem"] = ["item", "row", "entry", "項目", "行"],
        ["List"] = ["list", "リスト", "一覧"],
        ["TreeItem"] = ["node", "item", "ノード", "項目"],
        ["Tree"] = ["tree", "ツリー"],
        ["Hyperlink"] = ["link", "リンク"],
        ["Slider"] = ["slider", "スライダー"],
        ["Spinner"] = ["spinner", "スピナー"],
        ["ToolBar"] = ["toolbar", "ツールバー"],
        ["Window"] = ["window", "dialog", "ウィンドウ", "ダイアログ"],
        ["textbox"] = ["field", "box", "input", "textbox", "text", "入力", "欄", "テキスト"],
        ["button"] = ["button", "btn", "ボタン"],
        ["link"] = ["link", "リンク"],
        ["checkbox"] = ["checkbox", "check", "チェック"],
        ["combobox"] = ["combo", "dropdown", "select", "コンボ", "ドロップダウン", "選択"],
    };

    const double RoleBonus = 0.1;

    /// <summary>The candidates ordered by descending score (ties keep their order), each with its score in [0, 1].</summary>
    public static IReadOnlyList<(Candidate Candidate, double Probability)> Rank(string intent, IReadOnlyList<Candidate> candidates, int topK)
    {
        var intentTokens = Tokens(intent);
        var normIntent = Normalize(intent);
        return candidates
            .Select((c, i) => (Candidate: c, Score: Score(c, normIntent, intentTokens), Index: i))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .Take(topK)
            .Select(x => (x.Candidate, Math.Round(x.Score, 3)))
            .ToList();
    }

    /// <summary>Text-match score of one candidate against the intent, in [0, 1].</summary>
    public static double Score(Candidate c, string intent)
    {
        return Score(c, Normalize(intent), Tokens(intent));
    }

    static double Score(Candidate c, string normIntent, HashSet<string> intentTokens)
    {
        if (intentTokens.Count == 0) return 0;
        var best = 0.0;
        foreach (var text in new[] { c.Label, c.Value })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            best = Math.Max(best, TextScore(text, normIntent, intentTokens));
        }
        if (best <= 0) return 0;
        // The role word in the intent ("the Save button") is a weak confirmation, never a match on its own.
        if (RoleWords.TryGetValue(c.Role, out var words) && words.Any(w => intentTokens.Contains(w) || normIntent.Contains(w, StringComparison.Ordinal)))
            best = Math.Min(1, best + RoleBonus);
        return best;
    }

    static double TextScore(string text, string normIntent, HashSet<string> intentTokens)
    {
        var norm = Normalize(text);
        if (norm.Length == 0) return 0;
        if (norm == normIntent) return 1;
        // The whole label appears in the intent ("click Save As" vs label "Save As"), or the intent is one word of a longer label.
        if (norm.Length >= 2 && normIntent.Contains(norm, StringComparison.Ordinal)) return 0.9;
        var tokens = Tokens(text);
        if (tokens.Count == 0) return 0;
        var hits = tokens.Count(intentTokens.Contains);
        if (hits == 0) return 0;
        // Mostly how much of the label the intent covers; a little for how much of the intent the label explains.
        var labelCoverage = (double)hits / tokens.Count;
        var intentCoverage = (double)hits / intentTokens.Count;
        return Math.Min(0.85, 0.7 * labelCoverage + 0.15 * intentCoverage);
    }

    /// <summary>Lower case, Unicode-normalized (full-width letters become ASCII), whitespace collapsed, punctuation trimmed at both ends ("Open File..." / "Folder:").</summary>
    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        var trimmed = sb.ToString();
        var start = 0;
        var end = trimmed.Length;
        while (start < end && !char.IsLetterOrDigit(trimmed[start])) start++;
        while (end > start && !char.IsLetterOrDigit(trimmed[end - 1])) end--;
        return trimmed[start..end];
    }

    /// <summary>Words (letters and digits) plus character bigrams of every run of CJK characters, which carry no word spaces.</summary>
    static HashSet<string> Tokens(string s)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        var cjk = new StringBuilder();
        void FlushWord()
        {
            if (word.Length > 0) result.Add(word.ToString());
            word.Clear();
        }
        void FlushCjk()
        {
            if (cjk.Length == 1) result.Add(cjk.ToString());
            for (var i = 0; i + 1 < cjk.Length; i++) result.Add(cjk.ToString(i, 2));
            cjk.Clear();
        }
        foreach (var ch in Normalize(s))
        {
            if (IsCjk(ch)) { FlushWord(); cjk.Append(ch); }
            else if (char.IsLetterOrDigit(ch)) { FlushCjk(); word.Append(ch); }
            else { FlushWord(); FlushCjk(); }
        }
        FlushWord();
        FlushCjk();
        return result;
    }

    static bool IsCjk(char ch)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
        if (cat != UnicodeCategory.OtherLetter && cat != UnicodeCategory.ModifierLetter) return false;
        // Hiragana, Katakana, CJK ideographs, Hangul: scripts written without spaces between words.
        return ch is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= 'ｦ' and <= 'ﾟ';
    }
}
