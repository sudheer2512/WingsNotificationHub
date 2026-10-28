using System.Text.Json;

namespace Wings.NotificationHub.WinForms.Sample;

/// <summary>Settings from appsettings.json next to the executable; WINGS_SAMPLE_* environment variables override them.</summary>
public sealed record SampleSettings(string HubBaseUrl, string BackendBaseUrl, string UserId, string? WebBaseUrl)
{
    public static SampleSettings Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var s = File.Exists(file)
            ? JsonSerializer.Deserialize<SampleSettings>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;
        s ??= new SampleSettings("http://localhost:5480", "http://localhost:5492", "u-1001", null);
        return s with
        {
            HubBaseUrl = Environment.GetEnvironmentVariable("WINGS_SAMPLE_HUB_URL") ?? s.HubBaseUrl,
            BackendBaseUrl = Environment.GetEnvironmentVariable("WINGS_SAMPLE_BACKEND_URL") ?? s.BackendBaseUrl,
            UserId = Environment.GetEnvironmentVariable("WINGS_SAMPLE_USER_ID") ?? s.UserId,
            WebBaseUrl = Environment.GetEnvironmentVariable("WINGS_SAMPLE_WEB_URL") ?? s.WebBaseUrl,
        };
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(SampleSettings.Load()));
    }
}
