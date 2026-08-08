using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Geoblitz.Api.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
