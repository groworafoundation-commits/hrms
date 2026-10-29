namespace HrmsApp;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();

		// Optional deep link, e.g. HRMS_START=/payroll/loans, used for demos and UI checks.
		var start = Environment.GetEnvironmentVariable("HRMS_START");
		if (!string.IsNullOrWhiteSpace(start))
			blazorWebView.StartPath = start;
	}
}
