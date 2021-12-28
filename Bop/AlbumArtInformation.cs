using System.Buffers;
using System.Text.Json.Serialization;

namespace Bop;

[JsonConverter(typeof(AlbumArtInformationConverter))]
public record struct AlbumArtInformation(string MediaType, MemoryManager<byte> ImageData);
