using System.Text.RegularExpressions;
using Xunit;

namespace GameDirector.Core.Tests;

// Source guard for TestBudget: a hang guard is declared ONCE, so no test source
// may grow its own. Narrow by construction — only the three shapes that turned a
// loaded run into a TimeoutException are flagged:
//   WaitAsync with a TimeSpan.From… literal   a wait that declares its own budget
//                                             instead of TestBudget.Hang;
//   Assert.InRange over … Elapsed …           a wall-clock upper bound, i.e. a
//                                             speed assertion in hang-guard clothing;
//   for (… i < N …; i++) await Task.Delay(    a poll whose budget is an iteration
//                                             count, so the machine picks the timeout.
// Deliberately unmatched: a wait bounded by a production CancellationToken
// (`WaitAsync(ct)`), a stub delay that a timeout under test must outlast, fake
// clocks, process/HttpListener helper timeouts and InRange over non-time values.
// A budget that is itself the subject opts out with "// timing-under-test:" on a
// line the match spans — the same opt-out style as NoHttpListenerStopInTestSources.
// TestBudget.cs sits outside src/*Tests*, so the one declared number is never scanned.
public class TestBudgetGuardTests
{
    private const string Exemption = "// timing-under-test:";
    private static readonly (string Shape, string Pattern)[] Shapes =
    {
        ("literal wait budget", @"\.WaitAsync\s*\(\s*TimeSpan\.From"),
        ("wall-clock upper bound", @"Assert\.InRange\s*\([^;]*?Elapsed"),
        ("iteration-count poll", @"\bfor\s*\([^;]*;[^;]*\bi\s*<\s*\d+[^;]*;\s*(?:i\s*\+\+|\+\+\s*i)\s*\)\s*await\s+Task\.Delay\s*\("),
    };

    [Fact]
    public void HangGuardsUseTheOneDeclaredBudget()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var src = Path.Combine(root, "src");
        Assert.True(Directory.Exists(Path.Combine(src, "TestSupport")), "repo layout changed: src/TestSupport not found");
        var files = Directory.GetDirectories(src, "*Tests").SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file); var text = string.Join("\n", lines);
            foreach (var (shape, pattern) in Shapes)
                foreach (Match hit in Regex.Matches(text, pattern))
                {
                    var from = text[..hit.Index].Count(c => c == '\n');
                    var to = text[..(hit.Index + hit.Length)].Count(c => c == '\n');
                    if (lines.Skip(from).Take(to - from + 1).Any(line => line.Contains(Exemption))) continue;
                    offenders.Add(shape + "  " + file.Substring(root.Length) + ":" + (from + 1) + "  " + lines[from].Trim()
                        + "  -> use TestBudget.Hang, or a Stopwatch deadline, or mark the line " + Exemption);
                }
        }
        Assert.Empty(offenders);
    }
}
