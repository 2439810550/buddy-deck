// xUnit 的命名空间不在 ImplicitUsings 里，统一在这里引入，
// 免得每个测试文件都写一遍 using Xunit;。
global using Xunit;

// 镜像 src/BuddyDeck.App/GlobalUsings.cs 里**被链接的文件真正需要**的那几条。
//
// ⚠ 刻意**不**引入 CommunityToolkit.Mvvm（那个包这里也没引）：
//    被链接进测试程序集的几个文件（TimeText / QuotaMath / StatusAppearance /
//    ValueConverters / Controls/* / Services/ChatMedia）没有一个是用了 MVVM 的
//    —— **ViewModel 不在被链接的范围里**。
//
//    t62 一度试过链接 ChatViewModel.cs，结论是**走不通**：
//    它依赖 CommunityToolkit 的源生成器在编译期展开 [ObservableProperty]，
//    而源生成器不会为 Compile Include 进来的文件跑（属于另一个程序集的编译）。
//    结果是 CS0246 + CS0759（分部方法没有实现声明）—— 机制性的，加包无效。
//    **正确做法是本项目既有约定：纯逻辑进 Services，ViewModel 只留绑定 glue。**
global using Wpf.Ui.Controls;
global using BuddyDeck.App.Services;

// System.IO 不在本测试项目的 ImplicitUsings 默认集里。
// 测试要读 XAML 源文件、沿目录往上找项目根，所以补一条。
global using System.IO;
