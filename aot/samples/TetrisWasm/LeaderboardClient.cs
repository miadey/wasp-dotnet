using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Tetris;

public sealed record ScoreEntry(string Username, long Score, int Lines, int Level, long AtMs);

[JsonSerializable(typeof(ScoreEntry))]
[JsonSerializable(typeof(ScoreEntry[]))]
[JsonSerializable(typeof(LeaderboardResponse))]
[JsonSerializable(typeof(SubmitScoreRequest))]
internal partial class TetrisJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

public sealed record LeaderboardResponse(ScoreEntry[] Scores, int? UserRank);

public sealed record SubmitScoreRequest(string Username, long Score, int Lines, int Level);

public sealed class LeaderboardClient(HttpClient http)
{
    public async Task<ScoreEntry[]> TopAsync(int limit = 10)
    {
        try
        {
            var url = $"/api/tetris/scores?limit={limit}";
            var resp = await http.GetFromJsonAsync(url, TetrisJsonContext.Default.LeaderboardResponse);
            return resp?.Scores ?? Array.Empty<ScoreEntry>();
        }
        catch
        {
            return Array.Empty<ScoreEntry>();
        }
    }

    public async Task SubmitAsync(string username, long score, int lines, int level)
    {
        try
        {
            var req = new SubmitScoreRequest(username, score, lines, level);
            await http.PostAsJsonAsync("/api/tetris/score", req, TetrisJsonContext.Default.SubmitScoreRequest);
        }
        catch { /* leaderboard submission is best-effort */ }
    }
}
