using System.Windows;

namespace LicenseTool;

/// <summary>
/// 发卡工具的入口。
///
/// 只开一个窗口，没有别的启动逻辑 —— 这个工具的使用场景很简单：
/// 贴机器码、填名字、点签发。不需要托盘、不需要单实例检查。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
