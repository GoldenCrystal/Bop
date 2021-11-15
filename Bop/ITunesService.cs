using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using iTunesLib;

namespace Bop;

public class ITunesService : IHostedService, IAsyncDisposable
{
	private enum ITunesMessage
	{
		/// <summary>Notifies when iTunes starts playing a track.</summary>
		Play,
		/// <summary>Notifies when iTunes stops playing a track.</summary>
		Stop,
		/// <summary>Notifies when information on the currently playing track has changed.</summary>
		TrackChanged,
		/// <summary>Notifies that iTunes will be quitting. COM objects may become unusable after this.</summary>
		Quit,
	}

	private static readonly UnboundedChannelOptions ChannelOptions = new()
	{
		AllowSynchronousContinuations = true,
		SingleReader = true,
		SingleWriter = false,
	};

	private readonly object _lock = new();
	private readonly string _albumArtTemporaryFileName = Path.GetTempFileName();
	private readonly BehaviorSubject<TrackInformation?> _trackUpdateSubject = new(null);
	private readonly ILogger<ITunesService> _logger;
	private Task? _runTask;
	private readonly CancellationTokenSource _cancellationTokenSource = new();

	public ITunesService(ILogger<ITunesService> logger)
	{
		_logger = logger;
	}

	public IObservable<TrackInformation?> TrackUpdates => _trackUpdateSubject;

	public async ValueTask DisposeAsync()
	{
		_cancellationTokenSource.Cancel();
		if (Interlocked.Exchange(ref _runTask, null) is not null and var task)
		{
			await task.ConfigureAwait(false);
		}
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			if (_runTask is not null || _cancellationTokenSource.IsCancellationRequested) throw new InvalidOperationException("The service was already started.");

			var tcs = new TaskCompletionSource();
			_runTask = RunAsync(tcs, _cancellationTokenSource.Token);
			return tcs.Task;
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			if (!_cancellationTokenSource.IsCancellationRequested && Interlocked.Exchange(ref _runTask, null) is not null and var task)
			{
				_cancellationTokenSource.Cancel();
				return task;
			}
			else
			{
				throw new InvalidOperationException("The service was already stoped.");
			}
		}
	}

	private async Task RunAsync(TaskCompletionSource startTaskCompletionSource, CancellationToken cancellationToken)
	{
		iTunesApp iTunesApp;
		try
		{
			iTunesApp = new();
		}
		catch (Exception ex)
		{
			startTaskCompletionSource.TrySetException(ExceptionDispatchInfo.SetCurrentStackTrace(new Exception("Failed to start the iTunes COM server.", ex)));
			return;
		}
		startTaskCompletionSource.TrySetResult();

		var channel = Channel.CreateUnbounded<(ITunesMessage Message, IITTrack? Track)>(ChannelOptions);
		var (reader, writer) = (channel.Reader, channel.Writer);

		void OnPlay(object track) => writer!.TryWrite((ITunesMessage.Play, (IITTrack)track));
		void OnStop(object track) => writer!.TryWrite((ITunesMessage.Stop, (IITTrack)track));
		void OnPlayingTrackChanged(object track) => writer!.TryWrite((ITunesMessage.TrackChanged, (IITTrack)track));
		void OnAboutToPromptUserToQuit() => writer!.TryWrite((ITunesMessage.Quit, null));
		void OnQuitting() => writer!.TryComplete();

		TrackInformation? currentTrackInformation = null;
		IITTrack? currentTrack = null;

		async Task UpdateCurrentTrack(IITTrack? track)
		{
			if (currentTrack is not null) Marshal.ReleaseComObject(currentTrack);

			if (track is not null)
			{
				currentTrack = track;

				AlbumArtInformation? albumArtInformation = null;
				if (track!.Artwork is { Count: > 0 })
				{
					var albumArt = track.Artwork[1];

					albumArt.SaveArtworkToFile(_albumArtTemporaryFileName);

					albumArtInformation = new AlbumArtInformation
					(
						albumArt.Format switch
						{
							ITArtworkFormat.ITArtworkFormatJPEG => "image/jpeg",
							ITArtworkFormat.ITArtworkFormatPNG => "image/png",
							ITArtworkFormat.ITArtworkFormatBMP => "image/bmp",
							_ => "application/octet-stream"
						},
						await File.ReadAllBytesAsync(_albumArtTemporaryFileName, cancellationToken)
					);
				}

				currentTrackInformation = new TrackInformation(track.Name, track.Album, track.Artist, albumArtInformation);

				_logger.LogInformation("Now playing: {ArtistName} - {TrackName} 🎶", currentTrackInformation.GetValueOrDefault().Name, currentTrackInformation.GetValueOrDefault().Artist);
			}
			else
			{
				currentTrack = null;
				currentTrackInformation = null;
				_logger.LogInformation("No track currently playing.");
			}

			_trackUpdateSubject.OnNext(currentTrackInformation);
		}

		// Detect if a track is already playing: If we can Pause/Stop the player, it should imply that a track is currently playing.
		// Otherwise, the CurrentTrack property would always return the current track if there is one, regardless of whether it is playing.
		iTunesApp.GetPlayerButtonsState(out _, out var playerButtonState, out _);
		if (playerButtonState == ITPlayButtonState.ITPlayButtonStatePauseEnabled || playerButtonState == ITPlayButtonState.ITPlayButtonStateStopEnabled)
		{
			_logger.LogInformation("A track is currently playing.");
			await UpdateCurrentTrack(iTunesApp.CurrentTrack);
		}
		else
		{
			_logger.LogInformation("No track is currently playing.");
		}

		try
		{
			iTunesApp.OnPlayerPlayEvent += OnPlay;
			iTunesApp.OnPlayerStopEvent += OnStop;
			iTunesApp.OnPlayerPlayingTrackChangedEvent += OnPlayingTrackChanged;
			iTunesApp.OnAboutToPromptUserToQuitEvent += OnAboutToPromptUserToQuit;
			iTunesApp.OnQuittingEvent += OnQuitting;

			await foreach (var (message, track) in reader.ReadAllAsync(cancellationToken))
			{
				switch (message)
				{
					case ITunesMessage.Play:
					case ITunesMessage.TrackChanged:
						await UpdateCurrentTrack(track);
						break;
					case ITunesMessage.Quit:
						_logger.LogWarning("iTunes is about to quit. Connection will be lost.");
						goto case ITunesMessage.Stop;
					case ITunesMessage.Stop:
						if (currentTrack is not null) Marshal.ReleaseComObject(currentTrack);
						if (track is not null) Marshal.ReleaseComObject(track);

						currentTrackInformation = null;
						currentTrack = null;
						_logger.LogInformation("Track stopped.");
						_trackUpdateSubject.OnNext(null);
						break;
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (currentTrackInformation is not null)
			{
				currentTrackInformation = null;
				if (currentTrack is not null)
				{
					Marshal.ReleaseComObject(currentTrack);
					currentTrack = null;
				}
				_trackUpdateSubject.OnNext(null);
			}

			// Ideally, we'd quit iTunes here, but there desn't seem to be a way to know who started iTunes.
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.Log(LogLevel.Error, ex, "An exception has occured when controlling iTunes.");
		}
		finally
		{
			if (currentTrack is not null)
			{
				Marshal.ReleaseComObject(currentTrack);
				currentTrack = null;
			}
			if (iTunesApp is not null)
			{
				try
				{
					iTunesApp.OnPlayerPlayEvent -= OnPlay;
					iTunesApp.OnPlayerStopEvent -= OnStop;
					iTunesApp.OnPlayerPlayingTrackChangedEvent -= OnPlayingTrackChanged;
					iTunesApp.OnAboutToPromptUserToQuitEvent -= OnAboutToPromptUserToQuit;
					iTunesApp.OnQuittingEvent -= OnQuitting;
				}
				catch { }
				Marshal.FinalReleaseComObject(iTunesApp);
				iTunesApp = null!;
			}
			_trackUpdateSubject.OnCompleted();
		}
	}
}
