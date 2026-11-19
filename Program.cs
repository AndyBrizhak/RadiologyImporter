using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

// ==========================================
// НАСТРОЙКИ
// ==========================================
const string InputFolder = "InputJson";         // Папка с исходными JSON-файлами
const string OutputFolder = "OutputReports";    // Папка для отчетов и итоговых JSON
const int TotalExpectedQuestions = 3753;        // Всего вопросов в МедикТест+

// Если true — скрипт только соберет файлы и отчеты, НЕ подключаясь к MongoDB.
// Поставь false, когда проверишь отчеты и будешь готов залить данные в базу.
const bool DryRunOnly = true;

const string MongoConnectionString = "mongodb://localhost:27017";
const string DatabaseName = "RadiologyAccreditation";
const string QuestionsCollectionName = "questions";
const string TagsCollectionName = "tags";

// ==========================================
// ОСНОВНАЯ ЛОГИКА
// ==========================================
Console.OutputEncoding = Encoding.UTF8;
Directory.CreateDirectory(OutputFolder);

if (!Directory.Exists(InputFolder))
{
    Console.WriteLine($"Папка '{InputFolder}' не найдена. Создай её и положи туда JSON-файлы.");
    return;
}

var jsonReadOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
};

var jsonWriteOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    // Чтобы кириллица писалась нормально, а не как \u0410\u043d...
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var files = Directory.GetFiles(InputFolder, "*.json", SearchOption.AllDirectories);
Console.WriteLine($"Найдено JSON-файлов: {files.Length}");

var mergedQuestions = new Dictionary<int, QuestionDocument>();
var skippedObjectsLog = new List<string>();
int totalRawRecords = 0;

foreach (var file in files)
{
    var fileName = Path.GetFileName(file);
    try
    {
        var content = await File.ReadAllTextAsync(file, Encoding.UTF8);
        var items = JsonSerializer.Deserialize<List<RawQuestionDto>>(content, jsonReadOptions);

        if (items is null) continue;

        foreach (var raw in items)
        {
            totalRawRecords++;

            // Отсеиваем заглушки без ID или текста вопроса
            if (raw.Id is null || raw.Id <= 0 || string.IsNullOrWhiteSpace(raw.Question))
            {
                skippedObjectsLog.Add($"[{fileName}] Пропущена битая запись (Id={raw.Id?.ToString() ?? "null"}, Topic={raw.Topic ?? "null"})");
                continue;
            }

            var id = raw.Id.Value;
            var cleanedQuestion = CleanText(raw.Question);
            var cleanedCorrect = CleanText(raw.CorrectAnswer);
            var cleanedOptions = (raw.Options ?? [])
                .Select(CleanText)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .ToList();
            var cleanedExplanation = CleanExplanation(raw.DetailedExplanation);
            var cleanedTopic = CleanText(raw.Topic);
            var cleanedTags = (raw.Tags ?? [])
                .Select(CleanText)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (!mergedQuestions.TryGetValue(id, out var existing))
            {
                var doc = new QuestionDocument
                {
                    Id = id,
                    Question = cleanedQuestion,
                    Options = cleanedOptions,
                    CorrectAnswer = cleanedCorrect,
                    DetailedExplanation = cleanedExplanation,
                    UpdatedAt = DateTime.UtcNow
                };

                if (!string.IsNullOrWhiteSpace(cleanedTopic))
                    doc.Topics.Add(cleanedTopic);

                AddUniqueIgnoreCase(doc.Tags, cleanedTags);
                mergedQuestions[id] = doc;
            }
            else
            {
                // Умное слияние дубликата по ID
                if (!string.IsNullOrWhiteSpace(cleanedTopic))
                    AddUniqueIgnoreCase(existing.Topics, [cleanedTopic]);

                AddUniqueIgnoreCase(existing.Tags, cleanedTags);

                // Берем более полное (длинное) объяснение
                if (cleanedExplanation.Length > existing.DetailedExplanation.Length)
                    existing.DetailedExplanation = cleanedExplanation;

                // Если у существующей записи ответ не совпадал с options, а у новой совпадает — обновляем
                bool existingValid = existing.Options.Contains(existing.CorrectAnswer, StringComparer.Ordinal);
                bool incomingValid = cleanedOptions.Contains(cleanedCorrect, StringComparer.Ordinal);

                if (!existingValid && incomingValid)
                {
                    existing.Question = cleanedQuestion;
                    existing.Options = cleanedOptions;
                    existing.CorrectAnswer = cleanedCorrect;
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ОШИБКА при чтении файла '{fileName}': {ex.Message}");
    }
}

var sortedQuestions = mergedQuestions.Values.OrderBy(q => q.Id).ToList();
Console.WriteLine($"Всего обработано записей: {totalRawRecords}");
Console.WriteLine($"Уникальных валидных вопросов по ID: {sortedQuestions.Count}");
Console.WriteLine($"Пропущено битых объектов: {skippedObjectsLog.Count}");

// ==========================================
// 1. ВАЛИДАЦИЯ ОТВЕТОВ И ПОИСК ДУБЛЕЙ ПО ТЕКСТУ
// ==========================================
var validationIssues = new List<string>();
foreach (var q in sortedQuestions)
{
    if (q.Options.Count == 0)
    {
        validationIssues.Add($"ID {q.Id}: пустой список вариантов (options).");
    }
    else if (!q.Options.Contains(q.CorrectAnswer, StringComparer.Ordinal))
    {
        validationIssues.Add($"ID {q.Id}: correct_answer ('{q.CorrectAnswer}') НЕ совпадает ни с одним вариантом в options.");
    }
}

// Ищем дубли по связке "нормализованный вопрос + нормализованный правильный ответ"
var contentDuplicates = sortedQuestions
    .GroupBy(q => (NormalizeForCompare(q.Question), NormalizeForCompare(q.CorrectAnswer)))
    .Where(g => g.Count() > 1)
    .Select(g => $"IDs [{string.Join(", ", g.Select(x => x.Id))}]: {g.First().Question} -> {g.First().CorrectAnswer}")
    .ToList();

// ==========================================
// 2. ПОИСК ПРОПУЩЕННЫХ ID (1..3753)
// ==========================================
var presentIds = mergedQuestions.Keys.ToHashSet();
var missingIds = Enumerable.Range(1, TotalExpectedQuestions)
    .Where(id => !presentIds.Contains(id))
    .ToList();

var missingRanges = FormatRanges(missingIds);

// ==========================================
// 3. ГЕНЕРАЦИЯ ЕДИНОГО ФАЙЛА ТЕГОВ
// ==========================================
// Собираем уникальные теги без учета регистра, сохраняя красивое написание
var uniqueTagsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var q in sortedQuestions)
{
    foreach (var tag in q.Tags)
    {
        if (!uniqueTagsMap.ContainsKey(tag))
            uniqueTagsMap[tag] = tag;
    }
}

var tagDocuments = uniqueTagsMap.Values
    .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
    .Select(t => new TagDocument
    {
        TagGroup = "", // Пустая строка — заполнишь позже в Compass или приложении
        Tag = t
    })
    .ToList();

// ==========================================
// 4. СОХРАНЕНИЕ ОТЧЕТОВ И JSON-ФАЙЛОВ
// ==========================================
await File.WriteAllTextAsync(
    Path.Combine(OutputFolder, "questions_merged.json"),
    JsonSerializer.Serialize(sortedQuestions, jsonWriteOptions),
    Encoding.UTF8);

await File.WriteAllTextAsync(
    Path.Combine(OutputFolder, "tags_collection.json"),
    JsonSerializer.Serialize(tagDocuments, jsonWriteOptions),
    Encoding.UTF8);

await File.WriteAllTextAsync(
    Path.Combine(OutputFolder, "missing_ids.txt"),
    $"Всего отсутствует вопросов: {missingIds.Count} из {TotalExpectedQuestions}\n\n" +
    $"Диапазоны:\n{string.Join(", ", missingRanges)}\n\n" +
    $"Полный список:\n{string.Join("\n", missingIds)}",
    Encoding.UTF8);

await File.WriteAllTextAsync(
    Path.Combine(OutputFolder, "validation_issues.txt"),
    $"Найдено проблем с ответами: {validationIssues.Count}\n\n" +
    string.Join("\n", validationIssues) +
    "\n\n--- Пропущенные битые записи ---\n" +
    string.Join("\n", skippedObjectsLog),
    Encoding.UTF8);

await File.WriteAllTextAsync(
    Path.Combine(OutputFolder, "potential_duplicates.txt"),
    $"Найдено групп дублей (одинаковый вопрос + одинаковый ответ): {contentDuplicates.Count}\n\n" +
    string.Join("\n", contentDuplicates),
    Encoding.UTF8);

Console.WriteLine($"\nОтчеты и файлы сохранены в папку '{OutputFolder}':");
Console.WriteLine($" - Уникальных тегов: {tagDocuments.Count} (tags_collection.json)");
Console.WriteLine($" - Пропущено ID: {missingIds.Count} (missing_ids.txt)");
Console.WriteLine($" - Ошибок валидации ответов: {validationIssues.Count} (validation_issues.txt)");
Console.WriteLine($" - Потенциальных текстовых дублей: {contentDuplicates.Count} (potential_duplicates.txt)");

// ==========================================
// 5. ЗАГРУЗКА (UPSERT) В MONGODB
// ==========================================
if (DryRunOnly)
{
    Console.WriteLine("\n[DryRunOnly = true] Загрузка в MongoDB пропущена. Проверь отчеты и переключи флаг на false.");
    return;
}

Console.WriteLine("\nПодключаемся к MongoDB...");
var client = new MongoClient(MongoConnectionString);
var db = client.GetDatabase(DatabaseName);
var questionsCol = db.GetCollection<QuestionDocument>(QuestionsCollectionName);
var tagsCol = db.GetCollection<TagDocument>(TagsCollectionName);

// Upsert вопросов (не затираем images и references, если они уже были добавлены)
int upsertedQuestions = 0;
foreach (var q in sortedQuestions)
{
    var filter = Builders<QuestionDocument>.Filter.Eq(x => x.Id, q.Id);
    var update = Builders<QuestionDocument>.Update
        .Set(x => x.Topics, q.Topics)
        .Set(x => x.Question, q.Question)
        .Set(x => x.Options, q.Options)
        .Set(x => x.CorrectAnswer, q.CorrectAnswer)
        .Set(x => x.Tags, q.Tags)
        .Set(x => x.DetailedExplanation, q.DetailedExplanation)
        .Set(x => x.UpdatedAt, DateTime.UtcNow)
        .SetOnInsert(x => x.Images, q.Images)
        .SetOnInsert(x => x.References, q.References);

    await questionsCol.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    upsertedQuestions++;
}

// Upsert тегов (используем SetOnInsert для TagGroup, чтобы не стереть уже проставленные группы)
int upsertedTags = 0;
foreach (var t in tagDocuments)
{
    var filter = Builders<TagDocument>.Filter.Eq(x => x.Tag, t.Tag);
    var update = Builders<TagDocument>.Update
        .SetOnInsert(x => x.TagGroup, t.TagGroup)
        .SetOnInsert(x => x.Tag, t.Tag);

    await tagsCol.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    upsertedTags++;
}

// Создаем индексы для быстрого Drill-Down и текстового поиска
await questionsCol.Indexes.CreateManyAsync([
    new CreateIndexModel<QuestionDocument>(Builders<QuestionDocument>.IndexKeys.Ascending(x => x.Topics)),
    new CreateIndexModel<QuestionDocument>(Builders<QuestionDocument>.IndexKeys.Ascending(x => x.Tags)),
    new CreateIndexModel<QuestionDocument>(
        Builders<QuestionDocument>.IndexKeys
            .Text(x => x.Question)
            .Text(x => x.DetailedExplanation))
]);

await tagsCol.Indexes.CreateOneAsync(
    new CreateIndexModel<TagDocument>(
        Builders<TagDocument>.IndexKeys.Ascending(x => x.Tag),
        new CreateIndexOptions { Unique = true }));

Console.WriteLine($"Готово! Загружено/обновлено вопросов: {upsertedQuestions}, тегов: {upsertedTags}. Индексы созданы.");

// ==========================================
// ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ И МОДЕЛИ
// ==========================================
static string CleanText(string? input)
{
    if (string.IsNullOrWhiteSpace(input)) return "";
    return Regex.Replace(input.Trim(), @"\s{2,}", " ");
}

static string CleanExplanation(string? input)
{
    if (string.IsNullOrWhiteSpace(input)) return "";
    // Удаляем артефакты цитирования вида или[cite: 1, 2]
    var withoutCitations = Regex.Replace(input, @"\s*\[cite:\s*[\d,\s]+\]", "", RegexOptions.IgnoreCase);
    return CleanText(withoutCitations);
}

static string NormalizeForCompare(string input)
{
    return Regex.Replace(input.ToLowerInvariant(), @"[\s\p{P}_]+", "");
}

static void AddUniqueIgnoreCase(List<string> target, IEnumerable<string> items)
{
    foreach (var item in items)
    {
        if (!target.Contains(item, StringComparer.OrdinalIgnoreCase))
            target.Add(item);
    }
}

static List<string> FormatRanges(List<int> numbers)
{
    var result = new List<string>();
    if (numbers.Count == 0) return result;

    int start = numbers[0];
    int prev = numbers[0];

    for (int i = 1; i < numbers.Count; i++)
    {
        if (numbers[i] == prev + 1)
        {
            prev = numbers[i];
        }
        else
        {
            result.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = numbers[i];
        }
    }
    result.Add(start == prev ? $"{start}" : $"{start}-{prev}");
    return result;
}

public class RawQuestionDto
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    [JsonPropertyName("correct_answer")]
    public string? CorrectAnswer { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("detailed_explanation")]
    public string? DetailedExplanation { get; set; }
}

public class QuestionDocument
{
    [BsonId]
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [BsonElement("topics")]
    [JsonPropertyName("topics")]
    public List<string> Topics { get; set; } = [];

    [BsonElement("question")]
    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [BsonElement("options")]
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    [BsonElement("correct_answer")]
    [JsonPropertyName("correct_answer")]
    public string CorrectAnswer { get; set; } = "";

    [BsonElement("tags")]
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [BsonElement("detailed_explanation")]
    [JsonPropertyName("detailed_explanation")]
    public string DetailedExplanation { get; set; } = "";

    [BsonElement("images")]
    [JsonPropertyName("images")]
    public List<QuestionImage> Images { get; set; } = [];

    [BsonElement("references")]
    [JsonPropertyName("references")]
    public List<QuestionReference> References { get; set; } = [];

    [BsonElement("updated_at")]
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public class QuestionImage
{
    [BsonElement("file_name")]
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [BsonElement("caption")]
    [JsonPropertyName("caption")]
    public string Caption { get; set; } = "";

    [BsonElement("placement")]
    [JsonPropertyName("placement")]
    public string Placement { get; set; } = "explanation"; // "question" или "explanation"
}

public class QuestionReference
{
    [BsonElement("title")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [BsonElement("file_path")]
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = "";

    [BsonElement("page")]
    [JsonPropertyName("page")]
    public int? Page { get; set; }
}

public class TagDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [JsonIgnore]
    public string? Id { get; set; }

    [BsonElement("tag_group")]
    [JsonPropertyName("tag_group")]
    public string TagGroup { get; set; } = "";

    [BsonElement("tag")]
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";
}