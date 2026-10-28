using System.Text.Json.Serialization;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;

namespace ClipCompose.Core.Settings;

/// <summary>
/// PC ごとの設定（settings.json。仕様書 §11）。テンプレートとは別のファイルに保存し、エクスポートに含めない。
/// ホットキーの既定は通常・固定とも未設定（仕様書 §10）。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>固定テンプレートの ID。null は未設定。</summary>
    public Guid? FixedTemplateId { get; set; }

    /// <summary>通常モードのホットキー（"Ctrl+Alt+Space" 形式）。空は未設定。</summary>
    public string NormalHotkey { get; set; } = "";

    /// <summary>固定モードのホットキー。空は未設定。</summary>
    public string FixedHotkey { get; set; } = "";

    /// <summary>通常モードの自動貼り付け（既定 OFF）。</summary>
    public bool NormalAutoPaste { get; set; }

    /// <summary>固定モードの自動貼り付け（既定 ON）。</summary>
    public bool FixedAutoPaste { get; set; } = true;

    /// <summary>表示言語。"" = Windows の表示言語に合わせる / "en" / "ja"。</summary>
    public string Language { get; set; } = UiLanguage.System;

    /// <summary>トレイに常駐することを案内済みか（初回起動の案内で書く）。</summary>
    public bool TrayNoticeShown { get; set; }

    /// <summary>選択画面の本文パネルを開いておくか（前回の状態を次回も使う）。</summary>
    public bool PickerPreview { get; set; }

    /// <summary>
    /// 選択画面で閉じているカテゴリの ID（既定は空 = すべて開く）。1.1.0 で追加した任意の項目で、無い・null なら空として扱う。
    /// 無くなったカテゴリの ID は表示では無視し、次に開閉を保存するときに除く。
    /// </summary>
    public List<Guid> CollapsedCategoryIds { get; set; } = [];

    [JsonIgnore]
    public Shortcut NormalShortcut => Shortcut.TryParse(NormalHotkey, out var s) ? s : Shortcut.None;

    [JsonIgnore]
    public Shortcut FixedShortcut => Shortcut.TryParse(FixedHotkey, out var s) ? s : Shortcut.None;

    /// <summary>複製（リストも別のものにする。保存に成功するまで元の設定を変えないため）。</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.CollapsedCategoryIds = [.. CollapsedCategoryIds ?? []];
        return copy;
    }
}

/// <summary>
/// 読み込んだ設定の補正。使えない値は既定値（未設定）に戻し、記録を返す（error.log 用。表示言語によらず英語）。
/// </summary>
public static class SettingsNormalizer
{
    public static List<string> Normalize(AppSettings s)
    {
        var fixes = new List<string>();
        var language = UiLanguage.Normalize(s.Language);
        if (language != (s.Language ?? "").Trim().ToLowerInvariant()) fixes.Add($"Language: unknown value replaced with the Windows display language");
        s.Language = language;

        s.NormalHotkey = NormalizeHotkey(s.NormalHotkey, "NormalHotkey", fixes);
        s.FixedHotkey = NormalizeHotkey(s.FixedHotkey, "FixedHotkey", fixes);
        if (s.NormalHotkey.Length > 0 && s.NormalShortcut == s.FixedShortcut)
        {
            s.FixedHotkey = "";
            fixes.Add("FixedHotkey: same as NormalHotkey, cleared");
        }
        if (s.FixedTemplateId == Guid.Empty) s.FixedTemplateId = null;
        // 開閉状態は見た目だけなので、空の ID・重複は黙って除く（記録しない）。
        s.CollapsedCategoryIds = [.. (s.CollapsedCategoryIds ?? []).Where(id => id != Guid.Empty).Distinct()];
        return fixes;
    }

    private static string NormalizeHotkey(string? value, string name, List<string> fixes)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (Shortcut.TryParse(value, out var shortcut) && shortcut.Validate() == ShortcutValidation.Ok) return shortcut.ToString();
        fixes.Add($"{name}: invalid shortcut, cleared");
        return "";
    }
}
