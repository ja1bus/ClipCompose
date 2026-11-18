using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Core.Storage;

/// <summary>インポートの結果と、その後に使う設定（固定の解除を反映したもの、または元のまま）。</summary>
public sealed record ImportOutcome(bool Succeeded, string Problem, AppSettings Settings);

/// <summary>
/// 現在のデータを退避してから全体を保存する手順（全件置換・追加インポート・中身のあるカテゴリの削除。仕様書 §11）。
/// 固定のテンプレートが無くなる（または置き換わる）場合は、先にそのスロットの固定の解除を保存してから保存する（1.3.0〜はスロット単位）。
/// - 固定の解除を保存できなければ、テンプレートを保存せずに中止する（現在のデータを変えない）。
/// - テンプレートの保存に失敗したら、固定を元に戻す。
/// こうすることで「置き換わったのに固定が残る」（同じ ID のテンプレートを固定ホットキーで実行できる）状態を作らない。
/// </summary>
public static class ImportProcedure
{
    /// <summary>
    /// 全件置換（<paramref name="label"/> は退避の名前。全件置換 "before-import"、以前の状態に戻す "before-restore"）。
    /// 置換後はすべてのスロットの固定を解除する（同じ ID が含まれていても）。
    /// </summary>
    public static ImportOutcome Replace(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary imported,
        string label = "before-import")
    {
        var (unpinned, changed) = FixedSlotRules.WithoutAll(current);
        return Save(templates, settingsStore, current, imported, label, changed ? unpinned : null);
    }

    /// <summary>
    /// 追加インポート・カテゴリの削除。<paramref name="removedTemplateIds"/> のテンプレートが入っているスロットだけ固定を解除する。
    /// </summary>
    public static ImportOutcome SaveRemoving(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary library,
        IReadOnlySet<Guid> removedTemplateIds, string label)
    {
        var (unpinned, changed) = FixedSlotRules.WithoutTemplates(current, removedTemplateIds);
        return Save(templates, settingsStore, current, library, label, changed ? unpinned : null);
    }

    /// <param name="unpinned">固定を解除した設定（解除が要らなければ null）。</param>
    private static ImportOutcome Save(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary library, string label,
        AppSettings? unpinned)
    {
        if (unpinned is not null)
        {
            var saved = settingsStore.Save(unpinned);
            if (!saved.Succeeded) return new(false, string.Format(Strings.ImportUnpinFailedFormat, saved.Problem), current);
        }

        var replaced = templates.SaveAfterCopy(library, label);
        if (replaced.Succeeded) return new(true, "", unpinned ?? current);
        if (unpinned is null) return new(false, replaced.Problem, current);

        // テンプレートは元のまま。固定も元に戻す（戻せなければ解除したままで、その旨も伝える）。
        var restored = settingsStore.Save(current);
        return restored.Succeeded
            ? new(false, replaced.Problem, current)
            : new(false, replaced.Problem + Environment.NewLine + Environment.NewLine + restored.Problem, unpinned);
    }
}
