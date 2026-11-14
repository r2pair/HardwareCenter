using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HardwareCenter.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while something (normally a binding) is listening for changes.</summary>
    protected bool HasListeners => PropertyChanged != null;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/> using the fewest inserts and
    /// removals, so bound lists keep their scroll position and existing rows are not re-created.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        int i = 0;
        foreach (T item in desired)
        {
            if (i < target.Count && ReferenceEquals(target[i], item))
            {
                i++;
                continue;
            }

            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], item))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                // Everything between here and the item is no longer wanted.
                for (int j = existing - 1; j >= i; j--)
                    target.RemoveAt(j);
            }
            else
            {
                target.Insert(i, item);
            }

            i++;
        }

        while (target.Count > i)
            target.RemoveAt(target.Count - 1);
    }
}

public enum Severity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Colors defined once in Themes/Palette.xaml, exposed to code that draws with them.</summary>
public static class Palette
{
    public static Color Blue { get; } = Lookup("C.Blue", 0x39, 0x87, 0xE5);
    public static Color Orange { get; } = Lookup("C.Orange", 0xD9, 0x59, 0x26);
    public static Color Aqua { get; } = Lookup("C.Aqua", 0x19, 0x9E, 0x70);
    public static Color Violet { get; } = Lookup("C.Violet", 0x90, 0x85, 0xE9);
    public static Color Magenta { get; } = Lookup("C.Magenta", 0xD5, 0x51, 0x81);
    public static Color Slate { get; } = Lookup("C.Slate", 0x8E, 0x99, 0xAB);
    public static Color Warning { get; } = Lookup("C.Warning", 0xFA, 0xB2, 0x19);
    public static Color Critical { get; } = Lookup("C.Critical", 0xD0, 0x3B, 0x3B);
    public static Color Good { get; } = Lookup("C.Good", 0x0C, 0xA3, 0x0C);

    public static Color For(Severity severity, Color normal) => severity switch
    {
        Severity.Warning => Warning,
        Severity.Critical => Critical,
        _ => normal,
    };

    public static SolidColorBrush Brush(Color color, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Color Lookup(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) is Color color ? color : Color.FromRgb(r, g, b);
}
