using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Watchscraper;

public class PokeFunction
{
    private readonly ILogger<PokeFunction> _logger;
    private readonly IConfiguration _config;

    public PokeFunction(ILogger<PokeFunction> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    [Function("Poke")]
    public async Task Run([TimerTrigger("0 * * * * *")] TimerInfo timer)
    {
        var urls = GetConfiguredUrls();
        if (urls.Count == 0)
        {
            _logger.LogWarning("No URLs configured in 'Urls'.");
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        try
        {
            foreach (var configuredUrl in urls)
            {
                await ProbeSingleUrlAsync(browser, configuredUrl);
            }
        }
        finally
        {
            await browser.CloseAsync();
        }
    }

    private async Task ProbeSingleUrlAsync(IBrowser browser, string configuredUrl)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var result = new
        {
            TimestampUtc = startedAt,
            UrlFromConfig = configuredUrl,
            FinalUrl = string.Empty,
            HttpStatus = (int?)null,
            Title = string.Empty,
            HtmlLength = 0,
            LinkCount = 0,
            VisibleTextSnippet = string.Empty,
            DurationMs = 0,
            ResultCategory = "unknown",
            Exception = string.Empty
        };

        try
        {
            var page = await browser.NewPageAsync();
            var timeout = _config.GetValue<int?>("PlaywrightTimeoutMs") ?? 30000;

            var response = await page.GotoAsync(configuredUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = timeout
            });

            var finalUrl = page.Url;
            var title = await page.TitleAsync() ?? string.Empty;
            var html = await page.ContentAsync() ?? string.Empty;
            var linkCount = await page.Locator("a").CountAsync();
            var visibleText = await page.TextContentAsync("body") ?? string.Empty;
            var snippet = CleanSnippet(visibleText);
            var durationMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

            result = new
            {
                TimestampUtc = startedAt,
                UrlFromConfig = configuredUrl,
                FinalUrl = finalUrl,
                HttpStatus = response?.Status,
                Title = title,
                HtmlLength = html.Length,
                LinkCount = linkCount,
                VisibleTextSnippet = snippet,
                DurationMs = durationMs,
                ResultCategory = DetectCategory(finalUrl, title, visibleText, response?.Status),
                Exception = string.Empty
            };
        }
        catch (Exception ex)
        {
            result = new
            {
                TimestampUtc = startedAt,
                UrlFromConfig = configuredUrl,
                FinalUrl = string.Empty,
                HttpStatus = (int?)null,
                Title = string.Empty,
                HtmlLength = 0,
                LinkCount = 0,
                VisibleTextSnippet = string.Empty,
                DurationMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                ResultCategory = "exception",
                Exception = ex.Message
            };
        }

        _logger.LogInformation("ProbeResult: {Result}", JsonSerializer.Serialize(result));
    }

    private List<string> GetConfiguredUrls()
    {
        var fromSection = _config.GetSection("Urls")
            .GetChildren()
            .Select(child => child?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();

        if (fromSection.Count > 0)
        {
            return fromSection;
        }

        var raw = _config["Urls"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        if (raw.TrimStart().StartsWith("["))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(raw);
                return parsed is null ? new List<string>() : parsed.ToList();
            }
            catch
            {
                // fall back to comma-separated parsing
            }
        }

        return raw
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();
    }

    private static string CleanSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ");

        while (cleaned.Contains("  "))
        {
            cleaned = cleaned.Replace("  ", " ");
        }

        return cleaned.Length > 180 ? cleaned.Substring(0, 180) : cleaned;
    }

    private static string DetectCategory(string finalUrl, string title, string visibleText, int? status)
    {
        var combined = (finalUrl + " " + title + " " + visibleText).ToLowerInvariant();

        if (status is >= 400 and < 500)
        {
            return "access denied";
        }

        if (combined.Contains("captcha") || combined.Contains("verify you are human") || combined.Contains("challenge"))
        {
            return "captcha";
        }

        if (combined.Contains("sign in") || combined.Contains("log in") || combined.Contains("login"))
        {
            return "login";
        }

        if (combined.Contains("access denied") || combined.Contains("forbidden"))
        {
            return "access denied";
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(visibleText))
        {
            return "empty";
        }

        if (string.IsNullOrWhiteSpace(visibleText) || visibleText.Trim().Length < 20)
        {
            return "empty";
        }

        return "normal";
    }
}
