using ClipCompose.Core.Localization;
using ClipCompose.Core.Model;
using ClipCompose.Core.Settings;

namespace ClipCompose.Core.Storage;

/// <summary>インポートの結果と、その後に使う設定（固定の解除を反映したもの、または元のまま）。</summary>
public sealed record ImportOutcome(bool Succeeded, string Problem, AppSettings Settings);

/// <summary>
/// インポート（全件置換）の手順（仕様書 §11）。置換後は固定を解除するため、先に固定の解除を保存してから置き換える。
/// - 固定の解除を保存できなければ、テンプレートを置き換えずに中止する（現在のデータを変えない）。
/// - テンプレートの置き換えに失敗したら、固定を元に戻す。
/// こうすることで「置き換わったのに固定が残る」（同じ ID のテンプレートを固定ホットキーで実行できる）状態を作らない。
/// </summary>
public static class ImportProcedure
{
    public static ImportOutcome Replace(TemplateStore templates, SettingsStore settingsStore, AppSettings current, TemplateLibrary imported)
    {
        var unpinned = current;
        if (current.FixedTemplateId is not null)
        {
            unpinned = current.Clone();
            unpinned.FixedTemplateId = null;
            var saved = settingsStore.Save(unpinned);
            if (!saved.Succeeded) return new(false, string.Format(Strings.ImportUnpinFailedFormat, saved.Problem), current);
        }

        var replaced = templates.ReplaceWithImport(imported);
        if (replaced.Succeeded) return new(true, "", unpinned);
        if (current.FixedTemplateId is null) return new(false, replaced.Problem, current);

        // テンプレートは元のまま。固定も元に戻す（戻せなければ解除したままで、その旨も伝える）。
        var restored = settingsStore.Save(current);
        return restored.Succeeded
            ? new(false, replaced.Problem, current)
            : new(false, replaced.Problem + Environment.NewLine + Environment.NewLine + restored.Problem, unpinned);
    }
}
