using System.Windows;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel;

/// <summary>
/// 问一个口令。和 TextInputDialog 分开写，是因为 PasswordBox 没法共用 TextBox 的绑定。
///
/// 口令只走子进程的 stdin，不经过命令行参数 —— 否则它会出现在进程列表里，
/// 机器上任何进程都能看到。
/// </summary>
public partial class PasswordInputDialog : Window
{
    private readonly bool _confirm;

    private PasswordInputDialog(string title, string prompt, string hint, string warn, bool confirm)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        HintText.Text = hint;
        WarnText.Text = warn;
        _confirm = confirm;

        ConfirmLabel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        SecondBox.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        Height = confirm ? 300 : 240;

        Loaded += (_, _) => FirstBox.Focus();
    }

    /// <summary>口令；取消时为 null。</summary>
    public string? Value { get; private set; }

    /// <summary>
    /// 弹口令框。
    ///
    /// @param confirm true 时要求输两遍并对齐
    /// @returns 口令，取消为 null
    /// </summary>
    public static string? Ask(
        Window? owner,
        string title,
        string prompt,
        string hint = "",
        string warn = "",
        bool confirm = false)
    {
        var dialog = new PasswordInputDialog(title, prompt, hint, warn, confirm);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var first = FirstBox.Password;

        if (first.Length < TransferRules.MinPassphraseLength)
        {
            MessageBox.Show(
                $"口令至少 {TransferRules.MinPassphraseLength} 位。\n\n" +
                "这个文件里是账号凭据（refreshToken），口令太短挡不住离线破解。",
                "口令太短", MessageBoxButton.OK, MessageBoxImage.Warning);
            FirstBox.Focus();
            return;
        }

        if (_confirm && first != SecondBox.Password)
        {
            MessageBox.Show("两次输入的口令不一样。", "对不上", MessageBoxButton.OK, MessageBoxImage.Warning);
            SecondBox.Clear();
            SecondBox.Focus();
            return;
        }

        Value = first;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
    }
}
