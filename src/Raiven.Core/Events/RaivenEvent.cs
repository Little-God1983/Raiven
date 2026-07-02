using System.Text.Json;

namespace Raiven.Core.Events;

public sealed record RaivenEvent(string Source, string Type, JsonElement Payload);
