using System.Text.Json;

namespace Code2Obsidian.Tests.TestSupport;

public static class FakeClaudeJson
{
    public static string Success(string result, long? inputTokens = null, long? outputTokens = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["result"] = result,
            ["session_id"] = "sess_test_123",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["duration_ms"] = 1234,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = inputTokens,
                    ["output_tokens"] = outputTokens
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    public static string MissingResult() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["session_id"] = "sess_missing_result"
    });
}
