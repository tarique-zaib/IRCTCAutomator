using System.IO;
using System.Windows;
using IRCTCAutomator.Infrastructure.Browser;
using IRCTCAutomator.Infrastructure.Configuration;

namespace IRCTCAutomator.App;

public partial class MainWindow : Window
{
    private readonly BookingConfigurationLoader _loader = new();

    private readonly PlaywrightIrctcBrowserWorkflow _browser = new();

    private readonly string _configPath =
        Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "booking.json");

    public MainWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
            await LoadSummaryAsync();
    }

    // ============================================================
    // LOAD CONFIGURATION SUMMARY
    // ============================================================

    private async Task LoadSummaryAsync()
    {
        try
        {
            var configuration =
                await _loader.LoadAsync(
                    _configPath);

            ConfigSummary.Text =
                $"Journey: " +
                $"{configuration.Journey.From} → " +
                $"{configuration.Journey.To}\n" +

                $"Date: " +
                $"{configuration.Journey.JourneyDate:dd/MM/yyyy}\n" +

                $"Class: " +
                $"{configuration.Journey.Class} | " +

                $"Quota: " +
                $"{configuration.Journey.Quota}\n" +

                $"Preferred trains: " +
                $"{string.Join(
                    ", ",
                    configuration.TrainPreferences
                        .PreferredTrainNumbers)}\n" +

                $"Passengers: " +
                $"{configuration.Passengers.Count}";
        }
        catch (Exception ex)
        {
            ConfigSummary.Text =
                $"Configuration error:\n{ex.Message}";
        }
    }

    // ============================================================
    // START
    // ============================================================

    private async void StartButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;

        try
        {
            ConfigSummary.Text =
                "Starting IRCTC...\n\n" +
                "Preparing journey configuration...";

            var configuration =
                await _loader.LoadAsync(
                    _configPath);

            // ----------------------------------------------------
            // Open IRCTC
            // ----------------------------------------------------

            ConfigSummary.Text =
                "Opening IRCTC...";

            await _browser.OpenTrainSearchAsync();

            // ----------------------------------------------------
            // Prepare journey + search + locate train
            // ----------------------------------------------------

            ConfigSummary.Text =
                "Preparing journey...\n\n" +
                $"From: {configuration.Journey.From}\n" +
                $"To: {configuration.Journey.To}\n" +
                $"Date: {configuration.Journey.JourneyDate:dd/MM/yyyy}\n" +
                $"Class: {configuration.Journey.Class}";

            await _browser.PrepareJourneyAsync(
                configuration);

            // ----------------------------------------------------
            // Availability result
            // ----------------------------------------------------

            var result =
                _browser.LastAvailabilityResult;

            if (result is not null)
            {
                ConfigSummary.Text =
                    "TRAIN FOUND\n\n" +

                    $"Train: " +
                    $"{result.TrainNumber}\n\n" +

                    $"Class: " +
                    $"{result.ClassName}\n\n" +

                    $"Journey Date: " +
                    $"{result.JourneyDate:dd/MM/yyyy}\n\n" +

                    $"Availability: " +
                    $"{result.Availability}\n\n" +

                    $"Fare: " +
                    $"{result.Fare}\n\n" +

                    "Ready for the next user-controlled step.";
            }
            else
            {
                ConfigSummary.Text =
                    "Train was found, but availability " +
                    "could not be read.";
            }

            MessageBox.Show(
                result is null
                    ? "Train found, but availability could not be read."
                    : $"Train: {result.TrainNumber}\n" +
                      $"Class: {result.ClassName}\n" +
                      $"Availability: {result.Availability}\n" +
                      $"Fare: {result.Fare}",
                "IRCTC Availability",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ConfigSummary.Text =
                "ERROR\n\n" +
                ex.Message;

            MessageBox.Show(
                ex.ToString(),
                "IRCTC Automator Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    // ============================================================
    // CLOSE
    // ============================================================

    private async void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _browser.CloseAsync();

        Close();
    }
}