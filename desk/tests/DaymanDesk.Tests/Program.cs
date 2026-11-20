using System.Globalization;
using System.Reflection;
using DaymanDesk.Core;
using DaymanDesk.Tests;

// Replay harness: dotnet run -- --replay <folder of captured .bin jobs>
if (args.Length == 2 && args[0] == "--replay")
{
    foreach (var f in Directory.GetFiles(args[1], "*.bin").OrderBy(x => x))
    {
        var a = ReceiptAnalyzer.Analyze(File.ReadAllBytes(f));
        var e = a.Kind == ReceiptKind.Text ? ReceiptExtractor.Extract(a.Lines) : null;
        Console.WriteLine($"{Path.GetFileName(f)}\t{a.Kind}\t{a.EncodingName}\tinv={e?.InvoiceNo ?? "-"}\ttotal={e?.Total?.ToString(CultureInfo.InvariantCulture) ?? "-"}\tconf={e?.Confidence:0.00}\t{a.Reason}");
    }
    return 0;
}

// A tiny test runner: every public static method of a class in this assembly whose name starts with
// "Test" and that takes no parameters is a test. Exit code 1 when any test fails.
var tests = typeof(Assert).Assembly.GetTypes()
    .Where(t => t.IsClass && t.Name.EndsWith("Tests", StringComparison.Ordinal))
    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal) && m.GetParameters().Length == 0)
        .Select(m => (name: t.Name + "." + m.Name, method: m)))
    .Where(x => args.Length == 0 || args.Any(a => x.name.Contains(a, StringComparison.OrdinalIgnoreCase)))
    .OrderBy(x => x.name)
    .ToList();

int failed = 0;
foreach (var (name, method) in tests)
{
    try
    {
        var r = method.Invoke(null, null);
        if (r is Task task) await task;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        var e = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
        failed++;
        Console.WriteLine("FAIL " + name + ": " + e.Message);
        if (e is not AssertException) Console.WriteLine(e.StackTrace);
    }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 && tests.Count > 0 ? 0 : 1;
