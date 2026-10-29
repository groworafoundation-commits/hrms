using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace HrmsApp.Services;

public record OptionRight(string OptionId, bool View, bool Add, bool Edit, bool Delete, bool Print, int Level);

public record MenuEntry(string Id, string Name, string Route, string Icon, int Sequence);

public record SessionUser(string UserId, string UserName, string FullName, string RoleId, string RoleName, string Company, string FinYear,
    List<string> Companies, Dictionary<string, OptionRight> Rights, int EmployeeScope, bool IsAdmin, string? EmployeeCode, List<MenuEntry> Menu, bool MustChange = false);

public record LoginResponse(string Token, SessionUser User);

// Who is signed in and what the ERP role allows. Rights are the ERP's own accessright rows, keyed by menu option id.
public sealed class AuthState
{
    public const string EmployeeMaster = "137", ExtraDetails = "207", Grade = "208", Variable = "209", Leave = "210", Transfer = "1012", Loans = "1765", Reports = "162";

    public string? Token { get; private set; }
    public SessionUser? User { get; private set; }
    public bool IsSignedIn => User is not null && Token is not null;
    public event Action? Changed;

    public void SignIn(LoginResponse r) { Token = r.Token; User = r.User; Changed?.Invoke(); }
    public void PasswordChanged() { if (User is not null) { User = User with { MustChange = false }; Changed?.Invoke(); } }
    public void SignOut() { if (!IsSignedIn) return; Token = null; User = null; Changed?.Invoke(); }

    OptionRight? Right(string option) => User is not null && User.Rights.TryGetValue(option, out var r) && r.View ? r : null;
    public bool Can(string option) => Right(option) is not null;
    public bool CanAdd(string option) => Right(option)?.Add == true;
    public bool CanEdit(string option) => Right(option)?.Edit == true;
    public bool CanDelete(string option) => Right(option)?.Delete == true;
    public bool CanPrint(string option) => Right(option)?.Print == true;
    public bool CanAny(params string[] options) => options.Any(Can);

    // Screens of the organisation-wide HR workspace; a role without any of them lands on its own menu instead.
    public static readonly string[] OrgOptions = [EmployeeMaster, Grade, Variable, Leave, Transfer, Loans, Reports, "164"];
    public bool HasOrgAccess => CanAny(OrgOptions);
    public bool HasMenu(string id) => User?.Menu.Any(m => m.Id == id) == true;
    public string Landing => User?.MustChange == true ? "change-password" : HasOrgAccess ? "" : User?.Menu.OrderBy(m => m.Sequence).Select(m => m.Route).FirstOrDefault(r => !r.StartsWith("http")) ?? "my";

    // First screen the user is allowed to open (the dashboard is open to everyone signed in).
    public string Initials
    {
        get
        {
            var parts = (User?.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "?" : string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }
}

// Adds the bearer token to every API call and signs the user out when the service says the session has ended.
public sealed class AuthHandler(AuthState auth, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (auth.Token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        var res = await base.SendAsync(request, cancellationToken);
        if (res.StatusCode == HttpStatusCode.Unauthorized && request.RequestUri?.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase) != true)
            auth.SignOut();
        return res;
    }
}
