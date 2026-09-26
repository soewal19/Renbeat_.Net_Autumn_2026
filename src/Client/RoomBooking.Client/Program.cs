using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RoomBooking.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<IAuthClient, AuthClient>();
builder.Services.AddScoped<IResourceClient, ResourceClient>();
builder.Services.AddScoped<IScheduleClient, ScheduleClient>();
builder.Services.AddScoped<IBookingClient, BookingClient>();
builder.Services.AddScoped<IAdminClient, AdminClient>();
builder.Services.AddScoped<IDirectoryClient, DirectoryClient>();
builder.Services.AddScoped<IScheduleRealtimeClient, ScheduleRealtimeClient>();

await builder.Build().RunAsync();
