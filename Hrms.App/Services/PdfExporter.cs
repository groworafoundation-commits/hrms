namespace HrmsApp;

/// <summary>Saves the report on screen as a PDF using the browser engine's own print-to-PDF, so the PDF matches the print layout exactly.</summary>
public sealed class PdfExporter
{
#if WINDOWS
    public static Microsoft.UI.Xaml.Controls.WebView2? WebView { get; set; }
#endif

    public async Task<(bool Ok, string? Error)> Save(string path, bool landscape, string title, string footer)
    {
#if WINDOWS
        try
        {
            var web = WebView;
            if (web is null) return (false, "The page view isn't ready yet. Try again in a moment.");
            await web.EnsureCoreWebView2Async();
            var core = web.CoreWebView2;
            var s = core.Environment.CreatePrintSettings();
            s.Orientation = landscape ? Microsoft.Web.WebView2.Core.CoreWebView2PrintOrientation.Landscape : Microsoft.Web.WebView2.Core.CoreWebView2PrintOrientation.Portrait;
            s.PageWidth = landscape ? 11.69 : 8.27;
            s.PageHeight = landscape ? 8.27 : 11.69;
            s.MarginTop = 0.45; s.MarginBottom = 0.5; s.MarginLeft = 0.4; s.MarginRight = 0.4;
            s.ScaleFactor = 1.0;
            s.ShouldPrintBackgrounds = true;
            s.ShouldPrintHeaderAndFooter = true;
            s.HeaderTitle = title;
            s.FooterUri = footer;
            var ok = await core.PrintToPdfAsync(path, s);
            return ok ? (true, null) : (false, "The PDF could not be created.");
        }
        catch (Exception ex) { return (false, ex.Message); }
#else
        await Task.CompletedTask;
        return (false, "PDF export is only available on Windows.");
#endif
    }
}
