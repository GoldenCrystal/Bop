using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bop;
using Bop.Models;
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
	.AddSingleton
	(
		new PipeOptions
		(
			// Ideally, we'd use a pool of 4kb blocks. However, due to the way Utf8JsonWriter currently works, we'll be better with much larger blocks of memory.
			// Most album art seem to fit in about less than 300kB, so we'll use 512kB blocks. This will sadly not help reducing memory usage, but it should make memory usage much more consistent.
			// TODO: Reduce this to 4kB blocks once Utf8ByteWriter is eventually improved.
			pool: new FixedLengthMemoryPool(512 * 1024, 1),
			readerScheduler: PipeScheduler.Inline,
			writerScheduler: PipeScheduler.Inline,
			pauseWriterThreshold: 8192,
			resumeWriterThreshold: 4096,
			minimumSegmentSize: 4096,
			useSynchronizationContext: false
		)
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
	async (HttpContext context, IObservable<TrackInformation?> observable, PipeOptions pipeOptions, JsonSerializerOptions jsonSerializerOptions) =>
	{
		var ct = context.RequestAborted;

		if (context.WebSockets.IsWebSocketRequest)
		{
			using var ws = await context.WebSockets.AcceptWebSocketAsync();
			using var messageStream = new MemoryStream(16384);
			var pipe = new Pipe(pipeOptions);
			var jsonWriter = new Utf8JsonWriter(pipe.Writer, new JsonWriterOptions { Encoder = jsonSerializerOptions.Encoder, Indented = jsonSerializerOptions.WriteIndented });

			static async ValueTask SendMessage(WebSocket ws, PipeReader reader, CancellationToken cancellationToken)
			{
				while (true)
				{
					var result = await reader.ReadAsync(cancellationToken);
					var buffer = result.Buffer;

					if (buffer.IsSingleSegment)
					{
						await ws.SendAsync(buffer.First, WebSocketMessageType.Text, result.IsCompleted, cancellationToken);
					}
					else
					{
						var next = buffer.Start;

						while (buffer.TryGet(ref next, out var memory, true))
						{
							await ws.SendAsync(memory, WebSocketMessageType.Text, result.IsCompleted && next.GetObject() is null, cancellationToken);
						}
					}

					reader.AdvanceTo(buffer.End);

					if (result.IsCompleted)
					{
						await reader.CompleteAsync();
						return;
					}
				}
			}

			try
			{
				await foreach (var trackInfo in observable.ToAsyncEnumerable())
				{
					var sendTask = SendMessage(ws, pipe.Reader, ct);

					JsonSerializer.Serialize(jsonWriter, trackInfo?.ToImmediate(), jsonSerializerOptions);
					await pipe.Writer.CompleteAsync();
					await sendTask;
					jsonWriter.Reset();
					pipe.Reset();
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
