using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bop;
using Serilog;

Console.OutputEncoding = Encoding.Unicode;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog
(
	(context, services, configuration) => configuration
		.ReadFrom.Configuration(context.Configuration)
		.ReadFrom.Services(services)
);

builder.Services
	.AddSingleton
	(
		new JsonSerializerOptions()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		}
	)
	.AddSingleton<ITunesService>()
	.AddHostedService(services => services.GetRequiredService<ITunesService>())
	.AddSingleton(services => services.GetRequiredService<ITunesService>().TrackUpdates);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
}

app.UseSerilogRequestLogging();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.MapGet
(
	"/updates",
	async (HttpContext context, IObservable<TrackInformation?> observable, JsonSerializerOptions jsonSerializerOptions) =>
	{
		var ct = context.RequestAborted;

		if (context.WebSockets.IsWebSocketRequest)
		{
			using var ws = await context.WebSockets.AcceptWebSocketAsync();

			try
			{
				await foreach (var trackInfo in observable.ToAsyncEnumerable())
				{
					await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(trackInfo, jsonSerializerOptions), WebSocketMessageType.Text, true, ct);
				}
			}
			catch (OperationCanceledException)
			{
			}
		}
		else
		{
			context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
		}
	}
);

app.Run();
