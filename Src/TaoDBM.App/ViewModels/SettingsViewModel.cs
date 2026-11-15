using System;

namespace TaoDBM.ViewModels;

/// <summary>设置弹窗 ViewModel：持有关闭行为选择与说明文案，保存动作由视图触发。</summary>
public sealed class SettingsViewModel : Mvvm.ObservableObject
{
    private Core.CloseBehavior _closeBehavior;

    /// <summary>保存动作由视图注入，ViewModel 不直接依赖 UI 弹层。</summary>
    private readonly Action<Core.CloseBehavior>? _save;

    public SettingsViewModel(Core.CloseBehavior initial, Action<Core.CloseBehavior>? save = null)
    {
        _closeBehavior = initial;
        _save = save;
        SaveCommand = new Mvvm.RelayCommand(() => _save?.Invoke(CloseBehavior));
    }

    public Mvvm.RelayCommand SaveCommand { get; }

    public Core.CloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set { if (SetProperty(ref _closeBehavior, value)) OnPropertyChanged(nameof(Description)); }
    }

    /// <summary>当前选中行为对应的说明文案。</summary>
    public string Description => DescriptionFor(CloseBehavior);

    public static string DescriptionFor(Core.CloseBehavior kind) => kind switch
    {
        Core.CloseBehavior.Tray => "点关闭时收进系统托盘继续运行，点托盘图标随时回来。",
        Core.CloseBehavior.Exit => "点关闭就退出程序，托盘图标一并消失。",
        _ => "点关闭时弹一次选择：最小化到托盘还是退出，可记住选择。",
    };
}
