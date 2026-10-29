namespace HrmsApp.Services;

public record SavedFile(bool Ok, string Message, string? Path);

// What differs between the Windows app and the browser: where files go, how a PDF is made, and where small preferences are kept.
public interface IHostServices
{
    Task<string?> GetPreference(string key);
    Task SetPreference(string key, string? value);

    // A CSV or similar text file. The Windows app saves it to Documents; the browser downloads it.
    Task<SavedFile> SaveText(string fileName, string content);

    // The Windows app writes a PDF file; the browser opens its print window, where "Save as PDF" is a choice.
    Task<SavedFile> SavePdf(string fileName, bool landscape, string title, string footer);

    Task OpenFile(string path);
    Task OpenFolder(string? pathOfFile);
}
