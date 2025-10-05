using System; // Needed for DateTime
using Xunit;
using System.Linq;

public class DiffEngineTests
{
    private static readonly string[] Keys = new[]{"instrumentId","intervalSeconds","openTimeUtc","eventType"};

    [Fact]
    public void Diff_Identical_ReturnsNoDiff()
    {
        var bars = new[]{
            ("INST1", DateTime.UtcNow.AddMinutes(-2).ToString("O"), 100.0,101.0,99.5,100.5,5.5,60),
            ("INST2", DateTime.UtcNow.AddMinutes(-1).ToString("O"), 200.0,201.0,199.5,200.5,7.0,60),
        };
        var a = JournalTestHelper.CreateBarJournal(bars);
        var b = JournalTestHelper.CreateBarJournal(bars);
        var outcome = DiffEngine.Run(a,b, Keys, reportDuplicates:false);
        Assert.False(outcome.HasDiff);
        Assert.Empty(outcome.OnlyInA);
        Assert.Empty(outcome.OnlyInB);
        Assert.Empty(outcome.PayloadMismatch);
    }

    [Fact]
    public void Diff_SingleFieldChange_ReportsPayloadMismatch()
    {
        var start = DateTime.UtcNow.AddMinutes(-3).ToString("O");
        var baseBars = new[]{ ("INST1", start, 100.0,101.0,99.5,100.5,5.5,60) };
        var a = JournalTestHelper.CreateBarJournal(baseBars);
        // modify close
        var changedBars = new[]{ ("INST1", start, 100.0,101.0,99.5,100.7,5.5,60) };
        var b = JournalTestHelper.CreateBarJournal(changedBars);
        var outcome = DiffEngine.Run(a,b, Keys, reportDuplicates:false);
        Assert.True(outcome.HasDiff);
        Assert.Contains(outcome.PayloadMismatch, k => k.Contains("INST1"));
    }

    [Fact]
    public void Diff_MissingRow_ReportsOnlyInA()
    {
        var now = DateTime.UtcNow;
        var barsA = new[]{
            ("INST1", now.AddMinutes(-5).ToString("O"), 100.0,101.0,99.5,100.5,5.5,60),
            ("INST1", now.AddMinutes(-4).ToString("O"), 101.0,102.0,100.5,101.5,4.0,60)
        };
        var barsB = barsA.Take(1).ToArray();
        var a = JournalTestHelper.CreateBarJournal(barsA);
        var b = JournalTestHelper.CreateBarJournal(barsB);
        var outcome = DiffEngine.Run(a,b, Keys, reportDuplicates:false);
        Assert.True(outcome.HasDiff);
        Assert.Contains(outcome.OnlyInA, k => k.Contains(barsA[1].Item2));
    }

    [Fact]
    public void Diff_DuplicateCompositeKey_DetectedWhenReportingDuplicates()
    {
        var start = DateTime.UtcNow.AddMinutes(-10).ToString("O");
        var dup = JournalTestHelper.DuplicateBarJournal("INSTX", start, 60);
        // Compare file to itself to trigger duplicate detection
        var outcome = DiffEngine.Run(dup, dup, Keys, reportDuplicates:true);
        Assert.True(outcome.HasDiff); // duplicates recorded
        Assert.Contains(outcome.OnlyInA, k => k.StartsWith("DUP(A):"));
    }
}
