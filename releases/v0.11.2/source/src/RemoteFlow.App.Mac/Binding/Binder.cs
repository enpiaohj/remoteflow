using System.ComponentModel;
using AppKit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.App.Mac.Binding;

/// <summary>
/// VM 属性 ↔ AppKit 控件的最小单向 / 双向绑定辅助。
/// <para>
/// AppKit 无绑定引擎。这里订阅 <see cref="INotifyPropertyChanged"/>，属性变化时刷新控件；
/// 双向绑定另挂控件的 action 回写 VM。返回 <see cref="IDisposable"/> 供视图释放时退订。
/// 属性变更回调统一切回主线程 —— VM 的异步命令续体不保证在 UI 线程上。
/// </para>
/// </summary>
public static class Binder
{
    /// <summary>把 VM 的字符串属性单向绑到 <see cref="NSTextField.StringValue"/>。</summary>
    public static IDisposable Text(NSTextField field, ObservableObject vm, string property, Func<string> getter)
        => On(vm, property, () => field.StringValue = getter() ?? string.Empty);

    /// <summary>双向：VM 字符串属性 ↔ 可编辑 <see cref="NSTextField"/>。</summary>
    public static IDisposable TwoWayText(
        NSTextField field, ObservableObject vm, string property, Func<string> getter, Action<string> setter)
    {
        var sub = Text(field, vm, property, getter);
        field.Changed += (_, _) => setter(field.StringValue);
        return sub;
    }

    /// <summary>把 VM 的布尔属性单向绑到控件可见性（隐藏 = 折叠）。</summary>
    public static IDisposable Visible(NSView view, ObservableObject vm, string property, Func<bool> getter)
        => On(vm, property, () => view.Hidden = !getter());

    /// <summary>
    /// 通用：<paramref name="property"/>（或整体刷新）变化时执行 <paramref name="apply"/>。
    /// 立即同步执行一次；后续变更回调切回主线程。
    /// </summary>
    public static IDisposable On(ObservableObject vm, string property, Action apply)
    {
        apply();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == property || string.IsNullOrEmpty(e.PropertyName))
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(apply);
            }
        };
        vm.PropertyChanged += handler;
        return new Unsub(() => vm.PropertyChanged -= handler);
    }

    private sealed class Unsub : IDisposable
    {
        private readonly Action _dispose;
        public Unsub(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
