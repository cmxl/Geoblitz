using Xunit;

namespace HighPerf.Api.Tests;

[Collection("api")]
public class HealthTests(ApiFixture fixture)
{
    [Fact]
    public async Task Healthz_Returns200Ok()
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
