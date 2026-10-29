using HrmsApp.Services;
﻿using Microsoft.Extensions.Logging;

namespace HrmsApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// Android emulators reach the host machine at 10.0.2.2; everything else uses localhost.
		var apiBase = DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5080" : "http://localhost:5080";
		var auth = new AuthState();
		builder.Services.AddSingleton(auth);

		// The API serves HTTPS with its own certificate. The app trusts exactly that certificate (pinned by its SHA-256 fingerprint, kept in
		// ~/.hrms/api-cert.sha256) instead of anything installed on the computer. Without that file it falls back to plain HTTP for development.
		var inner = new HttpClientHandler();
		var pinFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hrms", "api-cert.sha256");
		if (DeviceInfo.Platform != DevicePlatform.Android && File.Exists(pinFile))
		{
			var pin = File.ReadAllText(pinFile).Trim();
			apiBase = "https://localhost:5443";
			inner.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
				cert is not null && string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData)), pin, StringComparison.OrdinalIgnoreCase);
		}
		builder.Services.AddSingleton(new HttpClient(new AuthHandler(auth, inner)) { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(90) });
		builder.Services.AddSingleton<HrmsClient>();
		builder.Services.AddSingleton<PortalClient>();
		builder.Services.AddSingleton<AttendanceClient>();
		builder.Services.AddSingleton<PdfExporter>();
		builder.Services.AddSingleton<IHostServices, DesktopHostServices>();

#if WINDOWS
		// Keep a handle on the WebView2 so reports can be saved as PDF with the browser engine's own print-to-PDF.
		Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("HrmsPdf", (handler, view) =>
		{
			PdfExporter.WebView = handler.PlatformView;
		});
#endif

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
