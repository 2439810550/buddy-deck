using System.IO;
using Microsoft.Win32;

namespace BuddyDeck.App.Services;

/// <summary>
/// <see cref="IFilePicker"/> 的**生产实现** —— 真的弹系统文件对话框。
///
/// ## 为什么单独一个文件
///
/// 因为它是这个功能里**唯一**依赖 WPF 的地方。
/// <see cref="AccountImport"/>（解析、校验、乱码还原、去重）刻意不碰 WPF，
/// 于是能在 xUnit 的 MTA 线程上被全量测试。
///
/// 这里的 `ShowDialog()` 需要 **STA 线程 + 窗口句柄**，所以它
/// **不参与单元测试** —— 只能靠界面里真点一次来验。
/// 这个证据等级差异在交付回报里如实标注（与 t64 的
/// `WpfClipboardImageSource` 同一处理）。
///
/// ## ⚠ 它**不**做解析
///
/// 只负责「拿到一个路径」。是不是合法账号库、要不要提示「格式不对」，
/// 全在 <see cref="AccountImport"/> 里 —— 那些是业务规则，
/// 不该散落在对话框旁边。
/// </summary>
public sealed class WpfFilePicker : IFilePicker
{
    /// <inheritdoc />
    public string? PickJsonFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "选择账号文件" : title,

            // ⚠ 过滤器同时给「JSON」与「所有文件」两项：
            //   旧版导出的文件名可能不带 .json 后缀，只给 JSON 一项的话
            //   用户在对话框里根本看不到自己要选的文件（这是最常见的
            //   「我明明有那个文件却选不中」）。
            Filter = "账号文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FilterIndex = 1,
            CheckFileExists = true,
            Multiselect = false,
        };

        // ⚠ 首次打开时定位到旧版数据目录 —— 大多数用户要选的就是那里那个文件，
        //   省他一路翻到隐藏目录（`.workbuddy-api` 在资源管理器里默认不显示）。
        if (Directory.Exists(AccountImport.LegacyDirectory))
        {
            dialog.InitialDirectory = AccountImport.LegacyDirectory;
        }

        // 返回 true 才代表用户点了「打开」；取消/关窗都是 false。
        // 这里**不抛异常** —— 取消是正常操作。
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickSaveFile(string title, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "导出账号" : title,
            FileName = string.IsNullOrWhiteSpace(suggestedName) ? "accounts-export.json" : suggestedName,

            // 与打开对话框同一套过滤器：导出文件也是 .json，
            // 而「所有文件」那一项让用户能自己改名（比如带上日期）。
            Filter = "账号文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FilterIndex = 1,

            // ⚠ `OverwritePrompt = true`（默认）—— **不能关**。
            //   导出会覆盖同名文件，而覆盖的是一个**加密凭据文件**：
            //   用户手滑覆盖掉上次的备份就永久丢了。让系统弹确认框挡住这一步。
            OverwritePrompt = true,

            // 默认落到桌面：导出文件是给用户**带走**的（备份/换机器），
            // 而"文档"目录下他往往找不到。
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        // 返回 true 才代表用户点了「保存」；取消/关窗都是 false。
        // 这里不抛异常 —— 取消是正常操作。
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
