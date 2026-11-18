using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);
var envFile = Path.Combine(builder.Environment.ContentRootPath, ".env");
var key = builder.Configuration["ELEVENLABS_API_KEY"];
if (string.IsNullOrWhiteSpace(key) && File.Exists(envFile))
{
    foreach (var line in File.ReadLines(envFile))
    {
        var pair = line.Split('=', 2);
        if (pair.Length == 2 && pair[0].Trim() is "API_KEY" or "ELEVENLABS_API_KEY")
        {
            key = pair[1].Trim().Trim('"', '\'');
            break;
        }
    }
}

if (string.IsNullOrWhiteSpace(key))
    throw new InvalidOperationException("Set ELEVENLABS_API_KEY or API_KEY in .env.");

builder.Services.AddHttpClient("ElevenLabs", client =>
{
    client.BaseAddress = new Uri("https://api.elevenlabs.io/");
    client.DefaultRequestHeaders.Add("xi-api-key", key);
});
builder.Services.AddMemoryCache();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var languages = new Dictionary<string, string>
{
    ["auto"] = "Авто", ["ru"] = "Русский", ["en"] = "Английский",
    ["de"] = "Немецкий", ["es"] = "Испанский", ["fr"] = "Французский",
    ["it"] = "Итальянский", ["pt"] = "Португальский", ["pl"] = "Польский",
    ["uk"] = "Украинский", ["tr"] = "Турецкий", ["ja"] = "Японский",
    ["ko"] = "Корейский", ["zh"] = "Китайский", ["fil"] = "Филиппинский"
};
var formats = new Dictionary<string, string>
{
    ["mp3_22050_32"] = "Экономное · 32 кбит/с",
    ["mp3_44100_64"] = "Стандартное · 64 кбит/с",
    ["mp3_44100_128"] = "Высокое · 128 кбит/с"
};

app.MapGet("/api/options", () => Results.Ok(new { languages, formats }));

app.MapGet("/api/voices", async (IHttpClientFactory factory, CancellationToken ct) =>
{
    using var client = factory.CreateClient("ElevenLabs");
    var voices = new List<Voice>();
    string? cursor = null;
    try
    {
        do
        {
            var path = "v2/voices?page_size=100";
            if (cursor is not null)
                path += "&next_page_token=" + Uri.EscapeDataString(cursor);
            using var response = await client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 401)
                {
                    using var error = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
                    if (error is not null && error.RootElement.TryGetProperty("detail", out var detail)
                        && detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("status", out var status)
                        && status.GetString() == "missing_permissions")
                        return Results.Ok(new Voice[]
                        {
                            new("pNInz6obpgDQGcFmaJgB", "Adam"),
                            new("JBFqnCBsd6RMkjVDRZzb", "George")
                        });
                }
                return ApiError(response, "загрузить голоса");
            }

            using var page = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
            var root = page!.RootElement;
            foreach (var voice in root.GetProperty("voices").EnumerateArray())
                voices.Add(new(voice.GetProperty("voice_id").GetString()!, voice.GetProperty("name").GetString()!));
            cursor = root.GetProperty("has_more").GetBoolean()
                && root.TryGetProperty("next_page_token", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { error = "Не удалось подключиться к ElevenLabs." }, statusCode: 502);
    }
    return Results.Ok(voices.OrderBy(voice => voice.Name));
});

app.MapPost("/api/speech", (Speech input, IMemoryCache cache) =>
{
    var text = input.Text?.Trim();
    if (string.IsNullOrEmpty(text) || text.Length > 5000 ||
        !Regex.IsMatch(input.Voice ?? "", @"\A[A-Za-z0-9_-]{1,128}\z") ||
        !languages.ContainsKey(input.Language ?? "") || !formats.ContainsKey(input.Format ?? ""))
        return Results.BadRequest(new { error = "Проверьте текст и настройки озвучивания." });

    var id = Guid.NewGuid();
    cache.Set(id, input with { Text = text }, TimeSpan.FromMinutes(2));
    return Results.Ok(new { url = $"/api/speech/{id}" });
});

app.MapGet("/api/speech/{id:guid}", async (Guid id, HttpContext context,
    IMemoryCache cache, IHttpClientFactory factory, CancellationToken ct) =>
{
    Speech? input;
    lock (cache)
    {
        if (!cache.TryGetValue(id, out input) || input is null)
        {
            context.Response.StatusCode = 404;
            return;
        }
        cache.Remove(id);
    }

    using var client = factory.CreateClient("ElevenLabs");
    using var request = new HttpRequestMessage(HttpMethod.Post,
        $"v1/text-to-speech/{input.Voice}/stream?output_format={input.Format}");
    request.Content = JsonContent.Create(new
    {
        text = input.Text,
        model_id = "eleven_flash_v2_5",
        language_code = input.Language == "auto" ? null : input.Language
    });

    try
    {
        using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!upstream.IsSuccessStatusCode)
        {
            await ApiError(upstream, "синтезировать речь").ExecuteAsync(context);
            return;
        }
        context.Response.ContentType = "audio/mpeg";
        context.Response.Headers.CacheControl = "no-store";
        await using var audio = await upstream.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16384];
        int bytesRead;
        while ((bytesRead = await audio.ReadAsync(buffer, ct)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }
    catch (HttpRequestException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 502;
    }
});

app.Run();

static IResult ApiError(HttpResponseMessage response, string operation) =>
    Results.Json(new { error = $"ElevenLabs не смог {operation} (HTTP {(int)response.StatusCode})." },
        statusCode: 502);

record Voice(string Id, string Name);
record Speech(string? Text, string? Voice, string? Language, string? Format);
