namespace Bop.Models;

public record struct TrackInformation(string Name, string Album, string Artist, int? DurationInSeconds, ProgressingTime? Position, AlbumArtInformation? AlbumArt)
{
	public ImmediateTrackInformation ToImmediate()
		=> new(Name, Album, Artist, DurationInSeconds, (int?)(long?)(Position?.GetRebasedTimeSpan())?.TotalMilliseconds, AlbumArt);
}
