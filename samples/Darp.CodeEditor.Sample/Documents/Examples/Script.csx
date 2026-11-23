// A C# script of this app. The editor checks it against the app's script API: press Ctrl+Space,
// rest the pointer on Report or Entries, or type Report( to see what the app offers.
var totals = Entries
    .GroupBy(entry => entry.Category)
    .Select(group => (Category: group.Key, Total: group.Sum(entry => entry.Amount)))
    .OrderByDescending(line => line.Total);

foreach (var (category, total) in totals)
    Report(category, total);
