using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DemoApi.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HealthEndpointsRemainAvailableWhenMongoIsNotConfigured()
    {
        var originalMongoUri = Environment.GetEnvironmentVariable("MONGODB_URI");
        Environment.SetEnvironmentVariable("MONGODB_URI", null);

        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var healthResponse = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            using var healthJson = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            Assert.Equal("Healthy", healthJson.RootElement.GetProperty("status").GetString());

            using var databaseResponse = await client.GetAsync("/health/db");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, databaseResponse.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MONGODB_URI", originalMongoUri);
        }
    }
}
