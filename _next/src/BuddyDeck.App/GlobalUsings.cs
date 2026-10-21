// 全局 using。
//
// 为什么有 System.IO 和 Microsoft.Extensions.DependencyInjection：
// WPF 项目的 ImplicitUsings 不带这两个（WPF 的默认集是 Windows 相关的几个），
// 但界面里到处要 Path/File 和从容器取服务。逐文件写太吵，集中在这里。
global using System.IO;
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using Microsoft.Extensions.DependencyInjection;
global using Wpf.Ui.Controls;
global using BuddyDeck.App.Services;

// 注意：Wpf.Ui.Controls 和 System.Windows.Controls 有同名类型
// （Button / TextBox / TextBlock）。所以**不要**全局 using System.Windows.Controls，
// 否则每个 XAML 代码后置文件都会撞上 CS0104 二义性 —— 需要的地方显式写全名。
