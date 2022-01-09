namespace Bop.Models;

public record struct ImmediateTrackInformation(string Name, string Album, string Artist, int? DurationInSeconds, int? PositionInMilliseconds, AlbumArtInformation? AlbumArt);
