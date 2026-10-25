using System.Text.Json.Serialization;
using ClipCompose.Core.Settings;

namespace ClipCompose.Core.Storage;

/// <summary>templates.json とエクスポートファイルの形式（仕様書 §11）。項目の欠落・未知の項目・重複は読み込みエラーにする。</summary>
public sealed class TemplateFileData
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required List<CategoryData> Categories { get; init; }
    public required List<TemplateData> Templates { get; init; }
}

public sealed class CategoryData
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Order { get; init; }
}

public sealed class TemplateData
{
    public required Guid Id { get; init; }
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
    public required string Body { get; init; }
    public required int Order { get; init; }
}

/// <summary>
/// System.Text.Json のソース生成。.NET 10 の厳格な読み込み（重複プロパティの拒否・null 許容注釈の尊重）を使い、
/// 壊れた・想定外のファイルを黙って補正しない。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(TemplateFileData))]
internal sealed partial class TemplateJsonContext : JsonSerializerContext;

/// <summary>
/// settings.json 用。設定は値を補正して読み込むため、未知の項目は無視する（新しい版で増えた項目を残すためではなく、
/// 個々の値の問題で PC の設定全体を使えなくしないため。schemaVersion が新しい場合は読み込まない）。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
