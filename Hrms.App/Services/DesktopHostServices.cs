using HrmsApp.Services;

namespace HrmsApp;

// The Windows app's side of IHostServices: files go to Documents\HRMS Reports, PDFs come from the WebView's own print-to-PDF.
public sealed class DesktopHostServices(PdfExporter pdf) : IHostServices
{
    static string Folder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HRMS Reports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public Task<string?> GetPreference(string key) => Task.FromResult<string?>(Preferences.ContainsKey(key) ? Preferences.Get(key, "") : null);

    public Task SetPreference(string key, string? value)
    {
        if (value is null) Preferences.Remove(key); else Preferences.Set(key, value);
        return Task.CompletedTask;
    }

    public async Task<SavedFile> SaveText(string fileName, string content)
    {
        try
        {
            var file = Path.Combine(Folder(), fileName);
            await File.WriteAllTextAsync(file, content, new System.Text.UTF8Encoding(true));
            return new SavedFile(true, "Saved to " + file, file);
        }
        catch (Exception ex) { return new SavedFile(false, "Couldn't save the file: " + ex.Message, null); }
    }

    public async Task<SavedFile> SavePdf(string fileName, bool landscape, string title, string footer)
    {
        try
        {
            var file = Path.Combine(Folder(), fileName);
            var (ok, err) = await pdf.Save(file, landscape, title, footer);
            return ok ? new SavedFile(true, "PDF saved to " + file, file) : new SavedFile(false, "Couldn't create the PDF: " + err, null);
        }
        catch (Exception ex) { return new SavedFile(false, "Couldn't create the PDF: " + ex.Message, null); }
    }

    public Task OpenFile(string path) => Launcher.Default.OpenAsync(new OpenFileRequest("Report", new ReadOnlyFile(path)));

    public Task OpenFolder(string? pathOfFile) => Launcher.Default.OpenAsync(new Uri(string.IsNullOrEmpty(pathOfFile) ? Folder() : Path.GetDirectoryName(pathOfFile)!));
}
