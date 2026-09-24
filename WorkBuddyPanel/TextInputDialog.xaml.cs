using System.Windows;

namespace WorkBuddyPanel;

/// <summary>
/// WPF 没有自带输入框（那是 WinForms 的），这里是补的一个最小实现。
/// 用来问「账号叫什么名字」这类事情。
/// </summary>
public partial class TextInputDialog : Window
{
    private TextInputDialog(string title, string prompt, string hint, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        HintText.Text = hint;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>弹出的值；用户取消时返回 null。</summary>
    public string? Value { get; private set; }

    /// <summary>
    /// 弹一个输入框。
    ///
    /// @param owner 父窗口（可以为 null)
    /// @param title 标题
    /// @param prompt 问什么
    /// @param initial 初始值
    /// @param hint 底下那行小字
    /// @returns 用户输入的内容，取消时为 null
    /// </summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "", string hint = "")
    {
        var dialog = new TextInputDialog(title, prompt, hint, initial);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
    }
}
