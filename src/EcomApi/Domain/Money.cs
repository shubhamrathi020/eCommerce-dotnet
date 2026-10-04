using System.Globalization;
using System.Text;

namespace EcomApi.Domain;

/// <summary>Money is always integer minor units (paise for INR); never a float.</summary>
public sealed class Money
{
    public long Amount { get; set; }
    public string Currency { get; set; } = "INR";

    public Money() { }
    public Money(long amount) { Amount = amount; }

    /// <summary>Builds a rupee amount from paise, rounding to a whole paisa the way JavaScript's Math.round does.</summary>
    public static Money Inr(double amount) => new(JsRound(amount));

    public static Money Inr(long amount) => new(amount);

    /// <summary>Mirrors JavaScript's Math.round: ties round toward +Infinity.</summary>
    public static long JsRound(double x)
    {
        var f = Math.Floor(x);
        return (long)(x - f >= 0.5 ? f + 1 : f);
    }

    public Money Copy() => new(Amount) { Currency = Currency };

    /// <summary>
    /// Formats paise as rupees the way Intl.NumberFormat('en-IN', INR) does: "₹1,299" for whole rupees and "₹1,299.50"
    /// otherwise, grouping the first three digits and then pairs (lakh style: ₹12,34,567).
    /// </summary>
    public static string Format(Money m)
    {
        var neg = m.Amount < 0;
        var abs = neg ? -m.Amount : m.Amount;
        var rupees = abs / 100;
        var paise = abs % 100;
        var sb = new StringBuilder("₹").Append(GroupIndian(rupees.ToString(CultureInfo.InvariantCulture)));
        if (paise != 0) sb.Append('.').Append(paise.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0'));
        return neg ? "-" + sb : sb.ToString();
    }

    private static string GroupIndian(string digits)
    {
        if (digits.Length <= 3) return digits;
        var head = digits[..^3];
        var tail = digits[^3..];
        var parts = new List<string>();
        while (head.Length > 2)
        {
            parts.Insert(0, head[^2..]);
            head = head[..^2];
        }
        if (head.Length > 0) parts.Insert(0, head);
        parts.Add(tail);
        return string.Join(',', parts);
    }
}
