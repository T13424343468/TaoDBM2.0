using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;

namespace TaoDBM;

/// <summary>.sql 三件套的装载侧（需求合并稿第一章之二，2026-11-19 23:52 老板令）：
/// 拖入 .sql / 双击 .sql（含双击关联启动与运行中重定向）→ 只装载进查询编辑器，绝不执行。</summary>
public partial class MainWindow
{
    /// <summary>装载 .sql 文件：读文本（流式，GBK/UTF-8 自适应）→ 选中连接上下文开查询页并归属文件。只装载不执行。</summary>
    public async Task LoadSqlFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) { _ = InfoAsync($"文件不存在：{path}"); return; }
            var sql = await Task.Run(() => Core.SqlFileIo.Load(path));
            var info = SelectedInfo();
            if (info?.Provider is null)
            {
                SetStatus("已读入文件，但请先在左侧树选中连接 / 库 / 表再装载 .sql。");
                _ = InfoAsync("装载 .sql 需要一个查询上下文：请先在左侧树选中一个连接、库或表。\n（脚本只装载进编辑器，不会执行。）");
                return;
            }

            var db = info.Kind == NodeKind.Database ? info.Name : info.Db;
            var tab = new TabViewItem { Header = $"📄 {Path.GetFileName(path)}" };
            var wt = new UI.WorkTab(this, info.Provider, db, "", UI.WorkMode.Query, sql);
            wt.SourceFile = Path.GetFullPath(path);
            tab.Content = wt;
            WorkTabs.TabItems.Add(tab);
            _workTabs[tab] = wt;
            WireDirtyHeader(tab, wt);
            SaveSession();
            WorkTabs.SelectedItem = tab;
            SetStatus($"已装载 {Path.GetFileName(path)}（{sql.Length:N0} 字符）· 只装载未执行");
        }
        catch (Exception ex)
        {
            Core.AppLog.Write($".sql 装载: {ex.GetType().Name}: {ex.Message}");
            _ = InfoAsync($"装载失败：{ex.Message}");
        }
    }

    /// <summary>工作区挂拖放：.sql 文件拖入工作区即装载。</summary>
    private void EnableSqlDrop()
    {
        WorkTabs.AllowDrop = true;
        WorkTabs.DragOver += (s, e) =>
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
            }
            else e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        };
        WorkTabs.Drop += async (s, e) =>
        {
            try
            {
                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var it in items.OfType<Windows.Storage.IStorageFile>())
                    if (Path.GetExtension(it.Path).Equals(".sql", StringComparison.OrdinalIgnoreCase))
                        await LoadSqlFileAsync(it.Path);
            }
            catch (Exception ex) { Core.AppLog.Write($".sql 拖放: {ex.Message}"); }
        };
    }

    /// <summary>冷启动：命令行携带 .sql（双击关联 / 拖到 exe）→ 装载。</summary>
    private void LoadSqlFromCommandLine()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var a in args.Skip(1))
            {
                if (a.Length == 0 || a[0] == '-') continue;
                if (Path.GetExtension(a).Equals(".sql", StringComparison.OrdinalIgnoreCase))
                    _ = LoadSqlFileAsync(a);
            }
        }
        catch (Exception ex) { Core.AppLog.Write($".sql 命令行: {ex.Message}"); }
    }

    /// <summary>运行中双击 .sql：重定向实例交接文件路径 → 读交接文件装载（进最近激活窗口）。</summary>
    public void ReceiveSqlHandoff()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var f = Core.TaoPaths.FilePath("sqlopen.handoff");
                if (!File.Exists(f)) return;
                var lines = File.ReadAllLines(f).Where(l => l.Trim().Length > 0).ToList();
                File.Delete(f);
                foreach (var l in lines) _ = LoadSqlFileAsync(l.Trim());
                if (lines.Count > 0) { ShowFromTray(); }
            }
            catch (Exception ex) { Core.AppLog.Write($".sql 交接读取: {ex.Message}"); }
        });
    }
}
