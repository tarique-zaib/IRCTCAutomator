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
        Loaded += async (_, _) => await LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        try
        {
            var c =
                await _loader.LoadAsync(_configPath);

            ConfigSummary.Text =
                $"Journey: {c.Journey.From} → {c.Journey.To}\n" +
                $"Date: {c.Journey.JourneyDate}\n" +
                $"Class: {c.Journey.Class} | Quota: {c.Journey.Quota}\n" +
                $"Preferred trains: " +
                $"{string.Join(", ", c.TrainPreferences.PreferredTrainNumbers)}\n" +
                $"Passengers: {c.Passengers.Count}";
        }
        catch (Exception ex)
        {
            ConfigSummary.Text =
                $"Configuration error: {ex.Message}";
        }
    }

    private async void StartButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;

        try
        {
            var c =
                await _loader.LoadAsync(_configPath);

            // ====================================================
            // 1. OPEN IRCTC
            // ====================================================

            await _browser.OpenTrainSearchAsync();

            // ====================================================
            // 2. LOGIN FIRST
            // ====================================================

            await _browser.LoginFirstAsync();

            // ====================================================
            // 3. ONLY AFTER LOGIN, PREPARE JOURNEY
            // ====================================================

            await _browser.PrepareJourneyAsync(c);

            // ====================================================
            // 4. PASSENGERS
            // ====================================================

            await _browser.PreparePassengersAsync(c);

            var result =
                _browser.LastAvailabilityResult;

            if (result is not null)
            {
                //MessageBox.Show(
                //    $"Train: {result.TrainNumber}\n" +
                //    $"Class: {result.ClassName}\n" +
                //    $"Availability: {result.Availability}\n" +
                //    $"Fare: {result.Fare}",
                //    "IRCTC Availability",
                //    MessageBoxButton.OK,
                //    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "IRCTC preparation completed.",
                    "IRCTC Automator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
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

    private async void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _browser.CloseAsync();
        Close();
    }
}
