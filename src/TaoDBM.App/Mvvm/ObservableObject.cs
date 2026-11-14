using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaoDBM.Mvvm;

/// <summary>MVVM 基类：实现 INotifyPropertyChanged 的最小基类，供全部 ViewModel 复用。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>字段已变化时写入并通知；返回是否发生了变更。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
