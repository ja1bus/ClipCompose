using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;

namespace ClipCompose.Core.Storage;

/// <summary>読み込みの結果。失敗の理由は表示用の文字列で、本文・名前を含めない。</summary>
public sealed record ParsedLibrary(TemplateLibrary? Library, string Problem)
{
    public bool Succeeded => Library is not null;
}

/// <summary>
/// templates.json・エクスポートファイルの変換と全件の検証（仕様書 §11）。
/// 不正な JSON・不正なレコード・未対応の schemaVersion は補正せずに失敗にする。
/// </summary>
public static class TemplateFileFormat
{
    /// <summary>理由の一覧に載せる件数（多すぎる場合は残りの件数だけ示す）。</summary>
    private const int MaxListedProblems = 5;

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        // 日本語を \uXXXX にせず、そのまま UTF-8 で書く（利用者がファイルを確認しやすいように）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = TemplateJsonContext.Default,
    };

    private static JsonTypeInfo<TemplateFileData> TypeInfo => (JsonTypeInfo<TemplateFileData>)Options.GetTypeInfo(typeof(TemplateFileData));

    public static byte[] Serialize(TemplateLibrary library)
    {
        var data = new TemplateFileData
        {
            SchemaVersion = TemplateFileData.CurrentSchemaVersion,
            Categories = [.. library.Categories.Select((c, i) => new CategoryData { Id = c.Id, Name = c.Name, Order = i })],
            // テンプレートはカテゴリの表示順に並べ、カテゴリ内の順を order に書く。
            Templates = [.. library.Categories.SelectMany(c => library.TemplatesIn(c.Id).Select((t, i) => new TemplateData
            {
                Id = t.Id, CategoryId = t.CategoryId, Name = t.Name, Body = t.Body, Order = i,
            }))],
        };
        return JsonSerializer.SerializeToUtf8Bytes(data, TypeInfo);
    }

    public static ParsedLibrary Parse(byte[] utf8)
    {
        // 新しい版で増えた項目を「未知の項目」として扱う前に、版を確かめる。
        int version;
        try
        {
            using var document = JsonDocument.Parse(utf8);
            if (!TryGetSchemaVersion(document, out version)) return new(null, Strings.ProblemNoSchemaVersion);
        }
        catch (JsonException ex)
        {
            return new(null, InvalidJson(ex));
        }
        if (version > TemplateFileData.CurrentSchemaVersion) return new(null, string.Format(Strings.ProblemNewerSchemaFormat, version));
        if (version != TemplateFileData.CurrentSchemaVersion) return new(null, string.Format(Strings.ProblemUnsupportedSchemaFormat, version));

        TemplateFileData? data;
        try
        {
            data = JsonSerializer.Deserialize(utf8, TypeInfo);
        }
        catch (JsonException ex)
        {
            return new(null, InvalidJson(ex));
        }
        if (data is null) return new(null, Strings.ProblemNoSchemaVersion);
        // null 許容注釈の検証はプロパティだけで、配列の要素の null は通る。
        if (data.Categories.Contains(null!) || data.Templates.Contains(null!)) return new(null, Strings.ProblemNullRecord);
        return Validate(data);
    }

    /// <summary>
    /// ルートのオブジェクトの schemaVersion（整数）。数値以外（文字列など）は読めないものとして扱う
    /// （JsonElement.TryGetInt32 は数値以外で例外になるため、型を先に確かめる）。
    /// </summary>
    internal static bool TryGetSchemaVersion(JsonDocument document, out int version)
    {
        version = 0;
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("schemaVersion", out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out version);
    }

    /// <summary>
    /// ID の一意性、参照整合性、名前・本文の規則を全件確かめる。1 件でも問題があればライブラリを返さない。
    /// </summary>
    private static ParsedLibrary Validate(TemplateFileData data)
    {
        var problems = new List<string>();
        var categories = new List<Category>();
        var categoryIds = new HashSet<Guid>();
        foreach (var (c, index) in data.Categories.Select((c, i) => (c, i + 1)).OrderBy(p => p.c.Order))
        {
            if (c.Id == Guid.Empty || !categoryIds.Add(c.Id))
                problems.Add(string.Format(Strings.ProblemCategoryFormat, index, Strings.ProblemDuplicateId));
            var error = TemplateRules.CheckName(c.Name, TemplateRules.CategoryNameMax, out var name);
            if (error == LibraryError.None && categories.Any(x => TemplateRules.SameName(x.Name, name))) error = LibraryError.NameDuplicate;
            if (error != LibraryError.None) problems.Add(string.Format(Strings.ProblemCategoryFormat, index, TemplateRules.Describe(error, category: true)));
            categories.Add(new Category(c.Id, name));
        }

        var templates = new List<Template>();
        var templateIds = new HashSet<Guid>();
        // カテゴリの表示順 → カテゴリ内の order の順（同じ order はファイル内の順）。
        var categoryRank = categories.Select((c, i) => (c.Id, i)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().i);
        var ordered = data.Templates.Select((t, i) => (t, index: i + 1))
            .OrderBy(p => categoryRank.TryGetValue(p.t.CategoryId, out var r) ? r : int.MaxValue)
            .ThenBy(p => p.t.Order);
        foreach (var (t, index) in ordered)
        {
            if (t.Id == Guid.Empty || !templateIds.Add(t.Id))
                problems.Add(string.Format(Strings.ProblemTemplateFormat, index, Strings.ProblemDuplicateId));
            if (!categoryIds.Contains(t.CategoryId))
                problems.Add(string.Format(Strings.ProblemTemplateFormat, index, Strings.ProblemMissingCategory));
            var error = TemplateRules.CheckName(t.Name, TemplateRules.TemplateNameMax, out var name);
            if (error == LibraryError.None && templates.Any(x => x.CategoryId == t.CategoryId && TemplateRules.SameName(x.Name, name)))
                error = LibraryError.NameDuplicate;
            if (error == LibraryError.None) error = TemplateRules.CheckBody(t.Body);
            if (error != LibraryError.None) problems.Add(string.Format(Strings.ProblemTemplateFormat, index, TemplateRules.Describe(error)));
            templates.Add(new Template(t.Id, t.CategoryId, name, t.Body));
        }

        if (problems.Count == 0) return new(new TemplateLibrary(categories, templates), "");
        var listed = problems.Take(MaxListedProblems).ToList();
        if (problems.Count > MaxListedProblems) listed.Add(string.Format(Strings.ProblemMoreFormat, problems.Count - MaxListedProblems));
        return new(null, string.Join(Environment.NewLine, listed));
    }

    /// <summary>JSON の誤りの位置（内容の文字列は示さない）。</summary>
    internal static string InvalidJson(JsonException ex) =>
        string.Format(Strings.ProblemInvalidJsonFormat, (ex.LineNumber ?? 0) + 1, (ex.BytePositionInLine ?? 0) + 1);
}
