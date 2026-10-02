namespace IRCTCAutomator.Models;

public sealed class BookingConfiguration
{
    public JourneyConfiguration Journey { get; set; } = new();
    public TrainPreferences TrainPreferences { get; set; } = new();
    public List<Passenger> Passengers { get; set; } = [];
    public BookingOptions Options { get; set; } = new();
}

public sealed class JourneyConfiguration
{
    public string From { get; set; } = "GZB";
    public string To { get; set; } = "HYB";
    public string JourneyDate { get; set; } = "2026-10-15";
    public string Quota { get; set; } = "GENERAL";
    public string Class { get; set; } = "3A";
}

public sealed class TrainPreferences
{
    public List<string> PreferredTrainNumbers { get; set; } = [];
    public bool SelectFirstAvailable { get; set; } = true;
}

public sealed class Passenger
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Gender { get; set; } = "";
    public string? BerthPreference { get; set; }
}

public sealed class BookingOptions
{
    public bool AutoFillJourney { get; set; } = true;
    public bool AutoSelectTrain { get; set; } = true;
    public bool AutoFillPassengers { get; set; } = true;
    public bool WaitForUserConfirmation { get; set; } = true;
}
