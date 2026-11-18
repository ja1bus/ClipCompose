using System.Text.Json.Serialization;
using ClipCompose.Core.Input;
using ClipCompose.Core.Localization;

namespace ClipCompose.Core.Settings;

/// <summary>
/// 固定スロット（1.3.0〜。仕様書 §6・§17.2）。テンプレート・ショートカット・自動貼り付けを持つ。既定はテンプレート・ショートカットとも未設定、自動貼り付けは ON。
/// </summary>
public sealed class FixedSlot
{
    /// <summary>固定するテンプレートの ID。null は未設定。</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>このスロットのショートカット（"Ctrl+Alt+1" 形式）。空は未設定。</summary>
    public string Hotkey { get; set; } = "";

    /// <summary>ショートカットからの実行で自動貼り付けするか（既定 ON）。</summary>
    public bool AutoPaste { get; set; } = true;

    [JsonIgnore]
    public Shortcut Shortcut => Shortcut.TryParse(Hotkey, out var s) ? s : Shortcut.None;

    public FixedSlot Clone() => (FixedSlot)MemberwiseClone();
}

/// <summary>
/// PC ごとの設定（settings.json。仕様書 §11）。テンプレートとは別のファイルに保存し、エクスポートに含めない。
/// ショートカットの既定は通常・固定とも未設定（仕様書 §10）。
/// schemaVersion 2（1.3.0〜）で固定を 3 スロットにした。schemaVersion 1 は読み込み時に移行する（<see cref="Storage.SettingsStore"/>）。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>通常モードのショートカット（"Ctrl+Alt+Space" 形式）。空は未設定。</summary>
    public string NormalHotkey { get; set; } = "";

    /// <summary>通常モードの自動貼り付け（既定 OFF）。</summary>
    public bool NormalAutoPaste { get; set; }

    /// <summary>固定スロット 1〜3（要素は <see cref="FixedSlotRules.Count"/> 個。補正で揃える）。</summary>
    public List<FixedSlot> FixedSlots { get; set; } = FixedSlotRules.Defaults();

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

    /// <summary>選択画面を開く位置（<see cref="PickerPlacements"/> の値。既定は中央）。</summary>
    public string PickerPlacement { get; set; } = PickerPlacements.Center;

    /// <summary>前回閉じたときの選択画面の左上（物理ピクセル）。「前回の位置」のときだけ書く。</summary>
    public int? PickerLeft { get; set; }

    public int? PickerTop { get; set; }

    /// <summary>起動時に「起動しました」の通知を出すか（既定 ON）。</summary>
    public bool StartupNotice { get; set; } = true;

    [JsonIgnore]
    public Shortcut NormalShortcut => Shortcut.TryParse(NormalHotkey, out var s) ? s : Shortcut.None;

    /// <summary>スロット番号（1〜3）の設定。</summary>
    public FixedSlot Slot(int slot) => FixedSlots[slot - 1];

    /// <summary>複製（リスト・スロットも別のものにする。保存に成功するまで元の設定を変えないため）。</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.CollapsedCategoryIds = [.. CollapsedCategoryIds ?? []];
        copy.FixedSlots = [.. (FixedSlots ?? []).Select(s => s?.Clone()!)];
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

        NormalizeSlots(s, fixes);

        // ショートカット: 各欄を規則で補正した後、通常 → スロット 1 → 2 → 3 の順に先のものを残す。
        s.NormalHotkey = NormalizeHotkey(s.NormalHotkey, "NormalHotkey", fixes);
        var used = new List<Shortcut>();
        if (s.NormalHotkey.Length > 0) used.Add(s.NormalShortcut);
        for (var i = 0; i < FixedSlotRules.Count; i++)
        {
            var slot = s.FixedSlots[i];
            slot.Hotkey = NormalizeHotkey(slot.Hotkey, $"FixedSlots[{i}].Hotkey", fixes);
            if (slot.Hotkey.Length == 0) continue;
            if (used.Contains(slot.Shortcut))
            {
                slot.Hotkey = "";
                fixes.Add($"FixedSlots[{i}].Hotkey: same as another shortcut, cleared");
            }
            else
            {
                used.Add(slot.Shortcut);
            }
        }

        var placement = (s.PickerPlacement ?? "").Trim().ToLowerInvariant();
        if (!PickerPlacements.IsKnown(placement))
        {
            // 空・null は既定値として黙って補う。知らない値だけ記録する。
            if (placement.Length > 0) fixes.Add("PickerPlacement: unknown value replaced with center");
            placement = PickerPlacements.Center;
        }
        s.PickerPlacement = placement;
        if (s.PickerLeft is null || s.PickerTop is null) s.PickerLeft = s.PickerTop = null;

        // 開閉状態は見た目だけなので、空の ID・重複は黙って除く（記録しない）。
        s.CollapsedCategoryIds = [.. (s.CollapsedCategoryIds ?? []).Where(id => id != Guid.Empty).Distinct()];
        return fixes;
    }

    private static void NormalizeSlots(AppSettings s, List<string> fixes)
    {
        if (s.FixedSlots is null)
        {
            s.FixedSlots = FixedSlotRules.Defaults();
            fixes.Add("FixedSlots: missing, reset to defaults");
        }
        else if (s.FixedSlots.Count != FixedSlotRules.Count)
        {
            fixes.Add($"FixedSlots: {s.FixedSlots.Count} entries, adjusted to {FixedSlotRules.Count}");
            var adjusted = s.FixedSlots.Take(FixedSlotRules.Count).ToList();
            while (adjusted.Count < FixedSlotRules.Count) adjusted.Add(new FixedSlot());
            s.FixedSlots = adjusted;
        }
        var seen = new HashSet<Guid>();
        for (var i = 0; i < FixedSlotRules.Count; i++)
        {
            if (s.FixedSlots[i] is null)
            {
                s.FixedSlots[i] = new FixedSlot();
                fixes.Add($"FixedSlots[{i}]: null, reset to defaults");
            }
            var slot = s.FixedSlots[i];
            slot.Hotkey ??= "";
            if (slot.TemplateId == Guid.Empty) slot.TemplateId = null;
            if (slot.TemplateId is { } id && !seen.Add(id))
            {
                slot.TemplateId = null;
                fixes.Add($"FixedSlots[{i}].TemplateId: the template is already in another slot, cleared");
            }
        }
    }

    private static string NormalizeHotkey(string? value, string name, List<string> fixes)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (Shortcut.TryParse(value, out var shortcut) && shortcut.Validate() == ShortcutValidation.Ok) return shortcut.ToString();
        fixes.Add($"{name}: invalid shortcut, cleared");
        return "";
    }
}
