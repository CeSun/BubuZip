using System.Text.Json.Serialization;

namespace BubuZip.Desktop;

// The PowerShell picker emits a UTF-8 JSON string array. Generate its metadata at build time
// so path decoding never needs reflection or runtime code generation in NativeAOT.
[JsonSerializable(typeof(string[]))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class WindowsDialogJsonContext : JsonSerializerContext
{
}
