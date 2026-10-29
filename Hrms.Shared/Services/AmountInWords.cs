namespace HrmsApp;

public static class AmountInWords
{
    static readonly string[] Ones =
    [
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen",
        "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    static string Below1000(int n)
    {
        var parts = new List<string>();
        if (n >= 100) { parts.Add(Ones[n / 100] + " Hundred"); n %= 100; }
        if (n >= 20) { parts.Add(Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "")); }
        else if (n > 0) parts.Add(Ones[n]);
        return string.Join(" ", parts);
    }

    static string Whole(long n)
    {
        if (n == 0) return "Zero";
        var parts = new List<string>();
        foreach (var (size, name) in new[] { (1_000_000_000L, "Billion"), (1_000_000L, "Million"), (1_000L, "Thousand") })
        {
            if (n >= size) { parts.Add(Below1000((int)(n / size)) + " " + name); n %= size; }
        }
        if (n > 0) parts.Add(Below1000((int)n));
        return string.Join(" ", parts);
    }

    /// <summary>"UAE Dirhams Six Thousand Seventy Six and Fils Fifty Five Only"</summary>
    public static string ToWords(decimal amount, string currency = "AED")
    {
        var (major, minor) = currency.ToUpperInvariant() switch
        {
            "AED" => ("UAE Dirhams", "Fils"),
            "USD" => ("US Dollars", "Cents"),
            "SAR" => ("Saudi Riyals", "Halalas"),
            "INR" => ("Indian Rupees", "Paise"),
            _ => (currency, "Cents"),
        };
        var negative = amount < 0;
        amount = Math.Round(Math.Abs(amount), 2);
        var whole = (long)Math.Floor(amount);
        var fraction = (int)Math.Round((amount - whole) * 100);
        var text = $"{major} {Whole(whole)}";
        if (fraction > 0) text += $" and {minor} {Whole(fraction)}";
        return (negative ? "Minus " : "") + text + " Only";
    }
}
