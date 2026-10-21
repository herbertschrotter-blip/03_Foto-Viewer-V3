using System.IO;
using Lumora.Services.Sorter;

namespace Lumora.Sorter.ViewModels;

/// <summary>Zeile der Plan-Vorschau (Quelle → Ziel mit Status/Konfliktmarkierung).</summary>
public sealed class PlanRowViewModel
{
    public PlanRowViewModel(SorterPlanItem item, string sourceFolder, string targetRoot)
    {
        Item = item;
        FileName = item.FileName;
        SourceDisplay = RelativeFolder(Path.GetDirectoryName(item.SourcePath) ?? item.SourcePath, sourceFolder, "(Quellordner)");
        var target = RelativeFolder(item.TargetFolder, targetRoot, "(Zielwurzel)");
        TargetDisplay = item.RenameTo is { } renamed ? $"{target} als „{renamed}“" : target;
        StatusText = SorterPlanItem.StatusText(item.Status);
        Note = item.Note;
        IsReady = item.IsReady;
    }

    public SorterPlanItem Item { get; }

    public string FileName { get; }

    public string SourceDisplay { get; }

    public string TargetDisplay { get; }

    public string StatusText { get; }

    public string? Note { get; }

    public bool IsReady { get; }

    public bool IsProblem => !IsReady;

    private static string RelativeFolder(string folder, string root, string rootLabel)
    {
        string relative;
        try
        {
            relative = SorterPaths.RelativeTo(SorterPaths.NormalizeDirectory(folder), SorterPaths.NormalizeDirectory(root));
        }
        catch (System.Exception)
        {
            relative = folder;
        }

        return relative == "." ? rootLabel : relative;
    }
}
