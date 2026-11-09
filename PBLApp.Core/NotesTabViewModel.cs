using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PBLEngine;
using System.IO;
using System.Threading.Tasks;

namespace PBLApp.ViewModels;

public partial class NotesTabViewModel : ViewModelBase
{
    private readonly BuildModel _build;
    private string _xmlPath;

    [ObservableProperty]
    private string _notes = "";

    public NotesTabViewModel(BuildModel build, string xmlPath)
    {
        _build = build;
        _xmlPath = xmlPath;
        _notes = build.Notes;
    }

    /// <summary>Repoint at a new file path after the build was renamed.</summary>
    public void UpdateXmlPath(string xmlPath) => _xmlPath = xmlPath;

    /// <summary>Agent/IPC hook: push the current notes text into the build model
    /// so /build/save persists it (the UI Save button calls SaveAsync instead).</summary>
    public void ApplyToBuild() => _build.Notes = Notes;

    [RelayCommand]
    private async Task SaveAsync()
    {
        _build.Notes = Notes;
        var xml = _build.SaveBuildToXml();
        if (xml != null && !string.IsNullOrEmpty(_xmlPath))
            await File.WriteAllTextAsync(_xmlPath, xml);
    }
}
