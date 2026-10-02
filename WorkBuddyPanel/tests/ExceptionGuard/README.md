# 异常处理器防递归测试

测 `App.xaml.cs` 的 `OnDispatcherUnhandledException` 里那两层保护。

## 为什么需要

真实事故：面板**开机就闪退**。

```
界面布局时抛异常（虚拟化列表生成容器时空引用）
  → OnDispatcherUnhandledException
    → MessageBox.Show(...)      ← 模态弹窗会重入消息循环
      → WPF 又跑一遍布局
        → 又抛同一个异常
          → 又弹窗 …… 29 层
            → 栈溢出（0xc00000fd，崩在 dwrite.dll）
              → 进程死
```

栈 dump 里那个处理器重复了 **29 次**，2900 层栈。

`e.Handled = true` 拦不住 —— 递归发生在 `MessageBox.Show` **内部**，
不是在它返回之后。

## 两层保护

1. **同签名只弹一次** —— 布局类异常会一轮轮重复抛，但它们其实是同一个
   问题，用户看一遍就够了
2. **总数熔断** —— 就算签名每次都不同（消息里带随机数），最多弹 3 次，
   保证界面还活着

## 怎么跑

```bash
cd tests/ExceptionGuard
dotnet run
```

## 注意

这里**复制**了产品那段判定逻辑（产品代码引 WPF，测试引不了）。
两边不一致时以产品为准 —— 这个测试的目的是验证判据思路，
不是替代真实调用。
