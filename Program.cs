using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FB2AccentConverter.Parser;

namespace FB2AccentConverter;

internal static class Program
{
    // Windows-1251 нужна для чтения и записи TXT без BOM, но в .NET она доступна
    // только через провайдер кодовых страниц. Без регистрации Encoding.GetEncoding(1251)
    // бросает «No data is available for encoding 1251». Статический конструктор
    // выполняется раньше Main, поэтому провайдер доступен и в CLI, и в окне.
    static Program() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // RUAccent puts '+' *before* the stressed vowel ("и+юньским"). DSR, like the Silero
    // engine, the manual editor and the AI hints, keeps the combining acute U+0301 *after*
    // the vowel ("ию́ньским"). The marked vowel is captured so the mark can be moved to the
    // other side of it: replacing '+' in place would leave the acute attached to the
    // preceding letter, and the comparison would then read the stress off the wrong vowel.
    private static readonly System.Text.RegularExpressions.Regex RuAccentMarker = new(
        "\\+(?<vowel>[а-яёА-ЯЁ])", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly object LogLock = new();
    private static readonly System.Text.RegularExpressions.Regex PunctuationTokens = new(
        @"[\p{L}\p{M}\p{N}_]+|[^\p{L}\p{M}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Safety net (C# side of the ruaccent worker protection). Even if the Python
    // worker leaves a stray combining acute mark, these rules guarantee that:
    //  • a combining accent never stays adjacent to the letter ё (ё is always stressed);
    //  • a short function word (preposition/conjunction/particle) never carries stress.
    // Keep this list identical to _UNSTRESSED_WORDS in ruaccent_worker.py: the two
    // layers must agree, otherwise one of them silently stops protecting a word.
    private static readonly HashSet<string> UnstressedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Prepositions
        "о", "об", "обо", "в", "во", "с", "со", "к", "ко", "у", "от", "до", "из",
        "без", "для", "про", "над", "под", "при", "на", "за",
        // Conjunctions and particles
        "и", "а", "но", "да", "ли", "бы", "же", "ль", "то",
    };
    // Подробный журнал правок: помогает разобрать, почему конкретная книга не применилась.
    internal static string ReviewLogPath => Path.Combine(DataDirectory, "DSR-правила.log");

    internal static void LogReview(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            lock (LogLock)
                File.AppendAllText(ReviewLogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    private static readonly System.Text.RegularExpressions.Regex WordTokens = new(
        @"[\p{L}\p{M}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex NotCyrillicWord = new(
        @"^[^а-яёА-ЯЁ]+$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex RussianVowel = new(
        "[аеёиоуыэюя]", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static string LogFilePath => Path.Combine(DataDirectory, "DSR-errors.txt");
    internal static string UserDictionaryPath => Path.Combine(DataDirectory, "пользовательские-ударения.tsv");
    internal static string BuiltInStressDictionaryPath => Path.Combine(DictionaryDirectory, "литературные-ударения.txt");
    internal static string YoDictionaryPath => Path.Combine(DataDirectory, "dictionary", "ё.txt");
    internal static string TrainingLogPath => Path.Combine(DataDirectory, "накопленные-примеры.jsonl");
    internal static string CorrectionsFor(string reviewPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reviewPath))!, Path.GetFileNameWithoutExtension(reviewPath).Replace("_проверка-слов", "_исправления") + ".txt");
    internal static string ReviewPathFor(string inputPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath))!, Path.GetFileNameWithoutExtension(inputPath) + "_проверка-слов.txt");
    // ConcurrentDictionary: the user dictionary is read on the background conversion
    // thread and written from the UI thread when corrections are saved, so a plain
    // Dictionary could be corrupted by a concurrent read and write.
    private static readonly Lazy<ConcurrentDictionary<string, string>> UserDictionary = new(LoadUserDictionary);
private static readonly Lazy<Dictionary<string, string>> BuiltInStressDictionary = new(LoadBuiltInStressDictionary);
    private static readonly Lazy<Dictionary<string, string>> YoDictionary = new(LoadYoDictionary);
    private static readonly Lazy<Dictionary<string, string>> SupplementaryStressDictionary = new(LoadSupplementaryStressDictionary);

    internal static string SupplementaryStressDictionaryPath => Path.Combine(DataDirectory, "dictionary", "\u0443\u0434\u0430\u0440\u0435\u043D\u0438\u044F-\u0437\u0430\u0440\u0432\u0430.txt");

    // ======================= Энклиномены =======================
    // Слова, у которых ударение переходит на предлог: «на́ пол», «по́ полу», «за́ руку».
    // Признак, который отличает «на́ пол» от «на по́л вагона», назван правилом: именная
    // группа состоит из одного слова — перед существительным нет определения, а сразу за
    // ним нет зависимого слова. Предлоги и существительные при этом перечислимы, поэтому
    // анализатор не разбирает предложение целиком, а проверяет закрытые списки и соседей.

    /// <summary>Список словоформ-энклиноменов, поставляемый со сборкой (папка dictionary).</summary>
    internal static string EnclinomenaPath => Path.Combine(DataDirectory, "dictionary", "энклиномены.txt");

    /// <summary>Дополнения пользователя: читаются вместе с основным списком.</summary>
    internal static string UserEnclinomenaPath => Path.Combine(DataDirectory, "энклиномены.txt");

    private static readonly Lazy<Dictionary<string, EnclinomenaForm>> EnclinomenaForms = new(LoadEnclinomenaForms);

    /// <summary>Словоформа-энклиномен: лемма и падежи, в которых она ходит с предлогом.</summary>
    private sealed class EnclinomenaForm
    {
        public EnclinomenaForm(string lemma) => Lemma = lemma;

        public string Lemma { get; }

        public List<string> Cases { get; } = new();
    }

    // Предлоги, которые умеют уводить ударение на себя. У каждого падежа свой набор:
    // «на́ пол» (винительный), «по́ полу» (дательный), «и́з лесу» (частичный), «до́ земли»
    // (родительный — только там, где частичной формы нет), «за́ городом» (творительный).
    private static readonly Dictionary<string, string[]> EnclinomenaCasePrepositions = new(StringComparer.Ordinal)
    {
        ["В"] = new[] { "на", "за", "под", "о", "об", "обо", "по" },
        ["Д"] = new[] { "по" },
        ["Р"] = new[] { "до" },
        ["Ч"] = new[] { "по", "до", "из", "от", "без" },
        ["Т"] = new[] { "за", "под" },
        ["М"] = new[] { "по", "до" },
    };

    /// <summary>Все предлоги, которые встречаются в разборе: по ним ищется предлог перед словом.</summary>
    private static readonly HashSet<string> EnclinomenaPrepositions = new(
        EnclinomenaCasePrepositions.Values.SelectMany(cases => cases), StringComparer.Ordinal);

    // Предлоги, с которыми перенос ударения возможен хотя бы в части выражений. Для ручного
    // переноса этого достаточно: решение принимает человек, а не закрытый список словоформ.
    // «с/со» добавлены для оборотов вроде «с го́лоду», «с пе́репугу».
    private static readonly HashSet<string> ManualStressPrepositions = new(EnclinomenaPrepositions, StringComparer.Ordinal)
    {
        "с", "со",
    };

    // Союзы, частицы и предлоги после слова переносу не мешают: к его именной группе
    // они не относятся и только начинают собой новое слово или оборот.
    private static readonly HashSet<string> EnclinomenaNeutralFollowers = new(StringComparer.Ordinal)
    {
        "и", "а", "но", "да", "же", "ли", "бы", "не", "ни", "то", "или", "либо", "что", "чтобы",
        "как", "чем", "когда", "где", "куда", "откуда", "если", "хоть", "хотя", "будто", "словно",
        "точно", "пока", "только", "даже", "уж", "вот", "вон", "ведь", "так",
        "в", "во", "с", "со", "к", "ко", "у", "от", "до", "из", "без", "для", "про", "над", "под",
        "при", "на", "за", "по", "о", "об", "обо", "между", "перед", "через", "сквозь", "около",
        "мимо", "вокруг", "вдоль", "кроме", "возле", "среди", "ради",
        // Подлежащее после именной группы — не зависимое слово: «встал на́ ноги она…».
        "он", "она", "оно", "они", "я", "ты", "мы", "вы",
    };

    // Окончания, по которым следующее слово похоже на зависимое: родительный падеж,
    // согласованное определение, причастие. Тогда ударение остаётся на существительном.
    private static readonly string[] EnclinomenaBlockingEndings =
    {
        "а", "я", "ов", "ев", "ей", "ий", "ого", "его", "ому", "ему", "ыми", "ими",
        "ая", "яя", "ое", "ее", "ые", "ие", "ый", "ой", "ую", "юю", "ою", "ею",
    };

    // Окончания глаголов: слово после существительного — сказуемое, а не зависимое,
    // переносу оно не мешает («упал на́ пол», «год от го́ду холоде́ла кровь»).
    private static readonly string[] EnclinomenaVerbEndings =
    {
        "ть", "ться", "лся", "лась", "лось", "лись", "л", "ла", "ло", "ли",
        "ет", "ит", "ут", "ют", "ат", "ят", "ешь", "ишь", "ем", "им", "ете", "ите",
        "ся", "сь", "в", "вши",
    };

    /// <summary>
    /// Найденное сочетание «предлог + энклиномен»: предлог с ударением и падеж,
    /// в котором стоит слово. Сомнительные случаи (после слова идёт слово, которое
    /// может оказаться зависимым) тоже попадают в разбор, но помечаются флажком.
    /// </summary>
    internal sealed record EnclinomenMatch(string Preposition, string PrepositionStressed, string Lemma,
        string Case, bool Doubtful, string Follower, string BlockReason = "")
    {
        /// <summary>Как сочетание выглядит в тексте: «на́ пол», «по́ полу».</summary>
        public string Display => PrepositionStressed + " " + PlainWord;
        public string PlainWord { get; init; } = string.Empty;
    }

    /// <summary>
    /// Проверяет, уводит ли предлог перед словом ударение на себя. Возвращает null,
    /// если сочетание не найдено или зависимое слово его запрещает.
    /// </summary>
    internal static EnclinomenMatch? TryMatchEnclinomen(string context, string word, int occurrence)
    {
        var candidate = AnalyzeEnclinomen(context, word, occurrence);
        return candidate is { BlockReason.Length: 0 } ? candidate : null;
    }

    /// <summary>
    /// Полный разбор одного вхождения: даже если сочетание отклонено, причина остаётся
    /// в результате — по ней строится диагностическая сводка по книге.
    /// </summary>
    internal static EnclinomenMatch? AnalyzeEnclinomen(string context, string word, int occurrence)
    {
        var key = ReviewWordKey(word);
        if (key.Length == 0 || string.IsNullOrEmpty(context)) return null;
        if (!EnclinomenaForms.Value.TryGetValue(key, out var form)) return null;
        if (!TryFindOccurrencePreposition(context, word, occurrence,
                out var wordIndex, out var wordLength, out var preposition)) return null;
        var prepositionKey = ReviewWordKey(preposition);
        if (!EnclinomenaPrepositions.Contains(prepositionKey)) return null;
        // Падеж формы решает, какой предлог может увести ударение: «по́ полу» можно,
        // а «и́з дома» (родительный) — нет, там переноса не бывает.
        var matchedCase = form.Cases.FirstOrDefault(item => EnclinomenaCasePrepositions.TryGetValue(item, out var prepositions)
            && prepositions.Contains(prepositionKey, StringComparer.Ordinal));
        if (matchedCase == null) return null;
        var follower = NextWordToken(context, wordIndex + wordLength);
        var (verdict, followerText) = JudgeEnclinomenaFollower(follower);
        return new EnclinomenMatch(preposition, StressPreposition(preposition), form.Lemma, matchedCase,
            verdict == EnclinomenaVerdict.Doubtful, followerText, verdict == EnclinomenaVerdict.Blocked ? followerText : string.Empty)
        {
            PlainWord = word,
        };
    }

    // Ручной перенос на предлог. Разбор энклиноменов знает только закрытый список словоформ,
    // поэтому в третье окно можно отправить любое вхождение, перед которым стоит предлог,
    // способный уводить ударение. Это именно ручное решение: в отличие от автоматического
    // разбора оно не проверяет падеж и зависимые слова, поэтому случай помечается вопросом.
    internal static bool TryFindManualPreposition(string context, string word, int occurrence, out string stressedPreposition)
    {
        stressedPreposition = string.Empty;
        if (!TryFindOccurrencePreposition(context, word, occurrence, out _, out _, out var preposition))
            return false;
        if (!ManualStressPrepositions.Contains(ReviewWordKey(preposition))) return false;
        stressedPreposition = StressPreposition(preposition);
        return true;
    }

    // Ищет нужное вхождение слова и слово непосредственно перед ним. Общий код для
    // автоматического разбора энклиноменов и ручного переноса: оба должны смотреть на
    // то же самое место в тексте, иначе знак ударения встанет на чужой предлог.
    private static bool TryFindOccurrencePreposition(string context, string word, int occurrence,
        out int wordIndex, out int wordLength, out string preposition)
    {
        wordIndex = 0;
        wordLength = 0;
        preposition = string.Empty;
        var key = ReviewWordKey(word);
        if (key.Length == 0 || string.IsNullOrEmpty(context)) return false;
        var seen = 0;
        foreach (System.Text.RegularExpressions.Match match in WordTokens.Matches(context))
        {
            if (!ReviewWordKey(match.Value).Equals(key, StringComparison.Ordinal)) continue;
            if (seen++ != occurrence) continue;
            var before = context[..match.Index];
            if (before.Length == 0 || (before[^1] != ' ' && before[^1] != '\t')) return false;
            var end = before.Length;
            while (end > 0 && (before[end - 1] == ' ' || before[end - 1] == '\t')) end--;
            var start = end;
            while (start > 0 && IsWordCharacter(before[start - 1])) start--;
            if (start == end) return false;
            wordIndex = match.Index;
            wordLength = match.Length;
            preposition = before[start..end];
            return true;
        }
        return false;
    }

    private enum EnclinomenaVerdict { Allowed, Doubtful, Blocked }

    /// <summary>
    /// Что стоит сразу за словом. Пунктуация и конец фразы переносу не мешают,
    /// поэтому возвращается пустая строка — как и при отсутствии следующего слова.
    /// </summary>
    private static string NextWordToken(string text, int fromIndex)
    {
        var index = fromIndex;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length || !IsWordCharacter(text[index])) return string.Empty;
        var start = index;
        while (index < text.Length && IsWordCharacter(text[index])) index++;
        return text[start..index];
    }

    private static (EnclinomenaVerdict Verdict, string Follower) JudgeEnclinomenaFollower(string follower)
    {
        if (follower.Length == 0) return (EnclinomenaVerdict.Allowed, string.Empty);
        var key = ReviewWordKey(follower);
        if (EnclinomenaNeutralFollowers.Contains(key)) return (EnclinomenaVerdict.Allowed, follower);
        // Глаголы проверяются раньше имён: «холоде́ла» — это сказуемое, а не зависимое,
        // хотя по окончанию «-ла» его можно принять за родительный падеж.
        if (EnclinomenaVerbEndings.Any(ending => key.EndsWith(ending, StringComparison.Ordinal)))
            return (EnclinomenaVerdict.Allowed, follower);
        if (EnclinomenaBlockingEndings.Any(ending => key.EndsWith(ending, StringComparison.Ordinal)))
            return (EnclinomenaVerdict.Blocked, follower);
        // Слово похоже на имя или наречие: зависимым оно быть может, а может и не быть
        // («шаря по́ полу чи́рики»). Такое сочетание показываем, но помечаем вопросом.
        return (EnclinomenaVerdict.Doubtful, follower);
    }

    /// <summary>
    /// Форма предлога со знаком ударения для записи в текст. Готовая форма от разбора энклиноменов
    /// подходит, только если знак стоит на её единственной гласной («на́», «по́»); иначе он
    /// ставится по правилу приложения — на последний гласный: «чере́з», а не «че́рез».
    /// </summary>
    internal static string StressPreposition(string stressedPreposition)
    {
        if (string.IsNullOrWhiteSpace(stressedPreposition)) return stressedPreposition;
        var bare = stressedPreposition.Replace("\u0301", "");
        var vowels = new List<int>();
        for (var i = 0; i < bare.Length; i++)
            if (RussianVowel.IsMatch(bare[i].ToString())) vowels.Add(i);
        if (vowels.Count == 0) return bare;
        var expected = bare.Insert(vowels[^1] + 1, "\u0301");
        // Односложный предлог: положение знака проверять не на чем — годится и готовое.
        // Но если знака в нём ещё нет (отчёт хранит «по»), его надо поставить: иначе
        // третье окно и заголовок «На предлог» остаются без ударения.
        return vowels.Count == 1 && stressedPreposition.Contains('\u0301')
            ? stressedPreposition
            : expected;
    }

    /// <summary>
    /// Для показа случая: в тексте слева от слова предлог получает знак ударения — «по» в
    /// «…шаря по полу» превращается в «по́». Если предлога перед словом нет, текст не меняется.
    /// </summary>
    internal static string MarkPrecedingPreposition(string before, string stressedPreposition)
    {
        if (before.Length == 0 || string.IsNullOrEmpty(stressedPreposition)) return before;
        var end = before.Length;
        while (end > 0 && (before[end - 1] == ' ' || before[end - 1] == '\t')) end--;
        var start = end;
        while (start > 0 && IsWordCharacter(before[start - 1])) start--;
        if (start == end) return before;
        var word = before[start..end];
        if (!ReviewWordKey(word).Equals(ReviewWordKey(stressedPreposition), StringComparison.Ordinal)) return before;
        return string.Concat(before.AsSpan(0, start), KeepFirstLetterCase(word, stressedPreposition), before.AsSpan(end));
    }

    /// <summary>
    /// Словоформы-энклиномены: «форма <таб> лемма <таб> падеж». Со сборкой идёт основной
    /// список, в папке ресурсов — дополнения пользователя; свои строки важнее поставляемых.
    /// </summary>
    private static Dictionary<string, EnclinomenaForm> LoadEnclinomenaForms()
    {
        EnsureBundledData();
        var forms = new Dictionary<string, EnclinomenaForm>(StringComparer.Ordinal);
        foreach (var path in new[] { EnclinomenaPath, UserEnclinomenaPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var trimmed = line.Trim().TrimStart('\uFEFF');
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    var parts = trimmed.Split('\t');
                    if (parts.Length < 3) continue;
                    var form = ReviewWordKey(parts[0]);
                    if (form.Length == 0) continue;
                    // У одного написания бывает несколько падежей («лесу» — дательный и
                    // частичный), поэтому падежи копятся, а не заменяют друг друга.
                    if (!forms.TryGetValue(form, out var known))
                    {
                        known = new EnclinomenaForm(parts[1]);
                        forms[form] = known;
                    }
                    if (!known.Cases.Contains(parts[2], StringComparer.Ordinal)) known.Cases.Add(parts[2]);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return forms;
    }

    /// <summary>
    /// Файл сохранённых словоформ всегда лежит рядом с отчётом расхождений: имя
    /// отчёта без хвоста «_расхождения» плюс «_словоформы.json».
    /// </summary>
    internal static string FormCorrectionsPathFor(string reviewPath)
    {
        var full = Path.GetFullPath(reviewPath);
        var directory = Path.GetDirectoryName(full)!;
        var baseName = Path.GetFileNameWithoutExtension(full);
        const string marker = "_расхождения";
        var formsBase = baseName.EndsWith(marker, StringComparison.OrdinalIgnoreCase)
            ? baseName[..^marker.Length] + "_словоформы"
            : baseName + "_словоформы";
        return Path.Combine(directory, formsBase + ".json");
    }

    /// <summary>
    /// Файл свободных правок фрагментов лежит рядом с отчётом: имя отчёта без хвоста
    /// «_расхождения» плюс «_фрагменты.json».
    /// </summary>
    internal static string FragmentEditsPathFor(string reviewPath)
    {
        var full = Path.GetFullPath(reviewPath);
        var directory = Path.GetDirectoryName(full)!;
        var baseName = Path.GetFileNameWithoutExtension(full);
        const string marker = "_расхождения";
        var formsBase = baseName.EndsWith(marker, StringComparison.OrdinalIgnoreCase)
            ? baseName[..^marker.Length] + "_фрагменты"
            : baseName + "_фрагменты";
        return Path.Combine(directory, formsBase + ".json");
    }

    internal static List<WordReviewFragmentEdit> ReadFragmentEdits(string? reviewPath)
    {
        if (string.IsNullOrWhiteSpace(reviewPath)) return new List<WordReviewFragmentEdit>();
        var path = FragmentEditsPathFor(reviewPath);
        if (!File.Exists(path)) return new List<WordReviewFragmentEdit>();
        try
        {
            return JsonSerializer.Deserialize<List<WordReviewFragmentEdit>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<WordReviewFragmentEdit>();
        }
        catch
        {
            return new List<WordReviewFragmentEdit>();
        }
    }

    internal static void SaveFragmentEdits(string reviewPath, IEnumerable<WordReviewFragmentEdit> edits)
    {
        var path = FragmentEditsPathFor(reviewPath);
        var list = edits.Where(item => !string.IsNullOrWhiteSpace(item.Edited)).ToList();
        if (list.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
    }

    /// <summary>Отчёт со статистикой конвертации, который появляется рядом с результатом.</summary>
    internal static string StatisticsPathFor(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, Path.GetFileNameWithoutExtension(outputPath) + "_статистика.txt");

    /// <summary>
    /// Рабочая папка одного документа. Всё, что создаёт DSR, лежит рядом с исходником
    /// в папке «DSR_&lt;имя книги&gt;»: отдельный прогон RUAccent, отдельный прогон Silero
    /// со своей статистикой, отчёт расхождений, словоформы и итоговый файл. Исходная
    /// книга не изменяется никогда, и это видно по расположению файлов.
    /// </summary>
    internal sealed class BookWorkspace
    {
        internal BookWorkspace(string sourcePath)
        {
            SourcePath = Path.GetFullPath(sourcePath);
            SourceDirectory = Path.GetDirectoryName(SourcePath)!;
            Name = Path.GetFileNameWithoutExtension(SourcePath);
            Extension = Path.GetExtension(SourcePath);
            Root = Path.Combine(SourceDirectory, "DSR_" + Name);
            RuAccentDirectory = Path.Combine(Root, "RUAccent");
            SileroDirectory = Path.Combine(Root, "Silero");
            BaseDirectory = Path.Combine(Root, "add-stress");
        }

        internal string SourcePath { get; }
        internal string SourceDirectory { get; }
        internal string Name { get; }
        internal string Extension { get; }
        internal string Root { get; }
        internal string RuAccentDirectory { get; }
        internal string SileroDirectory { get; }
        internal string BaseDirectory { get; }

        /// <summary>
        /// База поэтапного конвейера: результат add-stress-to-epub по исходной книге.
        /// Метки базы неприкосновенны — RUAccent и Silero заполняют только слова,
        /// которые база оставила без ударения и без буквы ё.
        /// </summary>
        internal string BaseBook => Path.Combine(BaseDirectory, Name + "_база" + Extension);
        /// <summary>Прогон RUAccent по базе (нужен как второй голос, не как итог).</summary>
        internal string StagedRuAccentBook => Path.Combine(RuAccentDirectory, Name + "_ruaccent_по_базе" + Extension);
        /// <summary>Прогон Silero по базе.</summary>
        internal string StagedSileroBook => Path.Combine(SileroDirectory, Name + "_silero_по_базе" + Extension);
        /// <summary>Промежуточный файл «база + совпадения»: редактируемая основа для ручной доводки.</summary>
        internal string StagedAppliedBook => Path.Combine(BaseDirectory, Name + "_база_совпадения" + Extension);

        /// <summary>Результат RUAccent со всеми исправлениями на границах и из словаря.</summary>
        internal string RuAccentBook => Path.Combine(RuAccentDirectory, Name + "_ruaccent" + Extension);
        /// <summary>Слова, которые RUAccent оставил без ударения.</summary>
        internal string RuAccentReview => Path.Combine(RuAccentDirectory, Name + "_ruaccent_нераспознанные.txt");
        internal string RuAccentStatistics => Path.Combine(RuAccentDirectory, Name + "_ruaccent_статистика.txt");

        /// <summary>Отдельный прогон Silero по исходной книге.</summary>
        internal string SileroBook => Path.Combine(SileroDirectory, Name + "_silero" + Extension);
        internal string SileroReview => Path.Combine(SileroDirectory, Name + "_silero_нераспознанные.txt");
        internal string SileroStatistics => Path.Combine(SileroDirectory, Name + "_silero_статистика.txt");

        /// <summary>Расхождения RUAccent и Silero: с ними работают ИИ-проход и ручная доводка.</summary>
        internal string ComparisonReport => Path.Combine(Root, Name + "_расхождения.txt");
        internal string ComparisonStatistics => Path.Combine(Root, Name + "_расхождения_статистика.txt");
        /// <summary>Словоформы и прочие принятые правки для повторного применения.</summary>
        internal string FormsPath => Path.Combine(Root, Name + "_словоформы.json");
        /// <summary>Итог после ручной доводки. Исходник и прогоны движков не перезаписываются.</summary>
        internal string CorrectedBook => Path.Combine(Root, Name + "_итог" + Extension);

        internal void EnsureDirectories()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(RuAccentDirectory);
            Directory.CreateDirectory(SileroDirectory);
            Directory.CreateDirectory(BaseDirectory);
        }
    }

    internal static BookWorkspace WorkspaceFor(string sourcePath) => new(sourcePath);

    /// <summary>Корень рабочей папки («DSR_…») по любому файлу внутри неё — включая отчёт.</summary>
    internal static string? WorkspaceRootOf(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(directory))
            {
                if (Path.GetFileName(directory).StartsWith("DSR_", StringComparison.Ordinal)) return directory;
                var parent = Path.GetDirectoryName(directory);
                if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)) break;
                directory = parent;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// Итоговый файл после ручной доводки. Внутри рабочей папки — «&lt;имя&gt;_итог»,
    /// иначе рядом с книгой добавляется привычный «_corrected».
    /// </summary>
    internal static string CorrectedPathFor(string sourceBookPath)
    {
        var full = Path.GetFullPath(sourceBookPath);
        if (WorkspaceRootOf(full) is { } root)
        {
            var name = Path.GetFileNameWithoutExtension(full);
            // Файл уже итоговый — повторное применение правок обновляет его же, а не плодит «_итог_итог».
            if (name.EndsWith("_итог", StringComparison.OrdinalIgnoreCase)) return full;
            // Итог собирается из любого прогона/базы: «имя_база», «имя_ruaccent» и т.п.
            // отдают одно и то же имя «имя_итог», чтобы не плодить «имя_база_итог».
            var stem = name;
            foreach (var suffix in new[] { "_база", "_ruaccent", "_silero", "_add-stress" })
            {
                var marker = stem.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (marker > 0 && marker < stem.Length) { stem = stem[..marker]; break; }
            }
            return Path.Combine(root, stem + "_итог" + Path.GetExtension(full));
        }
        var directory = Path.GetDirectoryName(full)!;
        var baseName = Path.GetFileNameWithoutExtension(full);
        return baseName.EndsWith("_corrected", StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(directory, baseName + "_corrected" + Path.GetExtension(full));
    }

    // The actual accenting runs in the child python.exe worker; the window process
    // itself mostly waits on it and uses almost no CPU. The load indicators therefore
    // sum the CPU and memory of the whole process tree (the window plus every child,
    // including the Python worker), otherwise they would show the near-idle window as 0%.
    private const uint Th32CsSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // CPU времени процесса накапливается как сумма приростов по каждому PID, а не как
    // абсолютная сумма дерева: при снимке дерева короткоживущий дочерний процесс
    // (python-воркер) мог завершиться между двумя замерами, и тогда его вклад
    // полностью пропадал из показаний индикатора.
    private static readonly Dictionary<int, double> ProcessCpuBaseline = new();
    private static readonly object ProcessCpuLock = new();
    private static double ProcessCpuAccumulatedMilliseconds;

    internal static (double CpuMilliseconds, long WorkingSetBytes) CurrentProcessTreeUsage()
    {
        var samples = new List<(int Id, double CpuMilliseconds, long WorkingSetBytes)>();
        using (var self = Process.GetCurrentProcess())
        {
            AddProcessUsage(self, samples);
            var children = ReadChildProcessIds();
            var rootId = self.Id;
            var pending = new Queue<int>();
            var visited = new HashSet<int> { rootId };
            pending.Enqueue(rootId);
            while (pending.Count > 0)
            {
                if (!children.TryGetValue(pending.Dequeue(), out var childIds)) continue;
                foreach (var childId in childIds)
                {
                    if (!visited.Add(childId)) continue;
                    pending.Enqueue(childId);
                    try
                    {
                        using var child = Process.GetProcessById(childId);
                        AddProcessUsage(child, samples);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        long workingSetBytes = 0;
        lock (ProcessCpuLock)
        {
            var seen = new HashSet<int>();
            foreach (var sample in samples)
            {
                seen.Add(sample.Id);
                var delta = ProcessCpuBaseline.TryGetValue(sample.Id, out var last)
                    ? sample.CpuMilliseconds - last
                    : sample.CpuMilliseconds;
                if (delta > 0) ProcessCpuAccumulatedMilliseconds += delta;
                ProcessCpuBaseline[sample.Id] = sample.CpuMilliseconds;
                workingSetBytes += sample.WorkingSetBytes;
            }
            foreach (var staleId in ProcessCpuBaseline.Keys.Where(id => !seen.Contains(id)).ToList())
                ProcessCpuBaseline.Remove(staleId);
        }
        return (ProcessCpuAccumulatedMilliseconds, workingSetBytes);
    }

    private static Dictionary<int, List<int>> ReadChildProcessIds()
    {
        var children = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return children;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return children;
            do
            {
                var parentId = (int)entry.th32ParentProcessID;
                if (!children.TryGetValue(parentId, out var list))
                    children[parentId] = list = new List<int>();
                list.Add((int)entry.th32ProcessID);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return children;
    }

    private static void AddProcessUsage(Process process, List<(int Id, double CpuMilliseconds, long WorkingSetBytes)> samples)
    {
        try
        {
            samples.Add((process.Id, process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64));
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (ArgumentException) { }
    }

    internal static void LogError(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            lock (LogLock)
                File.AppendAllText(LogFilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}",
                    new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    internal sealed class ConversionControl
    {
        private readonly ManualResetEventSlim _resume = new(true);
        private int _stopRequested;
        internal bool IsPaused => !_resume.IsSet && !IsStopped;
        internal bool IsStopped => Volatile.Read(ref _stopRequested) != 0;
        internal void Pause() => _resume.Reset();
        internal void Resume() => _resume.Set();
        internal void Stop() { Interlocked.Exchange(ref _stopRequested, 1); _resume.Set(); }
        internal void Checkpoint()
        {
            _resume.Wait();
            if (IsStopped) throw new OperationCanceledException("Обработка остановлена пользователем.");
        }
    }

    internal sealed class ConversionDiagnostics
    {
        internal string Input { get; init; } = "";
        internal string Output { get; init; } = "";
        internal string Format { get; init; } = "";
        internal string Model { get; init; } = "";
        internal string Device { get; init; } = "";
        internal long InputBytes { get; set; }
        internal long OutputBytes { get; set; }
        internal long CharacterCount { get; set; }
        internal int TotalUnits { get; set; }
        internal int CompletedUnits { get; set; }
        internal int FailedUnits { get; set; }
        internal int UnrecognizedWordCount { get; set; }
        internal int DictionaryStressCount { get; set; }
        // Итоги сравнения RUAccent и Silero: три категории, на которые делится каждое
        // многосложное слово после обоих прогонов. Совпало — слово проходит; расхождение
        // (в том числе когда одна модель промолчала) — идёт в доводку; не распознали обе —
        // отдельный список «нераспознанных».
        internal int ComparedWordCount { get; set; }
        internal int MatchedWordCount { get; set; }
        internal int DisagreementWordCount { get; set; }
        internal int BothUnrecognizedWordCount { get; set; }
        internal long ImagePayloadCharacterCount { get; set; }
        internal double CpuPercent { get; set; }
        internal long PeakWorkingSetBytes { get; set; }
        internal TimeSpan InferenceElapsed { get; set; }
        internal long InferenceCalls { get; set; }
        internal List<string> PerformanceEvents { get; } = new();
        internal TimeSpan Elapsed { get; set; }
        internal string Status { get; set; } = "Выполняется";
        internal List<string> Events { get; } = new();
    }

    internal static void LogConversion(string input, long bytes, long characters, TimeSpan elapsed, TimeSpan? estimatedRemaining, string status)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {status}: {input} | {bytes / 1024d / 1024d:0.00} МБ | {characters:N0} символов | обработка {elapsed:hh\\:mm\\:ss} | осталось {(estimatedRemaining.HasValue ? estimatedRemaining.Value.ToString("hh\\:mm\\:ss") : "н/д")}{Environment.NewLine}";
            lock (LogLock) File.AppendAllText(Path.Combine(DataDirectory, "DSR-processing.log"), line, new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    internal static void LogError(string context, string details)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            lock (LogLock)
                File.AppendAllText(LogFilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{details}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}",
                    new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    private static readonly HashSet<string> TextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "v", "subtitle", "text-author", "date", "book-title", "first-name", "middle-name", "last-name",
        "nickname", "home-page", "email", "genre", "sequence", "annotation", "poem", "stanza", "cite",
        "epigraph", "empty-line", "thesis", "src-title", "match"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // Some Windows shells have redirected output handles that reject encoding changes.
        }
        try
        {
            if (args.Length == 0)
            {
                try
                {
                    var application = new System.Windows.Application();
                    application.Run(new MainWindow());
                    return 0;
                }
                catch (Exception ex)
                {
                    LogError("Ошибка запуска интерфейса", ex);
                    System.Windows.MessageBox.Show(ex.Message, "Ошибка запуска DSR", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return 1;
                }
            }

            // Скрытый ключ проверки: применяет правки отчёта к книге теми же функциями, что и
            // кнопка «Применить все правки». Нужен, чтобы проверить запись без запуска окна.

            // Устройство движков подсказок на PyTorch: то же, что выбор во второй вкладке.
            // Сохраняется сразу, поэтому действует и на остальные команды этого запуска.
            for (var index = 0; index + 1 < args.Length; index++)
            {
                if (args[index] != "--llm-device") continue;
                var value = args[index + 1].ToUpperInvariant();
                if (value is not ("CPU" or "CUDA"))
                    throw new ArgumentException("--llm-device должен быть CPU или CUDA.");
                SaveLlmDevice(value);
                Console.WriteLine($"Устройство движков подсказок: {value}.");
            }

            if (args.Length >= 2 && args[0] == "--review")
            {
                // Прямой запуск доработки: удобно, когда книга уже сконвертирована.
                // Открывается вкладка «2. Расхождения и ИИ» с подставленными файлами.
                try
                {
                    var book = args.Length >= 3 ? args[2] : null;
                    var application = new System.Windows.Application();
                    application.Run(new MainWindow(args[1], book));
                    return 0;
                }
                catch (Exception ex)
                {
                    LogError("Ошибка запуска окна проверки", ex);
                    System.Windows.MessageBox.Show(ex.Message, "Ошибка запуска окна проверки",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return 1;
                }
            }

            if (args.Length == 1 && args[0] == "--probe-gpu")
            {
                var gpu = ProbeGpu();
                Console.WriteLine(gpu.Message);
                return gpu.Supported ? 0 : 1;
            }

            // Та же проверка, что у кнопки «Проверить ресурсы»: ничего не скачивает,
            // только распаковывает встроенные словари и сообщает, чего не хватает.
            if (args[0] == "--check-resources" && args.Length <= 2)
            {
                var model = args.Length == 2 ? args[1] : "big_poetry";
                var report = VerifyResources(model);
                Console.WriteLine(report);
                return report.StartsWith("Ресурсы в порядке", StringComparison.Ordinal) ? 0 : 1;
            }

            // Устройство движков подсказок на PyTorch: посмотреть, поставить сборку с CUDA
            // или вернуть сборку для процессора. То же самое делают кнопки во второй вкладке.
            if (args.Length == 1 && args[0] == "--torch-status")
            {
                var torch = ProbeTorchCuda();
                Console.WriteLine(torch.Message);
                return torch.Available ? 0 : 1;
            }

            if (args.Length == 1 && (args[0] == "--torch-cuda" || args[0] == "--torch-cpu"))
            {
                var install = args[0] == "--torch-cuda";
                // Загрузка сборки с CUDA измеряется гигабайтами, поэтому этапы показываем
                // и в командной строке — иначе несколько минут проходит без единой строки.
                SetupProgress = message => Console.WriteLine(message);
                try
                {
                    Console.WriteLine(install ? InstallTorchCudaRuntime() : InstallTorchCpuRuntime());
                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error.Message);
                    return 1;
                }
                finally
                {
                    SetupProgress = null;
                }
            }

            if (args.Length >= 2 && args[0] == "--omogre-cases")
                return RunOmogreCasesCommand(args[1], new LlmEngine(LlmEngine.Omogre, "", "", "", ""));

            // Разбор энклиноменов по отчёту: что нашлось, что отклонено и почему.
            // Только чтение: ничего не меняет и в отчёт не пишет.
            if (args.Length >= 2 && args[0] == "--enclinomena")
                return RunEnclinomenaCommand(args[1], args.Length >= 3 ? args[2] : null);

            if (args.Length >= 2 && args[0] == "--llm-stress")
                return RunLlmStressCommand(args[1], ParseLlmEngineArgs(args, 2));

            if (args.Length >= 2 && args[0] == "--silero")
            {
                var input = args[1];
                var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_silero" + Path.GetExtension(input));
                var device = "CPU";
                var overwrite = false;
                for (var index = 2; index < args.Length; index++)
                {
                    if (args[index] == "-o" || args[index] == "--output")
                    {
                        if (++index >= args.Length) throw new ArgumentException("Для --output не указан путь.");
                        output = args[index];
                    }
                    else if (args[index] == "--device")
                    {
                        if (++index >= args.Length) throw new ArgumentException("Для --device не указано CPU или CUDA.");
                        device = args[index].ToUpperInvariant();
                    }
                    else if (args[index] == "--overwrite-accents") overwrite = true;
                    else throw new ArgumentException($"Неизвестный параметр Silero: {args[index]}");
                }
                if (device is not ("CPU" or "CUDA")) throw new ArgumentException("--device должен быть CPU или CUDA.");
                if (!File.Exists(input)) throw new FileNotFoundException("Входной файл не найден.", input);
                var control = new ConversionControl();
                var progress = new Progress<(int Completed, int Total, string Message)>(item => Console.WriteLine(item.Message));
                ConvertWithSileroInteractive(input, output, device, overwrite, progress, control);
                Console.WriteLine("Готово: " + output);
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--stress-anchor")
            {
                // Расстановка ударений движком add-stress-to-epub (spaCy + Wiktionary).
                // Текст читает и пишет DSR-парсер FB2; ударения ставит Python-воркер.
                var input = args[1];
                var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_add-stress" + Path.GetExtension(input));
                for (var index = 2; index < args.Length; index++)
                {
                    if (args[index] == "-o" || args[index] == "--output")
                    {
                        if (++index >= args.Length) throw new ArgumentException("Для --output не указан путь.");
                        output = args[index];
                    }
                    else throw new ArgumentException($"Неизвестный параметр add-stress: {args[index]}");
                }
                if (!File.Exists(input)) throw new FileNotFoundException("Входной файл не найден.", input);
                var control = new ConversionControl();
                var progress = new Progress<(int Completed, int Total, string Message)>(item => Console.WriteLine(item.Message));
                ConvertWithStressAnchorInteractive(input, output, "CPU", progress, control);
                Console.WriteLine("Готово: " + output);
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--staged")
            {
                // Поэтапный конвейер без интерфейса: add-stress → RUAccent + Silero → сравнение.
                var input = args[1];
                if (!File.Exists(input)) throw new FileNotFoundException("Входной файл не найден.", input);
                var control = new ConversionControl();
                var workspace = ConvertStagedBookInteractive(input, "big_poetry", "CPU", "CPU",
                    Console.WriteLine, (_, _) => { }, Console.WriteLine, control);
                Console.WriteLine("База: " + workspace.BaseBook);
                Console.WriteLine("База+совпадения: " + workspace.StagedAppliedBook);
                Console.WriteLine("Отчёт: " + workspace.ComparisonReport);
                return 0;
            }

            if (args.Length >= 1 && args[0] == "--llm-benchmark")
                return RunLlmBenchmarkCommand(ParseLlmEngineArgs(args, 1));

            if (args.Length >= 1 && args[0] == "--llm-remote-models")
            {
                var engine = ParseLlmEngineArgs(args, 1);
                var remote = ListRemoteLlmModels(engine, out var remoteError);
                if (remoteError.Length > 0) Console.Error.WriteLine(remoteError);
                Console.WriteLine("Сервис: " + engine.Title);
                foreach (var name in remote) Console.WriteLine("  " + name);
                return remote.Count == 0 ? 1 : 0;
            }

            if (args.Length == 1 && args[0] == "--llm-models")
            {
                var available = DiscoverLlmModels();
                var engine = CurrentLlmEngine();
                Console.WriteLine("Движок сейчас: " + engine.Title + " (" + engine.Id + ")");
                Console.WriteLine("Модель по умолчанию: " + DefaultLlmModelFolder);
                Console.WriteLine("Выбрана сейчас: " + CurrentLlmModel());
                Console.WriteLine("Найдено локальных моделей: " + available.Count);
                foreach (var name in available) Console.WriteLine("  " + name + "  (" + LlmModelDirectory(name) + ")");
                Console.WriteLine("Папка моделей: " + LlmModelsDirectory);
                Console.WriteLine("Настройки: " + LlmSettingsPath);
                return 0;
            }

            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            // Delay environment setup until an actual conversion is requested. CLI users
            // get the same self-healing Python install as the GUI on first conversion.
            var pythonSetup = EnsurePythonRuntime();
            Console.WriteLine($"Python-компоненты готовы: {pythonSetup}");
            var files = Directory.Exists(options.Input)
                ? Directory.EnumerateFiles(options.Input, "*.*", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(file => Path.GetExtension(file).Equals(".fb2", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)).ToArray()
                : new[] { options.Input };
            if (files.Length == 0)
                throw new FileNotFoundException($"В папке не найдено FB2- или TXT-файлов: {options.Input}");

            using var accentizer = new RuAccentWorker(options.Model, options.Dictionary, options.Device);
            if (files.Any(file => Path.GetExtension(file).Equals(".fb2", StringComparison.OrdinalIgnoreCase)))
                accentizer.Start();
            if (options.OutputWasSpecified && options.SingleOutputFile == null)
                Directory.CreateDirectory(options.Output);
            var successes = 0;
            foreach (var input in files)
            {
                try
                {
                    var output = GetOutputPath(input, options);
                    if (Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        var diagnostics = new ConversionDiagnostics
                        {
                            Input = input, Output = output, Format = ".TXT", Model = options.Model, Device = options.Device,
                            InputBytes = new FileInfo(input).Length, CharacterCount = DecodeText(File.ReadAllBytes(input)).Length
                        };
                        var startedAt = DateTimeOffset.Now;
                        var timer = Stopwatch.StartNew();
                        using var currentProcess = Process.GetCurrentProcess();
                        try { ConvertTextFile(input, output, options.Model, options.Device, Console.WriteLine, (_, _) => { }, new ConversionControl(), diagnostics); diagnostics.Status = "Завершено"; }
                        catch (Exception ex) { diagnostics.Status = ex is OperationCanceledException ? "Остановлено пользователем" : "Ошибка"; diagnostics.Events.Add(ex.ToString()); throw; }
                        finally { timer.Stop(); diagnostics.Elapsed = timer.Elapsed; diagnostics.OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0; diagnostics.PeakWorkingSetBytes = currentProcess.PeakWorkingSet64; diagnostics.CpuPercent = timer.Elapsed.TotalMilliseconds > 0 ? currentProcess.TotalProcessorTime.TotalMilliseconds * 100 / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount : 0; WriteConversionReport(output, diagnostics, startedAt); }
                    }
                    else
                        ConvertFile(input, output, accentizer);
                    successes++;
                    Console.WriteLine($"Готово: {input} -> {output}");
                }
                catch (Exception ex)
                {
                    LogError($"Ошибка обработки входного файла: {input}", ex);
                    Console.Error.WriteLine($"Ошибка [{input}]: {ex.Message}");
                }
            }

            Console.WriteLine($"Обработано: {successes}/{files.Length}");
            return successes == files.Length ? 0 : 1;
        }
        catch (Exception ex)
        {
            LogError("Необработанная ошибка приложения", ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal sealed record GpuProbeResult(bool Supported, string Message, bool CanInstallGpuRuntime = false);

    internal static string ApplicationDirectory => AppContext.BaseDirectory;
    internal static string DataDirectory => Path.Combine(ApplicationDirectory, "ресурсы");
    internal static Action<string>? SetupProgress { get; set; }
    private static void ReportSetupOutput(string line)
    {
        if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Collecting ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Installing collected packages", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Building wheel", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Preparing metadata", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Retrying", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Starting new HTTPS connection", StringComparison.OrdinalIgnoreCase))
            SetupProgress?.Invoke(line.Trim());
    }
    internal static string ModelCacheDirectory => Path.Combine(DataDirectory, "модели");

    /// <summary>Словари, встроенные в DSR.exe и распакованные в папку ресурсов.</summary>
    internal static string DictionaryDirectory => Path.Combine(DataDirectory, "dictionary");

    /// <summary>Корень необязательного движка add-stress-to-epub внутри папки ресурсов.</summary>
    internal static string AddStressBaseDirectory => Path.Combine(DataDirectory, "add-stress");

    private static readonly object BundledDataLock = new();

    /// <summary>
    /// Распаковывает встроенные в exe словари в «ресурсы\dictionary». Папка ресурсов —
    /// единственное место, куда DSR что-либо пишет; её можно удалить целиком, и при
    /// следующем запуске словари восстановятся из exe, а остальное скачается заново.
    /// </summary>
    internal static void EnsureBundledData(bool includeAddStress = false)
    {
        lock (BundledDataLock)
        {
            try
            {
                Directory.CreateDirectory(DictionaryDirectory);
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var resourceName in assembly.GetManifestResourceNames())
                {
                    const string prefix = "DSR.dictionary.";
                    if (!resourceName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var fileName = resourceName[prefix.Length..];
                    if (fileName.Length == 0) continue;
                    var destination = Path.Combine(DictionaryDirectory, fileName);
                    using var resource = assembly.GetManifestResourceStream(resourceName);
                    if (resource == null) continue;
                    // Повторно не перезаписываем: файл мог быть намеренно исправлен.
                    // Но обрезанный/пустой файл заменяем — иначе словарь молча не работает.
                    if (File.Exists(destination) && new FileInfo(destination).Length == resource.Length) continue;
                    using var output = File.Create(destination);
                    resource.CopyTo(output);
                }
            }
            catch (Exception error)
            {
                LogError("Не удалось распаковать встроенные словари", error);
            }

            if (includeAddStress)
            {
                try { EnsureAddStressEngineCore(); }
                catch (Exception error) { LogError("Необязательный движок add-stress не распакован", error); }
            }
        }
    }

    /// <summary>Проверяет, что встроенные словари уже лежат в папке ресурсов.</summary>
    internal static bool BundledDictionariesReady()
    {
        try
        {
            return File.Exists(YoDictionaryPath)
                && File.Exists(SupplementaryStressDictionaryPath)
                && File.Exists(EnclinomenaPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Достаёт необязательный движок add-stress-to-epub (spaCy + Wiktionary) из exe
    /// в «ресурсы\add-stress». Большая база russian_dict.db в exe не входит: библиотека
    /// скачивает её сама при первом использовании, уже внутри папки ресурсов.
    /// </summary>
    internal static string EnsureAddStressEngine()
    {
        lock (BundledDataLock)
            return EnsureAddStressEngineCore();
    }

    private static string EnsureAddStressEngineCore()
    {
        var packageDirectory = Path.Combine(AddStressBaseDirectory, "russian_text_stresser");
        var marker = Path.Combine(packageDirectory, "text_stresser.py");
        if (File.Exists(marker)) return AddStressBaseDirectory;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var zipStream = assembly.GetManifestResourceStream("DSR.russian_text_stresser.zip");
            if (zipStream != null)
            {
                Directory.CreateDirectory(AddStressBaseDirectory);
                using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destination = Path.Combine(AddStressBaseDirectory,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
        }
        catch (Exception error)
        {
            LogError("Не удалось распаковать движок add-stress", error);
        }

        if (File.Exists(marker)) return AddStressBaseDirectory;

        // Совместимость со старой раскладкой: папка рядом с exe.
        var legacy = Path.Combine(ApplicationDirectory, "russian_text_stresser");
        if (File.Exists(Path.Combine(legacy, "text_stresser.py"))) return ApplicationDirectory;

        throw new InvalidOperationException(
            "В DSR.exe не найден встроенный движок add-stress-to-epub. Пересоберите программу или используйте обработку RUAccent + Silero.");
    }

    private static string ExtractEmbeddedFile(string resourceName, string fileName)
    {
        var destination = Path.Combine(DataDirectory, fileName);
        Directory.CreateDirectory(DataDirectory);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"В приложении не найден встроенный файл {fileName}.");
        using var output = File.Create(destination);
        resource.CopyTo(output);
        return destination;
    }

    private static readonly object PythonEnvironmentLock = new();

    /// <summary>Версия сборки: после обновления DSR окружение готовится заново.</summary>
    internal static string AppVersion
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() ?? "0" : informational;
        }
    }

    /// <summary>Версия для показа пользователю: без служебного суффикса сборки.</summary>
    internal static string DisplayVersion
    {
        get
        {
            var version = AppVersion;
            var suffix = version.IndexOfAny(new[] { '+', '-' });
            return suffix > 0 ? version[..suffix] : version;
        }
    }

    /// <summary>Python из локального окружения рядом с приложением.</summary>
    internal static string EnvironmentPythonPath => Path.Combine(DataDirectory, "python", "Scripts", "python.exe");

    // Переносимый Python: скачивается один раз в «ресурсы\python-base», если в системе
    // нет подходящего Python. Это полная сборка CPython с venv и pip (пакет NuGet
    // python от Python Software Foundation), поэтому установленный Python не нужен.
    private const string PortablePythonVersion = "3.11.9";
    private static string PortablePythonDirectory => Path.Combine(DataDirectory, "python-base");
    private static string PortablePythonPath => Path.Combine(PortablePythonDirectory, "python.exe");

    /// <summary>Ищет Python 3.10+, из которого можно создать локальное окружение.</summary>
    private static string? FindBootstrapPython()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        if (File.Exists(PortablePythonPath)) candidates.Add(PortablePythonPath);
        candidates.Add("python");

        foreach (var candidate in candidates)
        {
            try
            {
                var check = RunCommand(candidate, "-c",
                    "import sys; print(sys.version.split()[0]); raise SystemExit(0 if sys.version_info >= (3, 10) else 1)");
                if (check.ExitCode == 0) return candidate;
                LogReview($"Python не подошёл: {candidate} — {FirstOutputLine(check.Output)}");
            }
            catch (Exception error)
            {
                LogReview($"Python не найден: {candidate} — {error.GetBaseException().Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// Скачивает переносимый Python в папку ресурсов. Нужен только на машине без
    /// установленного Python 3.10+; после этого DSR полностью автономен.
    /// </summary>
    private static string DownloadPortablePython()
    {
        var url = $"https://api.nuget.org/v3-flatcontainer/python/{PortablePythonVersion}/python.{PortablePythonVersion}.nupkg";
        var temporary = Path.Combine(DataDirectory, "временные-файлы");
        Directory.CreateDirectory(temporary);
        var archivePath = Path.Combine(temporary, $"python-{PortablePythonVersion}.nupkg");
        var extractPath = Path.Combine(temporary, "python-base");
        SetupProgress?.Invoke("Python не найден. Скачиваю переносимый Python 3.11 — около 18 МБ…");

        try
        {
            using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var response = client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var output = File.Create(archivePath);
                var buffer = new byte[81920];
                long downloaded = 0;
                long lastReport = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    downloaded += read;
                    if (downloaded - lastReport < 1_000_000) continue;
                    lastReport = downloaded;
                    var percent = total > 0 ? (int)(downloaded * 100 / total) : 0;
                    SetupProgress?.Invoke($"Скачиваю переносимый Python: {downloaded / 1024 / 1024} МБ"
                        + (total > 0 ? $" из {total / 1024 / 1024} МБ ({percent}%)" : "…"));
                }
            }

            SetupProgress?.Invoke("Распаковываю переносимый Python…");
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractPath);

            // В пакете NuGet всё лежит в tools\ — переносим содержимое наверх.
            var tools = Path.Combine(extractPath, "tools");
            var source = Directory.Exists(tools) ? tools : extractPath;
            if (Directory.Exists(PortablePythonDirectory)) Directory.Delete(PortablePythonDirectory, recursive: true);
            Directory.CreateDirectory(PortablePythonDirectory);
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(PortablePythonDirectory, Path.GetFileName(directory)));
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(PortablePythonDirectory, Path.GetFileName(file)), overwrite: true);

            if (!File.Exists(PortablePythonPath))
                throw new InvalidOperationException("Скачанный Python не содержит python.exe.");
            var check = RunCommand(PortablePythonPath, "-c", "import sys; print(sys.version.split()[0])");
            if (check.ExitCode != 0)
                throw new InvalidOperationException("Скачанный Python не запускается: " + FirstOutputLine(check.Output));
            SetupProgress?.Invoke("Переносимый Python готов: " + FirstOutputLine(check.Output) + ".");
            return PortablePythonPath;
        }
        finally
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch (Exception) { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true); } catch (Exception) { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>Лежат ли на месте обязательные ONNX-модели выбранного размера омографов.</summary>
    internal static bool IsModelPrepared(string model)
    {
        try
        {
            var package = Path.Combine(DataDirectory, "python", "Lib", "site-packages", "ruaccent");
            return IsRuAccentModelDirectoryReady(Path.Combine(package, "nn", "nn_omograph", model))
                && IsRuAccentModelDirectoryReady(Path.Combine(package, "nn", "nn_accent"))
                && IsRuAccentModelDirectoryReady(Path.Combine(package, "nn", "nn_yo_homograph_resolver"))
                && File.Exists(Path.Combine(package, "dictionary", "omographs.json.gz"))
                && File.Exists(Path.Combine(package, "dictionary", "yo_words.json.gz"))
                && File.Exists(Path.Combine(package, "dictionary", "accents_nn.json.gz"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsRuAccentModelDirectoryReady(string directory) =>
        Directory.Exists(directory)
        && File.Exists(Path.Combine(directory, "model.onnx"))
        && (File.Exists(Path.Combine(directory, "tokenizer.json")) || File.Exists(Path.Combine(directory, "vocab.txt")));

    /// <summary>
    /// Удаляет недокачанные каталоги моделей RUAccent. Каталог мог появиться до того,
    /// как скачался model.onnx; библиотека проверяет только существование каталога и
    /// больше не пытается скачать модель — из-за этого перенос без ресурсов падал с
    /// «model.onnx: File doesn't exist». После удаления повторный запуск скачивает заново.
    /// </summary>
    internal static void RemoveIncompleteRuAccentModels(string model)
    {
        try
        {
            var package = Path.Combine(DataDirectory, "python", "Lib", "site-packages", "ruaccent");
            foreach (var directory in new[]
            {
                Path.Combine(package, "nn", "nn_accent"),
                Path.Combine(package, "nn", "nn_yo_homograph_resolver"),
                Path.Combine(package, "nn", "nn_omograph", model),
            })
            {
                if (!Directory.Exists(directory) || IsRuAccentModelDirectoryReady(directory)) continue;
                Directory.Delete(directory, recursive: true);
                LogReview("Удалён недокачанный каталог модели RUAccent: " + directory);
            }
            // Словари RUAccent скачиваются одним набором; неполный набор так же
            // приводит к падению загрузки и должен быть скачан заново.
            var dictionaries = Path.Combine(package, "dictionary");
            if (Directory.Exists(dictionaries)
                && (!File.Exists(Path.Combine(dictionaries, "omographs.json.gz"))
                    || !File.Exists(Path.Combine(dictionaries, "yo_words.json.gz"))
                    || !File.Exists(Path.Combine(dictionaries, "accents_nn.json.gz"))))
            {
                Directory.Delete(dictionaries, recursive: true);
                LogReview("Удалён недокачанный каталог словарей RUAccent: " + dictionaries);
            }
        }
        catch (Exception error)
        {
            LogError("Не удалось убрать недокачанные модели RUAccent", error);
        }
    }

    /// <summary>Глубокая проверка ресурсов для кнопки «Проверить ресурсы».</summary>
    internal static string VerifyResources(string model)
    {
        EnsureBundledData(includeAddStress: true);
        var lines = new List<string>();
        var ok = true;
        void Check(bool condition, string good, string bad)
        {
            // Обычные «OK:/ОШИБКА:» вместо галочек: так отчёт читается и в окне,
            // и в консоли, и в файле журнала при любой кодовой странице.
            lines.Add((condition ? "OK: " : "ОШИБКА: ") + (condition ? good : bad));
            if (!condition) ok = false;
        }

        Check(File.Exists(YoDictionaryPath), "словарь ё.txt на месте", "нет словаря ё.txt");
        Check(File.Exists(SupplementaryStressDictionaryPath), "словарь ударений на месте", "нет словаря ударений");
        Check(File.Exists(EnclinomenaPath), "список энклиноменов на месте", "нет списка энклиноменов");
        var addStressEngine = Path.Combine(AddStressBaseDirectory, "russian_text_stresser", "text_stresser.py");
        var legacyAddStress = Path.Combine(ApplicationDirectory, "russian_text_stresser", "text_stresser.py");
        Check(File.Exists(addStressEngine) || File.Exists(legacyAddStress),
            "движок add-stress распакован", "движок add-stress не распакован");
        Check(File.Exists(EnvironmentPythonPath), "Python-окружение на месте", "Python-окружение не создано");
        if (File.Exists(EnvironmentPythonPath))
        {
            try
            {
                var imports = RunCommand(EnvironmentPythonPath, "-c",
                    "import ruaccent, onnxruntime; print('ruaccent', ruaccent.__version__ if hasattr(ruaccent, '__version__') else 'ok', 'onnxruntime', onnxruntime.__version__)");
                Check(imports.ExitCode == 0, "пакеты ruaccent и onnxruntime загружаются", "пакеты ruaccent/onnxruntime не загружаются: " + FirstOutputLine(imports.Output));
            }
            catch (Exception error)
            {
                Check(false, "", "не удалось запустить Python: " + error.GetBaseException().Message);
            }
        }
        Check(IsModelPrepared(model), $"модель {model} скачана полностью", $"модель {model} не скачана или неполная");
        return (ok ? "Ресурсы в порядке.\n\n" : "Найдены проблемы.\n\n") + string.Join(Environment.NewLine, lines)
            + Environment.NewLine + Environment.NewLine + "Папка ресурсов: " + DataDirectory;
    }

    internal static string EnsurePythonRuntime()
    {
        // Подготовка, проверка GPU и установка CUDA пишут в одно локальное окружение Python.
        // Не даём им работать одновременно: параллельные pip-процессы блокируют файлы
        // друг друга, и один из них падает с WinError 32 («файл занят другим процессом»).
        lock (PythonEnvironmentLock)
            return EnsurePythonRuntimeCore();
    }

    private static string EnsurePythonRuntimeCore()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ModelCacheDirectory);
        EnsureBundledData();
        SetupProgress?.Invoke("Проверяю локальное окружение Python…");
        var pythonEnvironment = Path.Combine(DataDirectory, "python");
        var python = Path.Combine(pythonEnvironment, "Scripts", "python.exe");
        var systemPython = FindBootstrapPython();

        if (!File.Exists(python))
        {
            SetupProgress?.Invoke("Создаю локальное окружение Python…");
            if (systemPython == null) systemPython = DownloadPortablePython();
            var version = RunCommand(systemPython, "-c", "import sys; print(sys.version_info[0] * 100 + sys.version_info[1]); raise SystemExit(0 if sys.version_info >= (3, 10) else 1)");
            if (version.ExitCode != 0)
                throw new InvalidOperationException($"Для первого запуска нужен Python 3.10+ и доступ к интернету. {version.Output}");
            var createEnvironment = RunCommand(systemPython, "-m", "venv", pythonEnvironment);
            if (createEnvironment.ExitCode != 0 || !File.Exists(python))
            {
                try { if (Directory.Exists(pythonEnvironment)) Directory.Delete(pythonEnvironment, recursive: true); } catch (Exception) { }
                throw new InvalidOperationException("Не удалось создать локальное Python-окружение. " + createEnvironment.Output);
            }
        }

        var check = RunCommand(python, "-c", "import ruaccent, onnxruntime; print('ok')");
        if (check.ExitCode != 0)
        {
            SetupProgress?.Invoke("Устанавливаю RUAccent и ONNX Runtime; идёт скачивание зависимостей…");
            // Repair only required inference packages, without optional torch/crfsuite.
            // Ставим с PyPI, а не из git: на чужой машине может не быть git, и установка
            // из ссылки падала ещё до скачивания моделей. Версия закреплена — от неё
            // зависят имена ONNX-моделей, которые проверяет worker.
            const string accentSpec = "ruaccent==1.5.8.3";
            var install = InstallPipPackages(python, pythonEnvironment, "Установка RuAccent", accentSpec, "onnxruntime");
            if (install.ExitCode != 0)
            {
                LogError("Не удалось установить RUAccent и ONNX Runtime", install.Output);
                throw new InvalidOperationException(IsTransientFileLock(install.Output)
                    ? "Не удалось установить RUAccent и ONNX Runtime: файлы окружения заняты другим процессом. Закройте другие окна DSR и повторите «Подготовка к работе». Журнал: " + LogFilePath
                    : "Не удалось установить RUAccent и ONNX Runtime. Проверьте интернет и место на диске. Подробности в журнале: " + LogFilePath);
            }
            check = RunCommand(python, "-c", "import ruaccent, onnxruntime; print('onnxruntime', onnxruntime.__version__)");
            if (check.ExitCode != 0)
                throw new InvalidOperationException("Проверка RUAccent и ONNX Runtime не пройдена. " + check.Output);
        }

        return python;
    }

    // Windows на короткое время может блокировать файл окружения (антивирус, индексатор
    // или уходящий процесс python). Такие ошибки pip обычно проходят при повторе.
    private static bool IsTransientFileLock(string output) =>
        output.Contains("WinError 32", StringComparison.OrdinalIgnoreCase)
        || output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
        || output.Contains("занят другим процессом", StringComparison.OrdinalIgnoreCase);

    /// <summary>Останавливает Python из локального окружения перед удалением папки ресурсов.</summary>
    internal static void CloseResourceProcesses()
    {
        try { StopLocalEnvironmentPython(Path.Combine(DataDirectory, "python")); }
        catch (Exception error) { LogError("Не удалось остановить Python перед очисткой ресурсов", error); }
    }

    // Останавливает только те процессы python, которые запущены из нашего локального
    // окружения (ресурсы\python), чтобы не трогать посторонние установки Python.
    private static void StopLocalEnvironmentPython(string pythonEnvironment)    {
        string root;
        try { root = Path.GetFullPath(pythonEnvironment); }
        catch { return; }
        foreach (var process in Process.GetProcessesByName("python"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch { /* процесс недоступен или уже завершился */ }
            finally { process.Dispose(); }
        }
    }

    // Установка пакетов pip с повтором: Windows иногда на короткое время блокирует файлы
    // окружения (антивирус, индексатор или уходящий процесс python), и первая попытка падает.
    private static (int ExitCode, string Output) InstallPipPackages(string python, string pythonEnvironment, string label, params string[] packages)
    {
        var install = (ExitCode: 1, Output: string.Empty);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Оставшийся от прерванной подготовки Python держит файлы в site-packages.
            // Останавливаем только процессы из нашего локального окружения.
            StopLocalEnvironmentPython(pythonEnvironment);
            var arguments = new List<string> { "-m", "pip", "install", "--upgrade" };
            arguments.AddRange(packages);
            install = RunCommand(python, arguments.ToArray());
            if (install.ExitCode == 0) break;
            if (attempt < 3 && IsTransientFileLock(install.Output))
            {
                SetupProgress?.Invoke($"{label}: файлы окружения заняты, повтор ({attempt + 1}/3)…");
                Thread.Sleep(TimeSpan.FromSeconds(3 * attempt));
                continue;
            }
            break;
        }
        return install;
    }

    // Локальная модель ИИ работает через onnxruntime-genai. Пакет тяжёлый и не входит в базовую
    // подготовку, поэтому ставим его при первом использовании локального движка подсказок.
    internal static string? EnsureLlmRuntimeOrError(string python)
    {
        lock (PythonEnvironmentLock)
        {
            var check = RunCommand(python, "-c", "import onnxruntime_genai; print('ok')");
            if (check.ExitCode == 0) return null;
            SetupProgress?.Invoke("Устанавливаю onnxruntime-genai для локальной модели ИИ…");
            var install = InstallPipPackages(python, Path.Combine(DataDirectory, "python"),
                "Установка onnxruntime-genai", "onnxruntime-genai");
            if (install.ExitCode != 0)
            {
                LogError("Не удалось установить onnxruntime-genai", install.Output);
                return "Не удалось установить пакет onnxruntime-genai для локальной модели ИИ. "
                    + (IsTransientFileLock(install.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }
            check = RunCommand(python, "-c", "import onnxruntime_genai; print('ok')");
            return check.ExitCode == 0 ? null : "Пакет onnxruntime-genai установлен, но не загружается. " + check.Output;
        }
    }

    // Второй движок ударений — silero-stress. Это отдельный модуль, а не языковая модель:
    // своя модель внутри пакета, весит 39 МБ, но требует PyTorch. В базовую подготовку
    // он не входит и ставится при первом выборе этого движка.
    // Ставим именно CPU-сборку torch: она вчетверо легче, а 0,5 мс на слово GPU не дают,
    // зато рядом с onnxruntime-gpu не появляется второй набор CUDA-библиотек.
    internal static string? EnsureSileroRuntimeOrError(string python)
    {
        lock (PythonEnvironmentLock)
        {
            var check = RunCommand(python, "-c", "import silero_stress; print('ok')");
            if (check.ExitCode == 0) return null;

            SetupProgress?.Invoke("Готовлю движок ударений silero-stress: ставлю PyTorch (CPU) и silero-stress…");
            var torch = InstallPipPackages(python, Path.Combine(DataDirectory, "python"),
                "Установка PyTorch для silero-stress", "--index-url", "https://download.pytorch.org/whl/cpu", "torch");
            if (torch.ExitCode != 0)
            {
                LogError("Не удалось установить PyTorch для silero-stress", torch.Output);
                return "Не удалось скачать PyTorch (CPU) для движка silero-stress. "
                    + (IsTransientFileLock(torch.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }

            // torch — единственная зависимость пакета, и он уже стоит, поэтому зависимости
            // не трогаем: pip иначе обновит numpy рядом с onnxruntime.
            var silero = InstallPipPackages(python, Path.Combine(DataDirectory, "python"),
                "Установка silero-stress", "--no-deps", "silero-stress");
            if (silero.ExitCode != 0)
            {
                LogError("Не удалось установить silero-stress", silero.Output);
                return "Не удалось установить пакет silero-stress. "
                    + (IsTransientFileLock(silero.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }

            check = RunCommand(python, "-c", "import silero_stress; print('ok')");
            return check.ExitCode == 0 ? null : "Пакет silero-stress установлен, но не загружается. " + check.Output;
        }
    }

    // Необязательный движок add-stress-to-epub: пакет и модель spaCy уже в DSR.exe,
    // но сам spaCy и stressed-cyrillic-tools — это Python-зависимости. Ставим их в
    // локальное окружение при первом использовании, чтобы не требовать Python с машины.
    internal static string? EnsureAddStressRuntimeOrError(string python)
    {
        lock (PythonEnvironmentLock)
        {
            var check = RunCommand(python, "-c", "import spacy, stressed_cyrillic_tools; print('ok')");
            if (check.ExitCode == 0) return null;

            SetupProgress?.Invoke("Готовлю движок add-stress: ставлю spaCy 3.6.1 и stressed-cyrillic-tools…");
            // Версия spaCy закреплена под встроенную модель ru_core_news_sm-3.6.0.
            // numpy<2 обязателен: колёса spaCy 3.6 собраны под numpy 1.x и с numpy 2
            // падают при импорте («numpy.dtype size changed»). RUAccent и ONNX Runtime
            // с numpy 1.26 работают, это проверено.
            var install = InstallPipPackages(python, Path.Combine(DataDirectory, "python"),
                "Установка spaCy для add-stress", "spacy==3.6.1", "numpy<2", "stressed-cyrillic-tools");
            if (install.ExitCode != 0)
            {
                LogError("Не удалось установить spaCy для add-stress", install.Output);
                return "Не удалось установить spaCy для движка add-stress. "
                    + (IsTransientFileLock(install.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }

            check = RunCommand(python, "-c", "import spacy, stressed_cyrillic_tools; print('ok')");
            return check.ExitCode == 0 ? null : "spaCy установлен, но не загружается. " + check.Output;
        }
    }

    // Проверка без установки: окно спрашивает, готов ли движок, ещё до запуска проверки.
    internal static bool IsSileroReady()
    {
        try
        {
            var python = EnvironmentPythonPath;
            if (!File.Exists(python)) return false;
            return RunCommand(python, "-c", "import silero_stress").ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // Третий движок ударений — omogre: контекстная модель омографов. Отдельный пакет,
    // который тянет PyTorch (он же нужен silero-stress) и весит до сотни мегабайт на диске.
    // В базовую подготовку не входит и ставится при первом выборе этого движка.
    internal static string? EnsureOmogreRuntimeOrError(string python)
    {
        lock (PythonEnvironmentLock)
        {
            // Установка из git-ссылки иногда оставляет рядом с «ресурсы» папку-двойник с
            // искорёженным именем — подчищаем её до работы.
            RemoveDuplicateResourceFolders();
            var check = RunCommand(python, "-c", "import omogre, torch, requests; print('ok')");
            if (check.ExitCode == 0) return null;

            var pythonEnvironment = Path.Combine(DataDirectory, "python");
            SetupProgress?.Invoke("Готовлю движок omogre (контекстные ударения): проверяю PyTorch и ставлю библиотеку…");

            // omogre работает на PyTorch; ставим ту же CPU-сборку, что и для silero-stress,
            // чтобы рядом с onnxruntime не появился второй набор CUDA-библиотек.
            var torch = RunCommand(python, "-c", "import torch; print('ok')");
            if (torch.ExitCode != 0)
            {
                var installTorch = InstallPipPackages(python, pythonEnvironment,
                    "Установка PyTorch для omogre", "--index-url", "https://download.pytorch.org/whl/cpu", "torch");
                if (installTorch.ExitCode != 0)
                {
                    LogError("Не удалось установить PyTorch для omogre", installTorch.Output);
                    return "Не удалось скачать PyTorch (CPU) для движка omogre. "
                        + (IsTransientFileLock(installTorch.Output)
                            ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                            : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
                }
            }

            // requests и tqdm — зависимости omogre (скачивание модели и прогресс).
            var deps = InstallPipPackages(python, pythonEnvironment,
                "Установка зависимостей omogre", "requests", "tqdm");
            if (deps.ExitCode != 0)
            {
                LogError("Не удалось установить зависимости omogre", deps.Output);
                return "Не удалось установить зависимости движка omogre. "
                    + (IsTransientFileLock(deps.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }

            // Зависимости не трогаем: torch уже стоит, pip иначе обновит numpy рядом с onnxruntime.
            var omogre = InstallPipPackages(python, pythonEnvironment,
                "Установка omogre", "--no-deps", "git+https://github.com/omogr/omogre.git");
            if (omogre.ExitCode != 0)
            {
                LogError("Не удалось установить omogre", omogre.Output);
                return "Не удалось установить библиотеку omogre (нужен доступ к github.com). "
                    + (IsTransientFileLock(omogre.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }

            // pip из git-ссылки мог снова оставить двойник — убираем до проверки загрузки.
            RemoveDuplicateResourceFolders();
            check = RunCommand(python, "-c", "import omogre, torch, requests; print('ok')");
            return check.ExitCode == 0 ? null : "Библиотека omogre установлена, но не загружается. " + check.Output;
        }
    }

    // pip при установке из git-ссылки может оставить рядом с «ресурсы» папку-двойник с
    // искорёженным именем: путь к site-packages попадает в установщик как UTF-8, а читается
    // как ANSI («ресурсы» → «СЂРµСЃСѓСЂСЃС‹»). Внутри только служебный egg-info, ничего
    // полезного. Настоящее окружение узнаём по Scripts\python.exe, поэтому под удаление
    // попадают только такие обрубки, а не рабочие данные.
    internal static void RemoveDuplicateResourceFolders()
    {
        try
        {
            var canonical = Path.GetFileName(DataDirectory);
            foreach (var directory in Directory.GetDirectories(ApplicationDirectory))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(Path.Combine(directory, "python", "Lib", "site-packages"))) continue;
                if (File.Exists(Path.Combine(directory, "python", "Scripts", "python.exe"))) continue;
                Directory.Delete(directory, recursive: true);
                LogReview("Убрана папка-двойник «" + canonical + "»: " + name);
            }
        }
        catch (Exception error)
        {
            LogError("Уборка папок-двойников ресурсов", error);
        }
    }

    // Проверка без установки: окно показывает, готов ли движок omogre.
    internal static bool IsOmogreReady()
    {
        try
        {
            var python = EnvironmentPythonPath;
            if (!File.Exists(python)) return false;
            return RunCommand(python, "-c", "import omogre, torch").ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string InstallGpuRuntime()
    {
        var python = EnsurePythonRuntime();
        var remove = RunCommand(python, "-m", "pip", "uninstall", "-y", "onnxruntime", "onnxruntime-gpu");
        if (remove.ExitCode != 0)
            throw new InvalidOperationException("Не удалось подготовить Python-окружение к установке CUDA-пакета. " + remove.Output);

        // The official Windows wheel extra installs the matching CUDA/cuDNN runtime DLLs.
        var install = RunCommand(python, "-m", "pip", "install", "--upgrade", "onnxruntime-gpu[cuda,cudnn]");
        if (install.ExitCode != 0)
        {
            var restoreCpu = RunCommand(python, "-m", "pip", "install", "onnxruntime");
            throw new InvalidOperationException("Не удалось установить ONNX Runtime для GPU. Проверьте подключение к интернету. "
                + install.Output + (restoreCpu.ExitCode == 0 ? "\nВосстановлена CPU-версия ONNX Runtime." : "\nНе удалось восстановить CPU-версию: " + restoreCpu.Output));
        }
        return install.Output;
    }

    // --- Видеокарта (CUDA) для движков подсказок на PyTorch ------------------------------------
    // RUAccent считает через ONNX Runtime: у него своя проверка и своя CUDA-сборка (кнопка
    // «Проверить GPU / CUDA» в первой вкладке). silero-stress и omogre работают через PyTorch,
    // и он ставится сразу сборкой для процессора: она вчетверо легче, и рядом с CUDA-библиотеками
    // ONNX Runtime не появляется второй набор CUDA/cuDNN. Сборка PyTorch с CUDA ставится
    // отдельно и осознанно: весит она около трёх гигабайт, зато снимает с процессора сам счёт
    // сети — именно он и занимает почти всё время разбора расхождений.
    internal sealed record TorchCudaStatus(string TorchVersion, string CudaBuild, bool Available,
        string GpuName, int MemoryMb, string Message);

    // Сборки PyTorch с CUDA для текущей версии torch. Порядок — от самой совместимой:
    // CUDA 12.6 работает с драйверами прошлых лет, 13.0 и 13.2 требуют свежего. Если версия
    // torch обновится и в сборке не окажется нужной версии, pip ответит отказом и берётся
    // следующая; драйвер новее своей версии не пробуем даже скачивать.
    private static readonly (string Tag, int Major, int Minor, string Title)[] TorchCudaBuilds =
    {
        ("cu126", 12, 6, "CUDA 12.6"),
        ("cu130", 13, 0, "CUDA 13.0"),
        ("cu132", 13, 2, "CUDA 13.2"),
    };

    private const string TorchCudaProbeScript =
        "import json\n" +
        "import torch\n" +
        "info = {\"torch\": torch.__version__, \"cuda_build\": torch.version.cuda or \"\", \"available\": False, \"gpu\": \"\", \"memory_mb\": 0}\n" +
        "try:\n" +
        "    if torch.cuda.is_available():\n" +
        "        info[\"available\"] = True\n" +
        "        info[\"gpu\"] = torch.cuda.get_device_name(0)\n" +
        "        info[\"memory_mb\"] = int(torch.cuda.get_device_properties(0).total_memory / (1024 * 1024))\n" +
        "except Exception as error:\n" +
        "    info[\"error\"] = str(error)\n" +
        "print(json.dumps(info, ensure_ascii=False))\n";

    /// <summary>Что стоит сейчас и видит ли сборка PyTorch видеокарту. Ничего не меняет.</summary>
    internal static TorchCudaStatus ProbeTorchCuda()
    {
        lock (PythonEnvironmentLock)
        {
            var python = EnsurePythonRuntime();
            var run = RunCommand(python, "-c", TorchCudaProbeScript);
            if (run.ExitCode != 0 && run.Output.Contains("ModuleNotFoundError", StringComparison.Ordinal))
                return new TorchCudaStatus("", "", false, "", 0,
                    "PyTorch ещё не установлен: он ставится сам при первом выборе движка подсказок "
                    + "(silero-stress или omogre).");
            var json = LastJsonLine(run.Output);
            if (json.Length == 0)
                return new TorchCudaStatus("", "", false, "", 0, "Не удалось проверить PyTorch: " + FirstOutputLine(run.Output));
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var version = root.TryGetProperty("torch", out var versionValue) ? versionValue.GetString() ?? "" : "";
                var build = root.TryGetProperty("cuda_build", out var buildValue) ? buildValue.GetString() ?? "" : "";
                var available = root.TryGetProperty("available", out var availableValue) && availableValue.ValueKind == JsonValueKind.True;
                var gpu = root.TryGetProperty("gpu", out var gpuValue) ? gpuValue.GetString() ?? "" : "";
                var memory = root.TryGetProperty("memory_mb", out var memoryValue) && memoryValue.TryGetInt32(out var megabytes)
                    ? megabytes : 0;
                var message = available
                    ? $"PyTorch {version} ({build}): видеокарта {gpu}, {memory:N0} МБ — считать будут видеокарта и процессор."
                    : build.Length == 0
                        ? $"PyTorch {version} — сборка для процессора (без CUDA). Видеокарту можно подключить кнопкой."
                        : $"PyTorch {version} ({build}) — сборка с CUDA, но видеокарта не отвечает: считается на процессоре.";
                return new TorchCudaStatus(version, build, available, gpu, memory, message);
            }
            catch (JsonException error)
            {
                return new TorchCudaStatus("", "", false, "", 0, "Проверка PyTorch вернула непонятный ответ: " + error.Message);
            }
        }
    }

    /// <summary>
    /// Ставит сборку PyTorch с CUDA вместо сборки для процессора. Возвращает описание
    /// результата; если ни одна сборка не подошла, возвращает рабочую CPU-сборку и бросает
    /// исключение с перечнем попыток.
    /// </summary>
    internal static string InstallTorchCudaRuntime()
    {
        lock (PythonEnvironmentLock)
        {
            var python = EnsurePythonRuntime();
            var environment = Path.Combine(DataDirectory, "python");
            // PyTorch мог ещё не ставиться: он нужен только движкам подсказок и
            // устанавливается при первом их выборе. Чтобы поставить CUDA-сборку,
            // сначала ставим обычную CPU-сборку и узнаём её версию — иначе версии
            // для скачивания просто нет.
            var version = EnsureTorchInstalled(python, environment);

            var driver = NvidiaDriverCuda();
            var attempts = new StringBuilder();
            foreach (var build in TorchCudaBuilds)
            {
                if (driver.HasValue && (build.Major, build.Minor).CompareTo(driver.Value) > 0)
                {
                    attempts.AppendLine($"{build.Title}: драйвер видеокарты не поддерживает "
                        + $"(драйвер рассчитан на CUDA {driver.Value.Major}.{driver.Value.Minor})");
                    continue;
                }
                SetupProgress?.Invoke($"Ставлю PyTorch для {build.Title}: загрузка около трёх гигабайт, это несколько минут…");
                var install = InstallPipPackages(python, environment, "Установка PyTorch с CUDA",
                    "--force-reinstall", "--no-deps",
                    "--index-url", $"https://download.pytorch.org/whl/{build.Tag}",
                    $"torch=={version}+{build.Tag}");
                if (install.ExitCode != 0)
                {
                    attempts.AppendLine($"{build.Title}: {FirstOutputLine(install.Output)}");
                    continue;
                }
                var status = ProbeTorchCuda();
                if (status.Available)
                    return status.Message + $" Поставлена сборка {build.Title}; движки подсказок считают на видеокарте.";
                attempts.AppendLine($"{build.Title}: сборка встала, но видеокарта не отвечает — {status.Message}");
            }

            SetupProgress?.Invoke("Возвращаю рабочую сборку PyTorch для процессора…");
            var restore = InstallPipPackages(python, environment, "Возврат CPU-сборки PyTorch",
                "--force-reinstall", "--no-deps",
                "--index-url", "https://download.pytorch.org/whl/cpu", $"torch=={version}");
            throw new InvalidOperationException("Не удалось перейти на сборку PyTorch с CUDA."
                + Environment.NewLine + attempts.ToString().TrimEnd() + Environment.NewLine
                + (restore.ExitCode == 0
                    ? "Возвращена рабочая сборка для процессора: движки подсказок продолжают работать."
                    : "Сборку для процессора вернуть не удалось: " + FirstOutputLine(restore.Output)));
        }
    }

    /// <summary>Возвращает сборку PyTorch для процессора: освобождает около трёх гигабайт.</summary>
    internal static string InstallTorchCpuRuntime()
    {
        lock (PythonEnvironmentLock)
        {
            var python = EnsurePythonRuntime();
            var environment = Path.Combine(DataDirectory, "python");
            var version = TorchBaseVersion(python);
            if (version.Length == 0)
            {
                // PyTorch ещё не стоит — ставим обычную CPU-сборку с зависимостями.
                // Повторно скачивать её с --force-reinstall уже не нужно.
                EnsureTorchInstalled(python, environment);
                return ProbeTorchCuda().Message;
            }

            SetupProgress?.Invoke("Возвращаю сборку PyTorch для процессора…");
            var install = InstallPipPackages(python, environment, "Возврат CPU-сборки PyTorch",
                "--force-reinstall", "--no-deps",
                "--index-url", "https://download.pytorch.org/whl/cpu", $"torch=={version}");
            if (install.ExitCode != 0)
                throw new InvalidOperationException("Не удалось вернуть сборку PyTorch для процессора. "
                    + FirstOutputLine(install.Output));
            return ProbeTorchCuda().Message;
        }
    }

    // «2.14.1+cpu» → «2.14.1»: сборку pip ищет по версии без пометки устройства.
    private static string TorchBaseVersion(string python)
    {
        var run = RunCommand(python, "-c", "import torch; print(torch.__version__)");
        if (run.ExitCode != 0) return "";
        var line = run.Output.Split('\n').Select(text => text.Trim())
            .LastOrDefault(text => text.Length > 1 && char.IsDigit(text[0])) ?? "";
        var plus = line.IndexOf('+');
        return plus < 0 ? line : line[..plus];
    }

    /// <summary>
    /// Версия PyTorch для установки CUDA-сборки. Если PyTorch ещё не стоит (движки
    /// подсказок ни разу не запускались), сначала ставится обычная CPU-сборка вместе
    /// с зависимостями: без неё неизвестно, какую версию скачивать с индекса PyTorch.
    /// </summary>
    private static string EnsureTorchInstalled(string python, string environment)
    {
        var version = TorchBaseVersion(python);
        if (version.Length > 0) return version;

        SetupProgress?.Invoke("PyTorch ещё не установлен. Ставлю базовую сборку для процессора…");
        var install = InstallPipPackages(python, environment, "Установка PyTorch",
            "--index-url", "https://download.pytorch.org/whl/cpu", "torch");
        if (install.ExitCode != 0)
        {
            LogError("Не удалось установить базовую сборку PyTorch", install.Output);
            throw new InvalidOperationException("Не удалось установить PyTorch для движков подсказок. "
                + (IsTransientFileLock(install.Output)
                    ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                    : "Проверьте интернет и место на диске. Журнал: " + LogFilePath));
        }

        version = TorchBaseVersion(python);
        if (version.Length == 0)
            throw new InvalidOperationException("PyTorch установлен, но его версия не определяется. "
                + "Нажмите «Проверить» или «Загрузить ресурсы заново». Подробности: " + LogFilePath);
        return version;
    }

    // Предел драйвера: строка вида «CUDA Version: 12.6» или «CUDA UMD Version: 13.4».
    // Без видеокарты и без драйвера возвращает null — тогда сборку выбирает pip.
    private static (int Major, int Minor)? NvidiaDriverCuda()
    {
        try
        {
            // Обычный вывод nvidia-smi: в нём есть строка «CUDA UMD Version: 13.4».
            // Запрос --query-gpu её не печатает, поэтому спрашиваем полный отчёт.
            var run = RunCommand("nvidia-smi");
            var match = System.Text.RegularExpressions.Regex.Match(run.Output, @"CUDA(?: UMD)? Version:\s*(\d+)\.(\d+)");
            if (match.Success)
                return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
        catch (Exception error)
        {
            LogReview("Драйвер NVIDIA не опрошен: " + error.GetBaseException().Message);
        }
        return null;
    }

    private static string LastJsonLine(string output) =>
        output.Split('\n').Select(text => text.Trim())
            .LastOrDefault(text => text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
        ?? "";

    private static string FirstOutputLine(string output)
    {
        var line = output.Split('\n').Select(text => text.Trim()).FirstOrDefault(text => text.Length > 0) ?? "";
        return line.Length > 300 ? line[..300] : line;
    }

    // Trims leading control characters (notably a stray NUL emitted by onnxruntime on the
    // shared stderr handle) so protocol prefixes are still recognised, while keeping the
    // rest of the line — including meaningful Cyrillic text — untouched. Общий для обоих
    // воркеров: и onnxruntime, и torch умеют писать в stderr напрямую.
    internal static string TrimProtocolPrefix(string line)
    {
        var start = 0;
        while (start < line.Length && (line[start] == '\0' || line[start] == '\uFEFF' || line[start] == '\u200B'))
            start++;
        return start == 0 ? line : line[start..];
    }

    private static void ConfigureResourceEnvironment(ProcessStartInfo start)
    {
        var modelRoot = ModelCacheDirectory;
        var pipCache = Path.Combine(DataDirectory, "кэш", "pip");
        var temporary = Path.Combine(DataDirectory, "временные-файлы");
        var nativeTemporary = Path.Combine(Path.GetTempPath(), "DSR-temp");
        foreach (var folder in new[]
        {
            modelRoot,
            Path.Combine(modelRoot, "huggingface", "hub"),
            Path.Combine(modelRoot, "huggingface", "transformers"),
            Path.Combine(modelRoot, "torch"),
            pipCache,
            Path.Combine(DataDirectory, "кэш", "xdg"),
            temporary,
            nativeTemporary
        }) Directory.CreateDirectory(folder);
        start.Environment["PIP_CACHE_DIR"] = pipCache;
        start.Environment["HF_HOME"] = Path.Combine(modelRoot, "huggingface");
        start.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(modelRoot, "huggingface", "hub");
        start.Environment["TRANSFORMERS_CACHE"] = Path.Combine(modelRoot, "huggingface", "transformers");
        start.Environment["TORCH_HOME"] = Path.Combine(modelRoot, "torch");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(DataDirectory, "кэш", "xdg");
        // python-crfsuite's Windows C extension cannot open model paths containing
        // non-ASCII characters. Keep native temp staging ASCII-only; application
        // resources and model cache themselves remain alongside DSR.exe.
        start.Environment["TEMP"] = nativeTemporary;
        start.Environment["TMP"] = nativeTemporary;
    }

    private static (int ExitCode, string Output) RunCommand(string executable, params string[] arguments)
    {
        Directory.CreateDirectory(DataDirectory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        ConfigureResourceEnvironment(start);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return RunCommand(start);
    }

    private static (int ExitCode, string Output) RunCommand(ProcessStartInfo start)
    {
        Directory.CreateDirectory(DataDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Не удалось запустить {start.FileName}.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            ReportSetupOutput(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderr.AppendLine(e.Data);
            ReportSetupOutput(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return (process.ExitCode, (stdout.ToString() + Environment.NewLine + stderr.ToString()).Trim());
    }

    internal static GpuProbeResult ProbeGpu()
    {
        var script = ExtractEmbeddedFile("DSR.ruaccent_worker.py", "ruaccent_worker.py");
        var python = EnsurePythonRuntime();
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        ConfigureResourceEnvironment(start);
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--probe-gpu");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Python для проверки GPU.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        var error = errorTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            return new GpuProbeResult(false, string.IsNullOrWhiteSpace(error) ? "Проверка GPU завершилась ошибкой." : error.Trim());
        // Ignore any native/Python ANSI warning accidentally written before or after
        // the protocol response. Parse the JSON object containing our expected fields.
        var jsonStart = output.IndexOf('{');
        while (jsonStart >= 0)
        {
            var jsonEnd = output.LastIndexOf('}');
            if (jsonEnd > jsonStart)
            {
                try
                {
                    using var result = JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
                    if (result.RootElement.TryGetProperty("supported", out var supportedValue)
                        && result.RootElement.TryGetProperty("message", out var messageValue))
                    {
                        var supported = supportedValue.GetBoolean();
                        var message = messageValue.GetString() ?? "Статус GPU не получен.";
                        var canInstall = result.RootElement.TryGetProperty("can_install_gpu_runtime", out var installValue)
                            && installValue.GetBoolean();
                        return new GpuProbeResult(supported, message, canInstall);
                    }
                }
                catch (JsonException)
                {
                    // Try the next opening brace in case a warning contained braces.
                }
            }
            jsonStart = output.IndexOf('{', jsonStart + 1);
        }
        var preview = output.Length > 200 ? output[..200] : output;
        return new GpuProbeResult(false, $"Проверка GPU вернула некорректный ответ. {preview}");
    }

    internal static void PrepareInteractive(string model, string device, Action<string> report)
    {
        EnsureBundledData(includeAddStress: true);
        report("Словари распакованы из DSR.exe в папку ресурсов.");
        _ = EnsurePythonRuntime();
        report("Python и зависимости проверены.");

        // Модель скачивается при первом запуске worker. Если загрузка оборвалась,
        // каталог мог остаться неполным — убираем его и повторяем, иначе библиотека
        // навсегда считает модель установленной и падает с «model.onnx не существует».
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var accentizer = new RuAccentWorker(model, true, device, report);
                accentizer.Start();
                _ = accentizer.Process("Проверка готовности.");
                if (!IsModelPrepared(model))
                    throw new InvalidOperationException($"Модель {model} скачалась не полностью.");
                report($"Подготовка завершена: модель {model}, устройство {device} проверены.");
                return;
            }
            catch (Exception error)
            {
                lastError = error;
                RemoveIncompleteRuAccentModels(model);
                if (attempt >= 3) break;
                report($"Модель {model} не загрузилась (попытка {attempt} из 3). Убираю недокачанные файлы и повторяю…");
                Thread.Sleep(TimeSpan.FromSeconds(3 * attempt));
            }
        }
        throw new InvalidOperationException(
            $"Не удалось скачать и проверить модель {model}. Проверьте интернет и свободное место на диске. "
            + "Откройте «Проверить ресурсы» или «Загрузить ресурсы заново». "
            + "Подробности: " + LogFilePath, lastError);
    }

    private static IReadOnlyList<string> ReadRuAccentTextNodes(string input, byte[] sourceBytes, string extension)
    {
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return DecodeText(sourceBytes).Split('\n').Select(line => line.EndsWith("\r", StringComparison.Ordinal) ? line[..^1] : line)
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        _ = new Fb2Parser().Parse(sourceBytes);
        var document = XDocument.Parse(DecodeXml(sourceBytes), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
        var ns = root.Name.Namespace;
        var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#')).Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
        return root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct()
            .Select(node => node.Value).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
    }

    internal static void ConvertWithSileroInteractive(string input, string output, string device, bool overwriteAccents,
        IProgress<(int Completed, int Total, string Message)> progress, ConversionControl control,
        string? comparisonPath = null, string? reviewPath = null, string? statisticsPath = null,
        string? comparisonReportPath = null, string? comparisonStatisticsPath = null)
    {
        var timer = Stopwatch.StartNew();
        var sourceBytes = File.ReadAllBytes(input);
        var extension = Path.GetExtension(input);
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Silero поддерживает только FB2 и TXT.");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Результат Silero должен быть отдельным файлом: исходник нельзя перезаписать.");
        var sourceText = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ? DecodeText(sourceBytes) : DecodeXml(sourceBytes);
        var diagnostics = new ConversionDiagnostics
        {
            Input = Path.GetFullPath(input), Output = Path.GetFullPath(output), Format = extension.ToUpperInvariant(),
            Model = "silero-stress", Device = device, InputBytes = sourceBytes.LongLength, CharacterCount = sourceText.Length
        };
        var startedAt = DateTimeOffset.Now;
        var temporary = output + ".silero.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        SileroStressWorker? workerToDispose = null;
        try
        {
            control.Checkpoint();
            progress.Report((0, 0, "Проверяю локальный Python и зависимости…"));
            var python = EnsurePythonRuntime();
            var installError = EnsureSileroRuntimeOrError(python);
            if (installError != null) throw new InvalidOperationException(installError);
            progress.Report((0, 0, "Читаю результаты RUAccent и готовлю сравнение…"));
            // Для сравнения берём текст результата RUAccent (comparisonPath), а Silero
            // прогоняем по исходной книге (input). Обе книги построены из одной структуры,
            // поэтому текстовые фрагменты идут в одинаковом порядке.
            var referencePath = comparisonPath ?? input;
            var referenceBytes = string.Equals(Path.GetFullPath(referencePath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase)
                ? sourceBytes : File.ReadAllBytes(referencePath);
            var references = ReadRuAccentTextNodes(referencePath, referenceBytes, extension);
            if (references.Count == 0) throw new InvalidDataException("В книге нет текстовых фрагментов для сравнения.");
            progress.Report((0, 0, "Загружаю модель silero-stress…"));
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in UserDictionary.Value) replacements[pair.Key] = pair.Value;
            foreach (var pair in YoDictionary.Value)
                replacements.TryAdd(NormalizeWordLetters(pair.Key).ToLowerInvariant(), pair.Value);
            var disagreements = new List<WordReviewEntry>();
            // Слова, ударение в которых не поставила ни одна модель: отдельная категория
            // «нераспознанных», её тоже показываем в сводке и в ручной доводке.
            var unrecognized = new List<WordReviewEntry>();

            var worker = new SileroStressWorker(device, message => progress.Report((0, 0, message)), diagnostics);
            workerToDispose = worker;
            worker.EnsureStarted();
            var processable = new List<(string Source, Action<string> Set)>();
            XDocument? document = null;
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                var text = DecodeText(sourceBytes);
                var lines = text.Split('\n');
                diagnostics.TotalUnits = lines.Length;
                var outputLines = new string[lines.Length];
                for (var index = 0; index < lines.Length; index++)
                {
                    var original = lines[index];
                    var ending = original.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
                    var content = ending.Length == 0 ? original : original[..^1];
                    outputLines[index] = original;
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    var captured = index;
                    processable.Add((content, converted => outputLines[captured] = converted + ending));
                }
                ProcessSileroTextNodes(processable, replacements, worker, overwriteAccents, control, diagnostics, progress, disagreements, unrecognized, references);
                File.WriteAllBytes(temporary, EncodeText(string.Join("\n", outputLines), sourceBytes));
            }
            else
            {
                _ = new Fb2Parser().Parse(sourceBytes);
                document = XDocument.Parse(DecodeXml(sourceBytes), LoadOptions.PreserveWhitespace);
                var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
                var ns = root.Name.Namespace;
                var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
                    .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
                    .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#'))
                    .Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
                var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
                    .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
                var nodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
                    .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct().ToArray();
                var sourceTexts = nodes.Select(node => node.Value).ToArray();
                diagnostics.TotalUnits = sourceTexts.Length;
                for (var index = 0; index < nodes.Length; index++)
                {
                    if (string.IsNullOrWhiteSpace(sourceTexts[index])) continue;
                    var node = nodes[index];
                    processable.Add((sourceTexts[index], converted => node.Value = converted));
                }
                ProcessSileroTextNodes(processable, replacements, worker, overwriteAccents, control, diagnostics, progress, disagreements, unrecognized, references);
                if (document.Declaration != null) document.Declaration.Encoding = "utf-8";
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false), Indent = false,
                    OmitXmlDeclaration = document.Declaration == null, NewLineHandling = NewLineHandling.None
                })) document.Save(writer);
                _ = new Fb2Parser().Parse(File.ReadAllBytes(temporary));
            }
            control.Checkpoint();
            if (File.Exists(temporary)) File.Move(temporary, output, overwrite: true);
            else if (!File.Exists(output)) throw new IOException("Silero не создал выходной файл.");
            control.Checkpoint();
            diagnostics.Status = "Завершено";
            diagnostics.OutputBytes = new FileInfo(output).Length;
            diagnostics.UnrecognizedWordCount = CountReviewWords(diagnostics.Events.Where(item => item.Contains("Не распознаны:", StringComparison.Ordinal)));
            diagnostics.DisagreementWordCount = disagreements.Count;
            diagnostics.BothUnrecognizedWordCount = unrecognized.Count;
            diagnostics.PerformanceEvents.Add("silero-stress: " + worker.LoadedDevice);
            diagnostics.PerformanceEvents.AddRange(worker.PerformanceEvents);
            worker.Dispose();
            timer.Stop();
            diagnostics.Elapsed = timer.Elapsed;
            WriteWordReview(output, diagnostics.Events.Where(item => item.StartsWith("Фрагмент: ", StringComparison.Ordinal)).ToList(), reviewPath);
            // Natasha-якорь: для расхождений, где словарь+морфология дают однозначный ответ,
            // совпадающий с одним из вариантов моделей, убираем слово из списка.
            try
            {
                var anchorWords = disagreements.Select(d => d.Word).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (anchorWords.Count > 0)
                {
                    var anchorMap = RunNatashaAnchor(anchorWords, DictionaryDirectory);
                    var toRemove = new HashSet<int>();
                    for (var i = 0; i < disagreements.Count; i++)
                    {
                        var entry = disagreements[i];
                        if (!anchorMap.TryGetValue(entry.Word, out var anchor) || string.IsNullOrWhiteSpace(anchor)) continue;
                        if (!IsValidCorrection(entry.Word, anchor)) continue;
                        if ((entry.RuAccent != null && ReviewWordKey(anchor) == ReviewWordKey(entry.RuAccent)) ||
                            (entry.Silero != null && ReviewWordKey(anchor) == ReviewWordKey(entry.Silero)))
                        {
                            toRemove.Add(i);
                        }
                    }
                    if (toRemove.Count > 0)
                    {
                        for (var i = disagreements.Count - 1; i >= 0; i--)
                            if (toRemove.Contains(i)) disagreements.RemoveAt(i);
                    }
                }
            }
            catch (Exception ex) { LogError("Natasha-якорь", ex); }
            WriteDisagreementReview(output, disagreements, unrecognized, comparisonReportPath,
                diagnostics.ComparedWordCount, diagnostics.MatchedWordCount);
            WriteConversionReport(output, diagnostics, startedAt, statisticsPath);
            if (!string.IsNullOrWhiteSpace(comparisonStatisticsPath))
                WriteComparisonReport(comparisonStatisticsPath!, Path.GetFullPath(input), Path.GetFullPath(referencePath), Path.GetFullPath(output),
                    worker.LoadedDevice, diagnostics.TotalUnits, diagnostics.ComparedWordCount, diagnostics.MatchedWordCount,
                    diagnostics.DisagreementWordCount, diagnostics.BothUnrecognizedWordCount, timer.Elapsed, "Завершено");
            worker.Dispose();
            workerToDispose = null;
            progress.Report((diagnostics.CompletedUnits, diagnostics.TotalUnits, "Готово: " + output));
        }
        catch (Exception error)
        {
            diagnostics.Status = error is OperationCanceledException ? "Остановлено пользователем" : "Ошибка";
            diagnostics.Events.Add(error.ToString());
            throw;
        }
            finally
            {
                timer.Stop();
                workerToDispose?.Dispose();
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

                /// <summary>
        /// Полный проход по книге одной кнопкой: сначала RUAccent, затем Silero по исходной
        /// книге, затем сравнение. Всё складывается в рабочую папку «DSR_&lt;имя&gt;» рядом с
        /// исходником: отдельно прогон RUAccent со статистикой и списком неударенных слов,
        /// отдельно прогон Silero, отчёт расхождений и статистика сравнения. Исходная книга
        /// не изменяется, поэтому и прогоны, и отчёт всегда можно пересоздать.
        /// </summary>
        internal static BookWorkspace ConvertBookInteractive(string input, string model, string device,
            bool includeIllustrations, string sileroDevice, bool runSilero,
            Action<string> report, Action<int, int> reportProgress, Action<string> stageReport, ConversionControl control)
        {
            var workspace = WorkspaceFor(input);
            workspace.EnsureDirectories();

            control.Checkpoint();
            stageReport("Этап 1 из 2: RUAccent расставляет ударения");
            report("Этап 1 из 2: RUAccent. Прогон по исходной книге.");
            ConvertInteractive(input, workspace.RuAccentBook, model, device, includeIllustrations, report, reportProgress, control,
                workspace.RuAccentReview, workspace.RuAccentStatistics);

            if (!runSilero)
            {
                stageReport("Silero пропущен");
                report("Silero пропущен: для ручной доводки используется список неударенных слов RUAccent.");
                return workspace;
            }

            control.Checkpoint();
            stageReport("Этап 2 из 2: Silero даёт второе мнение и идёт сравнение");
            report("Этап 2 из 2: Silero по исходной книге, затем сравнение с RUAccent.");
            IProgress<(int Completed, int Total, string Message)> sileroProgress = new ActionProgress<(int Completed, int Total, string Message)>(update =>
                report(update.Total > 0 ? $"{update.Message} ({update.Completed:N0}/{update.Total:N0})" : update.Message));
            ConvertWithSileroInteractive(input, workspace.SileroBook, sileroDevice, overwriteAccents: true, sileroProgress, control,
                comparisonPath: workspace.RuAccentBook,
                reviewPath: workspace.SileroReview,
                statisticsPath: workspace.SileroStatistics,
                comparisonReportPath: workspace.ComparisonReport,
                comparisonStatisticsPath: workspace.ComparisonStatistics);
            // Убираем из отчёта RUAccent слова, которые уже попали в сводку расхождений:
            // иначе одно и то же слово висит и в «не распознанных RUAccent», и в расхождениях.
            try
            {
                var excludedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(workspace.ComparisonReport))
                {
                    foreach (var line in File.ReadAllLines(workspace.ComparisonReport, Encoding.UTF8))
                    {
                        if (line.StartsWith("Правка [", StringComparison.Ordinal))
                        {
                            var close = line.IndexOf(']');
                            if (close > 8) excludedWords.Add(ReviewWordKey(line[8..close]));
                        }
                        else if (line.StartsWith("Не распознаны: ", StringComparison.Ordinal))
                        {
                            foreach (var word in line["Не распознаны: ".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                excludedWords.Add(ReviewWordKey(word));
                        }
                    }
                }
                if (excludedWords.Count > 0 && File.Exists(workspace.RuAccentReview))
                {
                    var ruEntries = ReadWordReview(workspace.RuAccentReview);
                    var ruChecked = ReadCheckedWords(workspace.RuAccentReview);
                    var filtered = ruEntries.Where(entry => !excludedWords.Contains(ReviewWordKey(entry.Word))).ToList();
                    if (filtered.Count != ruEntries.Count)
                        File.WriteAllText(workspace.RuAccentReview, FormatWordReview(filtered, ruChecked), new UTF8Encoding(true));
                }
            }
            catch (Exception ex) { LogError("Фильтрация отчёта RUAccent по сводке расхождений", ex); }
            report("Готово: " + workspace.ComparisonReport);
            return workspace;
        }

        // Категории поэтапного сравнения (слова, которых база add-stress не тронула).
        internal static class StagedCategories
        {
            internal const string Disputed = "спорно";              // обе модели, разные гласные
            internal const string Doubtful = "сомнительно";        // поставила только одна модель
            internal const string Unknown = "неизвестно/опечатка"; // обе промолчали
        }

        internal sealed record StagedCompareResult(int Fragments, int Compared, int Matched, int Doubtful, int Disputed, int Unknown);

        /// <summary>
        /// Поэтапный конвейер. Базу создаёт add-stress-to-epub (её метки точные и
        /// неприкосновенные). RUAccent и Silero идут по базе и заполняют только слова,
        /// которые база оставила без ударения и без буквы ё. Затем результаты сравниваются:
        /// совпавшие ударения принимаются, остальное делится на «спорно» (обе модели,
        /// разные гласные), «сомнительно» (поставила только одна) и «неизвестно/опечатка»
        /// (обе промолчали). Итог ручная доводка собирает из базы.
        /// </summary>
        internal static BookWorkspace ConvertStagedBookInteractive(string input, string model, string device, string sileroDevice,
            Action<string> report, Action<int, int> reportProgress, Action<string> stageReport, ConversionControl control)
        {
            var workspace = WorkspaceFor(input);
            workspace.EnsureDirectories();

            // Этап 1. База — add-stress-to-epub.
            control.Checkpoint();
            stageReport("Этап 1 из 4: база (add-stress-to-epub)");
            report("Этап 1 из 4: база — add-stress-to-epub по исходной книге.");
            var baseProgress = new ActionProgress<(int Completed, int Total, string Message)>(update =>
                report(update.Total > 0 ? $"{update.Message} ({update.Completed:N0}/{update.Total:N0})" : update.Message));
            ConvertWithStressAnchorInteractive(input, workspace.BaseBook, "CPU", baseProgress, control);
            if (!File.Exists(workspace.BaseBook)) throw new IOException("add-stress-to-epub не создал базу.");

            // Этап 2. RUAccent по базе — заполняет только слова без ударения.
            control.Checkpoint();
            stageReport("Этап 2 из 4: RUAccent по базе");
            report("Этап 2 из 4: RUAccent по базе — заполняет только слова, оставленные базой без ударения.");
            // RUAccent, как и Silero, получает базу без уже расставленных акутов
            // (буква ё базы сохраняется) — чтобы условия у моделей были равные и
            // чужие ударения не «подсказывали» омографы.
            ConvertInteractive(workspace.BaseBook, workspace.StagedRuAccentBook, model, device, true,
                report, reportProgress, control, workspace.RuAccentReview, workspace.RuAccentStatistics,
                deaccentInput: true);

            // Этап 3. Silero по базе — второй голос по тем же словам.
            control.Checkpoint();
            stageReport("Этап 3 из 4: Silero по базе");
            report("Этап 3 из 4: Silero по базе — второй голос по словам без ударения.");
            IProgress<(int Completed, int Total, string Message)> sileroProgress =
                new ActionProgress<(int Completed, int Total, string Message)>(update =>
                    report(update.Total > 0 ? $"{update.Message} ({update.Completed:N0}/{update.Total:N0})" : update.Message));
            ConvertWithSileroInteractive(workspace.BaseBook, workspace.StagedSileroBook, sileroDevice, overwriteAccents: true,
                sileroProgress, control, reviewPath: workspace.SileroReview, statisticsPath: workspace.SileroStatistics);

            // Этап 4. Сравнение по словам, которых база не тронула.
            control.Checkpoint();
            stageReport("Этап 4 из 4: сравнение (совпало / сомнительно / спорно / опечатка)");
            report("Этап 4 из 4: сравнение RUAccent и Silero по словам без ударения в базе.");
            var entries = new List<WordReviewEntry>();
            var (compareResult, appliedNodes) = CompareStagedBooks(workspace.BaseBook, workspace.StagedRuAccentBook, workspace.StagedSileroBook, entries);
            WriteAppliedBook(workspace.BaseBook, appliedNodes, workspace.StagedAppliedBook);
            WriteStagedReview(workspace, entries, compareResult);
            WriteStagedStatistics(workspace, compareResult, input);
            report("Готово: " + workspace.ComparisonReport);
            return workspace;
        }

        /// <summary>Текстовые узлы книги в порядке обхода — так их видят движки и сравнение.</summary>
        private static string[] ReadBookTextNodes(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                return new[] { DecodeText(bytes) };
            _ = new Fb2Parser().Parse(bytes);
            var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
            var ns = root.Name.Namespace;
            var imageIds = root.Descendants().Where(e => e.Name.Namespace == ns && e.Name.LocalName == "image")
                .Select(e => (string?)e.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
                .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#'))
                .Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
            var binaryNodes = root.DescendantsAndSelf()
                .Where(e => e.Name.Namespace == ns && e.Name.LocalName == "binary" && imageIds.Contains((string?)e.Attribute("id") ?? ""))
                .SelectMany(e => e.DescendantNodes().OfType<XText>()).ToHashSet();
            return root.DescendantsAndSelf()
                .Where(e => e.Name.Namespace == ns && TextElements.Contains(e.Name.LocalName))
                .SelectMany(e => e.DescendantNodes().OfType<XText>())
                .Where(node => !binaryNodes.Contains(node)).Distinct()
                .Select(node => node.Value).ToArray();
        }

        /// <summary>Позиция ударной гласной с учётом акута и буквы ё; null — ударения нет.</summary>
        private static int? StressPositionWithYo(string word)
        {
            var acute = StressVowelPosition(word);
            if (acute != null) return acute;
            var yoIndex = word.IndexOf('ё');
            if (yoIndex < 0) yoIndex = word.IndexOf('Ё');
            if (yoIndex < 0) return null;
            return CountVowels(word[..yoIndex]);
        }

        /// <summary>
        /// Сравнивает базу с результатами RUAccent и Silero и раскладывает слова по
        /// категориям. В сравнение попадают только слова, которых база не тронула:
        /// без акута, без ё, не односложные и не служебные.
        /// </summary>
        private static (StagedCompareResult Result, string[] Applied) CompareStagedBooks(string basePath, string ruPath, string silPath, List<WordReviewEntry> entries)
        {
            var baseNodes = ReadBookTextNodes(basePath);
            var applied = (string[])baseNodes.Clone();
            var ruNodes = ReadBookTextNodes(ruPath);
            var silNodes = ReadBookTextNodes(silPath);
            var fragments = Math.Min(baseNodes.Length, Math.Min(ruNodes.Length, silNodes.Length));
            var compared = 0; var matched = 0; var doubtful = 0; var disputed = 0; var unknown = 0;
            for (var i = 0; i < fragments; i++)
            {
                var baseText = baseNodes[i];
                if (string.IsNullOrWhiteSpace(baseText)) continue;
                var baseWords = WordTokens.Matches(baseText).Cast<System.Text.RegularExpressions.Match>().ToArray();
                var ruWords = WordTokens.Matches(ruNodes[i]).Cast<System.Text.RegularExpressions.Match>().ToArray();
                var silWords = WordTokens.Matches(silNodes[i]).Cast<System.Text.RegularExpressions.Match>().ToArray();
                if (baseWords.Length != ruWords.Length || baseWords.Length != silWords.Length) continue;
                var fills = new List<(int Index, int Length, string Word)>();
                for (var w = 0; w < baseWords.Length; w++)
                {
                    var baseWord = baseWords[w].Value;
                    if (baseWord.IndexOf('\u0301') >= 0) continue;                          // база уже поставила ударение
                    if (baseWord.IndexOf('ё') >= 0 || baseWord.IndexOf('Ё') >= 0) continue;  // буква ё — уже ударная
                    if (NotCyrillicWord.IsMatch(baseWord)) continue;
                    var baseNorm = NormalizeWordLetters(baseWord).ToLowerInvariant();
                    if (UnstressedWords.Contains(baseNorm)) continue;
                    if (CountVowels(baseWord) < 2) continue;                                // односложные / одна гласная
                    var ruWord = ruWords[w].Value;
                    var silWord = silWords[w].Value;
                    if (!NormalizeWordLetters(ruWord).Equals(NormalizeWordLetters(baseWord), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!NormalizeWordLetters(silWord).Equals(NormalizeWordLetters(baseWord), StringComparison.OrdinalIgnoreCase)) continue;
                    compared++;
                    var ruStress = StressPositionWithYo(ruWord);
                    var silStress = StressPositionWithYo(silWord);
                    if (ruStress == null && silStress == null)
                    {
                        unknown++;
                        entries.Add(new WordReviewEntry(baseWord, baseText, baseWord, null, null, false, StagedCategories.Unknown));
                    }
                    else if (ruStress != null && silStress != null)
                    {
                        if (ruStress == silStress)
                        {
                            matched++;
                            // ё информативнее: если одна модель написала ё, берём её вариант.
                            var ruHasYo = ruWord.IndexOf('ё') >= 0 || ruWord.IndexOf('Ё') >= 0;
                            var silHasYo = silWord.IndexOf('ё') >= 0 || silWord.IndexOf('Ё') >= 0;
                            fills.Add((baseWords[w].Index, baseWords[w].Length, ruHasYo || !silHasYo ? ruWord : silWord));
                        }
                        else
                        {
                            disputed++;
                            entries.Add(new WordReviewEntry(baseWord, baseText, baseWord, ruWord, silWord, false, StagedCategories.Disputed));
                        }
                    }
                    else
                    {
                        // Одна модель промолчала. Одинокую букву ё считаем однозначным ответом
                        // и принимаем, а не отправляем в «сомнительно».
                        var ruHasYo = ruWord.IndexOf('ё') >= 0 || ruWord.IndexOf('Ё') >= 0;
                        var silHasYo = silWord.IndexOf('ё') >= 0 || silWord.IndexOf('Ё') >= 0;
                        if ((ruStress != null && ruHasYo) || (silStress != null && silHasYo))
                        {
                            matched++;
                            fills.Add((baseWords[w].Index, baseWords[w].Length, ruStress != null ? ruWord : silWord));
                        }
                        else
                        {
                            doubtful++;
                            entries.Add(new WordReviewEntry(baseWord, baseText, baseWord,
                                ruStress == null ? null : ruWord, silStress == null ? null : silWord, false, StagedCategories.Doubtful));
                        }
                    }
                }
                if (fills.Count > 0)
                {
                    var builder = new StringBuilder(baseText);
                    foreach (var fill in fills.OrderByDescending(item => item.Index))
                    {
                        builder.Remove(fill.Index, fill.Length);
                        builder.Insert(fill.Index, fill.Word);
                    }
                    applied[i] = builder.ToString();
                }
            }
            return (new StagedCompareResult(fragments, compared, matched, doubtful, disputed, unknown), applied);
        }

        /// <summary>Записывает промежуточный файл «база + совпадения» для ручной доводки.</summary>
        private static void WriteAppliedBook(string basePath, IReadOnlyList<string> nodes, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var extension = Path.GetExtension(basePath);
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                var sourceBytes = File.ReadAllBytes(basePath);
                File.WriteAllBytes(outputPath, EncodeText(nodes.Count > 0 ? nodes[0] : string.Empty, sourceBytes));
                return;
            }
            var bytes = File.ReadAllBytes(basePath);
            var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой элемент.");
            var ns = root.Name.Namespace;
            var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
                .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
                .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#'))
                .Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
            var binaryNodes = root.DescendantsAndSelf()
                .Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
                .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
            var targets = root.DescendantsAndSelf()
                .Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
                .SelectMany(element => element.DescendantNodes().OfType<XText>())
                .Where(node => !binaryNodes.Contains(node)).Distinct().ToArray();
            var count = Math.Min(targets.Length, nodes.Count);
            for (var i = 0; i < count; i++) targets[i].Value = nodes[i];
            using var writer = XmlWriter.Create(outputPath, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = document.Declaration == null,
                NewLineHandling = NewLineHandling.None
            });
            document.Save(writer);
        }

        private static void WriteStagedReview(BookWorkspace workspace, IReadOnlyList<WordReviewEntry> entries, StagedCompareResult counts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(workspace.ComparisonReport))!);
            var checkedWords = File.Exists(workspace.ComparisonReport)
                ? ReadCheckedWords(workspace.ComparisonReport)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disputed = entries.Where(e => e.Category == StagedCategories.Disputed).ToArray();
            var doubtful = entries.Where(e => e.Category == StagedCategories.Doubtful).ToArray();
            var unknown = entries.Where(e => e.Category == StagedCategories.Unknown).ToArray();
            File.WriteAllText(workspace.ComparisonReport,
                FormatStagedReview(disputed, doubtful, unknown, counts, checkedWords, workspace.BaseBook), new UTF8Encoding(true));
        }

        internal static string FormatStagedReview(IReadOnlyList<WordReviewEntry> disputed, IReadOnlyList<WordReviewEntry> doubtful,
            IReadOnlyList<WordReviewEntry> unknown, StagedCompareResult counts, IReadOnlySet<string>? checkedWords, string baseBook)
        {
            var output = new StringBuilder();
            output.AppendLine("DSR — поэтапная сводка (база add-stress + RUAccent + Silero)");
            output.AppendLine($"База (эталон): {baseBook}");
            output.AppendLine("Метки базы не переспрашиваются. RUAccent и Silero заполняют только слова, оставленные базой без ударения и без буквы ё.");
            output.AppendLine($"Проверено слов без ударения в базе: {counts.Compared:N0}; совпало и принято в итог: {counts.Matched:N0}.");
            output.AppendLine($"Спорно (обе модели, разные гласные): {disputed.Count:N0}.");
            output.AppendLine($"Сомнительно (ударение поставила только одна модель): {doubtful.Count:N0}.");
            output.AppendLine($"Неизвестно / возможна опечатка (обе промолчали): {unknown.Count:N0}.");
            output.AppendLine("Ниже — только то, что нужно решить вручную или подсказкой ИИ. Совпавшие слова уже в базе.");
            output.AppendLine();
            var stamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            if (disputed.Count > 0)
            {
                output.AppendLine("=== СПОРНО: обе модели поставили ударение на разные гласные ===").AppendLine();
                AppendReviewSection(output, disputed, "Разошлись: ", checkedWords, stamp);
            }
            if (doubtful.Count > 0)
            {
                output.AppendLine("=== СОМНИТЕЛЬНО: ударение поставила только одна модель ===").AppendLine();
                AppendReviewSection(output, doubtful, "Разошлись: ", checkedWords, stamp);
            }
            if (unknown.Count > 0)
            {
                output.AppendLine("=== НЕИЗВЕСТНО / ВОЗМОЖНА ОПЕЧАТКА: обе модели промолчали ===").AppendLine();
                AppendReviewSection(output, unknown, "Не распознаны: ", checkedWords, stamp);
            }
            if (disputed.Count == 0 && doubtful.Count == 0 && unknown.Count == 0)
                output.AppendLine("Расхождений нет: по словам без ударения в базе модели совпали или база уже всё расставила.");
            return output.ToString();
        }

        private static void WriteStagedStatistics(BookWorkspace workspace, StagedCompareResult counts, string input)
        {
            try
            {
                var text = new StringBuilder();
                text.AppendLine("DSR — поэтапный конвейер (add-stress → RUAccent + Silero → сравнение)");
                text.AppendLine($"Дата: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
                text.AppendLine($"Исходная книга: {Path.GetFullPath(input)}");
                text.AppendLine($"База: {workspace.BaseBook}");
                text.AppendLine($"База + совпадения: {workspace.StagedAppliedBook}");
                text.AppendLine($"RUAccent по базе: {workspace.StagedRuAccentBook}");
                text.AppendLine($"Silero по базе: {workspace.StagedSileroBook}");
                text.AppendLine($"Проверено фрагментов: {counts.Fragments:N0}");
                text.AppendLine($"Слов без ударения в базе: {counts.Compared:N0}");
                text.AppendLine($"Совпало и принято: {counts.Matched:N0}");
                text.AppendLine($"Спорно: {counts.Disputed:N0}");
                text.AppendLine($"Сомнительно: {counts.Doubtful:N0}");
                text.AppendLine($"Неизвестно/опечатка: {counts.Unknown:N0}");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(workspace.ComparisonStatistics))!);
                File.WriteAllText(workspace.ComparisonStatistics, text.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { LogError("Поэтапная статистика", ex); }
        }

        // Простой синхронный IProgress: Progress<T> пересылает вызовы через контекст
        // синхронизации и на фоне может менять их порядок, а здесь нужен точный порядок
        // сообщений о ходе прогона.
        private sealed class ActionProgress<T> : IProgress<T>
        {
            private readonly Action<T> _action;
            internal ActionProgress(Action<T> action) => _action = action;
            public void Report(T value) => _action(value);
        }

        private static string RestoreSourceCapitalization(string source, string converted)
    {
        var sourceWords = WordTokens.Matches(source).Cast<System.Text.RegularExpressions.Match>().ToArray();
        var outputWords = WordTokens.Matches(converted).Cast<System.Text.RegularExpressions.Match>().ToArray();
        if (sourceWords.Length != outputWords.Length) return converted;
        var result = new StringBuilder(converted.Length);
        var last = 0;
        for (var index = 0; index < sourceWords.Length; index++)
        {
            var sourceWord = sourceWords[index].Value;
            var outputWord = outputWords[index];
            result.Append(converted, last, outputWord.Index - last);
            var replacement = outputWord.Value;
            if (sourceWord.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))) replacement = replacement.ToUpperInvariant();
            else if (char.IsUpper(sourceWord[0])) replacement = UppercaseFirstLetter(replacement);
            result.Append(replacement);
            last = outputWord.Index + outputWord.Length;
        }
        result.Append(converted, last, converted.Length - last);
        return result.ToString();
    }

    private static void ProcessSileroTextNodes(IReadOnlyList<(string Source, Action<string> Set)> nodes,
        IReadOnlyDictionary<string, string> replacements, SileroStressWorker worker, bool overwriteAccents,
        ConversionControl control, ConversionDiagnostics diagnostics, IProgress<(int Completed, int Total, string Message)> progress,
        List<WordReviewEntry> disagreements, List<WordReviewEntry> unrecognized, IReadOnlyList<string> references)
    {
        diagnostics.TotalUnits = nodes.Count;
        // Сравнивать можно только при одинаковом числе фрагментов: обе книги построены из
        // одной структуры, но если структура разошлась, лучше пропустить сравнение, чем
        // приписать слову чужой фрагмент.
        var comparable = references.Count == nodes.Count;
        for (var index = 0; index < nodes.Count; index++)
        {
            control.Checkpoint();
            var source = nodes[index].Source;
            // Silero всегда получает текст без старой острой отметки: иначе его вариант
            // нельзя честно сравнить с уже размеченным словом RUAccent. Пользовательский
            // словарь не вмешивается в предсказание; принятые пользователем формы применяются ниже.
            var inferenceInput = source.Replace("\u0301", "", StringComparison.Ordinal);
            var response = worker.Send(JsonSerializer.Serialize(new[] { inferenceInput }), CancellationToken.None);
            using var parsed = JsonDocument.Parse(response);
            if (parsed.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString() ?? "Ошибка silero-stress.");
            string marked;
            var responseRoot = parsed.RootElement;
            if (responseRoot.ValueKind == JsonValueKind.Array)
            {
                var first = responseRoot.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("text", out var textValue))
                    throw new InvalidDataException("Silero вернул пустой или некорректный результат.");
                marked = textValue.GetString() ?? inferenceInput;
            }
            else if (responseRoot.TryGetProperty("text", out var objectTextValue)) marked = objectTextValue.GetString() ?? inferenceInput;
            else throw new InvalidDataException("Silero вернул неожиданный формат результата.");
            var valid = EnsureOriginalPunctuation(inferenceInput, marked);
            valid = ProtectUnstressedWords(valid);
            valid = RestoreSourceCapitalization(inferenceInput, valid);
            // Existing explicit marks take priority in the default second-opinion mode;
            // align tokens by order and keep the complete RUAccent token when already marked.
            var sourceMatches = WordTokens.Matches(source).Cast<System.Text.RegularExpressions.Match>().ToArray();
            var outputMatches = WordTokens.Matches(valid).Cast<System.Text.RegularExpressions.Match>().ToArray();
            if (sourceMatches.Length != outputMatches.Length)
            {
                // Silero иногда меняет число слов (дефисы, числа, сокращения, пунктуация):
                // записывать такой фрагмент нельзя, иначе сломается разметка. Пропускаем
                // фрагмент, оставляя исходный текст, и продолжаем прогон, а не рвём его.
                diagnostics.Events.Add($"Фрагмент: {source} Не распознаны: Silero изменил число слов; фрагмент оставлен без изменений.");
                diagnostics.CompletedUnits = index + 1;
                if (diagnostics.CompletedUnits == nodes.Count || diagnostics.CompletedUnits % 10 == 0)
                {
                    progress.Report((diagnostics.CompletedUnits, nodes.Count,
                        $"Silero обрабатывает фрагменты: {diagnostics.CompletedUnits:N0}/{nodes.Count:N0}"));
                    LogConversion(diagnostics.Input, diagnostics.InputBytes, diagnostics.CharacterCount, TimeSpan.Zero, null,
                        $"Silero {diagnostics.CompletedUnits}/{nodes.Count}");
                }
                continue;
            }
            var rebuilt = new StringBuilder(valid.Length + 8);
            var last = 0;
            for (var wordIndex = 0; wordIndex < sourceMatches.Length; wordIndex++)
            {
                var match = outputMatches[wordIndex];
                rebuilt.Append(valid, last, match.Index - last);
                var replacement = match.Value;
                var sourceWord = sourceMatches[wordIndex].Value;
                var normalized = NormalizeWordLetters(sourceWord).ToLowerInvariant();
                if (replacements.TryGetValue(normalized, out var dictionaryForm)
                    && NormalizeWordLetters(dictionaryForm).Equals(NormalizeWordLetters(sourceWord), StringComparison.OrdinalIgnoreCase))
                    replacement = KeepFirstLetterCase(sourceWord, dictionaryForm);
                else if (!overwriteAccents && sourceWord.Contains('\u0301')) replacement = sourceWord;
                rebuilt.Append(replacement);
                last = match.Index + match.Length;
            }
            rebuilt.Append(valid, last, valid.Length - last);
            var finalText = rebuilt.ToString();
            nodes[index].Set(finalText);
            // Сравниваем результат RUAccent (references) с результатом Silero (valid).
            // Исходный фрагмент нужен только для инференса и уже не связан с разметкой.
            if (comparable)
            {
                var comparison = CollectModelDisagreements(references[index], references[index], valid, disagreements, unrecognized);
                diagnostics.ComparedWordCount += comparison.Compared;
                diagnostics.MatchedWordCount += comparison.Matched;
            }
            var excludedForUnrecognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in disagreements) excludedForUnrecognized.Add(NormalizeWordLetters(entry.Word).ToLowerInvariant());
            foreach (var entry in unrecognized) excludedForUnrecognized.Add(NormalizeWordLetters(entry.Word).ToLowerInvariant());
            _ = CollectUnrecognizedWords(source, finalText, diagnostics.Events, excludedForUnrecognized);
            diagnostics.CompletedUnits = index + 1;
            if (diagnostics.CompletedUnits == nodes.Count || diagnostics.CompletedUnits % 10 == 0)
            {
                progress.Report((diagnostics.CompletedUnits, nodes.Count,
                    $"Silero обрабатывает фрагменты: {diagnostics.CompletedUnits:N0}/{nodes.Count:N0}"));
                LogConversion(diagnostics.Input, diagnostics.InputBytes, diagnostics.CharacterCount, TimeSpan.Zero, null,
                    $"Silero {diagnostics.CompletedUnits}/{nodes.Count}");
            }
        }
    }

    internal static void ConvertInteractive(string input, string output, string model, string device,
        bool includeIllustrations, Action<string> report, Action<int, int> reportProgress, ConversionControl control,
        string? reviewPath = null, string? statisticsPath = null, bool deaccentInput = false)
    {
        var bytes = new FileInfo(input).Length;
        var sourceBytes = File.ReadAllBytes(input);
        var sourceText = Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? DecodeText(sourceBytes)
            : DecodeXml(sourceBytes);
        var diagnostics = new ConversionDiagnostics
        {
            Input = Path.GetFullPath(input), Output = Path.GetFullPath(output),
            Format = Path.GetExtension(input).ToUpperInvariant(), Model = model, Device = device,
            InputBytes = bytes, CharacterCount = sourceText.Length
        };
        var conversionTimer = Stopwatch.StartNew();
        var startTimestamp = DateTimeOffset.Now;
        try
        {
            control.Checkpoint();
            report($"DIAGNOSTICS:{Path.GetFullPath(output)}");
            if (Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                ConvertTextFile(input, output, model, device, report, reportProgress, control, diagnostics, reviewPath, deaccentInput);
            else
            {
                using var accentizer = new RuAccentWorker(model, true, device, report, diagnostics);
                accentizer.Start();
                control.Checkpoint();
                ConvertFile(input, output, accentizer, includeIllustrations, reportProgress, control, diagnostics, reviewPath, deaccentInput);
                report($"Готово: {output}");
            }
            diagnostics.Status = "Завершено";
        }
        catch (OperationCanceledException)
        {
            diagnostics.Status = "Остановлено пользователем";
            diagnostics.OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Status = "Ошибка";
            diagnostics.OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            diagnostics.Events.Add("Критическая ошибка: " + ex);
            throw;
        }
        finally
        {
            conversionTimer.Stop();
            diagnostics.Elapsed = conversionTimer.Elapsed;
            using (var currentProcess = Process.GetCurrentProcess())
            {
                diagnostics.PeakWorkingSetBytes = Math.Max(diagnostics.PeakWorkingSetBytes, currentProcess.PeakWorkingSet64);
                diagnostics.CpuPercent = conversionTimer.Elapsed.TotalMilliseconds > 0
                    ? currentProcess.TotalProcessorTime.TotalMilliseconds * 100 / conversionTimer.Elapsed.TotalMilliseconds / Environment.ProcessorCount
                    : 0;
            }
            if (diagnostics.PerformanceEvents.Count == 0)
                diagnostics.Events.Add("Worker не сообщил данные о фактически загруженных ONNX execution providers.");
            WriteConversionReport(output, diagnostics, startTimestamp, statisticsPath);
        }
    }

    private static void WriteConversionReport(string outputPath, ConversionDiagnostics diagnostics, DateTimeOffset startTimestamp, string? reportPath = null)
    {
        reportPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, Path.GetFileNameWithoutExtension(outputPath) + "_статистика.txt");
        var report = new StringBuilder();
        report.AppendLine("DSR — отчёт конвертации и диагностики");
        report.AppendLine($"Дата начала: {startTimestamp:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Дата завершения отчёта: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Статус: {diagnostics.Status}");
        report.AppendLine($"Вход: {diagnostics.Input}");
        report.AppendLine($"Выход: {diagnostics.Output}");
        report.AppendLine($"Формат: {diagnostics.Format}");
        report.AppendLine($"Модель: {diagnostics.Model}; устройство: {diagnostics.Device}");
        report.AppendLine($"Входной объём: {diagnostics.InputBytes} байт ({diagnostics.InputBytes / 1024d / 1024d:0.000} МБ)");
        report.AppendLine($"Выходной объём: {diagnostics.OutputBytes} байт ({diagnostics.OutputBytes / 1024d / 1024d:0.000} МБ)");
        report.AppendLine($"Символов текста для обработки: {diagnostics.CharacterCount:N0}");
        report.AppendLine($"Символов внутри FB2 binary/изображений (пропущено): {diagnostics.ImagePayloadCharacterCount:N0}");
        report.AppendLine($"Обработано фрагментов: {diagnostics.CompletedUnits:N0}/{diagnostics.TotalUnits:N0}");
        report.AppendLine($"Ошибок отдельных фрагментов: {diagnostics.FailedUnits:N0}");
        report.AppendLine($"Нераспознанных слов в отчёте: {diagnostics.UnrecognizedWordCount:N0}");
        report.AppendLine(diagnostics.Model.Equals("silero-stress", StringComparison.OrdinalIgnoreCase)
            ? $"Дополнительно подставлено ударений из словаря RUAccent: {diagnostics.DictionaryStressCount:N0}"
            : $"Ударений подставлено из словаря RUAccent: {diagnostics.DictionaryStressCount:N0}");
        report.AppendLine($"Время обработки: {FormatDuration(diagnostics.Elapsed)}");
        report.AppendLine($"Скорость: {(diagnostics.Elapsed.TotalSeconds > 0 ? diagnostics.CharacterCount / diagnostics.Elapsed.TotalSeconds : 0):0.0} символов/с; {(diagnostics.Elapsed.TotalSeconds > 0 ? diagnostics.CompletedUnits / diagnostics.Elapsed.TotalSeconds : 0):0.00} фрагментов/с");
        report.AppendLine($"Пик RAM процесса: {diagnostics.PeakWorkingSetBytes / 1024d / 1024d:0.0} МБ");
        report.AppendLine($"Инференс ONNX: {FormatDuration(diagnostics.InferenceElapsed)} всего, {diagnostics.InferenceCalls:N0} вызов");
        report.AppendLine($"ONNX sessions: {string.Join("; ", diagnostics.PerformanceEvents)}");
        report.AppendLine($"Процесс: {Environment.ProcessPath}; ОС: {Environment.OSVersion}; .NET: {Environment.Version}");
        report.AppendLine($"Процессор: {Environment.ProcessorCount} логических ядер");
        report.AppendLine("События / исключения:");
        if (diagnostics.Events.Count == 0) report.AppendLine("Нет.");
        else foreach (var item in diagnostics.Events) report.AppendLine(item);
        try { File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { LogError($"Не удалось записать отчёт диагностики {reportPath}", ex); }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (long)duration.TotalHours;
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static void ConvertTextFile(string inputPath, string outputPath, string model, string device,
    Action<string> report, Action<int, int> reportProgress, ConversionControl control, ConversionDiagnostics diagnostics,
    string? reviewPath = null, bool deaccentInput = false)
    {
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var lines = text.Split('\n');
        diagnostics.TotalUnits = lines.Length;
        diagnostics.CharacterCount = text.Length;
        diagnostics.InputBytes = sourceBytes.LongLength;
        var conversionTimer = Stopwatch.StartNew();
        var bytesIn = sourceBytes.LongLength;
        var wordReview = new List<string>();
        // Polysyllabic words the model left unmarked, de-duplicated by dictionary key.
        var residueWords = new HashSet<string>(StringComparer.Ordinal);
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        accentizer.EnsureStarted();
        reportProgress(0, lines.Length);
        var result = new StringBuilder(text.Length + 64);
        var completed = 0;
        TimeSpan? estimatedRemaining = null;
        var contents = new string[lines.Length];
        var endings = new string[lines.Length];
        var accentedLines = new string?[lines.Length];
        // Lines that were accented successfully; failed lines are left untouched and
        // must never receive a dictionary stress mark or reappear in the review.
        var reviewable = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            contents[i] = ending.Length == 0 ? line : line[..^1];
            endings[i] = ending;
        }
        // Batch independent lines so the worker can accent them concurrently
        // instead of one blocking request/response round-trip per line.
        const int inferenceBatchSize = 128;
        for (var batchStart = 0; batchStart < lines.Length; batchStart += inferenceBatchSize)
        {
            control.Checkpoint();
            var batchEnd = Math.Min(batchStart + inferenceBatchSize, lines.Length);
            var indices = new List<int>();
            var inputs = new List<string>();
            for (var i = batchStart; i < batchEnd; i++)
            {
                if (string.IsNullOrWhiteSpace(contents[i])) continue;
                indices.Add(i);
                var lineSource = contents[i];
                if (deaccentInput) lineSource = lineSource.Replace("\u0301", "", StringComparison.Ordinal);
                inputs.Add(ApplyUserDictionary(lineSource));
            }
            (string? Text, string? Error)[] outputs = inputs.Count > 0
                ? accentizer.ProcessBatch(inputs)
                : Array.Empty<(string?, string?)>();
            for (var k = 0; k < indices.Count; k++)
            {
                var i = indices[k];
                var content = contents[i];
                var inference = outputs[k];
                if (inference.Error != null)
                {
                    diagnostics.FailedUnits++;
                    diagnostics.Events.Add($"Строка {i + 1}: {inference.Error}");
                    LogError($"Строка оставлена без изменений: {TruncateForLog(content)}", new InvalidOperationException(inference.Error));
                    accentedLines[i] = content;
                    continue;
                }
                try
                {
                    var modelOutput = MoveAccentAfterVowel(inference.Text ?? content);
                    var modelProcessed = ProtectUnstressedWords(EnsureDialogueInitialWordAccent(content, modelOutput, accentizer));
                    var modelProcessedWithPunctuation = EnsureOriginalPunctuation(content, modelProcessed);
                    accentedLines[i] = ApplyYoDictionary(modelProcessedWithPunctuation);
                    var reviewCountBefore = wordReview.Count;
                    foreach (var word in CollectUnrecognizedWords(content, accentedLines[i]!, wordReview))
                        residueWords.Add(DictionaryKey(word));
                    diagnostics.UnrecognizedWordCount += CountReviewWords(wordReview.Skip(reviewCountBefore));
                    reviewable[i] = true;
                }
                catch (Exception ex)
                {
                    diagnostics.FailedUnits++;
                    diagnostics.Events.Add($"Строка {i + 1}: {ex}");
                    // Keep the original line untouched so a single problem line
                    // never aborts the whole conversion.
                    LogError($"Строка оставлена без изменений: {TruncateForLog(content)}", ex);
                    accentedLines[i] = content;
                }
            }
            completed = batchEnd;
            diagnostics.CompletedUnits = completed;
            estimatedRemaining = completed > 0
                ? TimeSpan.FromSeconds(conversionTimer.Elapsed.TotalSeconds * (lines.Length - completed) / completed)
                : null;
            report($"Прогресс {completed}/{lines.Length}; прошло {conversionTimer.Elapsed:hh\\:mm\\:ss}; осталось {(estimatedRemaining.HasValue ? estimatedRemaining.Value.ToString("hh\\:mm\\:ss") : "расчёт…")}");
            LogConversion(inputPath, bytesIn, text.Length, conversionTimer.Elapsed, estimatedRemaining, $"Обработка {completed}/{lines.Length}");
            reportProgress(completed, lines.Length);
        }
        // The neural model leaves some polysyllabic words unmarked (rare words,
        // compounds, names). RUAccent ships a much larger stress dictionary that is
        // skipped in tiny_mode to save RAM: resolve those words once for the whole
        // file, add the marks it knows, then rebuild the review so only genuinely
        // unknown words are left for the manual pass.
        var dictionaryStress = accentizer.ResolveWords(residueWords.ToArray());
        if (dictionaryStress.Count > 0)
        {
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    accentedLines[i] = ApplyDictionaryStress(accentedLines[i]!, dictionaryStress);
            wordReview.Clear();
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    CollectUnrecognizedWords(contents[i], accentedLines[i]!, wordReview);
            diagnostics.DictionaryStressCount = dictionaryStress.Count;
            report($"Из словаря RUAccent подставлено ударений: {dictionaryStress.Count}.");
        }
        // Словарь Зарвы: дополнительный проход по словам, оставшимся без ударения.
        var supplementaryStress1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var residueWord in residueWords)
        {
            if (dictionaryStress.ContainsKey(residueWord)) continue;
            if (SupplementaryStressDictionary.Value.TryGetValue(residueWord, out var supplementaryForm))
                supplementaryStress1[residueWord] = supplementaryForm;
        }
        if (supplementaryStress1.Count > 0)
        {
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    accentedLines[i] = ApplyDictionaryStress(accentedLines[i]!, supplementaryStress1);
            wordReview.Clear();
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    CollectUnrecognizedWords(contents[i], accentedLines[i]!, wordReview);
            diagnostics.DictionaryStressCount += supplementaryStress1.Count;
            report($"Из словаря Зарвы подставлено ударений: {supplementaryStress1.Count}.");
        }
        // Встроенный словарь литературных ударений: жёсткая фиксация ударений
        // («по́днял», «по́дал» и т.д.) для слов, оставшихся без отметки.
        var builtInStress1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var residueWord in residueWords)
        {
            if (dictionaryStress.ContainsKey(residueWord) || supplementaryStress1.ContainsKey(residueWord)) continue;
            if (BuiltInStressDictionary.Value.TryGetValue(residueWord, out var builtInForm))
                builtInStress1[residueWord] = builtInForm;
        }
        if (builtInStress1.Count > 0)
        {
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    accentedLines[i] = ApplyDictionaryStress(accentedLines[i]!, builtInStress1);
            wordReview.Clear();
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    CollectUnrecognizedWords(contents[i], accentedLines[i]!, wordReview);
            diagnostics.DictionaryStressCount += builtInStress1.Count;
            report($"Из встроенного словаря литературных ударений подставлено ударений: {builtInStress1.Count}.");
        }
        for (var i = 0; i < lines.Length; i++)
        {
            result.Append(accentedLines[i] ?? contents[i]);
            result.Append(endings[i]);
            if (i + 1 < lines.Length) result.Append('\n');
        }
        control.Checkpoint();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, EncodeText(result.ToString(), sourceBytes));
        diagnostics.OutputBytes = new FileInfo(outputPath).Length;
        diagnostics.Elapsed = conversionTimer.Elapsed;
        diagnostics.UnrecognizedWordCount = CountReviewWords(wordReview);
        WriteWordReview(outputPath, wordReview, reviewPath);
        conversionTimer.Stop();
        LogConversion(inputPath, bytesIn, text.Length, conversionTimer.Elapsed, TimeSpan.Zero, "Завершено");
        report($"Готово: {outputPath}");
    }

    private static string EnsureOriginalPunctuation(string source, string converted)
    {
        var sourceTokens = PunctuationTokens.Matches(source).Select(match => match.Value).ToArray();
        var outputTokens = PunctuationTokens.Matches(converted).Select(match => match.Value).ToArray();
        var sourcePunctuation = sourceTokens.Where(token => !WordTokens.IsMatch(token, 0)).ToArray();
        var outputPunctuation = outputTokens.Where(token => !WordTokens.IsMatch(token, 0)).ToArray();
        var outputWordsForAlignment = outputTokens.Where(token => WordTokens.IsMatch(token, 0)).ToArray();
        if (outputWordsForAlignment.Length != sourceTokens.Count(token => WordTokens.IsMatch(token, 0))) return converted;
        if (sourcePunctuation.SequenceEqual(outputPunctuation, StringComparer.Ordinal))
        {
            // Restore positions too, not just the punctuation inventory (notably the
            // final periods of ellipses), while preserving each accented word.
            var restored = new StringBuilder(source.Length + 16);
            var wordIndex = 0;
            for (var i = 0; i < sourceTokens.Length; i++)
                restored.Append(WordTokens.IsMatch(sourceTokens[i], 0) ? outputWordsForAlignment[wordIndex++] : sourceTokens[i]);
            return restored.ToString();
        }

        // Preserve punctuation exactly while allowing the model to accent words:
        // align alternating word/punctuation tokens and use source punctuation.
        var sourceWords = sourceTokens.Where(token => WordTokens.IsMatch(token, 0)).ToArray();
        var outputWords = outputTokens.Where(token => WordTokens.IsMatch(token, 0)).ToArray();
        if (sourceWords.Length != outputWords.Length) return converted;
        var rebuilt = new StringBuilder(source.Length + 16);
        var sourceIndex = 0;
        var outputIndex = 0;
        var sourceWordIndex = 0;
        var outputWordIndex = 0;
        while (sourceIndex < sourceTokens.Length && outputIndex < outputTokens.Length)
        {
            if (WordTokens.IsMatch(sourceTokens[sourceIndex]) && WordTokens.IsMatch(outputTokens[outputIndex]))
            {
                if (!NormalizeWordLetters(sourceTokens[sourceIndex]).Equals(NormalizeWordLetters(outputTokens[outputIndex]), StringComparison.OrdinalIgnoreCase)) return converted;
                rebuilt.Append(outputTokens[outputIndex++]); sourceIndex++; sourceWordIndex++; outputWordIndex++;
                continue;
            }
            if (!WordTokens.IsMatch(sourceTokens[sourceIndex])) rebuilt.Append(sourceTokens[sourceIndex++]);
            if (outputIndex < outputTokens.Length && !WordTokens.IsMatch(outputTokens[outputIndex])) outputIndex++;
        }
        while (sourceIndex < sourceTokens.Length) rebuilt.Append(sourceTokens[sourceIndex++]);
        if (sourceWordIndex != sourceWords.Length || outputWordIndex != outputWords.Length) return converted;
        return rebuilt.ToString();
    }

    internal sealed record WordReviewEntry(string Word, string Context, string Proposed, string? RuAccent = null, string? Silero = null, bool Individual = false, string? Category = null)
    {
        /// <summary>
        /// Предлог с ударением, если ударение этого вхождения уходит на предлог («на́ пол»).
        /// Пусто — обычное решение: ударение внутри слова.
        /// </summary>
        public string? Preposition { get; init; }
        /// <summary>
        /// Это вхождение перенесено человеком в ручную доводку. Пометка хранится в отчёте,
        /// чтобы после перезапуска вкладка 4 открывалась с тем же набором случаев.
        /// </summary>
        public bool Manual { get; init; }
    }
    internal sealed record DeclensionCorrection(string Lemma, string Form, string Case, string Context);
    internal sealed record WordReviewPatternCorrection(string SourceWord, string Corrected, string? ExcludedContext, int ExcludedOccurrence);
    // Правка одного конкретного вхождения: применяется только к этому контексту и его номеру.
    // Нужна, когда одинаковое написание читается по-разному («за́мок» — строение, «замо́к» — запор):
    // общая правка идёт по всей группе, а снятые флажком вхождения получают каждое своё ударение.
    internal sealed record WordReviewContextCorrection(string SourceWord, string Corrected, string Context, int Occurrence);
    // Свободная правка целого фрагмента: человек изменил текст в редакторе вкладки 4,
    // и весь фрагмент (с новыми ударениями/опечатками) применяется к файлу целиком.
    internal sealed record WordReviewFragmentEdit(string Context, string Edited, string Word, int Occurrence);
    // Энклиномен: ударение этого вхождения уходит на предлог перед словом. Слово при этом
    // остаётся безударным, поэтому правка применяется не к слову, а к предлогу перед ним.
    internal sealed record WordReviewPrepositionCorrection(string SourceWord, string Preposition, string Context, int Occurrence);

    // В ручную доводку (вкладка 4) идут только опечатки и нераспознанные слова.
    // Разбор ИИ делается по расхождениям моделей: если вхождение есть в ответах ИИ
    // (или в вариантах страницы контроля) — его принимают на вкладке 3, и ещẽ раз на
    // вкладке 4 показывать незачем.
    private static bool IsManualReviewEntry(WordReviewEntry entry)
    {
        // Перенесённый на предлог случай решён разбором энклиноменов (вкладка 3).
        if (!string.IsNullOrWhiteSpace(entry.Preposition)) return false;
        var answers = ManualCaseAnswerKeys;
        if (answers.Count == 0) return true;
        // Разбор идёт по вхождениям, а в отчёте записано одно из них: его буквы, фрагмент
        // и номер должны совпасть с ключом ответа (знак ударения при сравнении не учитываем).
        var (context, occurrence) = SplitReviewContext(entry.Context);
        var prefix = ReviewWordKey(entry.Word) + "\u0001" + context + "\u0001";
        return !answers.Contains(prefix + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Все ответы по случаям текущего отчёта: заменяются при загрузке отчёта и при новом прогоне.
    private static HashSet<string> ManualCaseAnswerKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Обновляет таблицу ответов по случаям после смены отчёта или прогона ИИ.</summary>
    internal static void RefreshManualReviewCandidates(string? reportPath) => LoadManualCaseAnswerKeys(reportPath);

    /// <summary>Нужно ли слово в ручной доводке: только опечатки и нераспознанные.</summary>
    internal static bool IsManualReviewCandidate(WordReviewEntry entry) => IsManualReviewEntry(entry);

    private static void LoadManualCaseAnswerKeys(string? reportPath)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in LoadCaseAnswers(reportPath).Keys) keys.Add(key);
        ManualCaseAnswerKeys = keys;
    }

    private static void WriteDisagreementReview(string outputPath, IReadOnlyList<WordReviewEntry> disagreements,
        IReadOnlyList<WordReviewEntry> unrecognized, string? reportPath = null, int comparedWords = 0, int matchedWords = 0)
    {
        reportPath ??= ReviewPathFor(outputPath);
        var checkedWords = File.Exists(reportPath) ? ReadCheckedWords(reportPath) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var llmCache = LoadLlmSuggestions();
        // Ответы по случаям (omogre) относятся к прошлым прогонам: после перезаписи отчёта
        // они потеряли бы смысл, поэтому запись этой книги в кэше ответов сбрасывается.
        ClearCaseAnswers(reportPath);
        LoadManualCaseAnswerKeys(reportPath);
        var book = outputPath;
        WordReviewEntry ApplySuggestion(WordReviewEntry entry)
        {
            var suggestion = FindAnyLlmSuggestion(llmCache, book, entry.Word);
            return suggestion == null ? entry : entry with { Proposed = suggestion };
        }
        // Вкладка 4 — для того, где ИИ не справилась: опечатки и слова без ударения.
        // Расхождения с готовым вариантом ИИ туда не идут — их принимают на вкладке 3.
        var entries = disagreements.Select(ApplySuggestion).Where(IsManualReviewEntry).ToArray();
        var unknown = unrecognized.Select(ApplySuggestion).Where(IsManualReviewEntry).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, FormatDisagreementReview(entries, checkedWords, unknown, comparedWords, matchedWords), new UTF8Encoding(true));
    }

    /// <summary>
    /// Статистика этапа сравнения: сколько слов прошло проверку, сколько расхождений
    /// найдено и по какому движку. Нужна, чтобы «со всей статистикой» относилось и к
    /// сравнению, а не только к прогонам движков.
    /// </summary>
    private static void WriteComparisonReport(string reportPath, string sourceBook, string ruAccentBook, string sileroBook,
        string device, int fragments, int comparedWords, int matchedWords, int disagreements, int unrecognized,
        TimeSpan elapsed, string status)
    {
        var text = new StringBuilder();
        text.AppendLine("DSR — сравнение прогонов RUAccent и Silero");
        text.AppendLine($"Дата: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"Статус: {status}");
        text.AppendLine($"Исходная книга: {sourceBook}");
        text.AppendLine($"Прогон RUAccent: {ruAccentBook}");
        text.AppendLine($"Прогон Silero: {sileroBook}");
        text.AppendLine($"Устройство Silero: {device}");
        text.AppendLine($"Проверено фрагментов: {fragments:N0}");
        text.AppendLine($"Проверено многосложных слов: {comparedWords:N0}");
        text.AppendLine($"Ударение совпало и прошло: {matchedWords:N0}");
        text.AppendLine($"Расхождений (в ручную доводку): {disagreements:N0}");
        text.AppendLine($"Не распознали обе модели: {unrecognized:N0}");
        text.AppendLine($"Время сравнения: {FormatDuration(elapsed)}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, text.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { LogError($"Не удалось записать отчёт сравнения {reportPath}", ex); }
    }

    /// <summary>
    /// Делит каждое многосложное слово фрагмента на три категории:
    /// <list type="bullet">
    /// <item>ударение совпало — слово проходит без внимания;</item>
    /// <item>расхождение — в <paramref name="disagreements"/> (сюда же попадает случай,
    /// когда одна модель поставила ударение, а вторая промолчала: это тоже расхождение);</item>
    /// <item>обе модели не поставили ударение — в <paramref name="unrecognized"/>.</item>
    /// </list>
    /// Возвращает число сравнённых слов и число совпавших, чтобы статистика сравнения
    /// показывала реальные числа, а не число фрагментов.
    /// </summary>
    private static (int Compared, int Matched) CollectModelDisagreements(string source, string ruaccentText,
        string sileroText, List<WordReviewEntry> disagreements, List<WordReviewEntry> unrecognized)
    {
        var sourceWords = WordTokens.Matches(source).Cast<System.Text.RegularExpressions.Match>().ToArray();
        var ruaccentWords = WordTokens.Matches(ruaccentText).Cast<System.Text.RegularExpressions.Match>().ToArray();
        var sileroWords = WordTokens.Matches(sileroText).Cast<System.Text.RegularExpressions.Match>().ToArray();
        if (sourceWords.Length != ruaccentWords.Length || sourceWords.Length != sileroWords.Length) return (0, 0);
        var compared = 0;
        var matched = 0;
        for (var index = 0; index < sourceWords.Length; index++)
        {
            var original = sourceWords[index].Value;
            // Слова с жёстко заданным ударением (встроенный словарь литературных
            // ударений и пользовательский словарь) не должны попадать в разбор ИИ:
            // их ударение уже зафиксировано, и предлагать варианты для них бессмысленно.
            if (BuiltInStressDictionary.Value.ContainsKey(DictionaryKey(original))
                || UserDictionary.Value.ContainsKey(DictionaryKey(original))) continue;
            if (NotCyrillicWord.IsMatch(original) || RussianVowel.Matches(NormalizeWordLetters(original)).Count < 2
                || UnstressedWords.Contains(NormalizeWordLetters(original))) continue;
            var ruaccent = ruaccentWords[index].Value;
            var silero = sileroWords[index].Value;
            if (!NormalizeWordLetters(original).Equals(NormalizeWordLetters(ruaccent), StringComparison.OrdinalIgnoreCase)
                || !NormalizeWordLetters(ruaccent).Equals(NormalizeWordLetters(silero), StringComparison.OrdinalIgnoreCase)) continue;
            compared++;
            var ruStress = StressVowelPosition(ruaccent);
            var sileroStress = StressVowelPosition(silero);
            var word = original.Replace("\u0301", "", StringComparison.Ordinal);
            if (ruStress == null && sileroStress == null)
            {
                // Ни одна модель не поставила ударение: слово идёт в отдельный список
                // «нераспознанных», а не в расхождения.
                unrecognized.Add(new WordReviewEntry(word, source, word));
                continue;
            }
            if (ruStress == sileroStress)
            {
                // Обе модели поставили ударение на один и тот же слог — слово проходит.
                matched++;
                continue;
            }
            // Ё сама по себе ударная: если одна из моделей поставила ё, а вторая не
            // поставила ударение (или потеряла ё), это не реальное расхождение —
            // ударение уже задано буквой ё, и показывать слово в списке правок не нужно.
            var ruHasYo = ruaccent.Contains('ё', StringComparison.OrdinalIgnoreCase) || ruaccent.Contains('Ё', StringComparison.OrdinalIgnoreCase);
            var sileroHasYo = silero.Contains('ё', StringComparison.OrdinalIgnoreCase) || silero.Contains('Ё', StringComparison.OrdinalIgnoreCase);
            if ((ruHasYo || sileroHasYo) && (ruStress == null || sileroStress == null))
                continue;
            // Расхождение: либо модели поставили ударение на разные слоги, либо одна
            // промолчала. Промолчавшую сторону не показываем как вариант — предлагать нечего.
            disagreements.Add(new WordReviewEntry(word, source, word,
                ruStress == null ? null : ruaccent,
                sileroStress == null ? null : silero));
        }
        return (compared, matched);
    }

    // RUAccent returns '+' before the stressed vowel; DSR stores the combining acute after
    // it. Reuse the same conversion everywhere so no path leaves the mark on the wrong side.
    private static string MoveAccentAfterVowel(string text) =>
        RuAccentMarker.Replace(text, "${vowel}\u0301");

    private static int? StressVowelPosition(string word)
    {
        if (word.Any(char.IsUpper)) word = word.ToLowerInvariant();
        for (var index = 0; index < word.Length; index++)
        {
            if (word[index] == '\u0301')
            {
                // Акут должен стоять сразу после гласной. Если он «прилип» к согласной
                // (ошибка разметки), позицию не считаем — иначе ударение «перепрыгивает»
                // на соседнюю гласную и слово ложно попадает в список расхождений.
                if (index == 0 || !RussianVowel.IsMatch(word[index - 1].ToString())) return null;
                var acutePosition = CountVowels(word[..index]) - 1;
                return acutePosition >= 0 ? acutePosition : null;
            }
            if (char.ToLowerInvariant(word[index]) == 'ё')
            {
                var yoPosition = CountVowels(word[..(index + 1)]) - 1;
                return yoPosition >= 0 ? yoPosition : null;
            }
        }
        return null;
    }

    private static int CountVowels(string word) => RussianVowel.Matches(NormalizeWordLetters(word)).Count;

    private static List<string> CollectUnrecognizedWords(string source, string converted, List<string> report,
        IReadOnlySet<string>? excludedWords = null)
    {
        var sourceMatches = WordTokens.Matches(source).Cast<System.Text.RegularExpressions.Match>().ToArray();
        var outputMatches = WordTokens.Matches(converted).Cast<System.Text.RegularExpressions.Match>().ToArray();
        if (sourceMatches.Length != outputMatches.Length) return new List<string>();

        var suspiciousWords = new List<string>();
        for (var i = 0; i < sourceMatches.Length; i++)
        {
            var original = sourceMatches[i].Value;
            var normalized = NormalizeWordLetters(original);
            if (excludedWords != null && excludedWords.Contains(normalized.ToLowerInvariant())) continue;
            // Слова из встроенного словаря литературных ударений и пользовательского словаря
            // уже имеют жёстко заданное ударение: в ручную проверку они не попадают.
            if (BuiltInStressDictionary.Value.ContainsKey(DictionaryKey(original))) continue;
            if (UserDictionary.Value.ContainsKey(DictionaryKey(original))) continue;
            if (NotCyrillicWord.IsMatch(original) || normalized.Length < 2) continue;
            // RUAccent never marks stress inside a monosyllabic word: its position is
            // unambiguous, so the absence of a mark there is not a defect. Only words
            // with two or more vowels can genuinely be missing a required stress mark.
            if (RussianVowel.Matches(normalized).Count < 2) continue;
            // Short prepositions/conjunctions/particles are deliberately left unstressed
            // by ProtectUnstressedWords, so they must not be reported either.
            if (UnstressedWords.Contains(normalized)) continue;
            var convertedWord = outputMatches[i].Value;
            // Only a combining acute explicitly emitted by the model counts as
            // stress; ë is self-stressed in Russian and excludes the word from review.
            var hasExplicitStress = convertedWord.Contains('\u0301');
            var hasYo = convertedWord.Contains('ё', StringComparison.OrdinalIgnoreCase);
            if (!hasExplicitStress && !hasYo)
                suspiciousWords.Add(original);
        }

        if (suspiciousWords.Count > 0)
            report.Add($"Фрагмент: {source}{Environment.NewLine}Не распознаны: {string.Join(", ", suspiciousWords.Distinct(StringComparer.OrdinalIgnoreCase))}{Environment.NewLine}");
        return suspiciousWords;
    }

    internal static List<WordReviewEntry> ReadWordReview(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains("Правка [", StringComparison.Ordinal) || text.Contains("Своя форма [", StringComparison.Ordinal)
            || text.Contains("На предлог [", StringComparison.Ordinal) || text.Contains("Вручную [", StringComparison.Ordinal))
        {
            var structured = new List<WordReviewEntry>();
            string? currentContext = null;
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Фрагмент: ", StringComparison.Ordinal)) currentContext = line[10..];
                else if (line.StartsWith("Не распознаны: ", StringComparison.Ordinal) && currentContext != null)
                {
                    var contextText = currentContext;
                    foreach (var word in line[14..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        structured.Add(new WordReviewEntry(word, contextText, word));
                }
                else if (line.StartsWith("Правка [", StringComparison.Ordinal) && currentContext != null)
                {
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    if (close > 8) structured.Add(new WordReviewEntry(line[8..close], currentContext, line[(close + 3)..]));
                }
                else if (line.StartsWith("Своя форма [", StringComparison.Ordinal) && currentContext != null)
                {
                    // Отдельное ударение одного места: у образца своя форма, а не общая по группе.
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    // Самостоятельная запись «Своя форма» — уже отдельное вхождение; строка под
                    // «Правкой» только помечает образец, который ниже получает свою форму.
                    if (close > 12 && !(structured.Count > 0 && structured[^1].Context == currentContext
                        && structured[^1].Word == line[12..close] && !structured[^1].Individual))
                        structured.Add(new WordReviewEntry(line[12..close], currentContext, line[(close + 3)..], Individual: true));
                }
                else if (line.StartsWith("На предлог [", StringComparison.Ordinal) && currentContext != null)
                {
                    // Ударение этого вхождения уходит на предлог перед словом: «на́ пол».
                    // Само слово остаётся без знака, поэтому Proposed — это же слово без ударения.
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    if (close > 12)
                    {
                        var word = line[12..close];
                        var preposition = line[(close + 3)..].Trim();
                        // Строка может идти и за «Правкой», и сама по себе: тогда запись
                        // о слове создаётся здесь же.
                        var current = structured.Count - 1;
                        if (current >= 0 && ReviewWordKey(structured[current].Word) == ReviewWordKey(word)
                            && structured[current].Context == currentContext)
                            structured[current] = structured[current] with { Individual = true, Proposed = word, Preposition = preposition };
                        else
                            structured.Add(new WordReviewEntry(word, currentContext, word, null, null, true) { Preposition = preposition });
                    }
                }
                else if (line.StartsWith("Вручную [", StringComparison.Ordinal) && currentContext != null)
                {
                    // Пометка переноса в ручную доводку. Она идёт сразу за строкой решения
                    // этого же случая; привязываем её к последней подходящей записи.
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    if (close > 9)
                    {
                        var word = line[9..close];
                        var current = structured.Count - 1;
                        if (current >= 0 && ReviewWordKey(structured[current].Word) == ReviewWordKey(word)
                            && structured[current].Context == currentContext)
                            structured[current] = structured[current] with { Manual = true };
                        else
                            structured.Add(new WordReviewEntry(word, currentContext, word) { Manual = true });
                    }
                }
                else if ((line.StartsWith("RUAccent [", StringComparison.Ordinal) || line.StartsWith("Silero [", StringComparison.Ordinal)) && currentContext != null)
                {
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    if (close > 8)
                    {
                        var word = line[(line.IndexOf('[') + 1)..close];
                        // Эти строки сразу следуют за соответствующей «Правка [слово]».
                        // Привязываем маркеры к последней записи, чтобы одинаковые слова,
                        // повторённые в одном фрагменте, не получили чужую подсказку.
                        var current = structured.Count - 1;
                        if (current >= 0 && ReviewWordKey(structured[current].Word) == ReviewWordKey(word)
                            && structured[current].Context == currentContext)
                        {
                            var previous = structured[current];
                            structured[current] = line.StartsWith("RUAccent [", StringComparison.Ordinal)
                                ? previous with { RuAccent = line[(close + 3)..] }
                                : previous with { Silero = line[(close + 3)..] };
                        }
                    }
                }
            }
            // Both the "Не распознаны" and the "Правка [...]" lines describe the same
            // word in the same fragment, so keep a single entry per (word, fragment):
            // prefer the "Правка" one, which may carry an already saved correction.
            return AddContextOccurrences(structured
                .GroupBy(entry => NormalizeWordLetters(entry.Word).ToLowerInvariant() + "\u0001" + entry.Context, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(entry => !string.IsNullOrWhiteSpace(entry.RuAccent) || !string.IsNullOrWhiteSpace(entry.Silero))
                    .ThenByDescending(entry => !string.Equals(entry.Proposed, entry.Word, StringComparison.Ordinal))
                    .Aggregate((best, candidate) => best with { RuAccent = candidate.RuAccent ?? best.RuAccent, Silero = candidate.Silero ?? best.Silero, Individual = best.Individual || candidate.Individual, Preposition = candidate.Preposition ?? best.Preposition, Manual = best.Manual || candidate.Manual }))
                .ToList());
        }
        var entries = new List<WordReviewEntry>();
        string? context = null;
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("Фрагмент: ", StringComparison.Ordinal)) context = line[10..];
            else if (line.StartsWith("Не распознаны: ", StringComparison.Ordinal) && context != null)
            {
                var contextText = context;
                foreach (var word in line[14..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    entries.Add(new WordReviewEntry(word, contextText, word));
                context = null;
            }
        }
        return AddContextOccurrences(entries
            .GroupBy(entry => NormalizeWordLetters(entry.Word).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group)
            .ToList());
    }

    internal static (string Context, int Occurrence) SplitReviewContext(string encodedContext)
    {
        var marker = encodedContext.LastIndexOf('\u0001');
        if (marker < 0 || !int.TryParse(encodedContext[(marker + 1)..], out var occurrence)) return (encodedContext, 0);
        return (encodedContext[..marker], occurrence);
    }

    private static List<WordReviewEntry> AddContextOccurrences(List<WordReviewEntry> entries)
    {
        // Номер вхождения считается по каждому слову отдельно: он должен совпадать с тем,
        // как CountContextOccurrence нумерует это же слово в тексте фрагмента. Иначе правка
        // конкретного вхождения (снятый флажок, своё ударение) не находила бы своё место.
        var occurrences = new Dictionary<(string Context, string Word), int>();
        return entries.Select(entry =>
        {
            var visibleContext = SplitReviewContext(entry.Context).Context;
            var key = (visibleContext, ReviewWordKey(entry.Word));
            occurrences.TryGetValue(key, out var occurrence);
            occurrences[key] = occurrence + 1;
            return entry with { Context = $"{visibleContext}\u0001{occurrence}" };
        }).ToList();
    }

    /// <summary>
    /// Ключ слова для отчёта: буквы без знака ударения, е и ё приравнены, регистр не важен.
    /// </summary>
    internal static string ReviewWordKey(string word) => NormalizeWordLetters(word).ToLowerInvariant();

    // Слова, которые уже посмотрены человеком. Отмечаются строками «Проверено [слово]: …»
    // и остаются в отчёте: пустой отчёт не позволяет вернуться к документу позже.
    internal static HashSet<string> ReadCheckedWords(string path)
    {
        var checkedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return checkedWords;
        try
        {
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                // «Проверено [слово]: 17.10.2026 13:26» — слово начинается после открывающей скобки.
                if (!line.StartsWith("Проверено [", StringComparison.Ordinal)) continue;
                var close = line.IndexOf(']');
                if (close > 11) checkedWords.Add(ReviewWordKey(line[11..close]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return checkedWords;
    }

    /// <summary>
    /// Сравнительная сводка в трёх категориях. Категория 1 (ударение совпало) в файл не
    /// попадает — такие слова показаны только числом в шапке. Категория 2 — расхождения
    /// моделей, категория 3 — слова, ударение в которых не поставила ни одна модель.
    /// Формат блоков («Фрагмент:», «Разошлись:»/«Не распознаны:», «Правка [...]»,
    /// «RUAccent [...]», «Silero [...]») читает ReadWordReview, поэтому шапки-разделители
    /// не должны начинаться со служебных префиксов.
    /// </summary>
    internal static string FormatDisagreementReview(IEnumerable<WordReviewEntry> entries, IReadOnlySet<string>? checkedWords = null,
        IEnumerable<WordReviewEntry>? unrecognized = null, int comparedWords = 0, int matchedWords = 0)
    {
        var list = entries.ToArray();
        var unknown = unrecognized?.ToArray() ?? Array.Empty<WordReviewEntry>();
        if (list.Length == 0 && unknown.Length == 0)
            return "RUAccent и Silero не обнаружили расхождений по многосложным словам. Следующий этап не требуется." + Environment.NewLine;
        var output = new StringBuilder();
        output.AppendLine("DSR — сравнительная сводка RUAccent и Silero");
        output.AppendLine("Категория 1 — ударение совпало: слово проходит без правки (в этот файл не попадает).");
        output.AppendLine("Категория 2 — расхождения моделей: выберите вариант, поправьте вручную и подтвердите слово.");
        output.AppendLine("Категория 3 — ударение не поставила ни одна модель: его нужно задать вручную или подсказкой ИИ.");
        if (comparedWords > 0)
            output.AppendLine($"Проверено многосложных слов: {comparedWords:N0}; совпало: {matchedWords:N0}; расхождений: {list.Length:N0}; не распознали обе модели: {unknown.Length:N0}.");
        output.AppendLine();
        var stamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        if (list.Length > 0)
        {
            output.AppendLine("=== Категория 2. РАСХОЖДЕНИЯ МОДЕЛЕЙ ===").AppendLine();
            AppendReviewSection(output, list, "Разошлись: ", checkedWords, stamp);
        }
        if (unknown.Length > 0)
        {
            output.AppendLine("=== Категория 3. НЕ РАСПОЗНАЛИ ОБЕ МОДЕЛИ ===").AppendLine();
            AppendReviewSection(output, unknown, "Не распознаны: ", checkedWords, stamp);
        }
        return output.ToString();
    }

    private static void AppendReviewSection(StringBuilder output, IReadOnlyList<WordReviewEntry> list, string header,
        IReadOnlySet<string>? checkedWords, string stamp)
    {
        foreach (var group in list.GroupBy(item => SplitReviewContext(item.Context).Context))
        {
            output.AppendLine("Фрагмент: " + group.Key);
            output.AppendLine(header + string.Join(", ", group.Select(item => item.Word)));
            foreach (var item in group)
            {
                output.AppendLine($"Правка [{item.Word}]: {item.Proposed}");
                if (!string.IsNullOrWhiteSpace(item.RuAccent)) output.AppendLine($"RUAccent [{item.Word}]: {item.RuAccent}");
                if (!string.IsNullOrWhiteSpace(item.Silero)) output.AppendLine($"Silero [{item.Word}]: {item.Silero}");
                if (checkedWords is not null && checkedWords.Contains(ReviewWordKey(item.Word))) output.AppendLine($"Проверено [{item.Word}]: {stamp}");
            }
            output.AppendLine();
        }
    }

    internal static string FormatWordReview(IEnumerable<WordReviewEntry> entries, IReadOnlySet<string>? checkedWords = null)
    {
        var list = entries.ToArray();
        var output = new StringBuilder("Слова для ручной проверки: многосложные слова, оставленные после обработки без явной отметки ударения и буквы ё.");
        output.AppendLine().AppendLine();
        if (list.Length == 0) return "Многосложных слов без явного ударения и без буквы ё не найдено." + Environment.NewLine;
        var stamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        foreach (var group in list.GroupBy(item => SplitReviewContext(item.Context).Context))
        {
            output.AppendLine("Фрагмент: " + group.Key);
            output.AppendLine("Не распознаны: " + string.Join(", ", group.Select(item => item.Word)));
            foreach (var item in group)
            {
                // Ударение уходит на предлог: слово остаётся без знака, а решение пишется
                // отдельной строкой — по ней отчёт и собирается обратно при следующем открытии.
                if (!string.IsNullOrWhiteSpace(item.Preposition))
                {
                    output.AppendLine($"На предлог [{item.Word}]: {item.Preposition}");
                    if (!string.IsNullOrWhiteSpace(item.RuAccent)) output.AppendLine($"RUAccent [{item.Word}]: {item.RuAccent}");
                    if (!string.IsNullOrWhiteSpace(item.Silero)) output.AppendLine($"Silero [{item.Word}]: {item.Silero}");
                    if (checkedWords is not null && checkedWords.Contains(ReviewWordKey(item.Word))) output.AppendLine($"Проверено [{item.Word}]: {stamp}");
                    if (item.Manual) output.AppendLine($"Вручную [{item.Word}]: да");
                    continue;
                }
                // «Своя форма» — отдельное ударение этого места (снятый флажок). Префикс сохраняет
                // решение в отчёте: иначе после перезапуска образец снова брал бы общую форму.
                string proposed;
                if (item.Individual)
                    proposed = string.IsNullOrWhiteSpace(item.Proposed) ? item.Word : item.Proposed;
                else
                    proposed = !string.IsNullOrWhiteSpace(item.Proposed) ? item.Proposed
                        : UserDictionary.Value.TryGetValue(ReviewWordKey(item.Word), out var saved) ? saved : item.Word;
                output.AppendLine($"{(item.Individual ? "Своя форма" : "Правка")} [{item.Word}]: {proposed}");
                if (!string.IsNullOrWhiteSpace(item.RuAccent)) output.AppendLine($"RUAccent [{item.Word}]: {item.RuAccent}");
                if (!string.IsNullOrWhiteSpace(item.Silero)) output.AppendLine($"Silero [{item.Word}]: {item.Silero}");
                if (checkedWords is not null && checkedWords.Contains(ReviewWordKey(item.Word)))
                    output.AppendLine($"Проверено [{item.Word}]: {stamp}");
                if (item.Manual) output.AppendLine($"Вручную [{item.Word}]: да");
            }
            output.AppendLine();
        }
        return output.ToString();
    }

    internal static (int Applied, int Failed) ApplyFragmentEditsToTextFile(string inputPath, string outputPath, IEnumerable<WordReviewFragmentEdit> edits)
    {
        var list = edits.Where(item => !string.IsNullOrWhiteSpace(item.Edited)).OrderByDescending(item => item.Context.Length).ToList();
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var applied = 0;
        var failed = 0;
        foreach (var edit in list)
        {
            if (TryReplaceFragmentText(text, edit, out var updated))
            {
                text = updated;
                applied++;
            }
            else
            {
                failed++;
                LogReview($"Не удалось применить правку фрагмента: {edit.Context}");
            }
        }
        File.WriteAllBytes(outputPath, EncodeText(text, sourceBytes));
        return (applied, failed);
    }

    internal static (int Applied, int Failed) ApplyFragmentEditsToFb2(string inputPath, string outputPath, IEnumerable<WordReviewFragmentEdit> edits)
    {
        var list = edits.Where(item => !string.IsNullOrWhiteSpace(item.Edited)).OrderByDescending(item => item.Context.Length).ToList();
        if (list.Count == 0)
        {
            if (!string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(inputPath, outputPath, overwrite: true);
            return (0, 0);
        }
        var sourceBytes = File.ReadAllBytes(inputPath);
        var xml = DecodeXml(sourceBytes);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var textNodes = document.DescendantNodes().OfType<XText>().Where(node => node.Value.Length > 0).ToList();
        var applied = 0;
        var failed = 0;
        foreach (var edit in list)
        {
            var done = false;
            foreach (var node in textNodes)
            {
                if (TryReplaceFragmentText(node.Value, edit, out var updated))
                {
                    node.Value = updated;
                    applied++;
                    done = true;
                    break;
                }
            }
            if (!done)
            {
                failed++;
                LogReview($"Не удалось применить правку фрагмента в FB2: {edit.Context}");
            }
        }
        if (applied == 0)
        {
            if (!string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(inputPath, outputPath, overwrite: true);
            return (0, failed);
        }
        SaveFb2PreservingSourceStructure(inputPath, outputPath, document);
        return (applied, failed);
    }

    private static bool TryReplaceFragmentText(string text, WordReviewFragmentEdit edit, out string updated)
    {
        updated = text;
        var context = Program.SplitReviewContext(edit.Context).Context;
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(edit.Edited)) return false;
        var (needle, _) = NormalizeFragmentText(context);
        var (haystack, map) = NormalizeFragmentText(text);
        var position = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (position < 0) return false;
        var start = map[position];
        var end = map[position + needle.Length - 1] + 1;
        updated = text[..start] + edit.Edited + text[end..];
        return true;
    }

    private static (string Normalized, int[] Map) NormalizeFragmentText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var map = new List<int>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\u0301') continue;
            var folded = ch switch
            {
                'ё' => 'е',
                'Ё' => 'Е',
                _ => ch,
            };
            builder.Append(char.ToLowerInvariant(folded));
            map.Add(i);
        }
        return (builder.ToString(), map.ToArray());
    }

    internal static bool IsValidCorrection(string sourceWord, string corrected)
    {
        return !string.IsNullOrWhiteSpace(corrected) && !corrected.Any(char.IsWhiteSpace) &&
            NormalizeWordLetters(sourceWord).Equals(NormalizeWordLetters(corrected), StringComparison.OrdinalIgnoreCase) &&
            IsValidStressedWord(corrected);
    }

    // Typo correction: the corrected word may differ in letters from the source
    // (unlike a stress-only correction), but must remain a valid Russian word
    // and be close enough to the original to avoid accidental text corruption.
    internal static bool IsValidTypoCorrection(string sourceWord, string corrected)
    {
        if (string.IsNullOrWhiteSpace(corrected) || corrected.Any(char.IsWhiteSpace)) return false;
        if (!IsValidStressedWord(corrected)) return false;
        var sourceNorm = NormalizeWordLetters(sourceWord).ToLowerInvariant();
        var targetNorm = NormalizeWordLetters(corrected).ToLowerInvariant();
        if (sourceNorm == targetNorm) return false; // accent-only fix — handled by IsValidCorrection
        var distance = LevenshteinDistance(sourceNorm, targetNorm);
        var maxEdit = Math.Max(1, Math.Min(3, sourceNorm.Length / 2));
        return distance <= maxEdit;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[n, m];
    }

    internal static bool IsValidDeclensionForm(string form) => IsValidStressedWord(form);

    internal static bool IsValidStressedWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Any(char.IsWhiteSpace) ||
            !word.Any(ch => "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ".Contains(ch))) return false;
        var accents = word.Count(ch => ch == '\u0301');
        if (accents > 1) return false;
        if (accents == 0) return word.Contains('ё', StringComparison.OrdinalIgnoreCase);
        var index = word.IndexOf('\u0301');
        if (index == 0) return false;
        var stressed = word[index - 1];
        // Обычно ударение падает на гласную. Но в древнерусской и церковнославянской
        // орфографии ударной может быть и ѣ, ѧ, і и другие буквы вне современного алфавита
        // («свѣ́тлостію», «мѣ́дъ»), поэтому разрешаем знак ударения после любой кириллической буквы.
        if (!IsCyrillicLetter(stressed)) return false;
        // Ё is inherently stressed and does not take a separate acute mark.
        return char.ToLowerInvariant(stressed) != 'ё';
    }

    private static bool IsCyrillicLetter(char ch) =>
        ch is >= 'Ѐ' and <= 'ӿ' or >= 'а' and <= 'я' or >= 'А' and <= 'Я';

    internal static List<DeclensionCorrection> ReadDeclensionForms(string reviewPath, string word)
    {
        var path = FormCorrectionsPathFor(reviewPath);
        if (!File.Exists(path)) return new List<DeclensionCorrection>();
        try
        {
            return (JsonSerializer.Deserialize<List<DeclensionCorrection>>(File.ReadAllText(path)) ?? new())
                .Where(item => item.Lemma.Equals(word, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (JsonException ex)
        {
            LogError($"Повреждён файл форм слов {path}", ex);
            return new List<DeclensionCorrection>();
        }
    }

    // Все сохранённые словоформы разом: нужны для пакетного применения правок.
    internal static List<DeclensionCorrection> ReadAllDeclensionForms(string? reviewPath)
    {
        if (string.IsNullOrWhiteSpace(reviewPath)) return new List<DeclensionCorrection>();
        var path = FormCorrectionsPathFor(reviewPath);
        if (!File.Exists(path)) return new List<DeclensionCorrection>();
        // Повреждённый или пустой файл словоформ не должен ронять весь пакет правок:
        // остальные правки из отчёта надо применить, а сбой словаря — записать в журнал.
        try
        {
            return JsonSerializer.Deserialize<List<DeclensionCorrection>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<DeclensionCorrection>();
        }
        catch (JsonException ex)
        {
            LogError($"Повреждён файл словоформ {path}", ex);
            return new List<DeclensionCorrection>();
        }
    }

    internal static void SaveDeclensionForms(string reviewPath, WordReviewEntry entry, IEnumerable<DeclensionCorrection> forms)
    {
        var path = FormCorrectionsPathFor(reviewPath);
        var all = File.Exists(path)
            ? JsonSerializer.Deserialize<List<DeclensionCorrection>>(File.ReadAllText(path)) ?? new List<DeclensionCorrection>()
            : new List<DeclensionCorrection>();
        all.RemoveAll(item => item.Lemma.Equals(entry.Word, StringComparison.OrdinalIgnoreCase));
        var saved = forms.ToList();
        all.AddRange(saved);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        foreach (var form in saved)
        {
            var key = NormalizeWordLetters(form.Form).ToLowerInvariant();
            UserDictionary.Value[key] = form.Form;
        }
        File.WriteAllLines(UserDictionaryPath, UserDictionary.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "\t" + pair.Value), new UTF8Encoding(true));
        var examples = string.Join(Environment.NewLine, saved.Select(form => JsonSerializer.Serialize(new { type = "declension", lemma = form.Lemma, form = form.Form, grammaticalCase = form.Case, context = form.Context, created = DateTimeOffset.Now })));
        if (examples.Length > 0) lock (LogLock) File.AppendAllText(TrainingLogPath, examples + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static void SaveUserCorrection(WordReviewEntry entry, string corrected)
    {
        if (!IsValidCorrection(entry.Word, corrected) && !IsValidTypoCorrection(entry.Word, corrected))
            throw new ArgumentException("Некорректный вариант исправления.");
        Directory.CreateDirectory(DataDirectory);
        var key = NormalizeWordLetters(entry.Word).ToLowerInvariant();
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(UserDictionaryPath))
        {
            foreach (var line in File.ReadAllLines(UserDictionaryPath, Encoding.UTF8))
            {
                var columns = line.Split('\t', 2);
                if (columns.Length == 2 && columns[0].Length > 0) dict[columns[0]] = columns[1];
            }
        }
        dict[key] = corrected;
        File.WriteAllLines(UserDictionaryPath, dict.Select(item => item.Key + "\t" + item.Value), new UTF8Encoding(true));
        UserDictionary.Value[key] = corrected;
        var example = JsonSerializer.Serialize(new { source = entry.Word, corrected, context = entry.Context, normalized = key, created = DateTimeOffset.Now });
        lock (LogLock) File.AppendAllText(TrainingLogPath, example + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static string SaveDeclensionForm(WordReviewEntry entry, string form, string grammaticalCase)
    {
        if (!IsValidDeclensionForm(form)) throw new ArgumentException("Форма должна быть русским словом с одним ударением или буквой «ё».");
        var key = NormalizeWordLetters(form).ToLowerInvariant();
        Directory.CreateDirectory(DataDirectory);
        UserDictionary.Value[key] = form;
        var lines = UserDictionary.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "\t" + pair.Value);
        File.WriteAllLines(UserDictionaryPath, lines, new UTF8Encoding(true));
        var correction = new DeclensionCorrection(entry.Word, form, grammaticalCase, entry.Context);
        var example = JsonSerializer.Serialize(new { type = "declension", lemma = correction.Lemma, form = correction.Form, grammaticalCase = correction.Case, context = correction.Context, created = DateTimeOffset.Now });
        lock (LogLock) File.AppendAllText(TrainingLogPath, example + Environment.NewLine, new UTF8Encoding(false));
        return key;
    }

    internal static int ApplyWordCorrectionsToFb2(string inputPath, string outputPath, IEnumerable<DeclensionCorrection> corrections)
    {
        var correctionMap = corrections.GroupBy(correction => NormalizeWordLetters(correction.Form).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Form, StringComparer.OrdinalIgnoreCase);
        LogReview($"Применение сохранённых словоформ к {Path.GetFileName(inputPath)}: слов {correctionMap.Count}");
        var applied = ApplyWordReplacementMapToFb2(inputPath, outputPath, correctionMap);
        LogReview($"Словоформы: заменено вхождений {applied}");
        return applied;
    }

    internal static Dictionary<string, int> CountReviewWordOccurrences(string outputPath, IEnumerable<string> words)
    {
        if (Path.GetExtension(outputPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return CountReviewWordOccurrencesInText(outputPath, words);
        return CountReviewWordOccurrencesInFb2(outputPath, words);
    }

    private static Dictionary<string, int> CountReviewWordOccurrencesInFb2(string outputPath, IEnumerable<string> words)
    {
        var counts = words.Distinct(StringComparer.Ordinal).ToDictionary(word => word, _ => 0, StringComparer.Ordinal);
        if (!File.Exists(outputPath) || counts.Count == 0) return counts;
        var document = XDocument.Parse(DecodeXml(File.ReadAllBytes(outputPath)), LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root == null) return counts;
        var ns = root.Name.Namespace;
        var text = string.Join(" ", root.DescendantsAndSelf()
            .Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Select(node => node.Value));
        CountWordsInText(text, counts);
        return counts;
    }

    private static Dictionary<string, int> CountReviewWordOccurrencesInText(string path, IEnumerable<string> words)
    {
        var counts = words.Distinct(StringComparer.Ordinal).ToDictionary(word => word, _ => 0, StringComparer.Ordinal);
        if (!File.Exists(path) || counts.Count == 0) return counts;
        CountWordsInText(DecodeText(File.ReadAllBytes(path)), counts);
        return counts;
    }

    // Подсчёт вхождений слов отчёта в тексте. Раньше на каждое слово текста
    // перебирались все слова отчёта: на книгах это давало миллиарды сравнений и
    // окно переставало отвечать после прогона Silero. Теперь слова отчёта
    // разложены по нормализованному ключу один раз, а текст просматривается один
    // проход. Регистронезависимость сохранена: нормализация регистр не меняет,
    // поэтому ключи сравниваются без учёта регистра.
    private static void CountWordsInText(string text, Dictionary<string, int> counts)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in counts.Keys)
        {
            var normalized = NormalizeWordLetters(word);
            if (!index.TryGetValue(normalized, out var bucket)) index[normalized] = bucket = new List<string>();
            bucket.Add(word);
        }
        foreach (System.Text.RegularExpressions.Match match in WordTokens.Matches(text))
        {
            if (!index.TryGetValue(NormalizeWordLetters(match.Value), out var bucket)) continue;
            foreach (var word in bucket) counts[word]++;
        }
    }

    internal static int ApplyReviewPatternCorrectionsToFb2(string outputPath, IEnumerable<WordReviewPatternCorrection> corrections) =>
        ApplyReviewPatternCorrectionsToFb2(outputPath, outputPath, corrections);

    internal static int ApplyReviewPatternCorrectionsToFb2(string inputPath, string outputPath, IEnumerable<WordReviewPatternCorrection> corrections)
        => ApplyReviewPatternCorrectionsToFb2(inputPath, outputPath, corrections, null);

    internal static int ApplyReviewPatternCorrectionsToFb2(string inputPath, string outputPath,
        IEnumerable<WordReviewPatternCorrection> corrections, IEnumerable<WordReviewContextCorrection>? contextCorrections)
        => ApplyReviewPatternCorrectionsToFb2(inputPath, outputPath, corrections, contextCorrections, null);

    internal static int ApplyReviewPatternCorrectionsToFb2(string inputPath, string outputPath,
        IEnumerable<WordReviewPatternCorrection> corrections, IEnumerable<WordReviewContextCorrection>? contextCorrections,
        IEnumerable<WordReviewPrepositionCorrection>? prepositionCorrections)
    {
        var entries = corrections.ToArray();
        var overrides = BuildContextOverrideMap(contextCorrections);
        var prepositions = BuildPrepositionMap(prepositionCorrections);
        if (entries.Length == 0 && overrides.Count == 0 && prepositions.Count == 0) return 0;
        var map = entries.GroupBy(item => NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var excluded = entries.Where(item => item.ExcludedContext != null)
            .Select(item => (Key: NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), Context: item.ExcludedContext!, item.ExcludedOccurrence))
            .ToHashSet();
        return ApplyPatternReplacementMapToFb2(inputPath, outputPath, map, excluded, overrides, prepositions);
    }

    // TXT equivalents of the FB2 pattern and word-form apply functions.
    // They preserve the original encoding, BOM and line endings of the source file.
    internal static int ApplyReviewPatternCorrectionsToTextFile(string inputPath, string outputPath, IEnumerable<WordReviewPatternCorrection> corrections)
        => ApplyReviewPatternCorrectionsToTextFile(inputPath, outputPath, corrections, null);

    internal static int ApplyReviewPatternCorrectionsToTextFile(string inputPath, string outputPath,
        IEnumerable<WordReviewPatternCorrection> corrections, IEnumerable<WordReviewContextCorrection>? contextCorrections)
        => ApplyReviewPatternCorrectionsToTextFile(inputPath, outputPath, corrections, contextCorrections, null);

    internal static int ApplyReviewPatternCorrectionsToTextFile(string inputPath, string outputPath,
        IEnumerable<WordReviewPatternCorrection> corrections, IEnumerable<WordReviewContextCorrection>? contextCorrections,
        IEnumerable<WordReviewPrepositionCorrection>? prepositionCorrections)
    {
        var entries = corrections.ToArray();
        var overrides = BuildContextOverrideMap(contextCorrections);
        var prepositions = BuildPrepositionMap(prepositionCorrections);
        if (entries.Length == 0 && overrides.Count == 0 && prepositions.Count == 0) return 0;
        var map = entries.GroupBy(item => NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var excluded = entries.Where(item => item.ExcludedContext != null)
            .Select(item => (Key: NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), Context: item.ExcludedContext!, item.ExcludedOccurrence))
            .ToHashSet();
        return ApplyPatternReplacementMapToText(inputPath, outputPath, map, excluded, overrides, prepositions);
    }

    // Ключ — нормализованное слово, контекст и номер вхождения: так у одного написания
    // может быть сразу несколько отдельных правок, каждая на своё место в тексте.
    private static Dictionary<(string Key, string Context, int Occurrence), string> BuildContextOverrideMap(
        IEnumerable<WordReviewContextCorrection>? contextCorrections) =>
        (contextCorrections ?? Enumerable.Empty<WordReviewContextCorrection>())
            .GroupBy(item => (Key: NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), item.Context, item.Occurrence))
            .ToDictionary(group => group.Key, group => group.Last().Corrected);

    // Ударение на предлог: тот же ключ (слово, контекст, вхождение), но значение — предлог
    // со знаком ударения. Правка ложится на предлог перед словом, само слово не меняется.
    private static Dictionary<(string Key, string Context, int Occurrence), string> BuildPrepositionMap(
        IEnumerable<WordReviewPrepositionCorrection>? prepositionCorrections) =>
        (prepositionCorrections ?? Enumerable.Empty<WordReviewPrepositionCorrection>())
            .GroupBy(item => (Key: NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), item.Context, item.Occurrence))
            .ToDictionary(group => group.Key, group => group.Last().Preposition);

    internal static int ApplyWordCorrectionsToTextFile(string inputPath, string outputPath, IEnumerable<DeclensionCorrection> corrections)
    {
        var correctionMap = corrections.GroupBy(correction => NormalizeWordLetters(correction.Form).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Form, StringComparer.OrdinalIgnoreCase);
        return ApplyWordReplacementMapToText(inputPath, outputPath, correctionMap);
    }

    // Решение принимается один раз на слово, а в тексте оно встречается с разным регистром:
// «Мальчишка» в начале фразы должно остаться с заглавной буквы, даже если правка сохранена
// со строчной. Остальные буквы регистр не трогает: «МАЛЬЧИШКА» в заголовке не разбираем.
internal static string KeepFirstLetterCase(string sourceToken, string corrected)
{
    if (sourceToken.Length == 0 || corrected.Length == 0) return corrected;
    if (!char.IsUpper(sourceToken[0])) return corrected;
    return char.IsUpper(corrected[0]) ? corrected : char.ToUpperInvariant(corrected[0]) + corrected[1..];
}

/// <summary>
/// Правки одного текстового узла (абзаца): общая форма группы, отдельные вхождения
/// и перенос ударения на предлог. Логика одна и та же для TXT и FB2, поэтому она
/// вынесена сюда: число замен считается в переданном счётчике.
/// </summary>
private static string ApplyOneContent(string content,
        Dictionary<string, WordReviewPatternCorrection> correctionMap,
        (string Key, string Context, int ExcludedOccurrence)[] excludedContexts,
        Dictionary<(string Key, string Context, int Occurrence), string> overrides,
        Dictionary<(string Key, string Context, int Occurrence), string> prepositions,
        ref int replacements)
    {
        if (correctionMap.Count == 0 && overrides.Count == 0 && prepositions.Count == 0) return content;
        // Сдвиги пересчитываются после каждой вставки/замены: знак предлога сдвигает последующие
        // вхождения того же слова, а накопительная ошибка делала повторный прогон незаметно
        // ошибочным. Поиск предлога идёт по исходному тексту, запись — по текущему сдвигу.
        var builder = new StringBuilder(content.Length + 16);
        var position = 0;
        var shift = 0;
        // Сколько вхождений каждого ключа уже встретилось в этом узле: номер в отчёте считается
        // по всему фрагменту, а в FB2 абзац часто разрезан на несколько текстовых узлов (курсив,
        // диалог внутри предложения), — тогда номера расходятся и правка ложится не туда. Ключ
        // берётся по локальному номеру, если такая правка есть в карте, иначе — по номеру в книге.
        var handled = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in WordTokens.Matches(content))
        {
            var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
            var local = CountContextOccurrence(content, match.Index, match.Value);
            // Номер для ключа: по узлу (как пишет отчёт) или по книге; берётся именно тот,
            // которого ждёт карта правок. Счётчик по узлу всё равно ведётся на каждое вхождение.
            var total = handled.TryGetValue(key, out var seen) ? seen : 0;
            handled[key] = total + 1;
            var slot = overrides.ContainsKey((key, content, local)) || prepositions.ContainsKey((key, content, local))
                ? (key, content, local)
                : (key, content, total);
            // Энклиномен: знак ударения встаёт на предлог перед словом, само слово остаётся
            // безударным — меняется текст слева от совпадения, а не само совпадение.
            var onPreposition = prepositions.TryGetValue(slot, out var prepositionForm);
            // Предлог берётся из исходного текста сразу перед словом, а запись идёт по текущему
            // сдвигу. От двойной вставки защищает проверка startAt < position ниже.
            if (onPreposition && FindPrecedingWord(content, match.Index, out var start, out var end))
            {
                var startAt = start + shift;
                if (startAt < position)
                {
                    // Предлог уже переписан предыдущим случаем (подряд два переноса в одном
                    // абзаце): перенос невозможен, слово остаётся на общей форме группы.
                    onPreposition = false;
                }
                else
                {
                    builder.Append(content, position - shift, startAt - position);
                    // Прежний знак снимается: разбор энклиноменов отдаёт готовую форму предлога
                    // с ударением на единственной гласной, и повторный прогон не должен копить знаки.
                    var prepositionText = KeepFirstLetterCase(content[start..end], prepositionForm!);
                    builder.Append(prepositionText);
                    shift += prepositionText.Length - (end - start);
                    position = end + shift;
                    replacements++;
                }
            }
            else if (onPreposition) onPreposition = false;
            var replacement = match.Value;
            // Отдельная правка вхождения важнее общей: у него своё ударение именно для этого места.
            if (overrides.TryGetValue(slot, out var overrideCorrection))
            {
                replacement = KeepFirstLetterCase(match.Value, overrideCorrection);
                replacements++;
            }
            else if (correctionMap.TryGetValue(key, out var correction))
            {
                // Энклиномен слово не меняет: общая форма группы этого вхождения не касается.
                // Номер вхождения в отчёте считается по всему фрагменту, а узел может быть только
                // его куском: тогда сверяется контекст, если он целиком лежит в узле.
                var excludedByContext = excludedContexts.Any(item => item.Key == key && item.ExcludedOccurrence >= 0
                    && (string.Equals(item.Context, content, StringComparison.Ordinal)
                        ? item.ExcludedOccurrence == local
                        : content.Contains(item.Context, StringComparison.Ordinal) && item.ExcludedOccurrence == total));
                if (!onPreposition && !excludedByContext)
                {
                    replacement = KeepFirstLetterCase(match.Value, correction.Corrected);
                    replacements++;
                }
            }
            builder.Append(content, position - shift, match.Index - (position - shift));
            builder.Append(replacement);
            shift += replacement.Length - match.Length;
            position = match.Index + match.Length + shift;
        }
        builder.Append(content, position - shift, content.Length - (position - shift));
        return builder.ToString();
    }

    // Предлог перед словом: ближайший токен слева через пробелы. Нужен, чтобы знак ударения
    // встал на предлог именно этого вхождения, а не на первый похожий предлог в абзаце.
    private static bool FindPrecedingWord(string content, int wordIndex, out int start, out int end)
    {
        start = 0;
        end = 0;
        var cursor = wordIndex;
        while (cursor > 0 && (content[cursor - 1] == ' ' || content[cursor - 1] == '\t')) cursor--;
        if (cursor == wordIndex) return false;
        end = cursor;
        while (cursor > 0 && IsWordCharacter(content[cursor - 1])) cursor--;
        start = cursor;
        return start < end;
    }

private static int ApplyPatternReplacementMapToText(string inputPath, string outputPath,
        Dictionary<string, WordReviewPatternCorrection> correctionMap,
        HashSet<(string Key, string Context, int ExcludedOccurrence)> excluded,
        Dictionary<(string Key, string Context, int Occurrence), string> overrides,
        Dictionary<(string Key, string Context, int Occurrence), string> prepositions)
    {
        if (correctionMap.Count == 0 && overrides.Count == 0 && prepositions.Count == 0) return 0;
        LogReview($"Применение правок TXT: {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}; групп слов: {correctionMap.Count}; отдельных вхождений: {overrides.Count}; ударений на предлог: {prepositions.Count}; исключённых контекстов: {excluded.Count}");
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var lines = text.Split('\n');
        var replacements = 0;
        var excludedContexts = excluded.Where(item => item.Key.Length > 0).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            var content = ending.Length == 0 ? line : line[..^1];
            lines[i] = ApplyOneContent(content, correctionMap, excludedContexts, overrides, prepositions, ref replacements) + ending;
        }
        LogReview($"Заменено вхождений в TXT: {replacements}");
        // Результат пишется всегда, даже без замен: итоговый файл всё равно нужен,
        // а пустой файл на месте результата выглядел бы потерей книги.
        File.WriteAllBytes(outputPath, EncodeText(string.Join("\n", lines), sourceBytes));
        return replacements;
    }

    private static int ApplyWordReplacementMapToText(string inputPath, string outputPath, Dictionary<string, string> correctionMap)
    {
        if (correctionMap.Count == 0) return 0;
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var replacements = 0;
        var updated = WordTokens.Replace(text, match =>
        {
            var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
            if (!correctionMap.TryGetValue(key, out var corrected)) return match.Value;
            replacements++;
            return KeepFirstLetterCase(match.Value, corrected);
        });
        if (replacements == 0) return 0;
        File.WriteAllBytes(outputPath, EncodeText(updated, sourceBytes));
        return replacements;
    }

    private static int ApplyPatternReplacementMapToFb2(string inputPath, string outputPath,
        Dictionary<string, WordReviewPatternCorrection> correctionMap,
        HashSet<(string Key, string Context, int ExcludedOccurrence)> excluded,
        Dictionary<(string Key, string Context, int Occurrence), string> overrides,
        Dictionary<(string Key, string Context, int Occurrence), string> prepositions)
    {
        if (correctionMap.Count == 0 && overrides.Count == 0 && prepositions.Count == 0) return 0;
        LogReview($"Применение правок: {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}; групп слов: {correctionMap.Count}; отдельных вхождений: {overrides.Count}; ударений на предлог: {prepositions.Count}; исключённых контекстов: {excluded.Count}");
        LogReview("Правки по группам: " + string.Join("; ", correctionMap.Values.Select(item => $"{item.SourceWord} -> {item.Corrected}")));
        var bytes = File.ReadAllBytes(inputPath);
        _ = new Fb2Parser().Parse(bytes);
        var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В исходном FB2 отсутствует корневой элемент.");
        var ns = root.Name.Namespace;
        var originalTextNodes = root.DescendantNodes().OfType<XText>().ToArray();
        var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#')).Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
        // Узлы обрабатываются по одному: сбой в одном абзаце не должен отменять весь пакет.
        // Именно так падала запись итога с переносом на предлог — ошибка одного узла
        // выбрасывалась наружу и ни одна правка не попадала в файл.
        var replacements = 0;
        var failedNodes = 0;
        var excludedContexts = excluded.Where(item => item.Key.Length > 0).ToArray();
        foreach (var node in root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct())
        {
            try
            {
                var updated = ApplyOneContent(node.Value, correctionMap, excludedContexts, overrides, prepositions, ref replacements);
                if (!string.Equals(updated, node.Value, StringComparison.Ordinal)) node.Value = updated;
            }
            catch (Exception ex)
            {
                failedNodes++;
                LogReview($"Правки в узле не применены ({TruncateForLog(node.Value)}): {ex.Message}");
            }
        }
        LogReview($"Заменено вхождений в XML: {replacements}" + (failedNodes == 0 ? string.Empty : $"; узлов с ошибкой: {failedNodes}"));
        // Без замен сохраняем исходный файл без пересборки XML: копия точно повторит исходник.
        if (replacements == 0)
        {
            if (!string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(inputPath, outputPath, overwrite: true);
            return 0;
        }
        if (!originalTextNodes.SequenceEqual(root.DescendantNodes().OfType<XText>()))
            throw new InvalidDataException("Изменился состав XML-текстовых узлов; безопасно сохранить исходную структуру FB2 невозможно.");
        SaveFb2PreservingSourceStructure(inputPath, outputPath, document);
        _ = new Fb2Parser().Parse(File.ReadAllBytes(outputPath));
        return replacements;
    }

    private static void SaveFb2PreservingSourceStructure(string inputPath, string outputPath, XDocument editedDocument)
    {
        var sourceBytes = File.ReadAllBytes(inputPath);
        var originalXml = DecodeXml(sourceBytes);
        var editedXml = editedDocument.ToString(SaveOptions.DisableFormatting);
        var editedDocumentForDiff = XDocument.Parse(editedXml, LoadOptions.PreserveWhitespace);
        var originalDocument = XDocument.Parse(originalXml, LoadOptions.PreserveWhitespace);
        var originalNodes = originalDocument.DescendantNodes().OfType<XText>().Where(node => node.Value.Length > 0).ToArray();
        var updatedNodes = editedDocumentForDiff.DescendantNodes().OfType<XText>().Where(node => node.Value.Length > 0).ToArray();
        if (updatedNodes.Length != originalNodes.Length)
            throw new InvalidDataException("Невозможно сохранить исправления без изменения структуры FB2: изменилось количество текстовых узлов.");

        // Не используем самописные индексы XML-текстовых узлов: XDocument может разделять
        // текст иначе (CDATA, комментарии, сущности). Вместо этого ищем уникальные исходные
        // фрагменты справа налево и применяем только буквальную замену внутри найденного текста.
        var resultXml = originalXml;
        var changed = new List<(string OldValue, string NewValue, bool IsCData)>();
        for (var i = 0; i < originalNodes.Length; i++)
            if (!string.Equals(originalNodes[i].Value, updatedNodes[i].Value, StringComparison.Ordinal))
                changed.Add((originalNodes[i].Value, updatedNodes[i].Value, originalNodes[i] is XCData));

        foreach (var change in changed.OrderByDescending(item => item.OldValue.Length))
        {
            var oldText = change.OldValue;
            var newText = change.NewValue;
            var encodedOld = EscapeXmlTextForLexicalMatch(oldText);
            var encodedNew = change.IsCData
                ? newText.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal)
                : EscapeXmlTextForLexicalMatch(newText);
            var searchFrom = resultXml.Length;
            var found = false;
            while (searchFrom >= 0)
            {
                var candidate = resultXml.LastIndexOf(encodedOld, searchFrom - 1, StringComparison.Ordinal);
                if (candidate < 0) break;
                // Ensure candidate lies in text, not in an element attribute/tag.
                if (IsXmlTextPosition(resultXml, candidate, out var cdata))
                {
                    var absoluteEnd = candidate + encodedOld.Length;
                    if (IsXmlTextPosition(resultXml, absoluteEnd - 1, out var endCData) && cdata == endCData)
                    {
                        resultXml = resultXml[..candidate] + encodedNew + resultXml[absoluteEnd..];
                        found = true;
                        break;
                    }
                }
                searchFrom = candidate;
            }
            if (!found)
            {
                var sample = oldText.Length > 120 ? oldText[..120] + "…" : oldText;
                LogReview($"Не удалось найти исходный текстовый фрагмент «{sample}» при сохранении XML; CDATA={change.IsCData}");
                throw new InvalidDataException("Не удалось сопоставить изменённый текст с исходным XML FB2; файл не был перезаписан.");
            }
        }
        LogReview($"Сопоставление завершено: изменённых текстовых фрагментов {changed.Count}");
        var resultBytes = EncodeXmlLikeSource(resultXml, sourceBytes);
        var tempPath = outputPath + ".review.tmp";
        var backupPath = outputPath + ".review.bak";
        try
        {
            File.WriteAllBytes(tempPath, resultBytes);
            _ = new Fb2Parser().Parse(File.ReadAllBytes(tempPath));
            if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(inputPath, backupPath, overwrite: true);
                try { File.Move(tempPath, outputPath, overwrite: true); }
                catch { File.Copy(backupPath, outputPath, overwrite: true); throw; }
            }
            else
            {
                File.Move(tempPath, outputPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    // True, если позиция принадлежит текстовому содержимому (а не тегу/атрибуту).
    // Отслеживает кавычки в атрибутах, комментарии, декларации и CDATA.
    private static bool IsXmlTextPosition(string xml, int position, out bool isCData)
    {
        isCData = false;
        if (position < 0 || position >= xml.Length) return false;
        var inTag = false;
        var quote = '\0';
        var inComment = false;
        var inCData = false;
        for (var i = 0; i <= position; i++)
        {
            if (inComment)
            {
                if (i + 2 < xml.Length && xml.AsSpan(i, 3).SequenceEqual("-->")) { i += 2; inComment = false; }
                continue;
            }
            if (inCData)
            {
                if (i + 2 < xml.Length && xml.AsSpan(i, 3).SequenceEqual("]]>") ) { i += 2; inCData = false; continue; }
                if (i == position) { isCData = true; return true; }
                continue;
            }
            if (!inTag && i + 3 < xml.Length && xml.AsSpan(i, 4).SequenceEqual("<!--")) { inComment = true; i += 3; continue; }
            if (!inTag && i + 8 < xml.Length && xml.AsSpan(i, 9).SequenceEqual("<![CDATA[")) { inCData = true; i += 8; continue; }
            var ch = xml[i];
            if (inTag)
            {
                if (quote != '\0') { if (ch == quote) quote = '\0'; }
                else if (ch is '\'' or '\"') quote = ch;
                else if (ch == '>') inTag = false;
            }
            else if (ch == '<') inTag = true;
            else if (i == position) return true;
        }
        return false;
    }

    private static string EscapeXmlTextForLexicalMatch(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static byte[] EncodeXmlLikeSource(string xml, byte[] originalBytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        if (originalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) encoding = new UTF8Encoding(true);
        else if (originalBytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) encoding = Encoding.Unicode;
        else if (originalBytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) encoding = Encoding.BigEndianUnicode;
        else
        {
            var prefix = Encoding.ASCII.GetString(originalBytes, 0, Math.Min(originalBytes.Length, 512));
            var declaration = System.Text.RegularExpressions.Regex.Match(prefix, "<\\?xml[^>]*encoding\\s*=\\s*[\\\"'](?<enc>[^\\\"']+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            encoding = declaration.Success ? Encoding.GetEncoding(declaration.Groups["enc"].Value) : new UTF8Encoding(false);
        }
        return encoding.GetBytes(xml);
    }

    private static int CountContextOccurrence(string text, int characterIndex, string sourceWord)
    {
        var occurrence = 0;
        foreach (System.Text.RegularExpressions.Match match in WordTokens.Matches(text))
        {
            if (match.Index >= characterIndex) break;
            if (NormalizeWordLetters(match.Value).Equals(NormalizeWordLetters(sourceWord), StringComparison.OrdinalIgnoreCase)) occurrence++;
        }
        return occurrence;
    }

    private static int ApplyWordReplacementMapToFb2(string inputPath, string outputPath, Dictionary<string, string> correctionMap)
    {
        if (correctionMap.Count == 0) return 0;
        var bytes = File.ReadAllBytes(inputPath);
        _ = new Fb2Parser().Parse(bytes);
        var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В итоговом FB2 отсутствует корневой элемент.");
        var ns = root.Name.Namespace;
        var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#')).Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
        var originalTextNodes = root.DescendantNodes().OfType<XText>().ToArray();
        var replacements = 0;
        foreach (var node in root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct())
        {
            var updated = WordTokens.Replace(node.Value, match =>
            {
                var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
                if (!correctionMap.TryGetValue(key, out var corrected)) return match.Value;
                replacements++;
                return KeepFirstLetterCase(match.Value, corrected);
            });
            if (!string.Equals(updated, node.Value, StringComparison.Ordinal)) node.Value = updated;
        }
        if (replacements == 0) return 0;
        // Reuse the structure-preserving writer used by the review path so applying
        // saved forms never drops comments, CDATA or the original FB2 formatting.
        if (!originalTextNodes.SequenceEqual(root.DescendantNodes().OfType<XText>()))
            throw new InvalidDataException("Изменился состав XML-текстовых узлов; безопасно сохранить исходную структуру FB2 невозможно.");
        SaveFb2PreservingSourceStructure(inputPath, outputPath, document);
        _ = new Fb2Parser().Parse(File.ReadAllBytes(outputPath));
        return replacements;
    }

    private static Dictionary<string, string> LoadYoDictionary()
    {
        EnsureBundledData();
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dictionaryDirectory = DictionaryDirectory;
        var dictionaryFiles = Directory.Exists(dictionaryDirectory)
            ? Directory.EnumerateFiles(dictionaryDirectory, "*.txt")
            : Enumerable.Empty<string>();
        if (!dictionaryFiles.Any())
        {
            var projectDirectory = Path.Combine(Directory.GetCurrentDirectory(), "dictionary");
            dictionaryFiles = Directory.Exists(projectDirectory)
                ? Directory.EnumerateFiles(projectDirectory, "*.txt")
                : Enumerable.Empty<string>();
        }
        foreach (var dictionaryFile in dictionaryFiles)
        foreach (var line in File.ReadLines(dictionaryFile, Encoding.UTF8))
        {
            var word = line.Trim().TrimStart('\uFEFF');
            // Файлы-списки содержат по слову в строке; строки с табуляцией — это таблицы
            // (например, список энклиноменов), их разбирает свой загрузчик.
            if (word.Contains('\t')) continue;
            if (word.Length == 0 || word.StartsWith('#') || !word.Contains('ё', StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = NormalizeWordLetters(word).ToLowerInvariant();
            if (!words.ContainsKey(normalized)) words[normalized] = word;
        }
        return words;
    }

    private static Dictionary<string, string> LoadSupplementaryStressDictionary()
    {
        EnsureBundledData();
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = SupplementaryStressDictionaryPath;
        if (!File.Exists(path))
        {
            var projectPath = Path.Combine(Directory.GetCurrentDirectory(), "dictionary", Path.GetFileName(path));
            if (File.Exists(projectPath)) path = projectPath;
        }
        if (!File.Exists(path)) return words;
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var word = rawLine.Trim().TrimStart('\uFEFF');
            if (word.Length == 0 || word.StartsWith('#')) continue;
            if (!word.Contains('\u0301')) continue;
            var key = DictionaryKey(word);
            if (key.Length == 0) continue;
            if (!words.ContainsKey(key)) words[key] = word;
        }
        return words;
    }

    private static string ApplyYoDictionary(string text)
    {
        var words = YoDictionary.Value;
        if (words.Count == 0) return text;
        return WordTokens.Replace(text, match =>
        {
            var sourceWord = match.Value;
            var normalized = NormalizeWordLetters(sourceWord).ToLowerInvariant();
            if (!words.TryGetValue(normalized, out var dictionaryWord)) return sourceWord;
            var sourceLetters = sourceWord.Replace("\u0301", "", StringComparison.Ordinal);
            var dictionaryLetters = dictionaryWord.Replace("ё", "е", StringComparison.OrdinalIgnoreCase);
            if (!sourceLetters.Equals(dictionaryLetters, StringComparison.OrdinalIgnoreCase)) return sourceWord;
            if (sourceWord.Any(ch => char.IsDigit(ch) || ch == '_')) return sourceWord;
            var stressIndex = sourceWord.IndexOf('\u0301');
            if (stressIndex >= 0 && (stressIndex == 0 || char.ToLowerInvariant(sourceWord[stressIndex - 1]) != 'е'))
                return sourceWord;
            // Preserve the source token's capitalization while restoring ё.
            var replacement = dictionaryWord;
            if (sourceWord.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)))
                replacement = replacement.ToUpperInvariant();
            else if (char.IsUpper(sourceWord[0]))
                replacement = char.ToUpperInvariant(replacement[0]) + replacement[1..];
            return replacement;
        });
    }

    private static ConcurrentDictionary<string, string> LoadUserDictionary()
    {
        var replacements = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(UserDictionaryPath)) return replacements;
        foreach (var line in File.ReadAllLines(UserDictionaryPath, Encoding.UTF8))
        {
            var columns = line.Split('\t', 2);
            if (columns.Length == 2 && columns[0].Length > 0) replacements[columns[0]] = columns[1];
        }
        return replacements;
    }

    private static Dictionary<string, string> LoadBuiltInStressDictionary()
    {
        EnsureBundledData();
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = BuiltInStressDictionaryPath;
        if (!File.Exists(path))
        {
            var projectPath = Path.Combine(Directory.GetCurrentDirectory(), "dictionary", Path.GetFileName(path));
            if (File.Exists(projectPath)) path = projectPath;
        }
        if (!File.Exists(path)) return words;
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var word = rawLine.Trim().TrimStart('\uFEFF');
            if (word.Length == 0 || word.StartsWith('#')) continue;
            if (!word.Contains('\u0301')) continue;
            var key = DictionaryKey(word);
            if (key.Length == 0) continue;
            if (!words.ContainsKey(key)) words[key] = word;
        }
        return words;
    }

    internal static bool HasFixedStress(string word) =>
        BuiltInStressDictionary.Value.ContainsKey(DictionaryKey(word))
        || UserDictionary.Value.ContainsKey(DictionaryKey(word));

    private static string ApplyUserDictionary(string text)
    {
        // Встроенный словарь литературных ударений применяется жёстко, но уступает
        // пользовательскому: человек может переопределить любую форму.
        var replacements = new Dictionary<string, string>(BuiltInStressDictionary.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in UserDictionary.Value)
            replacements[pair.Key] = pair.Value;
        if (replacements.Count == 0) return text;
        return WordTokens.Replace(text, match =>
        {
            var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
            if (!replacements.TryGetValue(key, out var replacement)) return match.Value;
            // Форма из словаря может быть строчной, а в тексте слово с заглавной:
            // регистр первой буквы сохраняем, как и в других словарных проходах.
            return Program.KeepFirstLetterCase(match.Value, replacement);
        });
    }

    // Like NormalizeWordLetters, but keeps the ё/е distinction: RUAccent's stress
    // dictionary stores ё spellings verbatim, so a е/ё mismatch must not match.
    private static string DictionaryKey(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var ch in word)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    // Adds a stress mark to words the neural model left unmarked, using RUAccent's
    // full stress dictionary (accents.json.gz). Every dictionary entry is the same
    // word with a single '+' before the stressed vowel; the mark is moved after that
    // vowel, so applying a match only ever adds a mark — never changes letters.
    // Capitalisation is restored from the source token, and ё words are skipped
    // because ё already carries the stress.
    private static string ApplyDictionaryStress(string text, IReadOnlyDictionary<string, string> dictionary)
    {
        if (dictionary.Count == 0 || string.IsNullOrEmpty(text)) return text;
        return WordTokens.Replace(text, match =>
        {
            var word = match.Value;
            if (word.IndexOf('\u0301') >= 0 || word.IndexOf('ё') >= 0 || word.IndexOf('Ё') >= 0) return word;
            if (!dictionary.TryGetValue(DictionaryKey(word), out var stressed)) return word;
            var replacement = MoveAccentAfterVowel(stressed);
            if (word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)))
                return replacement.ToUpperInvariant();
            if (char.IsUpper(word[0])) return UppercaseFirstLetter(replacement);
            return replacement;
        });
    }

    // Uppercases the first letter, skipping any leading non-letter characters (a
    // restored source word may still start with a stress mark), so a dictionary
    // form like "+яхонтовым" becomes "Я́хонтовым" rather than "я́хонтовым".
    private static string UppercaseFirstLetter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i])) continue;
            return text[..i] + char.ToUpperInvariant(text[i]) + text[(i + 1)..];
        }
        return text;
    }

    private static int CountReviewWords(IEnumerable<string> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            var marker = entry.IndexOf("Не распознаны:", StringComparison.Ordinal);
            if (marker < 0) continue;
            count += entry[(marker + "Не распознаны:".Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }
        return count;
    }

    private static void WriteWordReview(string inputPath, List<string> review, string? reportPath = null)
    {
        // Старое поведение: рядом с результатом Silero отдельный отчёт не создавался.
        // Если путь отчёта задан явно (рабочая папка DSR_…), пишем именно туда.
        if (reportPath is null && Path.GetFileNameWithoutExtension(inputPath).EndsWith("_silero", StringComparison.OrdinalIgnoreCase)) return;
        reportPath ??= ReviewPathFor(inputPath);
        var entries = new List<WordReviewEntry>();
        foreach (var block in review)
        {
            var contextPrefix = "Фрагмент: ";
            var unknownPrefix = "Не распознаны: ";
            var contextStart = block.IndexOf(contextPrefix, StringComparison.Ordinal);
            var unknownStart = block.IndexOf(unknownPrefix, StringComparison.Ordinal);
            if (contextStart < 0 || unknownStart < 0) continue;
            var contextStartIndex = contextStart + contextPrefix.Length;
            var context = block[contextStartIndex..unknownStart].TrimEnd();
            var words = block[(unknownStart + unknownPrefix.Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            entries.AddRange(words.Select(word => new WordReviewEntry(word, context, word)));
        }
        File.WriteAllText(reportPath, FormatWordReview(entries), new UTF8Encoding(true));
    }

    // --- Локальный ИИ-проход по контексту -------------------------------------------------
    // Слова, которые нейромодель RUAccent оставила без ударения (редкие, диалектные,
    // устаревшие, имена), уточняются локальной языковой моделью на этом же компьютере.
    // Проход идёт отдельным процессом Python: onnxruntime-genai тянет своё CUDA-окружение
    // и не должен конфликтовать с сессиями RUAccent в основном процессе.
    // Результат — только подсказки: они показываются в окне проверки и применяются
    // пользователем вручную.

    internal const int LlmBatchSize = 8;
    internal const string DefaultLlmModelFolder = "Qwen2.5-3B-Instruct-onnx";
    internal const string LlmGenaiConfigName = "genai_config.json";

    // Контрольные слова для теста движка: ударение каждого сверено с полным словарём ударений
    // RUAccent (accents.json.gz). Тест отвечает на вопрос «какой движок стоит выбрать на этом
    // компьютере»: он показывает и скорость, и число верных ударений. Контекст — обычное
    // предложение со словом: в книге слово тоже приходит с контекстом, иначе задача не та.
    internal static readonly (string Word, int Stress, string Context)[] LlmBenchmarkWords =
    {
        ("договор", 5, "Мы подписали договор вчера вечером."),
        ("звонит", 4, "Он звонит мне каждый вечер."),
        ("каталог", 5, "Библиотекарь открыл каталог и нашёл нужную книгу."),
        ("квартал", 5, "Мы переехали в новый квартал."),
        ("красивее", 4, "Этот сад красивее прежнего."),
        ("банты", 1, "Она завязала банты и вышла на улицу."),
        ("торты", 1, "Мама испекла торты к празднику."),
        ("жалюзи", 5, "Он опустил жалюзи и включил свет."),
        ("щавель", 3, "Бабушка принесла щавель с огорода."),
        ("баловать", 5, "Не надо баловать детей сладостями."),
        ("ходатайство", 3, "Адвокат подал ходатайство в суд."),
        ("обеспечение", 5, "Отец отвечает за обеспечение семьи."),
        ("украинский", 4, "Он выучил украинский язык за год."),
        ("бухгалтер", 4, "Бухгалтер посчитал расходы за месяц."),
        ("мизерный", 1, "Он получил мизерный оклад."),
        ("вероисповедание", 9, "В анкете спрашивают вероисповедание."),
        ("нефтепровод", 9, "Через степь проложили нефтепровод."),
        ("туфля", 1, "Одна туфля куда-то пропала."),
        ("средство", 2, "Это средство хорошо помогает от кашля."),
        ("диспансер", 7, "Он пошёл в диспансер на проверку."),
        ("феномен", 3, "Этот феномен изучают уже сто лет."),
        ("гастрономия", 7, "Она любит французскую гастрономию."),
        ("цемент", 3, "Рабочие привезли цемент и песок."),
        // И простые слова: так видно и базовую работу модели, и провалы на редких словах.
        ("молоко", 5, "Он выпил молоко утром."),
        ("собака", 3, "Собака громко лаяла во дворе."),
        ("человек", 5, "Этот человек пришёл первым."),
        ("работа", 3, "Работа заняла весь день."),
        ("деревня", 3, "Деревня стоит у самой реки."),
        ("окно", 3, "Он открыл окно и впустил свежий воздух."),
        ("книга", 2, "Книга лежала на столе."),
        ("ворона", 3, "Ворона села на забор."),
    };

    // Знак ударения ставится после ударной гласной — так же, как в подсказках ИИ и в окне проверки.
    internal static string StressMarked(string word, int vowelIndex) =>
        word[..(vowelIndex + 1)] + "\u0301" + word[(vowelIndex + 1)..];

    internal static string LlmModelsDirectory => Path.Combine(ModelCacheDirectory, "llm");
    internal static string LlmSettingsPath => Path.Combine(DataDirectory, "llm-настройки.json");
    internal static string LlmSuggestionsPath => Path.Combine(DataDirectory, "llm-подсказки.json");

    // --- Возврат к уже прогнанному документу ------------------------------------------------
    // Отчёт, отредактированный файл и исходная книга последней работы. При следующем запуске
    // программа подставляет их в поля вкладки «1. Обработка книги», чтобы можно было продолжить
    // проверку или внести дополнительные правки в тот же документ.
    internal sealed record LastSession(string Review, string File, string Input)
    {
        // Свойство File перекрывает одноимённый класс, поэтому здесь нужен полный путь.
        internal bool IsUsable => !string.IsNullOrWhiteSpace(Review) && System.IO.File.Exists(Review);
    }

    internal static string LastSessionPath => Path.Combine(DataDirectory, "последняя-работа.json");

    internal static LastSession? LoadLastSession()
    {
        try
        {
            if (!File.Exists(LastSessionPath)) return null;
            return JsonSerializer.Deserialize<LastSession>(File.ReadAllText(LastSessionPath, Encoding.UTF8));
        }
        catch (Exception error)
        {
            LogError("Чтение последней работы", error);
            return null;
        }
    }

    internal static void SaveLastSession(string? reviewPath, string? outputPath, string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(reviewPath)) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LastSessionPath,
                JsonSerializer.Serialize(new LastSession(reviewPath!, outputPath ?? string.Empty, inputPath ?? string.Empty), LlmJsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись последней работы", error);
        }
    }

    // --- Настройки окна -----------------------------------------------------------------------
    // Их две: отключать ли всплывающие подсказки (их в интерфейсе много, и знакомому
    // пользователю они мешают — галочка в шапке убирает их все сразу) и на каком устройстве
    // считать движки подсказок на PyTorch (silero-stress и omogre). Обе сохраняются, чтобы
    // не выставлять их заново при каждом запуске.
    internal sealed record WindowSettings(bool HideToolTips, string? LlmDevice = null, double ControlFontSize = 0);

    internal static string WindowSettingsPath => Path.Combine(DataDirectory, "настройки-окна.json");

    internal static WindowSettings LoadWindowSettings()
    {
        try
        {
            if (!File.Exists(WindowSettingsPath)) return new WindowSettings(false);
            return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(WindowSettingsPath, Encoding.UTF8))
                ?? new WindowSettings(false);
        }
        catch (Exception error)
        {
            LogError("Чтение настроек окна", error);
            return new WindowSettings(false);
        }
    }

    internal static void SaveWindowSettings(WindowSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(WindowSettingsPath,
                JsonSerializer.Serialize(settings, LlmJsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись настроек окна", error);
        }
    }

    internal static bool LoadHideToolTips() => LoadWindowSettings().HideToolTips;

    internal static void SaveHideToolTips(bool hide) =>
        SaveWindowSettings(LoadWindowSettings() with { HideToolTips = hide });

    /// <summary>Устройство для движков подсказок на PyTorch: «CPU» или «CUDA».</summary>
    internal static string LoadLlmDevice()
    {
        var device = LoadWindowSettings().LlmDevice;
        return string.Equals(device, "CUDA", StringComparison.OrdinalIgnoreCase) ? "CUDA" : "CPU";
    }

    internal static void SaveLlmDevice(string device) =>
        SaveWindowSettings(LoadWindowSettings() with
        {
            LlmDevice = string.Equals(device, "CUDA", StringComparison.OrdinalIgnoreCase) ? "CUDA" : "CPU"
        });

    /// <summary>Размер шрифта примеров на странице контроля ИИ (допустимо 10–30).</summary>
    internal static double LoadControlFontSize()
    {
        var size = LoadWindowSettings().ControlFontSize;
        return size >= 10 && size <= 30 ? size : 14;
    }

    internal static void SaveControlFontSize(double size) =>
        SaveWindowSettings(LoadWindowSettings() with { ControlFontSize = Math.Clamp(size, 10, 30) });

    // Папка выбранной модели. В настройках хранится либо имя папки внутри ресурсы\модели\llm,
    // либо полный путь — тогда модель можно держать где угодно.
    internal static string LlmModelDirectory(string model) =>
        Path.IsPathRooted(model) ? model : Path.Combine(LlmModelsDirectory, model);

    // --- Способы получить подсказки: своя модель на диске, сервер Ollama или внешний API ---
    // Сервис с OpenAI-совместимым интерфейсом (OpenAI, OpenRouter, LM Studio, llama.cpp, vLLM)
    // подключается одинаково: адрес, ключ, имя модели. Модели без цензуры тоже подходят — это
    // просто другая модель у того же сервиса, особых настроек для них не требуется.
    internal sealed record LlmEngine(string Provider, string LocalModel, string RemoteModel, string BaseUrl, string ApiKey)
    {
        public const string Local = "local";
        public const string Ollama = "ollama";
        public const string OpenAi = "openai";
        // Второй движок расстановки ударений: не языковая модель, а silero-stress.
        // Работает локально, отдельная модель и адрес ему не нужны.
        public const string Silero = "silero";
        // Третий движок: omogre — контекстная модель омографов. Тоже работает локально,
        // без модели и адреса, но именно она разбирает спорные случаи по контексту
        // предложения, а не по слову в отрыве от него.
        public const string Omogre = "omogre";

        public bool IsLocal => Provider is not (Ollama or OpenAi);

        public bool IsSilero => Provider == Silero;

        public bool IsOmogre => Provider == Omogre;

        // Имя движка для кэша подсказок и для журнала. Ключ внешнего сервиса сюда не попадает.
        public string Id => Provider switch
        {
            Ollama => "ollama#" + RemoteModel,
            OpenAi => "api#" + HostOf(BaseUrl) + "#" + RemoteModel,
            Silero => "silero",
            Omogre => "omogre",
            _ => LocalModel,
        };

        public string Title => Provider switch
        {
            Ollama => string.IsNullOrWhiteSpace(RemoteModel) ? $"Ollama ({BaseUrl})" : $"Ollama ({RemoteModel})",
            OpenAi => string.IsNullOrWhiteSpace(RemoteModel)
                ? $"внешний API ({BaseUrl})"
                : $"внешний API ({RemoteModel}, {BaseUrl})",
            Silero => "silero-stress (движок ударений)",
            Omogre => "omogre (контекстные ударения)",
            _ => LocalModel,
        };

        public static LlmEngine FromSettings(LlmSettings settings, string localModel)
        {
            var provider = (settings.Provider ?? Local).ToLowerInvariant();
            if (provider == Silero) return new LlmEngine(Silero, "", "", "", "");
            if (provider == Omogre) return new LlmEngine(Omogre, "", "", "", "");
            return new LlmEngine(
                provider,
                localModel,
                provider == Ollama ? settings.OllamaModel : settings.ApiModel,
                provider == Ollama ? settings.OllamaUrl : settings.ApiUrl,
                provider == OpenAi ? settings.ApiKey : "");
        }

        private static string HostOf(string url)
        {
            try
            {
                var parsed = new Uri(url);
                return parsed.Host + parsed.AbsolutePath.TrimEnd('/');
            }
            catch (UriFormatException)
            {
                return url;
            }
        }
    }

    // Настройки ИИ сохраняются в ресурсы\llm-настройки.json. Ключ внешнего сервиса лежит там же
    // в открытом виде: файл не стоит никому передавать, а вместо него можно задать переменную
    // окружения DSR_LLM_API_KEY.
    internal sealed record LlmSettings
    {
        public string Provider { get; init; } = LlmEngine.Local;
        public string Model { get; init; } = "";
        public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";
        public string OllamaModel { get; init; } = "";
        public string ApiUrl { get; init; } = "https://api.openai.com/v1";
        public string ApiModel { get; init; } = "";
        public string ApiKey { get; init; } = "";
        // Искать ли подсказку у других движков, прежде чем спрашивать выбранный.
        public bool ReuseOtherEngines { get; init; } = true;
        public Dictionary<string, LlmBenchmark>? Benchmarks { get; init; }
    }

    internal sealed record LlmBenchmark(string Engine, string Title, int Correct, int Total, double Seconds, string When);

    private static readonly JsonSerializerOptions LlmJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Готовыми моделями считаются все папки рядом с программой, в которых есть genai_config.json:
    // чтобы добавить новую, достаточно положить её в ресурсы\модели\llm — она сразу появится в списке.
    internal static List<string> DiscoverLlmModels()
    {
        var models = new List<string>();
        try
        {
            if (Directory.Exists(LlmModelsDirectory))
                foreach (var directory in Directory.EnumerateDirectories(LlmModelsDirectory))
                    if (File.Exists(Path.Combine(directory, LlmGenaiConfigName)))
                        models.Add(Path.GetFileName(directory));
        }
        catch (Exception error)
        {
            LogError("Поиск моделей ИИ", error);
        }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    internal static LlmSettings LoadLlmSettings()
    {
        try
        {
            if (File.Exists(LlmSettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<LlmSettings>(File.ReadAllText(LlmSettingsPath, Encoding.UTF8));
                if (loaded != null)
                    return loaded.Benchmarks == null
                        ? loaded with { Benchmarks = new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal) }
                        : loaded;
            }
        }
        catch (Exception error)
        {
            LogError("Чтение настроек ИИ", error);
        }
        return new LlmSettings
        {
            Model = DefaultLlmModelFolder,
            Benchmarks = new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal),
        };
    }

    internal static void SaveLlmSettings(LlmSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LlmSettingsPath,
                JsonSerializer.Serialize(settings, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись настроек ИИ", error);
        }
    }

    // Движок из настроек: его используют и окно проверки, и командная строка.
    internal static LlmEngine CurrentLlmEngine() => LlmEngine.FromSettings(LoadLlmSettings(), CurrentLlmModel());

    // Выбранные в окне настройки: удобно собрать их одной строкой и сохранить.
    internal static void SaveLlmSettings(string provider, string localModel, string ollamaUrl, string ollamaModel,
        string apiUrl, string apiModel, string apiKey, bool reuseOtherEngines)
    {
        var benchmarks = LoadLlmSettings().Benchmarks ?? new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal);
        SaveLlmSettings(new LlmSettings
        {
            Provider = provider,
            Model = localModel,
            OllamaUrl = ollamaUrl,
            OllamaModel = ollamaModel,
            ApiUrl = apiUrl,
            ApiModel = apiModel,
            ApiKey = apiKey,
            ReuseOtherEngines = reuseOtherEngines,
            Benchmarks = benchmarks,
        });
    }

    internal static void SaveLlmBenchmark(LlmBenchmarkResult result)
    {
        if (!result.Ok) return;
        var settings = LoadLlmSettings();
        var benchmarks = new Dictionary<string, LlmBenchmark>(
            settings.Benchmarks ?? new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal), StringComparer.Ordinal)
        {
            [result.EngineId] = new LlmBenchmark(result.EngineId, result.Title, result.Correct, result.Total,
                result.Seconds, DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
        };
        SaveLlmSettings(settings with { Benchmarks = benchmarks });
    }

    // Выбранная модель: сохранённый выбор, если такая папка на месте, иначе модель по умолчанию,
    // иначе первая найденная.
    internal static string CurrentLlmModel()
    {
        var saved = LoadLlmSettings().Model;
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(Path.Combine(LlmModelDirectory(saved), LlmGenaiConfigName)))
            return saved;
        var available = DiscoverLlmModels();
        var preferred = available.FirstOrDefault(name =>
            string.Equals(name, DefaultLlmModelFolder, StringComparison.OrdinalIgnoreCase));
        return preferred ?? available.FirstOrDefault() ?? DefaultLlmModelFolder;
    }

    internal sealed record LlmStressItem(string Word, IReadOnlyList<string> Contexts);

    // Живой прогресс ИИ-прохода по одному слову: интерфейс показывает его списком,
    // чтобы было видно, что модель работает, и не казалось, что проход завис.
    internal enum LlmWordPhase { Thinking, Answered, NoAnswer }

    internal sealed record LlmWordProgress(string Word, LlmWordPhase Phase, string? Suggestion);

    // Ответ модели, который не удалось принять как правку: сырой ответ и причина отказа.
    internal sealed record LlmRejected(string Raw, string Reason);

    internal sealed record LlmStressOutcome(
        bool Ok, string Message, string Device, double Seconds, int Answered, int Items,
        Dictionary<string, string> Suggestions, Dictionary<string, LlmRejected> Rejected)
    {
        public static LlmStressOutcome Fail(string message, int items) =>
            new(false, message, "", 0, 0, items,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlmRejected>(StringComparer.Ordinal));
    }

    // Отдельное вхождение слова для контекстного разбора: одна и та же словоформа
    // разбирается по своему предложению, поэтому ответ привязан к месту в тексте
    // (слово + контекст + номер вхождения), а не к слову целиком.
    internal sealed record LlmCaseItem(string Word, string Context, int Occurrence);

    internal sealed record LlmCaseOutcome(
        bool Ok, string Message, string Device, double Seconds, int Answered, int Items,
        Dictionary<string, string> Cases, Dictionary<string, LlmRejected> Rejected)
    {
        public static LlmCaseOutcome Fail(string message, int items) =>
            new(false, message, "", 0, 0, items,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlmRejected>(StringComparer.Ordinal));
    }

    // Ключ отдельного вхождения: слово + видимый контекст + номер вхождения.
    // Тот же ключ используют отчёт (Своя форма) и страница «Контроль ИИ результатов».
    internal static string LlmCaseKey(string word, string context, int occurrence) =>
        ReviewWordKey(word) + "\u0001" + context + "\u0001" + occurrence;

    // Кэш подсказок: ключ — модель, имя книги и слово, значение — слово со знаком ударения.
    // Кэш экономит самый дорогой шаг (загрузка модели и генерация) при повторных прогонах
    // той же книги и хранится рядом с пользовательским словарём. Модель входит в ключ, чтобы
    // ответы одной модели не подменяли ответы другой при переключении.
    internal static Dictionary<string, string> LoadLlmSuggestions()
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(LlmSuggestionsPath)) return empty;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(LlmSuggestionsPath, Encoding.UTF8));
            if (loaded == null) return empty;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in loaded) result[MigrateLlmKey(pair.Key)] = pair.Value;
            return result;
        }
        catch (Exception error)
        {
            LogError("Чтение кэша ИИ-подсказок", error);
            return empty;
        }
    }

    internal static void SaveLlmSuggestions(Dictionary<string, string> suggestions)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LlmSuggestionsPath,
                JsonSerializer.Serialize(suggestions, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись кэша ИИ-подсказок", error);
        }
    }

    /// <summary>
    /// Ответы ИИ по каждому вхождению (а не по слову). Ключ — <see cref="LlmCaseKey"/>,
    /// значение — форма со знаком ударения. Лежат отдельно от отчёта, потому что отчёт
    /// описывает слово, а случай — конкретное вхождение в предложении. Нужны, чтобы после
    /// перезапуска осталась статистика: где ИИ была права, а где вхождение перенесли руками.
    /// </summary>
    internal static string CaseAnswersPath => Path.Combine(DataDirectory, "ии-по-случаям.json");

    // Ключ книги — имя файла отчёта: отчёт и книга всегда лежат в одной папке, поэтому
    // полный путь в ключ не берём и файл переживает перенос папки с книгой.
    private static string CaseAnswersBookKey(string? reportPath) =>
        string.IsNullOrWhiteSpace(reportPath) ? string.Empty : Path.GetFileName(reportPath);

    private static Dictionary<string, Dictionary<string, string>> LoadAllCaseAnswers()
    {
        try
        {
            if (!File.Exists(CaseAnswersPath)) return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                       File.ReadAllText(CaseAnswersPath, Encoding.UTF8))
                   ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            LogError("Чтение ответов ИИ по случаям", error);
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static Dictionary<string, string> LoadCaseAnswers(string? reportPath)
    {
        var book = CaseAnswersBookKey(reportPath);
        if (book.Length == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var all = LoadAllCaseAnswers();
        return all.TryGetValue(book, out var answers)
            ? new Dictionary<string, string>(answers, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // Сброс ответов по случаям для одного отчёта: нужен после нового сравнения, иначе
    // вкладка 4 продолжала бы считать слово «откорректированным ИИ» по старому разбору.
    internal static void ClearCaseAnswers(string? reportPath)
    {
        var book = CaseAnswersBookKey(reportPath);
        if (book.Length == 0) return;
        try
        {
            var all = LoadAllCaseAnswers();
            if (!all.Remove(book)) return;
            File.WriteAllText(CaseAnswersPath, JsonSerializer.Serialize(all), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Сброс ответов ИИ по случаям", error);
        }
    }

    internal static void SaveCaseAnswers(string? reportPath, IReadOnlyDictionary<string, string> answers)
    {
        var book = CaseAnswersBookKey(reportPath);
        if (book.Length == 0 || answers.Count == 0) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var all = LoadAllCaseAnswers();
            if (!all.TryGetValue(book, out var target))
            {
                target = new Dictionary<string, string>(StringComparer.Ordinal);
                all[book] = target;
            }
            foreach (var pair in answers) target[pair.Key] = pair.Value;
            File.WriteAllText(CaseAnswersPath, JsonSerializer.Serialize(all), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись ответов ИИ по случаям", error);
        }
    }

    /// <summary>
    /// Один пример для будущего обучения: контекст, ответ ИИ по этому вхождению, решение
    /// человека и ответы RUAccent/Silero. Пишется в тот же журнал примеров, что и правки
    /// ручной доводки, — по строке JSON на пример.
    /// </summary>
    internal static void AppendTrainingExample(object example)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var line = JsonSerializer.Serialize(example);
            lock (LogLock) File.AppendAllText(TrainingLogPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись примера для обучения", error);
        }
    }

    internal static string LlmCacheKey(string model, string book, string word) =>
        (model + "|" + LlmBookName(book) + "|" + word).ToLowerInvariant();

    // Ключи прежних версий не содержали модель: их ответы получены моделью по умолчанию,
    // поэтому при чтении старых файлов дописываем её имя, чтобы кэш не терялся.
    private static string MigrateLlmKey(string key) =>
        key.Count(character => character == '|') == 1
            ? DefaultLlmModelFolder.ToLowerInvariant() + "|" + key
            : key;

    // Готовая подсказка по этому слову, найденная у любой модели. Нужна при переключении
    // моделей: слова, которые уже разобрала прежняя модель, повторно не отправляются —
    // иначе каждая смена модели стоила бы полного прогона по всей книге.
    internal static string? FindAnyLlmSuggestion(Dictionary<string, string> suggestions, string book, string word)
    {
        var suffix = "|" + LlmBookName(book).ToLowerInvariant() + "|" + word.ToLowerInvariant();
        foreach (var pair in suggestions)
            if (pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                return pair.Value;
        return null;
    }

    // Имя книги одинаково для файла-книги и для отчёта «…_проверка-слов.txt», чтобы кэш
    // подсказок совпадал при запуске из окна и из командной строки.
    internal static string LlmBookName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string reviewSuffix = "_проверка-слов";
        return name.EndsWith(reviewSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^reviewSuffix.Length]
            : name;
    }

    internal static LlmStressOutcome RunLlmStressCheck(IReadOnlyList<LlmStressItem> items, string book,
        Action<string> report, CancellationToken token, LlmEngine? engine = null, bool benchmark = false,
        Action<LlmWordProgress>? wordProgress = null)
    {
        if (items.Count == 0)
            return new LlmStressOutcome(true, "Нет слов для проверки.", "", 0, 0, 0,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlmRejected>(StringComparer.Ordinal));
        var chosen = engine ?? CurrentLlmEngine();
        // silero-stress — не языковая модель: у него нет папки с ONNX-весами, поэтому
        // дальше по коду путь другой, а всё остальное (кэш, отчёт, кнопки) остаётся общим.
        if (chosen.IsSilero) return RunSileroStressCheck(items, book, report, token, chosen);
        // omogre — тоже локальный движок без папки модели: контекстные ударения по омографам.
        if (chosen.IsOmogre) return RunOmogreStressCheck(items, book, report, token, chosen);
        var modelDirectory = LlmModelDirectory(chosen.LocalModel);
        if (chosen.IsLocal && (string.IsNullOrWhiteSpace(chosen.LocalModel)
                || !File.Exists(Path.Combine(modelDirectory, LlmGenaiConfigName))))
            return LlmStressOutcome.Fail(
                "Не найдена выбранная модель ИИ: " + (string.IsNullOrWhiteSpace(chosen.LocalModel) ? "(не выбрана)" : chosen.LocalModel) +
                Environment.NewLine + "Ожидается папка:" + Environment.NewLine + modelDirectory +
                Environment.NewLine + Environment.NewLine +
                "Модель скачивается один раз командой:" + Environment.NewLine +
                "python -c \"from huggingface_hub import snapshot_download; " +
                "snapshot_download('elbruno/Qwen2.5-3B-Instruct-onnx', local_dir=r'" + modelDirectory + "')\"",
                items.Count);
        if (!chosen.IsLocal && (string.IsNullOrWhiteSpace(chosen.BaseUrl) || string.IsNullOrWhiteSpace(chosen.RemoteModel)))
            return LlmStressOutcome.Fail(
                chosen.Provider == LlmEngine.Ollama
                    ? "Не выбран сервер Ollama или модель. Укажите адрес (обычно http://127.0.0.1:11434) " +
                      "и имя загруженной модели, например qwen2.5:7b."
                    : "Не указан адрес сервиса или имя модели. Адрес нужен вместе с версией, " +
                      "например https://api.openai.com/v1.",
                items.Count);

        string script;
        string python;
        try
        {
            script = ExtractEmbeddedFile("DSR.llm_stress_worker.py", "llm_stress_worker.py");
            python = EnsurePythonRuntime();
            // Локальная модель ONNX требует пакет onnxruntime-genai — доустанавливаем его здесь.
            if (chosen.IsLocal)
            {
                var runtimeError = EnsureLlmRuntimeOrError(python);
                if (runtimeError != null)
                    return LlmStressOutcome.Fail(runtimeError, items.Count);
            }
        }
        catch (Exception error)
        {
            LogError("Подготовка ИИ-прохода", error);
            return LlmStressOutcome.Fail(error.Message, items.Count);
        }

        var work = Path.Combine(DataDirectory, "временные-файлы");
        Directory.CreateDirectory(work);
        var requestPath = Path.Combine(work, "llm-запрос.json");
        var responsePath = Path.Combine(work, "llm-ответ.json");
        try
        {
            if (File.Exists(responsePath)) File.Delete(responsePath);
            var payload = new
            {
                book = new { title = Path.GetFileNameWithoutExtension(book), author = "" },
                words = items.Select(item => new { word = item.Word, contexts = item.Contexts.ToArray() }).ToArray(),
            };
            File.WriteAllText(requestPath, JsonSerializer.Serialize(payload, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись запроса для ИИ", error);
            return LlmStressOutcome.Fail("Не удалось подготовить запрос к ИИ: " + error.Message, items.Count);
        }

        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.Environment["PYTHONUTF8"] = "1";
        ConfigureResourceEnvironment(start);
        var arguments = new List<string>
        {
            "-u", script,
            "--in", requestPath,
            "--out", responsePath,
            "--batch", LlmBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (chosen.IsLocal)
        {
            arguments.Add("--model");
            arguments.Add(modelDirectory);
        }
        else
        {
            arguments.Add("--provider");
            arguments.Add(chosen.Provider);
            arguments.Add("--base-url");
            arguments.Add(chosen.BaseUrl);
            arguments.Add("--remote-model");
            arguments.Add(chosen.RemoteModel);
            if (!string.IsNullOrWhiteSpace(chosen.ApiKey))
                start.Environment["DSR_LLM_API_KEY"] = chosen.ApiKey;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var errors = new StringBuilder();
        Process process;
        try
        {
            process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить процесс ИИ.");
        }
        catch (Exception error)
        {
            LogError("Запуск ИИ-прохода", error);
            return LlmStressOutcome.Fail("Не удалось запустить ИИ-проход: " + error.Message, items.Count);
        }

        void Handle(string? line)
        {
            if (line == null) return;
            var data = TrimProtocolLine(line);
            if (!data.StartsWith("DSR_LLM:", StringComparison.Ordinal))
            {
                if (data.Length > 0) lock (errors) errors.AppendLine(data);
                return;
            }
            var payload = data["DSR_LLM:".Length..];
            // События по отдельным словам не идут в строку состояния: их показывает список
            // в окне прохода. В консольном режиме (без обработчика) они просто не нужны.
            if (TryParseLlmWordEvent(payload, out var wordEvent))
            {
                wordProgress?.Invoke(wordEvent);
                return;
            }
            foreach (var message in TranslateLlmProgress(payload))
                report(message);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (process)
        {
            try
            {
                while (!process.WaitForExit(300))
                {
                    if (!token.IsCancellationRequested) continue;
                    try { process.Kill(true); } catch { /* процесс уже завершился */ }
                    return LlmStressOutcome.Fail("ИИ-проход отменён.", items.Count);
                }
                process.WaitForExit();
            }
            catch (Exception error)
            {
                LogError("Ожидание ИИ-прохода", error);
                return LlmStressOutcome.Fail("Ошибка ИИ-прохода: " + error.Message, items.Count);
            }
        }

        if (!File.Exists(responsePath))
            return LlmStressOutcome.Fail(
                "ИИ-проход завершился без ответа." + Environment.NewLine + errors.ToString().Trim(), items.Count);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(responsePath, Encoding.UTF8));
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                return LlmStressOutcome.Fail(errorElement.GetString() ?? "Ошибка ИИ-прохода", items.Count);

            var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
            var rejected = new Dictionary<string, LlmRejected>(StringComparer.Ordinal);
            var rows = new StringBuilder();
            var answered = 0;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var word = item.TryGetProperty("word", out var wordElement) && wordElement.ValueKind == JsonValueKind.String
                        ? wordElement.GetString() : null;
                    if (string.IsNullOrEmpty(word)) continue;
                    var suggestion = item.TryGetProperty("suggestion", out var suggestionElement) && suggestionElement.ValueKind == JsonValueKind.String
                        ? suggestionElement.GetString() : null;
                    var raw = item.TryGetProperty("raw", out var rawElement) && rawElement.ValueKind == JsonValueKind.String
                        ? rawElement.GetString() : null;
                    var reason = item.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                        ? sourceElement.GetString() ?? "" : "";
                    // Ответ принимается только если он проходит ту же проверку, что и правка,
                    // введённая человеком: те же буквы и ровно один знак ударения.
                    if (!string.IsNullOrEmpty(suggestion) && IsValidCorrection(word, suggestion))
                    {
                        suggestions[word] = suggestion;
                        answered++;
                        rows.AppendLine($"принято\t{word}\t{suggestion}\t{(string.IsNullOrEmpty(reason) ? "model" : reason)}");
                    }
                    else
                    {
                        // Сюда попадают все слова без готового ответа — и те, где модель ответила
                        // не тем словом, и те, где ответа не было вовсе: их надо показать честно.
                        if (!string.IsNullOrEmpty(suggestion))
                            rejected[word] = new LlmRejected(suggestion, "ответ не похож на исходное слово");
                        else
                            rejected[word] = new LlmRejected(raw ?? "", string.IsNullOrEmpty(reason) ? "none" : reason);
                        rows.AppendLine($"отклонено\t{word}\t{raw ?? ""}\t{(string.IsNullOrEmpty(reason) ? "нет ответа" : reason)}\t{suggestion ?? ""}");
                    }
                }
            }

            var device = "";
            var seconds = 0d;
            if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                if (stats.TryGetProperty("device", out var deviceElement) && deviceElement.ValueKind == JsonValueKind.String)
                    device = deviceElement.GetString() ?? "";
                if (stats.TryGetProperty("seconds", out var secondsElement) && secondsElement.ValueKind == JsonValueKind.Number)
                    seconds = secondsElement.GetDouble();
            }
            var outcome = new LlmStressOutcome(true, "", device, seconds, answered, items.Count, suggestions, rejected);
            if (!benchmark) WriteLlmLog(items, outcome, rows, errors, chosen);
            return outcome;
        }
        catch (Exception error)
        {
            LogError("Чтение ответа ИИ", error);
            return LlmStressOutcome.Fail("Не удалось разобрать ответ ИИ: " + error.Message, items.Count);
        }
    }

    // Второй прогон по словам отчёта — движком silero-stress. Ответ приходит уже в виде
    // готового написания, поэтому проверяется ровно тем же IsValidCorrection, что и ответ
    // языковой модели, и попадает в ту же таблицу подсказок: человек решает, принимать ли.
    // Слова без ответа пишутся в журнал и в список «без ответа», повторно они не спрашиваются.
    private static LlmStressOutcome RunSileroStressCheck(IReadOnlyList<LlmStressItem> items, string book,
        Action<string> report, CancellationToken token, LlmEngine engine)
    {
        string python;
        try
        {
            python = EnsurePythonRuntime();
            var runtimeError = EnsureSileroRuntimeOrError(python);
            if (runtimeError != null) return LlmStressOutcome.Fail(runtimeError, items.Count);
        }
        catch (Exception error)
        {
            LogError("Подготовка silero-stress", error);
            return LlmStressOutcome.Fail("Не удалось подготовить silero-stress: " + error.Message, items.Count);
        }

        var watch = Stopwatch.StartNew();
        var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new Dictionary<string, LlmRejected>(StringComparer.Ordinal);
        var rows = new StringBuilder();
        var errors = new StringBuilder();
        // Слова идут пачками: отчёт на 2000 слов — это несколько мегабайт JSON с контекстами,
        // а по одной строке ответа приложение ещё и показывает ход работы.
        const int batchSize = 250;
        var answered = 0;
        var device = "";
        try
        {
            // Устройство берётся из настроек окна: silero-stress и omogre работают через
            // PyTorch, поэтому у них общий выбор «процессор или видеокарта». Сборка PyTorch
            // ставится сразу для процессора, а CUDA-сборка — отдельной кнопкой во вкладке
            // «2. Расхождения и ИИ». Если выбранной сборки нет, воркер сам вернётся на
            // процессор и скажет об этом в строке хода работы.
            using var worker = new SileroStressWorker(LoadLlmDevice(), report);
            worker.EnsureStarted();
            device = worker.LoadedDevice;
            for (var offset = 0; offset < items.Count; offset += batchSize)
            {
                if (token.IsCancellationRequested) return LlmStressOutcome.Fail("Прогон silero-stress отменён.", items.Count);
                var batch = items.Skip(offset).Take(batchSize).ToArray();
                var request = JsonSerializer.Serialize(new
                {
                    op = "words",
                    items = batch.Select(item => new { word = item.Word, contexts = item.Contexts }).ToArray()
                });
                var response = worker.Send(request, token);
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                    return LlmStressOutcome.Fail(errorElement.GetString() ?? "Ошибка silero-stress", items.Count);

                // Сначала отказы, потом подсказки: так слово с ответом не потеряется,
                // если воркер вернул его в обоих списках.
                if (root.TryGetProperty("rejected", out var rejectedList) && rejectedList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rejectedList.EnumerateArray())
                    {
                        var word = item.TryGetProperty("word", out var wordElement) && wordElement.ValueKind == JsonValueKind.String
                            ? wordElement.GetString() : null;
                        if (string.IsNullOrEmpty(word) || suggestions.ContainsKey(word)) continue;
                        var reason = item.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                            ? reasonElement.GetString() ?? "" : "нет ответа";
                        rejected[word] = new LlmRejected("", reason);
                        rows.AppendLine($"отклонено\t{word}\t\t{reason}\t");
                    }
                }
                if (root.TryGetProperty("suggestions", out var suggestionsList) && suggestionsList.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pair in suggestionsList.EnumerateObject())
                    {
                        if (pair.Value.ValueKind != JsonValueKind.String) continue;
                        var suggestion = pair.Value.GetString() ?? "";
                        var item = batch.FirstOrDefault(candidate => candidate.Word == pair.Name);
                        var word = item?.Word ?? pair.Name;
                        // Принимаем ответ только если это то же слово с ровно одним знаком:
                        // иначе silero выдала омограф целиком или соседнее слово.
                        if (!string.IsNullOrEmpty(suggestion) && IsValidCorrection(word, suggestion))
                        {
                            suggestions[word] = suggestion;
                            rejected.Remove(word);
                            answered++;
                            rows.AppendLine($"принято\t{word}\t{suggestion}\tsilero");
                        }
                        else
                        {
                            if (!rejected.ContainsKey(word))
                                rejected[word] = new LlmRejected(suggestion, "ответ не похож на исходное слово");
                            rows.AppendLine($"отклонено\t{word}\t\tответ не похож на исходное слово\t{suggestion}");
                        }
                    }
                }
                report($"silero-stress: {Math.Min(offset + batchSize, items.Count)} из {items.Count} слов…");
            }
        }
        catch (OperationCanceledException)
        {
            return LlmStressOutcome.Fail("Прогон silero-stress отменён.", items.Count);
        }
        catch (Exception error)
        {
            LogError("Прогон silero-stress", error);
            return LlmStressOutcome.Fail("Не удалось получить ответ silero-stress: " + error.Message, items.Count);
        }

        var outcome = new LlmStressOutcome(true, "", device, watch.Elapsed.TotalSeconds, answered, items.Count,
            suggestions, rejected);
        WriteLlmLog(items, outcome, rows, errors, engine);
        return outcome;
    }

    // Третий движок подсказок — omogre: локальная контекстная модель омографов.
    // Именно она разбирает спорные случаи (RUAccent и silero разошлись), потому что
    // выбирает ударение по предложению. Слова идут пачками, ответ приходит на всю пачку
    // и проверяется тем же способом, что и правка человека.
    private static LlmStressOutcome RunOmogreStressCheck(IReadOnlyList<LlmStressItem> items, string book,
        Action<string> report, CancellationToken token, LlmEngine engine)
    {
        string python;
        try
        {
            python = EnsurePythonRuntime();
            var runtimeError = EnsureOmogreRuntimeOrError(python);
            if (runtimeError != null) return LlmStressOutcome.Fail(runtimeError, items.Count);
        }
        catch (Exception error)
        {
            LogError("Подготовка omogre", error);
            return LlmStressOutcome.Fail("Не удалось подготовить omogre: " + error.Message, items.Count);
        }

        var watch = Stopwatch.StartNew();
        var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new Dictionary<string, LlmRejected>(StringComparer.Ordinal);
        var rows = new StringBuilder();
        var errors = new StringBuilder();
        // Пачка меньше, чем у silero: контексты в отчёте — целые фразы, и модель
        // читает их целиком, поэтому сотни фраз сразу дали бы большой запрос.
        const int batchSize = 100;
        var answered = 0;
        var device = "";
        var dataPath = Path.Combine(ModelCacheDirectory, "omogre");
        try
        {
            using var worker = new OmogreStressWorker(dataPath, LoadLlmDevice(), report);
            worker.EnsureStarted();
            device = worker.LoadedDevice;
            for (var offset = 0; offset < items.Count; offset += batchSize)
            {
                if (token.IsCancellationRequested) return LlmStressOutcome.Fail("Прогон omogre отменён.", items.Count);
                var batch = items.Skip(offset).Take(batchSize).ToArray();
                var request = JsonSerializer.Serialize(new
                {
                    op = "words",
                    items = batch.Select(item => new
                    {
                        word = item.Word,
                        // В контекст добавляется невидимый маркер номера вхождения; модели он не нужен.
                        contexts = item.Contexts.Select(context => SplitReviewContext(context).Context).ToArray()
                    }).ToArray()
                });
                var response = worker.Send(request, token);
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                    return LlmStressOutcome.Fail(errorElement.GetString() ?? "Ошибка omogre", items.Count);

                // Сначала отказы, потом подсказки: так слово с ответом не потеряется,
                // если воркер вернул его в обоих списках.
                if (root.TryGetProperty("rejected", out var rejectedList) && rejectedList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rejectedList.EnumerateArray())
                    {
                        var word = item.TryGetProperty("word", out var wordElement) && wordElement.ValueKind == JsonValueKind.String
                            ? wordElement.GetString() : null;
                        if (string.IsNullOrEmpty(word) || suggestions.ContainsKey(word)) continue;
                        var reason = item.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                            ? reasonElement.GetString() ?? "" : "нет ответа";
                        rejected[word] = new LlmRejected("", reason);
                        rows.AppendLine($"отклонено\t{word}\t\t{reason}\t");
                    }
                }
                if (root.TryGetProperty("suggestions", out var suggestionsList) && suggestionsList.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pair in suggestionsList.EnumerateObject())
                    {
                        if (pair.Value.ValueKind != JsonValueKind.String) continue;
                        var suggestion = pair.Value.GetString() ?? "";
                        var item = batch.FirstOrDefault(candidate => candidate.Word == pair.Name);
                        var word = item?.Word ?? pair.Name;
                        // Принимаем ответ только если это то же слово с ровно одним знаком:
                        // иначе omogre выдал омограф целиком или соседнее слово.
                        if (!string.IsNullOrEmpty(suggestion) && IsValidCorrection(word, suggestion))
                        {
                            suggestions[word] = suggestion;
                            rejected.Remove(word);
                            answered++;
                            rows.AppendLine($"принято\t{word}\t{suggestion}\tomogre");
                        }
                        else
                        {
                            if (!rejected.ContainsKey(word))
                                rejected[word] = new LlmRejected(suggestion, "ответ не похож на исходное слово");
                            rows.AppendLine($"отклонено\t{word}\t\tответ не похож на исходное слово\t{suggestion}");
                        }
                    }
                }
                report($"omogre: {Math.Min(offset + batchSize, items.Count)} из {items.Count} слов…");
            }
        }
        catch (OperationCanceledException)
        {
            return LlmStressOutcome.Fail("Прогон omogre отменён.", items.Count);
        }
        catch (Exception error)
        {
            LogError("Прогон omogre", error);
            return LlmStressOutcome.Fail("Не удалось получить ответ omogre: " + error.Message, items.Count);
        }

        var outcome = new LlmStressOutcome(true, "", device, watch.Elapsed.TotalSeconds, answered, items.Count,
            suggestions, rejected);
        WriteLlmLog(items, outcome, rows, errors, engine);
        return outcome;
    }

    // Контекстный разбор по каждому вхождению: omogre отвечает не на слово целиком,
    // а на конкретный случай, поэтому омографы («за́мок»/«замо́к») раскладываются по
    // предложениям, а не сводятся к одному варианту большинством. Это и есть работа
    // «по контексту», ради которой движок и подключён: страница «Контроль ИИ
    // результатов» показывает случаи, разнесённые по двум вариантам ударения.
    internal static LlmCaseOutcome RunOmogreCaseCheck(IReadOnlyList<LlmCaseItem> items, string book,
        Action<string> report, CancellationToken token, LlmEngine? engine = null)
    {
        var chosen = engine ?? CurrentLlmEngine();
        if (items.Count == 0)
            return new LlmCaseOutcome(true, "Нет случаев для проверки.", "", 0, 0, 0,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlmRejected>(StringComparer.Ordinal));

        string python;
        try
        {
            python = EnsurePythonRuntime();
            var runtimeError = EnsureOmogreRuntimeOrError(python);
            if (runtimeError != null) return LlmCaseOutcome.Fail(runtimeError, items.Count);
        }
        catch (Exception error)
        {
            LogError("Подготовка omogre (случаи)", error);
            return LlmCaseOutcome.Fail("Не удалось подготовить omogre: " + error.Message, items.Count);
        }

        var watch = Stopwatch.StartNew();
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new Dictionary<string, LlmRejected>(StringComparer.Ordinal);
        var rows = new StringBuilder();
        // Контексты — целые фразы, поэтому пачка небольшая: модель читает каждую фразу целиком.
        const int batchSize = 100;
        var answered = 0;
        var device = "";
        var dataPath = Path.Combine(ModelCacheDirectory, "omogre");
        try
        {
            using var worker = new OmogreStressWorker(dataPath, LoadLlmDevice(), report);
            worker.EnsureStarted();
            device = worker.LoadedDevice;
            for (var offset = 0; offset < items.Count; offset += batchSize)
            {
                if (token.IsCancellationRequested) return LlmCaseOutcome.Fail("Прогон omogre отменён.", items.Count);
                var batch = new List<(int Id, LlmCaseItem Item)>();
                for (var index = offset; index < Math.Min(offset + batchSize, items.Count); index++) batch.Add((index, items[index]));
                var request = JsonSerializer.Serialize(new
                {
                    op = "cases",
                    items = batch.Select(pair => new
                    {
                        // id обязателен: омонимы соседних предложений нумеруются независимо,
                        // и пары «слово + вхождение» для разных контекстов совпадают.
                        id = pair.Id,
                        word = pair.Item.Word,
                        occurrence = pair.Item.Occurrence,
                        context = pair.Item.Context
                    }).ToArray()
                });
                var response = worker.Send(request, token);
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                    return LlmCaseOutcome.Fail(errorElement.GetString() ?? "Ошибка omogre", items.Count);

                if (root.TryGetProperty("cases", out var caseList) && caseList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in caseList.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) continue;
                        var id = idElement.GetInt32();
                        if (id < 0 || id >= items.Count) continue;
                        var item = items[id];
                        var key = LlmCaseKey(item.Word, item.Context, item.Occurrence);
                        var suggestion = entry.TryGetProperty("form", out var formElement) && formElement.ValueKind == JsonValueKind.String
                            ? formElement.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(suggestion) && IsValidCorrection(item.Word, suggestion))
                        {
                            answers[key] = suggestion;
                            rejected.Remove(key);
                            answered++;
                            rows.AppendLine($"принято\t{item.Word}\t{suggestion}\tomogre");
                        }
                        else if (!rejected.ContainsKey(key))
                        {
                            rejected[key] = new LlmRejected(suggestion, "ответ не похож на исходное слово");
                            rows.AppendLine($"отклонено\t{item.Word}\t\tответ не похож на исходное слово\t{suggestion}");
                        }
                    }
                }
                if (root.TryGetProperty("rejected", out var rejectedList) && rejectedList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in rejectedList.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) continue;
                        var id = idElement.GetInt32();
                        if (id < 0 || id >= items.Count) continue;
                        var item = items[id];
                        var key = LlmCaseKey(item.Word, item.Context, item.Occurrence);
                        if (answers.ContainsKey(key) || rejected.ContainsKey(key)) continue;
                        var reason = entry.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                            ? reasonElement.GetString() ?? "нет ответа" : "нет ответа";
                        rejected[key] = new LlmRejected("", reason);
                        rows.AppendLine($"отклонено\t{item.Word}\t\t{reason}\t");
                    }
                }
                report($"omogre: разобрано случаев {Math.Min(offset + batchSize, items.Count)} из {items.Count}…");
            }
        }
        catch (OperationCanceledException)
        {
            return LlmCaseOutcome.Fail("Прогон omogre отменён.", items.Count);
        }
        catch (Exception error)
        {
            LogError("Прогон omogre по случаям", error);
            return LlmCaseOutcome.Fail("Не удалось получить ответ omogre: " + error.Message, items.Count);
        }

        var outcome = new LlmCaseOutcome(true, "", device, watch.Elapsed.TotalSeconds, answered, items.Count, answers, rejected);
        LogReview($"omogre (по случаям): ответов {answered} из {items.Count}; движок {chosen.Title}; устройство {device}; " +
            $"{watch.Elapsed.TotalSeconds:0} с; книга {Path.GetFileName(book)}");
        try
        {
            File.AppendAllText(LlmLogPath,
                $"omogre (по случаям) {DateTime.Now:dd.MM.yyyy HH:mm}{Environment.NewLine}{rows}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception error) { LogError("Журнал ИИ (случаи omogre)", error); }
        return outcome;
    }

    private static Dictionary<string, string> RunNatashaAnchor(IReadOnlyList<string> words, string dictionaryPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (words.Count == 0) return result;
        var python = EnsurePythonRuntime();
        var scriptPath = ExtractEmbeddedFile("DSR.natasha_stress_worker.py", "natasha_stress_worker.py");

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{scriptPath}\" --dictionary \"{dictionaryPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        // Wait for ready line.
        var readyLine = process.StandardOutput.ReadLine();
        // Send request.
        var request = JsonSerializer.Serialize(new { op = "words", items = words.Select(w => new { word = w }).ToArray() });
        process.StandardInput.WriteLine(request);
        process.StandardInput.Flush();
        // Read response.
        var responseLine = process.StandardOutput.ReadLine();
        process.StandardInput.Close();
        process.WaitForExit(30000);
        if (!string.IsNullOrWhiteSpace(responseLine))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseLine);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        var word = item.TryGetProperty("word", out var w) ? w.GetString() : null;
                        var accented = item.TryGetProperty("accented", out var a) ? a.GetString() : null;
                        if (!string.IsNullOrEmpty(word) && !string.IsNullOrEmpty(accented)) result[word] = accented;
                    }
                }
            }
            catch { }
        }
        try { if (!process.HasExited) process.Kill(); } catch { }
        return result;
    }

    internal static void ConvertWithStressAnchorInteractive(string input, string output, string device,
        IProgress<(int Completed, int Total, string Message)> progress, ConversionControl control)
    {
        // Использует add-stress-to-epub (spaCy + Wiktionary SQLite) как движок расстановки ударений.
        // Текст извлекается и записывается DSR-парсером FB2, а ударения ставит Python-воркер.
        var sourceBytes = File.ReadAllBytes(input);
        var extension = Path.GetExtension(input);
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Поддерживаются только FB2 и TXT.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var temporary = output + ".anchor.tmp";
        try
        {
            control.Checkpoint();
            progress.Report((0, 0, "Подготовка add-stress-to-epub…"));
            // PYTHON оставляем для опытных пользователей: они сами следят за пакетами.
            // Без него берём локальное окружение и при первом запуске ставим spaCy.
            var python = Environment.GetEnvironmentVariable("PYTHON");
            if (string.IsNullOrWhiteSpace(python))
            {
                var previousProgress = SetupProgress;
                SetupProgress = message => progress.Report((0, 0, message));
                try
                {
                    python = EnsurePythonRuntime();
                    var runtimeError = EnsureAddStressRuntimeOrError(python);
                    if (runtimeError != null) throw new InvalidOperationException(runtimeError);
                }
                finally { SetupProgress = previousProgress; }
            }
            // Always use the worker embedded in this build so a stale copy next to
            // the exe can never desynchronise the protocol.
            var scriptPath = ExtractEmbeddedFile("DSR.add_stress_anchor.py", "add_stress_anchor.py");
            var baseDir = EnsureAddStressEngine();
            if (!File.Exists(Path.Combine(baseDir, "russian_dict.db")))
                progress.Report((0, 0, "Первый запуск add-stress: скачиваю словарь Wiktionary (около 1,3 ГБ)…"));
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{scriptPath}\" --base-dir \"{baseDir}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            using var process = new Process { StartInfo = startInfo };

            // The worker (and the spaCy/dictionary libraries it loads) can be very
            // chatty on stderr. Redirection without draining fills the OS pipe
            // buffer, which blocks the worker and then blocks our next stdin write —
            // an infinite, CPU-free hang. Keep a bounded tail for diagnostics while
            // always emptying the pipe.
            var stderrTail = new StringBuilder();
            var stderrGate = new object();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderrGate) { if (stderrTail.Length < 8000) stderrTail.AppendLine(e.Data); }
            };
            try
            {
                process.Start();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "Не удалось запустить Python для add-stress (команда \"" + python + "\"). "
                    + "Нажмите «Загрузить ресурсы заново», чтобы восстановить локальное окружение. "
                    + "Если задана переменная PYTHON, проверьте путь к python.exe.", error);
            }
            process.BeginErrorReadLine();

            // Drain stdout on a background thread as well: the main thread consumes
            // complete protocol lines from a queue, so a busy pipe can never wedge us.
            var stdoutLines = new BlockingCollection<string>(new ConcurrentQueue<string>());
            var stdoutReader = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) != null) stdoutLines.Add(line);
                }
                catch { }
                finally { try { stdoutLines.CompleteAdding(); } catch { } }
            })
            { IsBackground = true, Name = "dsr-stress-anchor-stdout" };
            stdoutReader.Start();

            string Diagnostics()
            {
                lock (stderrGate) return stderrTail.ToString();
            }

            string? ReadWorkerLine(TimeSpan timeout) => stdoutLines.TryTake(out var line, timeout) ? line : null;

            string SendWorkerText(string text, TimeSpan timeout, string kind, int index, int total)
            {
                if (control.IsStopped)
                {
                    try { process.Kill(); } catch { }
                    throw new OperationCanceledException("Обработка остановлена пользователем.");
                }
                var request = JsonSerializer.Serialize(new { op = "text", text });
                try
                {
                    process.StandardInput.WriteLine(request);
                    process.StandardInput.Flush();
                }
                catch (Exception error)
                {
                    try { process.Kill(); } catch { }
                    throw new InvalidOperationException("add-stress-to-epub: канал ввода закрыт. " + Diagnostics(), error);
                }
                var responseLine = ReadWorkerLine(timeout);
                if (responseLine == null)
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException($"add-stress-to-epub завис на {kind} {index}/{total}. " + Diagnostics());
                }
                return responseLine;
            }

            // Первый запуск может скачивать словарь Wiktionary на 1,3 ГБ, поэтому
            // пяти минут мало: ждём до получаса, но не бесконечно.
            var ready = ReadWorkerLine(TimeSpan.FromMinutes(30));
            if (ready == null)
                throw new InvalidOperationException("add-stress-to-epub не ответил при инициализации. " + Diagnostics());
            if (!string.IsNullOrWhiteSpace(ready))
            {
                try
                {
                    using var readyDoc = JsonDocument.Parse(ready);
                    if (readyDoc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                    {
                        var err = errEl.GetString();
                        if (!string.IsNullOrWhiteSpace(err))
                        {
                            try { process.Kill(); } catch { }
                            throw new InvalidOperationException("add-stress-to-epub не инициализирован: " + err);
                        }
                    }
                }
                catch (JsonException) { }
            }
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                var text = DecodeText(sourceBytes);
                var lines = text.Split('\n');
                var outputLines = new string[lines.Length];
                for (var i = 0; i < lines.Length; i++)
                {
                    control.Checkpoint();
                    var original = lines[i];
                    var ending = original.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
                    var content = ending.Length == 0 ? original : original[..^1];
                    if (string.IsNullOrWhiteSpace(content)) { outputLines[i] = original; continue; }
                    var responseLine = SendWorkerText(content, TimeSpan.FromSeconds(120), "строке", i + 1, lines.Length);
                    string converted = content;
                    if (!string.IsNullOrWhiteSpace(responseLine))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(responseLine);
                            if (doc.RootElement.TryGetProperty("text", out var t)) converted = t.GetString() ?? content;
                        }
                        catch { }
                    }
                    outputLines[i] = converted + ending;
                    progress.Report((i + 1, lines.Length, $"add-stress-to-epub: {i + 1}/{lines.Length}"));
                }
                File.WriteAllBytes(temporary, EncodeText(string.Join("\n", outputLines), sourceBytes));
            }
            else
            {
                _ = new Fb2Parser().Parse(sourceBytes);
                var document = XDocument.Parse(DecodeXml(sourceBytes), LoadOptions.PreserveWhitespace);
                var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
                var ns = root.Name.Namespace;
                var imageIds = root.Descendants().Where(e => e.Name.Namespace == ns && e.Name.LocalName == "image")
                    .Select(e => (string?)e.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
                    .Where(h => !string.IsNullOrWhiteSpace(h) && h!.StartsWith('#')).Select(h => h![1..]).ToHashSet(StringComparer.Ordinal);
                var binaryNodes = root.DescendantsAndSelf().Where(e => e.Name.Namespace == ns && e.Name.LocalName == "binary" && imageIds.Contains((string?)e.Attribute("id") ?? ""))
                    .SelectMany(e => e.DescendantNodes().OfType<XText>()).ToHashSet();
                var nodes = root.DescendantsAndSelf().Where(e => e.Name.Namespace == ns && TextElements.Contains(e.Name.LocalName))
                    .SelectMany(e => e.DescendantNodes().OfType<XText>()).Where(n => !binaryNodes.Contains(n)).Distinct().ToArray();
                var total = nodes.Length;
                for (var i = 0; i < nodes.Length; i++)
                {
                    control.Checkpoint();
                    var node = nodes[i];
                    if (string.IsNullOrWhiteSpace(node.Value)) continue;
                    var responseLine = SendWorkerText(node.Value, TimeSpan.FromSeconds(120), "фрагменте", i + 1, total);
                    if (!string.IsNullOrWhiteSpace(responseLine))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(responseLine);
                            if (doc.RootElement.TryGetProperty("text", out var t)) node.Value = t.GetString() ?? node.Value;
                        }
                        catch { }
                    }
                    if (i % 10 == 0 || i == total - 1)
                        progress.Report((i + 1, total, $"add-stress-to-epub: {i + 1}/{total}"));
                }
                if (document.Declaration != null) document.Declaration.Encoding = "utf-8";
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false), Indent = false,
                    OmitXmlDeclaration = document.Declaration == null, NewLineHandling = NewLineHandling.None
                })) document.Save(writer);
                _ = new Fb2Parser().Parse(File.ReadAllBytes(temporary));
            }
            try { process.StandardInput.Close(); } catch { }
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(); } catch { }
            }
            try { stdoutReader.Join(2000); } catch { }
            progress.Report((1, 1, "Готово: " + output));
            if (File.Exists(temporary)) File.Move(temporary, output, overwrite: true);
        }
        catch (Exception ex)
        {
            LogError("ConvertWithStressAnchorInteractive", ex);
            if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    internal static string LlmLogPath => Path.Combine(DataDirectory, "DSR-ии-проверка.log");

    // Слова, по которым модель уже не смогла дать ответ. Генерация идёт без случайности
    // (greedy) и повторный проход по одному слову даёт тот же результат, поэтому такие
    // слова больше не отправляются — иначе каждая проверка снова занимала бы минуты.
    internal static string LlmFailuresPath => Path.Combine(DataDirectory, "llm-без-ответа.json");

    internal sealed record LlmFailure(string Word, string Raw, string Reason);

    internal static Dictionary<string, LlmFailure> LoadLlmFailures()
    {
        var empty = new Dictionary<string, LlmFailure>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(LlmFailuresPath)) return empty;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LlmFailure>>(
                File.ReadAllText(LlmFailuresPath, Encoding.UTF8));
            if (loaded == null) return empty;
            var result = new Dictionary<string, LlmFailure>(StringComparer.Ordinal);
            foreach (var pair in loaded) result[MigrateLlmKey(pair.Key)] = pair.Value;
            return result;
        }
        catch (Exception error)
        {
            LogError("Чтение списка слов без ответа ИИ", error);
            return empty;
        }
    }

    internal static void SaveLlmFailures(Dictionary<string, LlmFailure> failures)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LlmFailuresPath,
                JsonSerializer.Serialize(failures, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись списка слов без ответа ИИ", error);
        }
    }

    // Подробный журнал последнего ИИ-прохода: что спросили, что ответила модель, что принято.
    // Нужен, когда модель ответила, но подсказок не появилось.
    private static void WriteLlmLog(IReadOnlyList<LlmStressItem> items, LlmStressOutcome outcome,
        StringBuilder rows, StringBuilder errors, LlmEngine engine)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var logged = new HashSet<string>(
                rows.ToString().Split('\n').Select(line => line.Split('\t')).Where(parts => parts.Length > 1)
                    .Select(parts => parts[1]), StringComparer.Ordinal);
            var text = new StringBuilder();
            text.AppendLine("ИИ-проверка ударений, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("Движок: " + engine.Title + (engine.IsLocal && !engine.IsSilero ? " (" + LlmModelDirectory(engine.LocalModel) + ")" : ""));
            text.AppendLine($"Отправлено слов: {items.Count}; принято: {outcome.Answered}; отклонено: {outcome.Rejected.Count}.");
            text.AppendLine($"Вычисления: {outcome.Device}; время: {outcome.Seconds:0} с.");
            text.AppendLine("Строки: итог, слово, что ответила модель, причина.");
            text.Append(rows);
            foreach (var item in items)
            {
                if (logged.Contains(item.Word)) continue;
                text.AppendLine($"без ответа\t{item.Word}\t\tнет строки в ответе модели");
            }
            var errorText = errors.ToString().Trim();
            if (errorText.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Вывод воркера:");
                foreach (var line in errorText.Split('\n').TakeLast(20)) text.AppendLine(line.TrimEnd());
            }
            File.WriteAllText(LlmLogPath, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись журнала ИИ-проверки", error);
        }
    }

    // Разбор расхождений по каждому вхождению из командной строки: тот же путь, что и
    // страница «Контроль ИИ результатов», но без окна — удобно проверять движок на
    // реальном отчёте и видеть, как омографы разложились по вариантам ударения.
    private static int RunOmogreCasesCommand(string reviewPath, LlmEngine engine)
    {
        if (!File.Exists(reviewPath))
        {
            Console.Error.WriteLine("Файл отчёта не найден: " + reviewPath);
            return 1;
        }
        var entries = ReadWordReview(reviewPath)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RuAccent) || !string.IsNullOrWhiteSpace(entry.Silero))
            .ToArray();
        var cases = entries.Select(entry =>
        {
            var split = SplitReviewContext(entry.Context);
            return new LlmCaseItem(entry.Word, split.Context, split.Occurrence);
        }).ToArray();
        Console.WriteLine($"Движок: {engine.Title}; случаев расхождений: {cases.Length}");
        var outcome = RunOmogreCaseCheck(cases, reviewPath, Console.WriteLine, CancellationToken.None, engine);
        if (!outcome.Ok)
        {
            Console.Error.WriteLine(outcome.Message);
            return 1;
        }
        var splits = 0;
        foreach (var group in cases.Select((item, index) => (item, entry: entries[index]))
                     .GroupBy(pair => ReviewWordKey(pair.entry.Word), StringComparer.Ordinal)
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var forms = new Dictionary<string, int>(StringComparer.Ordinal);
            var missing = 0;
            foreach (var pair in group)
            {
                if (outcome.Cases.TryGetValue(LlmCaseKey(pair.item.Word, pair.item.Context, pair.item.Occurrence), out var form))
                    forms[form] = forms.TryGetValue(form, out var count) ? count + 1 : 1;
                else missing++;
            }
            if (forms.Count > 1) splits++;
            var layout = string.Join("  |  ", forms.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} ×{pair.Value}"));
            Console.WriteLine($"{group.First().entry.Word} — случаев {group.Count()}, ответов {group.Count() - missing}" +
                (missing > 0 ? $", без ответа {missing}" : "") + (layout.Length > 0 ? ":  " + layout : ":  нет ответов"));
        }
        Console.WriteLine($"Слов с расхождением вариантов ИИ: {splits}; ответов {outcome.Answered} из {outcome.Items}; " +
            $"устройство {outcome.Device}; {outcome.Seconds:0} с.");
        return 0;
    }

    // Режим командной строки: разбор энклиноменов по готовому отчёту
    // («на́ пол» вместо «на полу́»). Только чтение: отчёт не меняется.
    private static int RunEnclinomenaCommand(string reviewPath, string? outputPath)
    {
        if (!File.Exists(reviewPath))
        {
            Console.Error.WriteLine("Файл отчёта не найден: " + reviewPath);
            return 1;
        }
        var entries = ReadWordReview(reviewPath);
        var text = new StringBuilder();
        text.AppendLine("DSR — разбор энклиноменов (ударение уходит на предлог)");
        text.AppendLine($"Отчёт: {Path.GetFullPath(reviewPath)}");
        text.AppendLine($"Форм в списке энклиноменов: {EnclinomenaForms.Value.Count:N0}");
        text.AppendLine($"Предлогов, уводящих ударение: {string.Join(", ", EnclinomenaPrepositions.OrderBy(item => item, StringComparer.Ordinal))}");
        text.AppendLine($"Слов в отчёте: {entries.Count:N0}");
        var accepted = new List<(WordReviewEntry Entry, EnclinomenMatch Match)>();
        var doubtful = new List<(WordReviewEntry Entry, EnclinomenMatch Match)>();
        var blocked = new List<(WordReviewEntry Entry, EnclinomenMatch Match)>();
        foreach (var entry in entries)
        {
            var split = SplitReviewContext(entry.Context);
            var match = AnalyzeEnclinomen(split.Context, entry.Word, split.Occurrence);
            if (match == null) continue;
            if (match.BlockReason.Length > 0) blocked.Add((entry, match));
            else if (match.Doubtful) doubtful.Add((entry, match));
            else accepted.Add((entry, match));
        }
        text.AppendLine();
        text.AppendLine($"Сочетаний «предлог + слово» найдено: {accepted.Count + doubtful.Count + blocked.Count:N0}");
        text.AppendLine($"  принято уверенно: {accepted.Count:N0}");
        text.AppendLine($"  принято к проверке (после слова слово, которое может быть зависимым): {doubtful.Count:N0}");
        text.AppendLine($"  отклонено: {blocked.Count:N0}");
        text.AppendLine();
        foreach (var (title, list) in new[]
        {
            ("ПРИНЯТО УВЕРЕННО", accepted),
            ("ПРИНЯТО К ПРОВЕРКЕ", doubtful),
            ("ОТКЛОНЕНО (за словом идёт зависимое)", blocked),
        })
        {
            text.AppendLine($"=== {title}: {list.Count:N0} ===");
            foreach (var group in list.GroupBy(item => item.Match.PrepositionStressed + " " + item.Entry.Word,
                         StringComparer.Ordinal).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"{group.Key} — {group.Count()} раз ({group.First().Match.Lemma}, {group.First().Match.Case})");
                foreach (var (entry, match) in group.Take(3))
                    text.AppendLine($"    за словом: {(match.Follower.Length == 0 ? "—" : match.Follower)}{Environment.NewLine}    {SplitReviewContext(entry.Context).Context}");
            }
            text.AppendLine();
        }
        var result = text.ToString();
        Console.Write(result);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(outputPath, result, new UTF8Encoding(true));
            Console.WriteLine("Записано: " + Path.GetFullPath(outputPath));
        }
        return 0;
    }

    // Режим командной строки: прогон ИИ-подсказок по готовому отчёту «…_проверка-слов.txt»
    // без запуска интерфейса. Удобен для длинных книг и для проверки самого механизма.
    // Движок берётся из настроек, если не указан явно ключами --llm-provider/--llm-model/--llm-url.
    private static int RunLlmStressCommand(string reviewPath, LlmEngine engine)
    {
        if (!File.Exists(reviewPath))
        {
            Console.Error.WriteLine("Файл отчёта не найден: " + reviewPath);
            return 1;
        }

        Console.WriteLine("Движок: " + engine.Title);

        List<WordReviewEntry> entries;
        try
        {
            entries = ReadWordReview(reviewPath);
        }
        catch (Exception error)
        {
            LogError("Чтение отчёта для ИИ-проверки", error);
            Console.Error.WriteLine("Не удалось прочитать отчёт: " + error.Message);
            return 1;
        }

        if (entries.Count == 0)
        {
            Console.WriteLine("В отчёте нет слов для проверки.");
            return 0;
        }

        var groups = entries
            .GroupBy(entry => entry.Word.Normalize(NormalizationForm.FormC).ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        var items = groups
            .Select(group => new LlmStressItem(group.First().Word,
                group.Select(entry => entry.Context).Where(context => !string.IsNullOrWhiteSpace(context))
                    .Distinct().Take(2).ToArray()))
            .ToList();

        var settings = LoadLlmSettings();
        var failures = LoadLlmFailures();
        var cached = LoadLlmSuggestions();
        var pending = items.Where(item =>
        {
            var key = LlmCacheKey(engine.Id, reviewPath, item.Word);
            if (failures.ContainsKey(key)) return false;
            // Свой прежний ответ повторно не спрашиваем: ответ того же движка не изменится.
            if (cached.ContainsKey(key)) return false;
            // Чужой ответ принимаем, пока включена галочка «не переспрашивать».
            return !settings.ReuseOtherEngines || FindAnyLlmSuggestion(cached, reviewPath, item.Word) == null;
        }).ToList();
        if (pending.Count < items.Count)
            Console.WriteLine($"Уже проверялись ранее (повторно не отправляются): {items.Count - pending.Count}.");

        Console.WriteLine($"Слов для ИИ-проверки: {pending.Count} (групп слов {groups.Count}).");
        var outcome = RunLlmStressCheck(pending, reviewPath, message => Console.WriteLine(message),
            CancellationToken.None, engine);
        if (!outcome.Ok)
        {
            Console.Error.WriteLine(outcome.Message);
            return 1;
        }

        Console.WriteLine($"Подсказок принято: {outcome.Answered} из {outcome.Items}; отклонено: {outcome.Rejected.Count}; движок: {outcome.Device}; {outcome.Seconds:0} с.");
        foreach (var item in items)
        {
            var itemKey = LlmCacheKey(engine.Id, reviewPath, item.Word);
            if (outcome.Suggestions.TryGetValue(item.Word, out var suggestion))
            {
                Console.WriteLine($"  {item.Word} → {suggestion}");
                continue;
            }
            if (cached.TryGetValue(itemKey, out var knownGood))
            {
                Console.WriteLine($"  {item.Word} → {knownGood} (из кэша)");
                continue;
            }
            if (outcome.Rejected.TryGetValue(item.Word, out var rejected) && !string.IsNullOrEmpty(rejected.Raw))
            {
                Console.WriteLine($"  {item.Word} → модель ответила «{rejected.Raw}» ({rejected.Reason})");
                continue;
            }
            if (failures.TryGetValue(itemKey, out var known) && !string.IsNullOrEmpty(known.Raw))
            {
                Console.WriteLine($"  {item.Word} → модель отвечала «{known.Raw}» ({known.Reason})");
                continue;
            }
            Console.WriteLine($"  {item.Word} → (нет ответа)");
        }

        foreach (var pair in outcome.Suggestions) cached[LlmCacheKey(engine.Id, reviewPath, pair.Key)] = pair.Value;
        SaveLlmSuggestions(cached);
        foreach (var pair in outcome.Rejected)
            failures[LlmCacheKey(engine.Id, reviewPath, pair.Key)] = new LlmFailure(pair.Key, pair.Value.Raw, pair.Value.Reason);
        SaveLlmFailures(failures);
        Console.WriteLine("Подсказки сохранены в " + LlmSuggestionsPath);
        Console.WriteLine("Подробности по каждому слову: " + LlmLogPath);
        return 0;
    }

    // Тест движка на контрольных словах: скорость и число верных ударений.
    internal static LlmBenchmarkResult RunLlmBenchmark(LlmEngine engine, Action<string> report, CancellationToken token)
    {
        var items = LlmBenchmarkWords
            .Select(pair => new LlmStressItem(pair.Word, new[] { pair.Context }))
            .ToList();
        var outcome = RunLlmStressCheck(items, "контрольные-слова", report, token, engine, benchmark: true);
        if (!outcome.Ok)
            return new LlmBenchmarkResult(false, outcome.Message, engine.Id, engine.Title, 0, items.Count, 0,
                Array.Empty<string>());

        var correct = 0;
        var details = new List<string>();
        foreach (var (word, stress, _) in LlmBenchmarkWords)
        {
            var expected = StressMarked(word, stress);
            var got = outcome.Suggestions.TryGetValue(word, out var value) ? value : null;
            if (string.Equals(got, expected, StringComparison.Ordinal))
            {
                correct++;
                details.Add($"верно\t{word}\t{expected}");
            }
            else
            {
                details.Add($"неверно\t{word}\t{expected}\t{got ?? "(нет ответа)"}");
            }
        }
        return new LlmBenchmarkResult(true, "", engine.Id, engine.Title, correct, items.Count, outcome.Seconds, details);
    }

    internal sealed record LlmBenchmarkResult(bool Ok, string Message, string EngineId, string Title,
        int Correct, int Total, double Seconds, IReadOnlyList<string> Details);

    // Рекомендация по движкам: сначала больше верных ударений, затем меньше времени.
    internal static string? LlmBenchmarkAdvice()
    {
        var benchmarks = LoadLlmSettings().Benchmarks;
        if (benchmarks == null || benchmarks.Count == 0) return null;
        var best = benchmarks.Values
            .OrderByDescending(item => item.Total == 0 ? 0 : (double)item.Correct / item.Total)
            .ThenBy(item => item.Seconds)
            .First();
        return $"Лучший результат: {best.Title} — {best.Correct} из {best.Total} верных ударений, " +
               $"{best.Seconds:0} с ({best.Seconds / Math.Max(1, best.Total):0.0} с на слово).";
    }

    private static int RunLlmBenchmarkCommand(LlmEngine engine)
    {
        Console.WriteLine("Движок: " + engine.Title);
        var result = RunLlmBenchmark(engine, message => Console.WriteLine(message), CancellationToken.None);
        if (!result.Ok)
        {
            Console.Error.WriteLine(result.Message);
            return 1;
        }
        Console.WriteLine($"Верных ударений: {result.Correct} из {result.Total}; время: {result.Seconds:0} с " +
                          $"({result.Seconds / Math.Max(1, result.Total):0.0} с на слово).");
        foreach (var line in result.Details) Console.WriteLine("  " + line.Replace('\t', ' '));
        SaveLlmBenchmark(result);
        var advice = LlmBenchmarkAdvice();
        if (advice != null) Console.WriteLine(advice);
        return 0;
    }

    // Список моделей у внешнего сервиса: Ollama отдаёт его на /api/tags, OpenAI-совместимые — на /models.
    internal static List<string> ListRemoteLlmModels(LlmEngine engine, out string error)
    {
        error = "";
        var models = new List<string>();
        string script;
        string python;
        try
        {
            script = ExtractEmbeddedFile("DSR.llm_stress_worker.py", "llm_stress_worker.py");
            python = EnsurePythonRuntime();
        }
        catch (Exception problem)
        {
            error = problem.Message;
            return models;
        }

        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.Environment["PYTHONUTF8"] = "1";
        foreach (var argument in new[]
                 {
                     "-u", script, "--list-models", "--provider", engine.Provider,
                     "--base-url", engine.BaseUrl, "--remote-model", engine.RemoteModel,
                 })
            start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(engine.ApiKey)) start.Environment["DSR_LLM_API_KEY"] = engine.ApiKey;

        try
        {
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить список моделей.");
            var output = process.StandardOutput.ReadToEnd();
            var errorText = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(true); } catch { /* процесс уже завершился */ }
                error = "Сервис не ответил за 30 с. Проверьте адрес: " + engine.BaseUrl;
                return models;
            }
            foreach (var line in output.Split('\n'))
            {
                var data = TrimProtocolLine(line.TrimEnd('\r'));
                if (data.StartsWith("DSR_LLM:available:", StringComparison.Ordinal))
                    models.Add(data["DSR_LLM:available:".Length..]);
                else if (data.StartsWith("DSR_LLM:error:", StringComparison.Ordinal))
                    error = data["DSR_LLM:error:".Length..];
            }
            if (models.Count == 0 && error.Length == 0)
                error = errorText.Trim().Length > 0
                    ? errorText.Trim().Split('\n')[0]
                    : "Сервис не сообщил ни одной модели.";
        }
        catch (Exception problem)
        {
            LogError("Список моделей внешнего сервиса", problem);
            error = problem.Message;
        }
        return models;
    }

    // Ключи выбора движка для командной строки: --llm-provider local|ollama|openai|silero|omogre,
    // --llm-model <имя|путь>, --llm-url <адрес>, --llm-key <ключ>.
    private static LlmEngine ParseLlmEngineArgs(string[] args, int start)
    {
        string? provider = null;
        string? model = null;
        string? url = null;
        string? key = null;
        for (var index = start; index + 1 < args.Length; index++)
            switch (args[index])
            {
                case "--llm-provider": provider = args[index + 1].Trim().ToLowerInvariant(); break;
                case "--llm-model": model = args[index + 1]; break;
                case "--llm-url": url = args[index + 1]; break;
                case "--llm-key": key = args[index + 1]; break;
            }

        var settings = LoadLlmSettings();
        var chosen = provider ?? (settings.Provider ?? LlmEngine.Local).ToLowerInvariant();
        if (chosen == LlmEngine.Silero) return new LlmEngine(LlmEngine.Silero, "", "", "", "");
        if (chosen == LlmEngine.Omogre) return new LlmEngine(LlmEngine.Omogre, "", "", "", "");
        if (chosen is not (LlmEngine.Local or LlmEngine.Ollama or LlmEngine.OpenAi))
            throw new ArgumentException("--llm-provider принимает local, ollama, openai, silero или omogre.");
        var local = CurrentLlmModel();
        if (chosen == LlmEngine.Local) return new LlmEngine(chosen, model ?? local, "", "", "");
        return new LlmEngine(
            chosen,
            local,
            model ?? (chosen == LlmEngine.Ollama ? settings.OllamaModel : settings.ApiModel) ?? "",
            url ?? (chosen == LlmEngine.Ollama ? settings.OllamaUrl : settings.ApiUrl) ?? "",
            chosen == LlmEngine.OpenAi ? key ?? settings.ApiKey ?? Environment.GetEnvironmentVariable("DSR_LLM_API_KEY") ?? "" : "");
    }

    private static string TrimProtocolLine(string line)
    {
        var start = 0;
        while (start < line.Length && (line[start] == '\0' || line[start] == '\uFEFF' || line[start] == '\u200B'))
            start++;
        return start == 0 ? line : line[start..];
    }

    // Превращает технические строки воркера в короткие понятные сообщения о ходе работы.
    // Разбирает событие прохода по одному слову: DSR_LLM:ask:<проход>:<слово> и
    // DSR_LLM:answer:<проход>:<слово>:<ok|none>:<подсказка>. Тег отделяем по двоеточию,
    // а слово и подсказку — по следующим: ни в слове, ни в готовом написании двоеточий нет.
    private static bool TryParseLlmWordEvent(string payload, out LlmWordProgress progress)
    {
        progress = null!;
        if (payload.StartsWith("ask:", StringComparison.Ordinal))
        {
            var rest = payload[4..];
            var separator = rest.IndexOf(':');
            if (separator < 0) return false;
            progress = new LlmWordProgress(rest[(separator + 1)..], LlmWordPhase.Thinking, null);
            return true;
        }
        if (!payload.StartsWith("answer:", StringComparison.Ordinal)) return false;
        var afterTag = payload[7..];
        var first = afterTag.IndexOf(':');
        if (first < 0) return false;
        var afterLabel = afterTag[(first + 1)..];
        var second = afterLabel.IndexOf(':');
        if (second < 0) return false;
        var word = afterLabel[..second];
        var afterWord = afterLabel[(second + 1)..];
        var third = afterWord.IndexOf(':');
        var status = third < 0 ? afterWord : afterWord[..third];
        var suggestion = third < 0 ? string.Empty : afterWord[(third + 1)..];
        progress = new LlmWordProgress(word,
            string.Equals(status, "ok", StringComparison.Ordinal) ? LlmWordPhase.Answered : LlmWordPhase.NoAnswer,
            string.IsNullOrEmpty(suggestion) ? null : suggestion);
        return true;
    }

    private static IEnumerable<string> TranslateLlmProgress(string payload)
    {
        // Путь к модели содержит «:», поэтому тег отделяем только по первому двоеточию.
        var colon = payload.IndexOf(':');
        var tag = colon < 0 ? payload : payload[..colon];
        var rest = colon < 0 ? string.Empty : payload[(colon + 1)..];
        switch (tag)
        {
            case "model":
                yield return "ИИ: модель " + Path.GetFileName(rest);
                break;
            case "words":
                yield return $"ИИ: слов для проверки — {rest}";
                break;
            case "device":
                yield return $"ИИ: вычисления на {rest}";
                break;
            case "loaded":
                yield return $"ИИ: модель загружена за {rest} с";
                break;
            case "warning":
                yield return "ИИ: " + rest;
                break;
            case "batch_error":
                yield return "ИИ: ошибка запроса — " + rest;
                break;
            case "retry_start":
                yield return $"ИИ: уточняю {rest} слов по одному…";
                break;
            case "progress":
                // progress:<проход>:<готово>/<всего>:<секунды>
                var fields = rest.Split(':');
                if (fields.Length >= 3)
                {
                    var stage = fields[0] == "2" ? "уточнение" : "первый проход";
                    yield return $"ИИ ({stage}): {fields[1]}, {fields[2]}";
                }
                break;
            case "done":
                yield return "ИИ: подсказок получено " + rest;
                break;
            default:
                yield return "ИИ: " + payload;
                break;
        }
    }

    private static string EnsureDialogueInitialWordAccent(string source, string modelOutput, RuAccentWorker accentizer)
    {
        var start = 0;
        while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
        if (start >= source.Length || source[start] is not ('—' or '–' or '―')) return modelOutput;
        start++;
        while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
        var end = start;
        // The source text may already contain a combining acute accent (\u0301),
        // so the scan must treat it as a word character too, otherwise the first
        // spoken word is truncated at the accent (e.g. «Воро́ны» -> «Воро»).
        while (end < source.Length && IsWordCharacter(source[end])) end++;
        if (end == start) return modelOutput;

        var originalWord = source[start..end];
        var expected = NormalizeWordLetters(originalWord);
        var outputStart = SkipDialoguePrefix(modelOutput, 0);
        var actualEnd = outputStart;
        while (actualEnd < modelOutput.Length && IsWordCharacter(modelOutput[actualEnd])) actualEnd++;
        var outputWord = modelOutput[outputStart..actualEnd];
        if (NormalizeWordLetters(outputWord).Equals(expected, StringComparison.OrdinalIgnoreCase))
            return modelOutput;

        // Retry the whole dialogue via the worker's lossless word-alignment fallback.
        // That fallback accentuates all words and never drops letters.
        var retriedOutput = MoveAccentAfterVowel(accentizer.Process(source));
        var retriedStart = SkipDialoguePrefix(retriedOutput, 0);
        var retriedEnd = retriedStart;
        while (retriedEnd < retriedOutput.Length && IsWordCharacter(retriedOutput[retriedEnd])) retriedEnd++;
        var retriedWord = retriedOutput[retriedStart..retriedEnd];
        if (NormalizeWordLetters(retriedWord).Equals(expected, StringComparison.OrdinalIgnoreCase))
            return retriedOutput;

        // Last resort: keep the first spoken word exactly as written in the source
        // so the conversion never loses letters because of a single word.
        if (actualEnd > outputStart)
            return modelOutput[..outputStart] + originalWord + modelOutput[actualEnd..];
        return modelOutput;
    }

    // The dialogue dash (—, –, ―) may be followed by the first spoken word.
    // Skip the leading whitespace and the dash to reach that word.
    private static int SkipDialoguePrefix(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start < text.Length && text[start] is '—' or '–' or '―')
        {
            start++;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        }
        return start;
    }

    // A word may contain the combining acute accent (\u0301) placed right before
    // the stressed vowel, so it must not stop the scan: it is a word character too.
    private static bool IsWordCharacter(char ch) =>
        char.IsLetter(ch) ||
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark;

    internal static string NormalizeWordLettersForReview(string word) => NormalizeWordLetters(word);

    // Applies the ё and function-word stress protections to an accented text run.
    // Works token by token so only whole words are matched; the combining acute
    // mark (U+0301) is dropped when it touches ё or when the word is a short
    // preposition/conjunction/particle that must stay unstressed.
    internal static string ProtectUnstressedWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return WordTokens.Replace(text, match => ProtectWord(match.Value));
    }

    private static string ProtectWord(string word)
    {
        if (word.IndexOf('\u0301') < 0) return word;
        // 1) A combining accent may never sit next to ё — ё is always stressed.
        var builder = new StringBuilder(word.Length);
        for (var i = 0; i < word.Length; i++)
        {
            var ch = word[i];
            if (ch == '\u0301')
            {
                var prev = builder.Length > 0 ? builder[builder.Length - 1] : '\0';
                var next = i + 1 < word.Length ? word[i + 1] : '\0';
                if (char.ToLowerInvariant(prev) == 'ё' || char.ToLowerInvariant(next) == 'ё')
                    continue; // drop the accent that touches ё
            }
            builder.Append(ch);
        }
        var cleaned = builder.ToString();
        // 2) A short function word must stay unstressed.
        if (UnstressedWords.Contains(NormalizeWordLetters(cleaned)))
            cleaned = cleaned.Replace("\u0301", "");
        return cleaned;
    }

    // Compare words ignoring the accent marks and the ё/е distinction.
    private static string NormalizeWordLetters(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var ch in word)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(ch is 'ё' or 'Ё' ? 'е' : ch);
        }
        return builder.ToString();
    }

    // Shorten a text fragment for the error log so long paragraphs stay readable.
    private static string TruncateForLog(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 120 ? singleLine : singleLine[..120] + "…";
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(bytes); }
    }

    private static byte[] EncodeText(string text, byte[] original)
    {
        if (original.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        if (original.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
        if (original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        try
        {
            var encoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _ = encoding.GetBytes(text);
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException) { return new UTF8Encoding(false).GetBytes(text); }
    }

    private static string GetOutputPath(string input, Options options)
    {
        if (options.SingleOutputFile != null)
            return Path.GetFullPath(options.SingleOutputFile);
        var folder = options.OutputWasSpecified ? options.Output : Path.GetDirectoryName(Path.GetFullPath(input))!;
        if (options.OutputWasSpecified && options.Recursive && Directory.Exists(options.Input))
        {
            var relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(options.Input, input));
            if (!string.IsNullOrEmpty(relativeFolder) && relativeFolder != ".")
                folder = Path.Combine(folder, relativeFolder);
        }
        var name = Path.GetFileNameWithoutExtension(input) + options.Suffix + Path.GetExtension(input);
        var candidate = Path.Combine(folder, name);
        var source = Path.GetFullPath(input);
        if (string.Equals(Path.GetFullPath(candidate), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходного файла совпадает с исходным. Укажите другой суффикс или папку результата.");
        return candidate;
    }

    private static void ConvertFile(string inputPath, string outputPath, RuAccentWorker accentizer,
        bool includeIllustrations = true, Action<int, int>? reportProgress = null,
        ConversionControl? control = null, ConversionDiagnostics? diagnostics = null, string? reviewPath = null,
        bool deaccentInput = false)
    {
        var input = File.ReadAllBytes(inputPath);

        // Задействуем именно FB2-парсер DeepReader, чтобы проверять тот же формат,
        // который впоследствии будет отображать читатель. Дерево XML ниже нужно
        // для точечного изменения текста с сохранением разметки и ресурсов FB2.
        _ = new Fb2Parser().Parse(input);

        var xml = DecodeXml(input);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
        var fictionBookNs = root.Name.Namespace;
        var changed = 0;
        var normalized = 0;
        var imageElements = root.Descendants()
            .Where(element => element.Name.Namespace == fictionBookNs && element.Name.LocalName == "image").ToArray();
        var embeddedImageIds = imageElements
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#'))
            .Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var embeddedText = root.DescendantsAndSelf()
            .Where(element => element.Name.Namespace == fictionBookNs && element.Name.LocalName == "binary"
                && embeddedImageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Where(node => !string.IsNullOrWhiteSpace(node.Value)).ToArray();
        var embeddedTextSet = embeddedText.ToHashSet();
        var eligibleText = root.DescendantsAndSelf()
            .Where(element => element.Name.Namespace == fictionBookNs && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Where(node => !embeddedTextSet.Contains(node))
            .Distinct()
            .ToArray();
        var processableText = eligibleText.Where(textNode => !string.IsNullOrWhiteSpace(textNode.Value)).ToArray();
        var completedText = 0;
        var conversionTimer = Stopwatch.StartNew();
        var byteCount = input.LongLength;
        var characterCount = processableText.Sum(node => (long)node.Value.Length);
        var wordReview = new List<string>();
        // Polysyllabic words the model left unmarked, the original text of every node
        // (kept so the review can be rebuilt after the dictionary pass), and which
        // nodes were accented successfully — failed nodes stay untouched and must not
        // receive a dictionary stress mark or reappear in the review.
        var residueWords = new HashSet<string>(StringComparer.Ordinal);
        var sourceTexts = processableText.Select(node => node.Value).ToArray();
        var reviewable = new bool[processableText.Length];
        if (diagnostics != null)
        {
            diagnostics.TotalUnits = processableText.Length;
            diagnostics.CharacterCount = characterCount;
            diagnostics.InputBytes = byteCount;
        }
        reportProgress?.Invoke(0, processableText.Length);

        // Binary image payloads are excluded from inference regardless of whether
        // they are preserved in the output. Keep their original XML nodes untouched.

        // Send independent text nodes to the worker in batches. The worker accents
        // them concurrently, hiding the per-node ONNX latency that left the CPU and
        // GPU idle in the previous one-request-at-a-time loop.
        const int inferenceBatchSize = 64;
        accentizer.EnsureStarted();
        for (var batchStart = 0; batchStart < processableText.Length; batchStart += inferenceBatchSize)
        {
            control?.Checkpoint();
            var batchCount = Math.Min(inferenceBatchSize, processableText.Length - batchStart);
            var batchInputs = new string[batchCount];
            for (var offset = 0; offset < batchCount; offset++)
            {
                var batchSource = processableText[batchStart + offset].Value;
                // Поэтапный режим: RUAccent, как и Silero, должен видеть текст без уже
                // расставленных акутов (буква ё сохраняется). Тогда условия у моделей
                // равные, а чужие ударения не «подсказывают» омографы.
                if (deaccentInput) batchSource = batchSource.Replace("\u0301", "", StringComparison.Ordinal);
                batchInputs[offset] = ApplyUserDictionary(batchSource);
            }
            var batchOutputs = accentizer.ProcessBatch(batchInputs);
            for (var offset = 0; offset < batchCount; offset++)
            {
                control?.Checkpoint();
                var textNode = processableText[batchStart + offset];
                var sourceText = sourceTexts[batchStart + offset];
                var inference = batchOutputs[offset];
                completedText++;
                if (inference.Error != null)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.FailedUnits++;
                        diagnostics.Events.Add($"Фрагмент {completedText}: {inference.Error}");
                    }
                    LogError($"Фрагмент оставлен без изменений: {TruncateForLog(sourceText)}", new InvalidOperationException(inference.Error));
                }
                else
                {
                    try
                    {
                        var modelOutput = MoveAccentAfterVowel(inference.Text ?? sourceText);
                        var modelProcessed = ProtectUnstressedWords(EnsureDialogueInitialWordAccent(sourceText, modelOutput, accentizer));
                        var modelProcessedWithPunctuation = EnsureOriginalPunctuation(sourceText, modelProcessed);
                        var accented = ApplyYoDictionary(modelProcessedWithPunctuation);
                        var reviewCountBefore = wordReview.Count;
                        foreach (var word in CollectUnrecognizedWords(sourceText, accented, wordReview))
                            residueWords.Add(DictionaryKey(word));
                        if (diagnostics != null) diagnostics.UnrecognizedWordCount += CountReviewWords(wordReview.Skip(reviewCountBefore));
                        reviewable[batchStart + offset] = true;
                        if (!string.Equals(accented, sourceText, StringComparison.Ordinal))
                        {
                            textNode.Value = accented;
                            changed++;
                        }
                        if (!string.Equals(accented, modelOutput, StringComparison.Ordinal))
                            normalized++;
                    }
                    catch (Exception ex)
                    {
                        if (diagnostics != null)
                        {
                            diagnostics.FailedUnits++;
                            diagnostics.Events.Add($"Фрагмент {completedText}: {ex}");
                        }
                        // Keep the original fragment untouched so a single problem fragment
                        // never aborts the whole conversion.
                        LogError($"Фрагмент оставлен без изменений: {TruncateForLog(sourceText)}", ex);
                    }
                }
                if (diagnostics != null) diagnostics.CompletedUnits = completedText;
                if (completedText == processableText.Length || completedText % 10 == 0)
                {
                    var remaining = completedText > 0
                        ? TimeSpan.FromSeconds(conversionTimer.Elapsed.TotalSeconds * (processableText.Length - completedText) / completedText)
                        : (TimeSpan?)null;
                    LogConversion(inputPath, byteCount, characterCount, conversionTimer.Elapsed, remaining, $"Обработка {completedText}/{processableText.Length}");
                    reportProgress?.Invoke(completedText, processableText.Length);
                }
            }
        }

        // The model leaves some polysyllabic words unmarked; RUAccent's bundled full
        // stress dictionary usually knows them. Resolve once for the whole file, add
        // the marks it knows, then rebuild the review so only truly unknown words
        // remain for the manual pass (see ConvertTextFile).
        var dictionaryStress = accentizer.ResolveWords(residueWords.ToArray());
        if (dictionaryStress.Count > 0)
        {
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    processableText[index].Value = ApplyDictionaryStress(processableText[index].Value, dictionaryStress);
            wordReview.Clear();
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    CollectUnrecognizedWords(sourceTexts[index], processableText[index].Value, wordReview);
            changed = 0;
            for (var index = 0; index < processableText.Length; index++)
                if (!string.Equals(processableText[index].Value, sourceTexts[index], StringComparison.Ordinal)) changed++;
            if (diagnostics != null) diagnostics.DictionaryStressCount = dictionaryStress.Count;
        }
        // Словарь Зарвы: дополнительный проход по словам, оставшимся без ударения.
        var supplementaryStress2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var residueWord in residueWords)
        {
            if (dictionaryStress.ContainsKey(residueWord)) continue;
            if (SupplementaryStressDictionary.Value.TryGetValue(residueWord, out var supplementaryForm2))
                supplementaryStress2[residueWord] = supplementaryForm2;
        }
        if (supplementaryStress2.Count > 0)
        {
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    processableText[index].Value = ApplyDictionaryStress(processableText[index].Value, supplementaryStress2);
            wordReview.Clear();
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    CollectUnrecognizedWords(sourceTexts[index], processableText[index].Value, wordReview);
            changed = 0;
            for (var index = 0; index < processableText.Length; index++)
                if (!string.Equals(processableText[index].Value, sourceTexts[index], StringComparison.Ordinal)) changed++;
            if (diagnostics != null) diagnostics.DictionaryStressCount += supplementaryStress2.Count;
        }
        // Встроенный словарь литературных ударений: жёсткая фиксация ударений
        // для слов, оставшихся без отметки после прохода Silero.
        var builtInStress2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var residueWord in residueWords)
        {
            if (dictionaryStress.ContainsKey(residueWord) || supplementaryStress2.ContainsKey(residueWord)) continue;
            if (BuiltInStressDictionary.Value.TryGetValue(residueWord, out var builtInForm2))
                builtInStress2[residueWord] = builtInForm2;
        }
        if (builtInStress2.Count > 0)
        {
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    processableText[index].Value = ApplyDictionaryStress(processableText[index].Value, builtInStress2);
            wordReview.Clear();
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    CollectUnrecognizedWords(sourceTexts[index], processableText[index].Value, wordReview);
            changed = 0;
            for (var index = 0; index < processableText.Length; index++)
                if (!string.Equals(processableText[index].Value, sourceTexts[index], StringComparison.Ordinal)) changed++;
            if (diagnostics != null) diagnostics.DictionaryStressCount += builtInStress2.Count;
        }

        if (document.Declaration != null)
            document.Declaration.Encoding = "utf-8";

        control?.Checkpoint();
        var temporary = outputPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = document.Declaration == null,
                NewLineHandling = NewLineHandling.None
            }))
                document.Save(writer);

            // Проверяем, что результат по-прежнему читается парсером DeepReader.
            _ = new Fb2Parser().Parse(File.ReadAllBytes(temporary));
            control?.Checkpoint();
            File.Move(temporary, outputPath, overwrite: true);
            if (diagnostics != null)
            {
                diagnostics.OutputBytes = new FileInfo(outputPath).Length;
                diagnostics.ImagePayloadCharacterCount = embeddedText.Sum(node => (long)node.Value.Length);
                conversionTimer.Stop();
                diagnostics.Elapsed = conversionTimer.Elapsed;
            }
            WriteWordReview(outputPath, wordReview, reviewPath);
            conversionTimer.Stop();
            LogConversion(inputPath, byteCount, characterCount, conversionTimer.Elapsed, TimeSpan.Zero, "Завершено");
            Console.WriteLine($"  Узлов текста с изменениями: {changed}; исправлено пробелов на границах: {normalized}; подставлено из словаря: {dictionaryStress.Count}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string DecodeXml(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        var declaration = System.Text.RegularExpressions.Regex.Match(prefix, "<\\?xml[^>]*encoding\\s*=\\s*[\"'](?<enc>[^\"']+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) encoding = Encoding.UTF8;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) encoding = Encoding.Unicode;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) encoding = Encoding.BigEndianUnicode;
        else if (declaration.Success) encoding = Encoding.GetEncoding(declaration.Groups["enc"].Value);
        else encoding = new UTF8Encoding(false, true);

        var preamble = encoding.GetPreamble();
        var offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset).TrimStart('\uFEFF');
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Конвертер FB2/TXT: расстановка ударений через ruaccent; FB2 проверяется парсером DeepReader.");
        Console.WriteLine("Использование:");
        Console.WriteLine("  DSR.exe <файл.fb2 | файл.txt | папка> [-o <папка | файл.fb2 | файл.txt>] [--recursive]");
        Console.WriteLine("      [--suffix _ударения] [--model big_poetry] [--no-dictionary] [--device CPU|CUDA]\n");
        Console.WriteLine("По умолчанию создаётся копия рядом с источником с суффиксом _ударения.");
        Console.WriteLine("При передаче папки в -o файлы помещаются в неё; структура подпапок при --recursive сохраняется.");
        Console.WriteLine("\nПроверка остатка слов моделью ИИ (без интерфейса):");
        Console.WriteLine("  DSR.exe --llm-stress \"<книга>_ударения_проверка-слов.txt\" [--llm-provider local|ollama|openai|silero|omogre]");
        Console.WriteLine("  DSR.exe --omogre-cases \"<книга>_ударения_проверка-слов.txt\"   (разбор расхождений по каждому вхождению)");
        Console.WriteLine("      [--llm-model <модель>] [--llm-url <адрес>] [--llm-key <ключ>]");
        Console.WriteLine("      Подбирает ударения для слов, которых нет в словаре, и сохраняет подсказки");
        Console.WriteLine("      в кэш. Ничего не применяется автоматически. Слова, по которым ответа не");
        Console.WriteLine("      было, повторно не отправляются (список: ресурсы\\llm-без-ответа.json).");
        Console.WriteLine("      --llm-provider local — своя модель из ресурсы\\модели\\llm (по умолчанию),");
        Console.WriteLine("      ollama — свой сервер Ollama, openai — внешний сервис с OpenAI-совместимым API,");
        Console.WriteLine("      silero — локальный движок silero-stress (устанавливает PyTorch CPU при первом запуске).");
        Console.WriteLine("      omogre — локальная контекстная модель омографов (лучший выбор для спорных случаев):");
        Console.WriteLine("      ставит библиотеку и PyTorch CPU при первом запуске, модель ~120 МБ скачивает сама.");
        Console.WriteLine("      --llm-model: для local имя папки или полный путь, для сервисов — имя модели.");
        Console.WriteLine("      Ключ внешнего сервиса можно задать переменной окружения DSR_LLM_API_KEY.\n");
        Console.WriteLine("Самостоятельная обработка книги через Silero (первый запуск устанавливает silero-stress):");
        Console.WriteLine("  DSR.exe --silero \"<книга.fb2|файл.txt>\" [-o \"<результат.fb2|файл.txt>\"] [--device CPU|CUDA] [--overwrite-accents]");
        Console.WriteLine("  По умолчанию создаётся отдельная копия с суффиксом _silero; существующие ударения сохраняются.\n");
        Console.WriteLine("Расстановка ударений движком add-stress-to-epub (spaCy + Wiktionary-словарь):");
        Console.WriteLine("  DSR.exe --stress-anchor \"<книга.fb2|файл.txt>\" [-o \"<результат.fb2|файл.txt>\"]");
        Console.WriteLine("  По умолчанию создаётся копия с суффиксом _add-stress. Движок и модель spaCy");
        Console.WriteLine("  встроены в DSR.exe и распаковываются в ресурсы\\add-stress при первом использовании.\n");
        Console.WriteLine("Поэтапный конвейер (база add-stress → RUAccent + Silero → сравнение):");
        Console.WriteLine("  DSR.exe --staged \"<книга.fb2|файл.txt>\"");
        Console.WriteLine("  Создаёт базу add-stress, два прогона по базе и отчёт расхождений с категориями");
        Console.WriteLine("  «спорно», «сомнительно» и «неизвестно/опечатка» в рабочей папке DSR_<имя>.\n");
        Console.WriteLine("Тест движка на контрольных словах (скорость и точность, с рекомендацией):");
        Console.WriteLine("  DSR.exe --llm-benchmark [--llm-provider ... --llm-model ... --llm-url ... --llm-key ...]\n");
        Console.WriteLine("Список моделей ИИ, лежащих рядом с программой:");
        Console.WriteLine("  DSR.exe --llm-models");
        Console.WriteLine("Список моделей у внешнего сервиса (например, загруженных в Ollama):");
        Console.WriteLine("  DSR.exe --llm-remote-models --llm-provider ollama [--llm-url http://127.0.0.1:11434]");
        Console.WriteLine("Видеокарта для движков подсказок на PyTorch (silero-stress, omogre):");
        Console.WriteLine("  DSR.exe --torch-status   (какая сборка PyTorch стоит и видит ли она видеокарту)");
        Console.WriteLine("  DSR.exe --torch-cuda     (поставить сборку с CUDA, около трёх гигабайт)");
        Console.WriteLine("  DSR.exe --torch-cpu      (вернуть сборку для процессора)");
        Console.WriteLine("  DSR.exe --llm-device CPU|CUDA   (выбрать устройство движков подсказок)");
        Console.WriteLine("  Устройство хранится в настройках окна и меняется во второй вкладке.");
        Console.WriteLine("\nПроверка ресурсов без запуска окна (ничего не скачивает):");
        Console.WriteLine("  DSR.exe --check-resources [big_poetry|medium_poetry|small_poetry]");
        Console.WriteLine("\nОкно проверки слов отдельно (когда книга уже сконвертирована):");
        Console.WriteLine("  DSR.exe --review \"<книга>_ударения_проверка-слов.txt\" [\"<книга>_ударения.fb2\"]");
        Console.WriteLine($"Ресурсы DSR хранятся рядом с приложением: {DataDirectory}");
        Console.WriteLine("Словари и движок add-stress встроены в DSR.exe: папку «ресурсы» можно удалить целиком,");
        Console.WriteLine("и при следующем запуске программа восстановит её сама.");
    }

    private sealed class Options
    {
        public required string Input { get; init; }
        public string Output { get; init; } = "";
        public bool OutputWasSpecified { get; init; }
        public string? SingleOutputFile { get; init; }
        public string Suffix { get; init; } = "_ударения";
        public string Model { get; init; } = "big_poetry";
        public bool Dictionary { get; init; } = true;
        public string Device { get; init; } = "CPU";
        public bool Recursive { get; init; }
        public bool ShowHelp { get; init; }

        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
                return new Options { Input = "", ShowHelp = true };

            string? input = null;
            string output = "";
            var outputSpecified = false;
            string suffix = "_ударения";
            string model = "big_poetry";
            var dictionary = true;
            var device = "CPU";
            var recursive = false;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Value()
                {
                    if (++i >= args.Length) throw new ArgumentException($"Для параметра {arg} не задано значение.");
                    return args[i];
                }
                switch (arg.ToLowerInvariant())
                {
                    case "-o": case "--output": output = Value(); outputSpecified = true; break;
                    case "--suffix": suffix = Value(); break;
                    case "--model": model = Value(); break;
                    case "--device": device = Value().ToUpperInvariant(); break;
                    // Ключи интерфейса и режимов проверки: они разбираются в Main до этой
                    // точки, но если стоят рядом с путём книги, Parse не должен падать.
                    case "--llm-device": case "--llm-provider": case "--llm-model": case "--llm-url":
                    case "--llm-cases": Value(); break;
                    case "--no-dictionary": dictionary = false; break;
                    case "--recursive": recursive = true; break;
                    default:
                        if (arg.StartsWith('-')) throw new ArgumentException($"Неизвестный параметр: {arg}");
                        if (input != null) throw new ArgumentException("Укажите только один исходный файл или папку.");
                        input = arg;
                        break;
                }
            }
            if (input == null) throw new ArgumentException("Не указан исходный файл или папка.");
            if (device is not ("CPU" or "CUDA")) throw new ArgumentException("--device должен быть CPU или CUDA.");

            string? singleOutput = null;
            if (outputSpecified && (output.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase) || output.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                if (Directory.Exists(input)) throw new ArgumentException("Для папки -o должен задавать папку, не имя файла.");
                var sourceExtension = Path.GetExtension(input);
                if (!sourceExtension.Equals(Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Расширение выходного файла должно совпадать с форматом исходного файла.");
                if (File.Exists(input) && string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Выходной файл не должен совпадать с исходным.");
                singleOutput = output;
            }
            else if (outputSpecified)
                Directory.CreateDirectory(output);

            return new Options { Input = input, Output = output, OutputWasSpecified = outputSpecified,
                SingleOutputFile = singleOutput, Suffix = suffix, Model = model, Dictionary = dictionary,
                Device = device, Recursive = recursive };
        }
    }

    private sealed class RuAccentWorker : IDisposable
    {
        private readonly string _model;
        private readonly bool _dictionary;
        private readonly string _device;
        private readonly Action<string>? _report;
        private Process? _process;
        private readonly object _sync = new();
        private readonly ConversionDiagnostics? _diagnostics;
        private TimeSpan _workerInferenceTime;
        private long _workerCallCount;

        public RuAccentWorker(string model, bool dictionary, string device, Action<string>? report = null, ConversionDiagnostics? diagnostics = null)
        {
            _diagnostics = diagnostics;
            _model = model;
            _dictionary = dictionary;
            _device = device;
            _report = report;
        }

        public void EnsureStarted()
        {
            if (_process != null) return;
            Start();
        }

        public void Start()
        {
            if (_process != null) return;
            var script = ExtractEmbeddedFile("DSR.ruaccent_worker.py", "ruaccent_worker.py");
            var python = EnsurePythonRuntime();
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            start.Environment["USE_TORCH"] = "1";
            var cpuThreads = Math.Max(1, Environment.ProcessorCount);
            start.Environment["DSR_CPU_THREADS"] = cpuThreads.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ConfigureResourceEnvironment(start);
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--model"); start.ArgumentList.Add(_model);
            start.ArgumentList.Add("--device"); start.ArgumentList.Add(_device);
            start.ArgumentList.Add(_dictionary ? "--dictionary" : "--no-dictionary");
            _process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Python для ruaccent.");
            var errors = new StringBuilder();
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                // onnxruntime writes to the shared stderr fd directly and can leave a stray
                // NUL/control char in front of the first line our worker prints afterwards
                // (observed: "\0DSR_PERF:omograph_model:…"). Without this trim the line was
                // silently dropped from the report, making it look like the omograph model
                // never loaded and hiding real model switches.
                var data = TrimProtocolPrefix(e.Data);
                if (data.StartsWith("DSR_PROGRESS:", StringComparison.Ordinal))
                    _report?.Invoke(data["DSR_PROGRESS:".Length..]);
                else if (data.StartsWith("DSR_PERF:", StringComparison.Ordinal))
                {
                    _diagnostics?.PerformanceEvents.Add(data["DSR_PERF:".Length..]);
                    _report?.Invoke(data);
                }
                else if (data.StartsWith("DSR_INFERENCE:", StringComparison.Ordinal))
                {
                    if (double.TryParse(data.AsSpan("DSR_INFERENCE:".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        _workerInferenceTime += TimeSpan.FromSeconds(seconds);
                        _workerCallCount++;
                        if (_diagnostics != null)
                        {
                            _diagnostics.InferenceElapsed = _workerInferenceTime;
                            _diagnostics.InferenceCalls = _workerCallCount;
                        }
                    }
                    _report?.Invoke(data);
                }
                else lock (errors) errors.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
            var ready = _process.StandardOutput.ReadLine();
            if (ready != "READY")
            {
                var detail = errors.ToString();
                var failure = new InvalidOperationException(ready == null
                    ? $"Не удалось запустить ruaccent через '{python}'. Нажмите «Загрузить ресурсы заново», чтобы восстановить окружение. {detail}"
                    : $"ruaccent не загрузился: {ready}. Нажмите «Проверить ресурсы» или «Загрузить ресурсы заново». {detail}");
                LogError("Загрузка worker RUAccent завершилась ошибкой", failure);
                if (_process.HasExited && _process.ExitCode != 0)
                    LogError("RUAccent worker завершился с ошибкой", $"Exit code: {_process.ExitCode}{Environment.NewLine}{detail}");
                throw failure;
            }
            _report?.Invoke($"Модель {_model} загружена ({_device}). Начинаю обработку…");
            Console.WriteLine($"ruaccent запущен (модель {_model}, {_device}).");
        }

        public string Process(string text)
        {
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("ruaccent не запущен.");
                var json = JsonSerializer.Serialize(text);
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс ruaccent завершился без ответа.");
                using var result = JsonDocument.Parse(response);
                if (result.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString() ?? "Ошибка ruaccent");
                return result.RootElement.GetProperty("text").GetString() ?? text;
            }
        }

        // Sends a batch of independent text nodes in one request. The worker
        // processes them concurrently, which overlaps the many tiny ONNX
        // sessions that made the sequential request/response loop latency-bound.
        // Results are returned in input order; a failed node carries its error.
        public (string? Text, string? Error)[] ProcessBatch(IReadOnlyList<string> texts)
        {
            if (texts.Count == 0) return Array.Empty<(string?, string?)>();
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("ruaccent не запущен.");
                var json = JsonSerializer.Serialize(texts);
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс ruaccent завершился без ответа.");
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("ruaccent вернул неожиданный ответ на пакет.");
                var results = new (string? Text, string? Error)[texts.Count];
                var index = 0;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (index >= results.Length) break;
                    if (element.TryGetProperty("error", out var error))
                        results[index] = (null, error.GetString() ?? "Ошибка ruaccent");
                    else if (element.TryGetProperty("text", out var value))
                        results[index] = (value.GetString(), null);
                    index++;
                }
                if (index != texts.Count)
                    throw new InvalidOperationException("ruaccent вернул неполный ответ на пакет.");
                return results;
            }
        }

        // Asks the worker to look words up in RUAccent's bundled full stress
        // dictionary (accents.json.gz). The worker streams the file on first use and
        // caches the answers, so repeated conversions are cheap and its memory stays
        // proportional to the requested words, never to the 3.2M-entry dictionary.
        public IReadOnlyDictionary<string, string> ResolveWords(IReadOnlyList<string> words)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (words.Count == 0) return map;
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("ruaccent не запущен.");
                var json = JsonSerializer.Serialize(new { op = "resolve", words });
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс ruaccent завершился без ответа.");
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString() ?? "Ошибка ruaccent");
                if (document.RootElement.TryGetProperty("resolved", out var resolved) && resolved.ValueKind == JsonValueKind.Object)
                    foreach (var property in resolved.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.String)
                            map[property.Name] = property.Value.GetString()!;
                return map;
            }
        }

        public void Dispose()
        {
            if (_process == null) return;
            try { _process.StandardInput.Close(); } catch { }
            try { if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
    }

    // Второй движок расстановки ударений — silero-stress. Это не языковая модель:
    // внутри пакета своя небольшая нейросеть со словарём омографов, адрес сервера и
    // папка с весами не нужны. Тот же протокол обмена строками JSON, что у RUAccent,
    // чтобы вторая колонка отчёта и конвертация выглядели одинаково.
    internal sealed class SileroStressWorker : IDisposable
    {
        private readonly string _device;
        private readonly Action<string>? _report;
        private readonly ConversionDiagnostics? _diagnostics;
        private Process? _process;
        private readonly object _sync = new();
        private readonly List<string> _performance = new();
        private readonly StringBuilder _errors = new();
        private string _loadedDevice = "";

        public SileroStressWorker(string device, Action<string>? report = null, ConversionDiagnostics? diagnostics = null)
        {
            _device = device;
            _report = report;
            _diagnostics = diagnostics;
        }

        // Фактическое устройство после загрузки: если CUDA не взялась, внутри остаётся cpu.
        public string LoadedDevice => _loadedDevice;

        public string Errors => _errors.ToString();

        public void EnsureStarted()
        {
            if (_process != null) return;
            var script = ExtractEmbeddedFile("DSR.silero_stress_worker.py", "silero_stress_worker.py");
            var python = EnsurePythonRuntime();
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            // Один поток на слово быстрее, чем распараллеливание: модель крошечная,
            // а лишние потоки torch только добавляют накладных расходов на каждый вызов.
            start.Environment["OMP_NUM_THREADS"] = "1";
            start.Environment["MKL_NUM_THREADS"] = "1";
            ConfigureResourceEnvironment(start);
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(_device);
            _process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить Python для silero-stress.");
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var data = TrimProtocolPrefix(e.Data);
                if (data.StartsWith("DSR_DEVICE:", StringComparison.Ordinal))
                    _loadedDevice = data["DSR_DEVICE:".Length..];
                else if (data.StartsWith("DSR_PROGRESS:", StringComparison.Ordinal))
                    _report?.Invoke(data["DSR_PROGRESS:".Length..]);
                else if (data.StartsWith("DSR_PERF:", StringComparison.Ordinal))
                {
                    lock (_performance) _performance.Add(data["DSR_PERF:".Length..]);
                    _diagnostics?.PerformanceEvents.Add(data["DSR_PERF:".Length..]);
                    _report?.Invoke(data);
                }
                else lock (_errors) _errors.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
            var ready = _process.StandardOutput.ReadLine();
            if (ready != "READY")
            {
                var detail = _errors.ToString();
                var failure = new InvalidOperationException(ready == null
                    ? $"Не удалось запустить silero-stress через '{python}'. {detail}"
                    : $"silero-stress не загрузился: {ready}. {detail}");
                LogError("Загрузка worker silero-stress завершилась ошибкой", failure);
                throw failure;
            }
            Console.WriteLine($"silero-stress запущен (устройство {_loadedDevice}).");
        }

        public IReadOnlyList<string> PerformanceEvents
        {
            get { lock (_performance) return _performance.ToArray(); }
        }

        // Отправляет одну строку JSON и читает одну строку ответа.
        public string Send(string request, CancellationToken token)
        {
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("silero-stress не запущен.");
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                process.StandardInput.WriteLine(request);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс silero-stress завершился без ответа. " + _errors);
                return response;
            }
        }

        public void Dispose()
        {
            var process = _process;
            _process = null;
            if (process == null) return;
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    // Третий движок ударений — omogre. Отдельный пакет с контекстной моделью омографов;
    // тот же протокол обмена строками JSON, что и у silero-stress, поэтому отчёт и
    // кэш подсказок выглядят одинаково для всех трёх движков.
    internal sealed class OmogreStressWorker : IDisposable
    {
        private readonly string _dataPath;
        private readonly string _device;
        private readonly Action<string>? _report;
        private Process? _process;
        private readonly object _sync = new();
        private readonly StringBuilder _errors = new();
        private string _loadedDevice = "";

        public OmogreStressWorker(string dataPath, string device, Action<string>? report = null)
        {
            _dataPath = dataPath;
            _device = device;
            _report = report;
        }

        // Фактическое устройство после загрузки: если CUDA не взялась, внутри остаётся cpu.
        public string LoadedDevice => _loadedDevice;

        public string Errors => _errors.ToString();

        public void EnsureStarted()
        {
            if (_process != null) return;
            var script = ExtractEmbeddedFile("DSR.omogre_stress_worker.py", "omogre_stress_worker.py");
            var python = EnsurePythonRuntime();
            Directory.CreateDirectory(_dataPath);
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            ConfigureResourceEnvironment(start);
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--data-path");
            start.ArgumentList.Add(_dataPath);
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(_device);
            _process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить Python для omogre.");
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var data = TrimProtocolPrefix(e.Data);
                if (data.StartsWith("DSR_DEVICE:", StringComparison.Ordinal))
                    _loadedDevice = data["DSR_DEVICE:".Length..];
                else if (data.StartsWith("DSR_PROGRESS:", StringComparison.Ordinal))
                    _report?.Invoke(data["DSR_PROGRESS:".Length..]);
                else if (data.StartsWith("DSR_PERF:", StringComparison.Ordinal))
                    _report?.Invoke(data);
                else lock (_errors) _errors.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
            var ready = _process.StandardOutput.ReadLine();
            if (ready == null || !ready.StartsWith("READY", StringComparison.Ordinal))
            {
                var detail = _errors.ToString();
                var failure = new InvalidOperationException(ready == null
                    ? $"Не удалось запустить omogre через '{python}'. {detail}"
                    : $"omogre не загрузился: {ready}. {detail}");
                LogError("Загрузка worker omogre завершилась ошибкой", failure);
                throw failure;
            }
            // Устройство приходит в самой строке готовности («READY:cpu»), поэтому не
            // зависит от того, успел ли обработчик stderr разобрать DSR_DEVICE.
            if (ready.StartsWith("READY:", StringComparison.Ordinal)) _loadedDevice = ready[6..].Trim();
            Console.WriteLine($"omogre запущен (устройство {_loadedDevice}).");
        }

        // Отправляет одну строку JSON и читает одну строку ответа.
        public string Send(string request, CancellationToken token)
        {
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("omogre не запущен.");
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                process.StandardInput.WriteLine(request);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс omogre завершился без ответа. " + _errors);
                return response;
            }
        }

        public void Dispose()
        {
            var process = _process;
            _process = null;
            if (process == null) return;
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }
}
