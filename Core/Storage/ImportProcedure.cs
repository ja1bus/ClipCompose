using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Core.Storage;

/// <summary>インポートの結果と、その後に使う設定（固定の解除を反映したもの、または元のまま）。</summary>
public sealed record ImportOutcome(bool Succeeded, string Problem, AppSettings Settings);

/// <summary>
/// 現在のデータを退避してから全体を保存する手順（全件置換・追加インポート・中身のあるカテゴリの削除。仕様書 §11）。
/// 固定のテンプレートが無くなる（または置き換わる）場合は、先に固定の解除を保存してから保存する。
/// - 固定の解除を保存できなければ、テンプレートを保存せずに中止する（現在のデータを変えない）。
/// - テンプレートの保存に失敗したら、固定を元に戻す。
/// こうすることで「置き換わったのに固定が残る」（同じ ID のテンプレートを固定ホットキーで実行できる）状態を作らない。
/// </summary>
public static class ImportProcedure
{
    /// <summary>全件置換。置換後は固定を解除する（同じ ID が含まれていても）。</summary>
    public static ImportOutcome Replace(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary imported) =>
        Save(templates, settingsStore, current, imported, "before-import", unpin: current.FixedTemplateId is not null);

    /// <summary>
    /// 追加インポート・カテゴリの削除。固定のテンプレートが <paramref name="removedTemplateIds"/> に含まれる場合だけ固定を解除する。
    /// </summary>
    public static ImportOutcome SaveRemoving(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary library,
        IReadOnlySet<Guid> removedTemplateIds, string label) =>
        Save(templates, settingsStore, current, library, label,
            unpin: current.FixedTemplateId is { } fixedId && removedTemplateIds.Contains(fixedId));

    private static ImportOutcome Save(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary library, string label, bool unpin)
    {
        var unpinned = current;
        if (unpin)
        {
            unpinned = current.Clone();
            unpinned.FixedTemplateId = null;
            var saved = settingsStore.Save(unpinned);
            if (!saved.Succeeded) return new(false, string.Format(Strings.ImportUnpinFailedFormat, saved.Problem), current);
        }

        var replaced = templates.SaveAfterCopy(library, label);
        if (replaced.Succeeded) return new(true, "", unpinned);
        if (!unpin) return new(false, replaced.Problem, current);

        // テンプレートは元のまま。固定も元に戻す（戻せなければ解除したままで、その旨も伝える）。
        var restored = settingsStore.Save(current);
        return restored.Succeeded
            ? new(false, replaced.Problem, current)
            : new(false, replaced.Problem + Environment.NewLine + Environment.NewLine + restored.Problem, unpinned);
    }
}
