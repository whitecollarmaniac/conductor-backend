using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Json
{
    /// <summary>
    /// Serialises <see cref="IPAddress"/> as its plain string representation and
    /// deserialises from the same format.  This prevents System.Text.Json from
    /// touching the problematic <c>ScopeId</c> property that throws on IPv4
    /// addresses.
    /// </summary>
    public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
    {
        public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return IPAddress.None;
            return IPAddress.TryParse(s, out var ip) ? ip : IPAddress.None;
        }

        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value?.ToString() ?? "");
        }
    }
}
