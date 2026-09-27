using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RoomBooking.Server.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace RoomBooking.ConcurrencyTests;

public class ConcurrencyTestWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _msSqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("StrongPassword123!")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _msSqlContainer.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            var descriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor != null) services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlServer(ConnectionString, o =>
                    o.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null));

            });
        });

        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("Seed:AdminEmail", "concurrency-admin@test.com");
        builder.UseSetting("Seed:AdminPassword", "Admin123!");
    }

    public async Task InitializeAsync()
    {
        await _msSqlContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _msSqlContainer.StopAsync();
        await base.DisposeAsync();
    }
}

[CollectionDefinition("ConcurrencyTests")]
public class ConcurrencyTestCollection : ICollectionFixture<ConcurrencyTestWebFactory>
{
}
