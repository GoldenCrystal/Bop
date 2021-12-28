using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bop;

public sealed class AlbumArtInformationConverter : JsonConverter<AlbumArtInformation>
{
	private static readonly JsonEncodedText MediaTypePropertyName = JsonEncodedText.Encode("mediaType");
	private static readonly JsonEncodedText ImageDataPropertyName = JsonEncodedText.Encode("imageData");

	public override AlbumArtInformation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotImplementedException();

	public override void Write(Utf8JsonWriter writer, AlbumArtInformation value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		writer.WriteString(MediaTypePropertyName, value.MediaType);
		using (value.ImageData.Pin())
		{
			writer.WriteBase64String(ImageDataPropertyName, value.ImageData.Memory.Span);
		}
		writer.WriteEndObject();
	}
}
