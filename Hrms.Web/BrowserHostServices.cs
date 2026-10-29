using HrmsApp.Services;
using Microsoft.JSInterop;

namespace HrmsApp.Web;

// The browser's side of IHostServices: preferences in local storage, files as downloads, PDFs through the print window.
public sealed class BrowserHostServices(IJSRuntime js) : IHostServices
{
    public async Task<string?> GetPreference(string key) => await js.InvokeAsync<string?>("hrms.get", key);

    public async Task SetPreference(string key, string? value) => await js.InvokeVoidAsync("hrms.set", key, value);

    public async Task<SavedFile> SaveText(string fileName, string content)
    {
        await js.InvokeVoidAsync("hrms.download", fileName, "\uFEFF" + content, "text/csv;charset=utf-8");
        return new SavedFile(true, "Downloaded " + fileName, null);
    }

    public async Task<SavedFile> SavePdf(string fileName, bool landscape, string title, string footer)
    {
        await js.InvokeVoidAsync("print");
        return new SavedFile(true, "In the print window, choose Save as PDF as the destination.", null);
    }

    public Task OpenFile(string path) => Task.CompletedTask;
    public Task OpenFolder(string? pathOfFile) => Task.CompletedTask;
}
