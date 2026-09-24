namespace WorkBuddyPanel.Services;

/// <summary>
/// 账号迁移的几条规矩。跟 workbuddy_api/src/transfer.js 里的常量保持一致 ——
/// 界面先拦一道，用户就不必等子进程报错了。
/// </summary>
public static class TransferRules
{
    /// <summary>口令最短长度。太短挡不住对导出文件的离线爆破。</summary>
    public const int MinPassphraseLength = 8;

    /// <summary>导出文件的建议文件名。</summary>
    public static string SuggestedFileName(string provider)
        => $"workbuddy-accounts-{provider}-{DateTime.Now:yyyy-MM-dd}.json";

    /// <summary>导出文件里到底是什么，给界面提示用。</summary>
    public const string ExportWarning =
        "这个文件里有账号的 refreshToken，等同于账号密码：\n" +
        "· 拿到文件 + 口令的人，可以一直用这些号的额度\n" +
        "· 口令忘了就解不开，只能重新扫码\n" +
        "· 在另一台电脑导入后记得把文件删掉，别留在微信/网盘里";
}
