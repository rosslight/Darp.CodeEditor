namespace Sample.ScriptApi;

/// <summary>A booked amount.</summary>
/// <param name="Category">What it was for.</param>
/// <param name="Amount">How much, in whole euros.</param>
public sealed record Entry(string Category, int Amount);

/// <summary>What a report script sees: its members can be used without qualification.</summary>
public sealed class ReportGlobals
{
    /// <summary>Yesterday's entries.</summary>
    public IReadOnlyList<Entry> Entries { get; } = [new("Rent", 900), new("Food", 42), new("Food", 18)];

    /// <summary>Adds a line to the report.</summary>
    /// <param name="category">The line's name.</param>
    /// <param name="amount">The line's amount, in whole euros.</param>
    public void Report(string category, int amount) => Console.WriteLine($"{category, -12} {amount, 8}");
}
