using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GGs.E2ETests;

public class Prompt15Tests
{
    [Fact]
    public async Task Tweaks_Etag_IfMatch_Concurrency_Works()
    {
        await using var factory = new WebApplicationFactory<GGs.Server.Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "admin@ggs.local", password = "ChangeMe!123" });
        login.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString()!;

        var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create a tweak
        var create = await authed.PostAsJsonAsync("/api/v1/tweaks", new { Name = "Concurrency Test", Description = "A", CommandType = 2, ScriptContent = "Write-Host hi" });
        create.EnsureSuccessStatusCode();
        var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("tweak").GetProperty("id").GetGuid();

        // GET to retrieve ETag
        var get1 = await authed.GetAsync($"/api/v1/tweaks/{id}");
        get1.EnsureSuccessStatusCode();
        Assert.True(get1.Headers.ETag != null);
        var etag1 = get1.Headers.ETag!.Tag;

        // Update with correct If-Match
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tweaks");
        req.Headers.TryAddWithoutValidation("If-Match", etag1);
        req.Content = JsonContent.Create(new { Id = id, Name = "Concurrency Test Updated", Description = "B", CommandType = 2, ScriptContent = "Write-Host hi2" });
        var putOk = await authed.SendAsync(req);
        putOk.EnsureSuccessStatusCode();
        Assert.True(putOk.Headers.ETag != null);
        var etag2 = putOk.Headers.ETag!.Tag;
        Assert.NotEqual(etag1, etag2);

        // Try update with stale ETag -> 412
        var stale = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tweaks");
        stale.Headers.TryAddWithoutValidation("If-Match", etag1);
        stale.Content = JsonContent.Create(new { Id = id, Name = "Stale", Description = "C", CommandType = 2, ScriptContent = "Write-Host hi3" });
        var putStale = await authed.SendAsync(stale);
        Assert.Equal(System.Net.HttpStatusCode.PreconditionFailed, putStale.StatusCode);

        // Try update without If-Match -> 428
        var noPre = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tweaks");
        noPre.Content = JsonContent.Create(new { Id = id, Name = "No If-Match", Description = "D", CommandType = 2, ScriptContent = "hi" });
        var putNo = await authed.SendAsync(noPre);
        Assert.Equal((System.Net.HttpStatusCode)428, putNo.StatusCode);
    }
}

