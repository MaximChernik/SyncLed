using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SyncLed.Avalonia.Services;

/// <summary>Результат проверки обновлений в GitHub Releases.</summary>
/// <param name="Success">Проверка дошла до GitHub (даже если обновлений нет).</param>
/// <param name="Latest">Версия нового релиза, если она новее установленной.</param>
/// <param name="Url">Ссылка на страницу релиза.</param>
/// <param name="Error">Причина, если проверка не удалась.</param>
public sealed record UpdateCheckResult(bool Success, string? Latest, string? Url, string? Error);

/// <summary>
/// Проверка обновлений через GitHub Releases репозитория MaximChernik/SyncLed.
/// Работает анонимно: репозиторий должен быть публичным, токен не используется.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/MaximChernik/SyncLed/releases/latest";

    /// <summary>Страница релизов для кнопки «Открыть».</summary>
    public const string ReleasesPage = "https://github.com/MaximChernik/SyncLed/releases";

    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SyncLed/" + currentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);

            string tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString() ?? ""
                : "";
            string url = doc.RootElement.TryGetProperty("html_url", out var urlProp)
                ? urlProp.GetString() ?? ReleasesPage
                : ReleasesPage;

            string latest = Normalize(tag);
            if (latest.Length == 0)
                return new UpdateCheckResult(false, null, null, "релизы не опубликованы");

            return Compare(latest, Normalize(currentVersion)) > 0
                ? new UpdateCheckResult(true, latest, url, null)
                : new UpdateCheckResult(true, null, null, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(false, null, null, "релизы не опубликованы");
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(false, null, null, "нет связи с GitHub");
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult(false, null, null, "GitHub не ответил вовремя");
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(false, null, null, "неожиданный ответ GitHub");
        }
    }

    /// <summary>"v1.2.3" → "1.2.3"; лишние суффиксы вида "-beta" отбрасываются.</summary>
    private static string Normalize(string? value)
    {
        string v = (value ?? "").Trim().TrimStart('v', 'V');
        int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        return cut >= 0 ? v[..cut] : v;
    }

    /// <summary>Сравнение версий: > 0, если <paramref name="a"/> новее <paramref name="b"/>.</summary>
    private static int Compare(string a, string b)
    {
        int[] pa = Parse(a);
        int[] pb = Parse(b);
        for (int i = 0; i < pa.Length; i++)
        {
            int diff = pa[i].CompareTo(pb[i]);
            if (diff != 0) return diff;
        }
        return 0;
    }

    private static int[] Parse(string version)
    {
        string[] parts = version.Split('.', ' ');
        var result = new int[3];
        for (int i = 0; i < result.Length && i < parts.Length; i++)
            int.TryParse(parts[i], out result[i]);
        return result;
    }
}
