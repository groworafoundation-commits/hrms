using HrmsApp;
using HrmsApp.Components;
using HrmsApp.Services;
using HrmsApp.Web;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");

// The site is served by the API itself, so the API is on the same address: no cross-site calls, no CORS.
var auth = new AuthState();
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(new HttpClient(new AuthHandler(auth, new HttpClientHandler())) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = TimeSpan.FromSeconds(90) });
builder.Services.AddSingleton<HrmsClient>();
builder.Services.AddSingleton<PortalClient>();
builder.Services.AddSingleton<AttendanceClient>();
builder.Services.AddSingleton<IHostServices, BrowserHostServices>();

var host = builder.Build();
var js = host.Services.GetRequiredService<IJSRuntime>();
var http = host.Services.GetRequiredService<HttpClient>();

// Keep the sign-in for this browser tab only (sessionStorage), so a refresh does not sign you out; closing the tab does.
auth.Changed += () => _ = js.InvokeVoidAsync("hrms.setSession", auth.IsSignedIn ? auth.Token : null);
var saved = await js.InvokeAsync<string?>("hrms.getSession");
if (!string.IsNullOrEmpty(saved))
{
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/auth/me");
        req.Headers.Authorization = new("Bearer", saved);
        using var res = await http.SendAsync(req);
        var user = res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<SessionUser>() : null;
        if (user is not null) auth.SignIn(new LoginResponse(saved, user)); else await js.InvokeVoidAsync("hrms.setSession", null);
    }
    catch { await js.InvokeVoidAsync("hrms.setSession", null); }
}

await host.RunAsync();
