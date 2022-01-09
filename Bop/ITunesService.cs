using System;
using System.Diagnostics;
using System.Net.Security;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Bop.Models;
using iTunesLib;
using Microsoft.Win32.SafeHandles;

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

	private enum TrackChangeSituation
	{
		/// <summary>Indicates that the iTunes COM server has just been initialized.</summary>
		Initialization,
		/// <summary>Indicates that the track started playing or information changed.</summary>
		TrackPlayOrChange,
		/// <summary>Indicates that the track stopped playing.</summary>
		TrackStop,
		/// <summary>Indicates that iTunes will quit.</summary>
		ITunesQuit,
		/// <summary>Indicates that the service is stopped.</summary>
		Dispose,
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
	// A timer used to trigger GC after track change. This seems to be the only way to somewhat guarantee that memory is freed timely.
	private readonly Timer _gcTimer = new Timer(_ => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced), null, Timeout.Infinite, Timeout.Infinite);

	public ITunesService(ILogger<ITunesService> logger)
	{
		_logger = logger;
	}

	public IObservable<TrackInformation?> TrackUpdates => _trackUpdateSubject;

	public ValueTask DisposeAsync() => DisposeAsync(false);

	private async ValueTask DisposeAsync(bool isStopping)
	{
		Task? runTask = null;
		lock (_lock)
		{
			if (!_cancellationTokenSource.IsCancellationRequested)
			{
				_cancellationTokenSource.Cancel();
				_trackUpdateSubject.OnCompleted();
				runTask = Interlocked.Exchange(ref _runTask, null);
			}
			else if (isStopping)
			{
				throw new InvalidOperationException("The service was already stoped.");
			}
		}
		if (runTask is not null)
		{
			await runTask;
		}
		await _gcTimer.DisposeAsync();
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

	public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync(true);

	private async Task RunAsync(TaskCompletionSource startTaskCompletionSource, CancellationToken cancellationToken)
	{
		// When quitting, iTunes will have a delay between showing a popup for 20sec before trying to exist, which will also take some time.
		// We'll give it 40sec to quit before waiting for a new instance to show up *or* reusing the previous one.
		const int ITunesQuitTimeout = 40_000;

		int sessionId;
		using (var process = Process.GetCurrentProcess())
		{
			sessionId = process.SessionId;
		}

		var iTunesStartTaskCompletionSource = startTaskCompletionSource;
		bool isFirstRun = true;
		SafeProcessHandle? iTunesProcess = null;
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var iTunesRunTask = RunITunesAsync(iTunesStartTaskCompletionSource, cancellationToken);
				// Handle errors occuring when starting iTunes (or more exactly connecting/starting the COM server of iTunes.exe)
				try
				{
					await iTunesStartTaskCompletionSource.Task;
				}
				catch (Exception ex)
				{
					if (isFirstRun)
					{
						// For the first run, the StartAsync method will fail, and as such, we don't expect to try to restart iTunes.
						return;
					}
					else
					{
						_logger.LogError(ex, "Failed to restart the iTunes server 💥");
						// Wait one minute in case of error.
						await Task.Delay(60 * 1000);
					}
				}
				if (iTunesProcess is not null && iTunesProcess.GetExitCode() is not null)
				{
					iTunesProcess.Dispose();
					iTunesProcess = null;
				}
				if ((iTunesProcess ??= ITunesProcessHelper.FindITunesProcess()) is null)
				{
					_logger.LogWarning("iTunes process was not found 🐛");
				}
				await iTunesRunTask;

				cancellationToken.ThrowIfCancellationRequested();

				// Wait for the iTunes process to close or not close.
				if (iTunesProcess is null)
				{
					await Task.Delay(ITunesQuitTimeout);
				}
				else
				{
					_logger.LogInformation("Waiting for the iTunes process to exit ⌛");

					using (var waitHandle = new ProcessWaitHandle(iTunesProcess))
					{
						if (await waitHandle.WaitAsync(ITunesQuitTimeout, cancellationToken))
						{
							_logger.LogInformation("iTunes process has exited 💀");
							iTunesProcess.Dispose();
							iTunesProcess = null;
						}
						else
						{
							_logger.LogWarning("iTunes process has not exited 👻");
						}
					}
				}

				if (iTunesProcess is null)
				{
					iTunesProcess = await ITunesProcessHelper.WaitForITunesProcessAsync(cancellationToken);
					_logger.LogInformation("Detected iTunes start 💡");
				}

				iTunesStartTaskCompletionSource = new();
				isFirstRun = false;
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			iTunesProcess?.Dispose();
		}
	}

	private async Task RunITunesAsync(TaskCompletionSource startTaskCompletionSource, CancellationToken cancellationToken)
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

		var channel = Channel.CreateUnbounded<(ITunesMessage Message, IITTrack? Track, ProgressingTime? Position)>(ChannelOptions);
		var (reader, writer) = (channel.Reader, channel.Writer);

		void OnPlay(object track) => writer!.TryWrite((ITunesMessage.Play, (IITTrack)track, iTunesApp.GetPlayerPositionInProgressingTime()));
		void OnStop(object track) => writer!.TryWrite((ITunesMessage.Stop, (IITTrack)track, iTunesApp.GetPlayerPositionInProgressingTime()));
		void OnPlayingTrackChanged(object track) => writer!.TryWrite((ITunesMessage.TrackChanged, (IITTrack)track, iTunesApp.GetPlayerPositionInProgressingTime()));
		void OnAboutToPromptUserToQuit() => writer!.TryWrite((ITunesMessage.Quit, null, null));
		void OnQuitting() => writer!.TryComplete();

		TrackInformation? currentTrackInformation = null;
		IITTrack? currentTrack = null;
		// Always keep the last known album art in memory in order to improve memory usage.
		// We will still end up with a few images in memory at the same time, but there will be less and they will be evicted faster.
		// The last album art handles the cases of pause/play and multiple songs from the same album played in sequence.
		AlbumArtInformation lastAlbumArt = default;

		async ValueTask UpdateCurrentTrack(IITTrack? track, ProgressingTime? position, TrackChangeSituation situation)
		{
			if (currentTrack is not null) Marshal.ReleaseComObject(currentTrack);

			if (track is not null)
			{
				currentTrack = track;

				AlbumArtInformation? albumArtInformation = null;
				if (track!.Artwork is { Count: > 0 } and var artwork)
				{
					try
					{
						var albumArt = artwork[1];

						try
						{
							albumArt.SaveArtworkToFile(_albumArtTemporaryFileName);

							var mediaType = albumArt.Format switch
							{
								ITArtworkFormat.ITArtworkFormatJPEG => "image/jpeg",
								ITArtworkFormat.ITArtworkFormatPNG => "image/png",
								ITArtworkFormat.ITArtworkFormatBMP => "image/bmp",
								_ => "application/octet-stream"
							};
							var imageData = await FileHelper.ReadToNativeMemoryAsync(_albumArtTemporaryFileName, cancellationToken);

							// Try to keep the same image data reference in memory.
							// That way we quickly release native memory when the player is paused/started.
							if (lastAlbumArt.ImageData is not null &&
								lastAlbumArt.MediaType == mediaType &&
								lastAlbumArt.ImageData.GetSpan().SequenceEqual(imageData.GetSpan()))
							{
								// Since the album art is inchanged, we can reuse the instance that was kept in memory and immediately release the native memory associated with the new instance.
								// Compared to the naive array-allocating method, which would rely on LOH, this should improve things quite a bit.
								// Objects are not in LOH anymore, and memory can be released on demand.
								// However due to the use of IObservable, published/other instances of MemoryManager<byte> would still have to be finalized in order for the memory to be released.
								((IDisposable)imageData).Dispose();
								albumArtInformation = lastAlbumArt;
							}
							else
							{
								albumArtInformation = new AlbumArtInformation(mediaType, imageData);
								lastAlbumArt = albumArtInformation.GetValueOrDefault();
							}
						}
						finally
						{
							Marshal.ReleaseComObject(albumArt);
						}
					}
					finally
					{
						Marshal.ReleaseComObject(artwork);
					}
				}

				int duration = track.Duration;

				currentTrackInformation = new TrackInformation(track.Name, track.Album, track.Artist, duration > 0 ? duration : null, position, albumArtInformation);

				_logger.LogInformation("Now playing: {ArtistName} - {TrackName} 🎶", currentTrackInformation.GetValueOrDefault().Name, currentTrackInformation.GetValueOrDefault().Artist);
			}
			else
			{
				currentTrack = null;
				currentTrackInformation = null;

				switch (situation)
				{
				case TrackChangeSituation.TrackStop:
					_logger.LogInformation("Track stopped ⏹️");
					break;
				case TrackChangeSituation.ITunesQuit:
					_logger.LogWarning("iTunes is about to quit. Connection will be lost ✂️");
					break;
				case TrackChangeSituation.Dispose:
					break;
				case TrackChangeSituation.Initialization:
				case TrackChangeSituation.TrackPlayOrChange:
				default:
					_logger.LogInformation("No track currently playing 🙉");
					break;
				}
			}

			_trackUpdateSubject.OnNext(currentTrackInformation);
		}

		// Detect if a track is already playing: If we can Pause/Stop the player, it should imply that a track is currently playing.
		// Otherwise, the CurrentTrack property would always return the current track if there is one, regardless of whether it is playing.
		iTunesApp.GetPlayerButtonsState(out _, out var playerButtonState, out _);
		await UpdateCurrentTrack
		(
			playerButtonState is ITPlayButtonState.ITPlayButtonStatePauseEnabled or ITPlayButtonState.ITPlayButtonStateStopEnabled ? iTunesApp.CurrentTrack : null,
			playerButtonState is ITPlayButtonState.ITPlayButtonStatePauseEnabled or ITPlayButtonState.ITPlayButtonStateStopEnabled ? iTunesApp.GetPlayerPositionInProgressingTime() : null,
			TrackChangeSituation.Initialization
		);

		try
		{
			iTunesApp.OnPlayerPlayEvent += OnPlay;
			iTunesApp.OnPlayerStopEvent += OnStop;
			iTunesApp.OnPlayerPlayingTrackChangedEvent += OnPlayingTrackChanged;
			iTunesApp.OnAboutToPromptUserToQuitEvent += OnAboutToPromptUserToQuit;
			iTunesApp.OnQuittingEvent += OnQuitting;

			await foreach (var (message, track, position) in reader.ReadAllAsync(cancellationToken))
			{
				switch (message)
				{
				case ITunesMessage.Play:
				case ITunesMessage.TrackChanged:
					await UpdateCurrentTrack(track, position, TrackChangeSituation.TrackPlayOrChange);
					_gcTimer.Change(1_000, Timeout.Infinite);
					break;
				case ITunesMessage.Stop:
					await UpdateCurrentTrack(null, null, TrackChangeSituation.TrackStop);
					if (track is not null) Marshal.ReleaseComObject(track);
					break;
				case ITunesMessage.Quit:
					await UpdateCurrentTrack(null, null, TrackChangeSituation.ITunesQuit);
					return;
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// NB: This may cause emission of duplicate "null" updates, but it should not be a serious problem.
			await UpdateCurrentTrack(null, null, TrackChangeSituation.Dispose);

			// Ideally, we'd quit iTunes here, but there desn't seem to be a way to know who started iTunes.
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.Log(LogLevel.Error, ex, "An exception has occured when controlling iTunes 💥");
		}
		finally
		{
			if (currentTrack is not null)
			{
				Marshal.ReleaseComObject(currentTrack);
				currentTrack = null;
			}
			if (lastAlbumArt.ImageData is not null)
			{
				((IDisposable)lastAlbumArt.ImageData).Dispose();
				lastAlbumArt = default;
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
				Marshal.ReleaseComObject(iTunesApp);
				iTunesApp = null!;
			}
		}
	}
}
