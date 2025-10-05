using System.Text.Json;
using TiYf.Engine.Core;

namespace TiYf.Engine.Tests;

public class JournalRoundtripTests
{
    private sealed class InMemoryJournal : IJournalWriter
    {
        public readonly List<string> Lines = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
        {
            Lines.Add(evt.ToCsvLine());
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EscapingAndParsingRoundtrip()
    {
        var journal = new InMemoryJournal();
    var payload = JsonDocument.Parse("{\"text\":\"value,with,commas\",\"quote\":\"He said \\\"Hi\\\"\"}").RootElement;
        await journal.AppendAsync(new JournalEvent(1, DateTime.UtcNow, "TEST,EVENT", payload));
        var line = journal.Lines.Single();
        // Simple parse: split respecting quotes
        var parsed = ParseCsv(line);
        Assert.Equal("1", parsed[0]);
        Assert.Contains("TEST,EVENT", parsed[2]);
    // Extract JSON part (4th column) and parse
    var payloadJson = parsed[3].Trim('"');
    using var parsedDoc = JsonDocument.Parse(payloadJson);
    Assert.Equal("value,with,commas", parsedDoc.RootElement.GetProperty("text").GetString());
    Assert.Equal("He said \"Hi\"", parsedDoc.RootElement.GetProperty("quote").GetString());
    }

    private static List<string> ParseCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i=0; i<line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i+1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                continue;
            }
            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString()); sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}