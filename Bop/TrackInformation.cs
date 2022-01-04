namespace Bop;

public record struct TrackInformation(string Name, string Album, string Artist, int? DurationInSeconds, int? PositionInMilliseconds, AlbumArtInformation? AlbumArt);
