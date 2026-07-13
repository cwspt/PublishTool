using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PublishTool.App.Models;

public class ScriptEntry : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _path = string.Empty;
    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}