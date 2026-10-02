using System.Text.Json;
using IRCTCAutomator.Models;

namespace IRCTCAutomator.Infrastructure.Configuration;

public sealed class BookingConfigurationLoader
{
    public async Task<BookingConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Booking configuration was not found.", path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BookingConfiguration>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
            ?? throw new InvalidDataException("booking.json is empty or invalid.");
    }
}
