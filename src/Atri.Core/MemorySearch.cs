using System.Text;
using System.Text.RegularExpressions;

namespace Atri.Core;

// A deliberately small, inspectable offline semantic projection. It does not claim
// the general language understanding of a trained embedding model.
internal static partial class MemorySearch
{
    private static readonly string[][] Concepts =
    [
        ["coffee", "cafe", "咖啡", "コーヒー", "珈琲"], ["tea", "茶", "お茶", "紅茶", "红茶"],
        ["birthday", "生日", "誕生日"], ["name", "名字", "姓名", "名前", "称呼", "呼び方"],
        ["like", "love", "喜欢", "喜歡", "喜爱", "偏好", "好み", "好き", "気に入"],
        ["dislike", "hate", "讨厌", "討厭", "苦手", "嫌い"], ["allergy", "过敏", "過敏", "アレルギー"],
        ["cat", "猫", "ねこ", "ネコ"], ["dog", "狗", "犬", "いぬ", "イヌ"],
        ["work", "工作", "仕事", "職場", "公司", "会社"], ["school", "学校", "學校", "大学", "大學", "勉強"],
        ["sleep", "睡觉", "睡眠", "失眠", "眠", "寝る"], ["food", "食物", "食べ物", "料理", "吃饭", "ご飯"],
        ["music", "音乐", "音樂", "音楽", "歌"], ["game", "游戏", "遊戲", "ゲーム"],
        ["sad", "难过", "難過", "悲しい", "寂しい", "失落"], ["happy", "开心", "開心", "嬉しい", "楽しい"],
        ["plan", "计划", "計画", "約束", "约定", "予定"], ["family", "家庭", "家人", "家族"],
        ["friend", "朋友", "友達", "友人"], ["sea", "海", "海洋", "うみ"],
        ["read", "reading", "阅读", "閱讀", "読書", "本を読む"], ["travel", "旅行", "旅游", "旅遊"],
        ["health", "健康", "体調", "身体", "身體"], ["timezone", "时区", "時区", "タイムゾーン"],
        ["address", "住址", "住处", "住所", "住んで"], ["pet", "宠物", "寵物", "ペット"],
        ["vegetarian", "素食", "ベジタリアン"], ["weather", "天气", "天氣", "天気"]
    ];

    public static string Normalize(string text) => text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();

    public static Dictionary<string, double> Features(string text)
    {
        string normalized = Normalize(text);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string key, double weight) => result[key] = result.GetValueOrDefault(key) + weight;
        foreach (Match match in WordPattern().Matches(normalized)) Add("w:" + match.Value, 1);
        for (int i = 0; i < normalized.Length; i++)
        {
            if (!IsCjk(normalized[i])) continue;
            Add("c:" + normalized[i], .15);
            if (i + 1 < normalized.Length && IsCjk(normalized[i + 1])) Add("b:" + normalized.Substring(i, 2), .8);
        }
        for (int i = 0; i < Concepts.Length; i++)
            if (Concepts[i].Any(term => ContainsTerm(normalized, term))) Add("concept:" + i, 2.5);
        return result;
    }

    private static bool ContainsTerm(string text, string term)
    {
        if (term.All(c => c is >= 'a' and <= 'z'))
            return Regex.IsMatch(text, @"\b" + Regex.Escape(term) + @"\b", RegexOptions.CultureInvariant);
        return text.Contains(term, StringComparison.Ordinal);
    }

    public static double Similarity(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        double left = a.Values.Sum(v => v * v), right = b.Values.Sum(v => v * v);
        if (left == 0 || right == 0) return 0;
        return a.Sum(pair => pair.Value * b.GetValueOrDefault(pair.Key)) / Math.Sqrt(left * right);
    }

    private static bool IsCjk(char c) => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';
    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)] private static partial Regex WordPattern();
}
