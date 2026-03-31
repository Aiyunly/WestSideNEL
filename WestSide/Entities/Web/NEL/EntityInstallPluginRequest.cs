using System.Text.Json.Serialization;

namespace WestSide.Entities.Web.NEL;

public class EntityInstallPluginRequest
{
	[JsonPropertyName("id")]
	public required string Id { get; set; }

	[JsonPropertyName("plugin")]
	public required EntityInstallPlugin Plugin { get; set; }
}
