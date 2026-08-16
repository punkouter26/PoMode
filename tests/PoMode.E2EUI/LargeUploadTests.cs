using System.Net.Http.Headers;
using System.Net.Http.Json;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("LargeUploadApp")]
public class LargeUploadTests(LargeUploadAppFixture app)
{
    [Fact]
    public async Task Upload_over_30mb_is_accepted_by_kestrel()
    {
        // ~35 MB of valid wav (silence): 8000 Hz * 2 bytes * ~2200 s
        var wav = TestAudio.MakeWav(seconds: 2200, sampleRate: 8000);
        Assert.True(wav.Length > 30_000_000);

        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl), Timeout = TimeSpan.FromMinutes(2) };
        var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { content, "file", "large.wav" } };

        var response = await http.PostAsync("/api/analysis", form);

        response.EnsureSuccessStatusCode(); // 413 before the fix
        var status = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(status);
    }
}
