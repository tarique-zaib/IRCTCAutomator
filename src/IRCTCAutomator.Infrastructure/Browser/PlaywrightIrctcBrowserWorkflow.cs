using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IRCTCAutomator.Core;
using IRCTCAutomator.Models;
using Microsoft.Playwright;
using System.IO;

namespace IRCTCAutomator.Infrastructure.Browser;

public sealed class PlaywrightIrctcBrowserWorkflow : IIrctcBrowserWorkflow
{
    private const string TrainSearchUrl =
        "https://www.irctc.co.in/nget/train-search";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public TrainAvailabilityResult? LastAvailabilityResult
    {
        get;
        private set;
    }

    // ============================================================
    // OPEN IRCTC
    // ============================================================

    public async Task OpenTrainSearchAsync(
    CancellationToken cancellationToken = default)
    {
        _playwright = await Playwright.CreateAsync();

        var chromePath = FindChromePath();

        if (string.IsNullOrWhiteSpace(chromePath))
        {
            throw new FileNotFoundException(
                "Google Chrome was not found on this computer.");
        }

        Console.WriteLine(
            $"Starting Google Chrome for IRCTC: {chromePath}");

        _browser = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = false,
                ExecutablePath = chromePath
            });

        // Keep the isolated context.
        // We do not reuse or copy protected authentication state.
        var context =
            await _browser.NewContextAsync();

        _page =
            await context.NewPageAsync();

        ConfigureSafeNetworkDiagnostics(_page);

        await _page.GotoAsync(
            TrainSearchUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            });

        await _page.WaitForTimeoutAsync(1500);

        await SelectEnglishAsync();
    }

    // ============================================================
    // SAFE NETWORK DIAGNOSTICS
    //
    // Records only method, URL, status, resource type, and timing.
    // Never records request/response bodies, headers, cookies,
    // Authorization values, username, password, CAPTCHA, or OTP.
    // ============================================================

    private void ConfigureSafeNetworkDiagnostics(IPage page)
    {
        var logsDirectory =
            Path.Combine(
                AppContext.BaseDirectory,
                "logs");

        Directory.CreateDirectory(logsDirectory);

        var logPath =
            Path.Combine(
                logsDirectory,
                "irctc-network-diagnostics.txt");

        try
        {
            File.WriteAllText(
                logPath,
                "IRCTC NETWORK DIAGNOSTICS\r\n" +
                "==========================\r\n" +
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\r\n\r\n");
        }
        catch
        {
            // Diagnostics must never break the booking preparation flow.
        }

        page.Request += (_, request) =>
        {
            try
            {
                var line =
                    $"REQUEST | {DateTime.Now:HH:mm:ss.fff} | " +
                    $"{request.Method} | {SanitizeDiagnosticUrl(request.Url)} | " +
                    $"{request.ResourceType}" +
                    Environment.NewLine;

                File.AppendAllText(logPath, line);
            }
            catch
            {
            }
        };

        page.Response += (_, response) =>
        {
            try
            {
                var status = response.Status;

                // Log all responses, but make 4xx/5xx responses especially
                // obvious so the endpoint responsible for a failure is easy
                // to identify.
                var marker =
                    status >= 400
                        ? "ERROR"
                        : "RESPONSE";

                var line =
                    $"{marker} | {DateTime.Now:HH:mm:ss.fff} | " +
                    $"{status} | {response.Request.Method} | " +
                    $"{SanitizeDiagnosticUrl(response.Url)} | " +
                    $"{response.Request.ResourceType}" +
                    Environment.NewLine;

                File.AppendAllText(logPath, line);
            }
            catch
            {
            }
        };

        page.RequestFailed += (_, request) =>
        {
            try
            {
                var failure =
                    request.Failure ?? "unknown";

                var line =
                    $"FAILED | {DateTime.Now:HH:mm:ss.fff} | " +
                    $"{request.Method} | {SanitizeDiagnosticUrl(request.Url)} | " +
                    $"{request.ResourceType} | {failure}" +
                    Environment.NewLine;

                File.AppendAllText(logPath, line);
            }
            catch
            {
            }
        };

        Console.WriteLine(
            $"IRCTC safe network diagnostics: {logPath}");
    }

    private static string SanitizeDiagnosticUrl(string url)
    {
        try
        {
            var uri = new Uri(url);

            // Keep only scheme + host + path. Query strings are deliberately
            // removed because they can contain session or tracking values.
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        catch
        {
            return "<invalid-url>";
        }
    }

    // ============================================================
    // LOGIN FIRST
    //
    // Opens the IRCTC login dialog from the train-search page,
    // fills credentials from Windows user environment variables,
    // clicks SIGN IN, and waits for the authenticated session.
    //
    // CAPTCHA / OTP remain user-controlled.
    // ============================================================

    public async Task LoginFirstAsync(
        CancellationToken cancellationToken = default)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        await OpenLoginDialogAsync();

        await LoginToIrctcAsync();

        await _page.WaitForTimeoutAsync(1000);
    }

    // ============================================================
    // SELECT ENGLISH
    // ============================================================

    private async Task SelectEnglishAsync()
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        await _page.WaitForTimeoutAsync(500);

        var englishButton =
            _page.Locator(
                "button[type='submit'].btn.btn-primary")
            .Filter(
                new LocatorFilterOptions
                {
                    HasTextString = "English"
                });

        try
        {
            await englishButton.WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 10000
                });

            await englishButton.ScrollIntoViewIfNeededAsync();

            await englishButton.ClickAsync(
                new LocatorClickOptions
                {
                    Force = true,
                    Timeout = 5000
                });

            await _page.WaitForTimeoutAsync(1000);

            return;
        }
        catch
        {
        }

        try
        {
            await _page.Keyboard.PressAsync("Enter");

            await _page.WaitForTimeoutAsync(1000);
        }
        catch
        {
        }
    }

    // ============================================================
    // PREPARE JOURNEY
    // ============================================================

    public async Task PrepareJourneyAsync(
        BookingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        // ========================================================
        // FROM
        // ========================================================

        var from =
            _page.Locator(
                "input[aria-label='Enter From station. Input is Mandatory.']");

        await from.WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30000
            });

        await from.ClickAsync();

        await from.FillAsync(
            configuration.Journey.From);

        await _page.WaitForTimeoutAsync(1000);

        await SelectStationSuggestionAsync(
            from,
            configuration.Journey.From);

        // ========================================================
        // TO
        // ========================================================

        var to =
            _page.Locator(
                "input[aria-label='Enter To station. Input is Mandatory.']");

        await to.WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30000
            });

        await to.ClickAsync();

        await to.FillAsync(
            configuration.Journey.To);

        await _page.WaitForTimeoutAsync(1000);

        await SelectStationSuggestionAsync(
            to,
            configuration.Journey.To);

        // ========================================================
        // DATE
        // ========================================================

        await SelectJourneyDateAsync(
            configuration.Journey.JourneyDate);

        // ========================================================
        // CLASS
        // ========================================================

        await SelectJourneyClassAsync(
            configuration.Journey.Class);

        // ========================================================
        // SEARCH
        // ========================================================

        await SearchTrainsAsync();

        // ========================================================
        // STOP AFTER TRAIN SEARCH
        //
        // The train listing is now displayed. Train selection,
        // class selection inside a train, availability refresh,
        // and Book Now are intentionally left completely manual.
        // ========================================================
    }

    // ============================================================
    // STATION AUTOCOMPLETE
    // ============================================================

    private async Task SelectStationSuggestionAsync(
        ILocator input,
        string configuredStation)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        var station =
            configuredStation.Trim();

        await _page.WaitForTimeoutAsync(500);

        var exact =
            _page.GetByText(
                station,
                new PageGetByTextOptions
                {
                    Exact = true
                });

        var count =
            await exact.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var candidate =
                exact.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                await candidate.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 5000
                    });

                await _page.WaitForTimeoutAsync(500);

                return;
            }
            catch
            {
            }
        }

        var suggestions =
            _page.Locator(
                "li, " +
                ".ui-autocomplete-item, " +
                ".ui-dropdown-item, " +
                "[role='option']");

        count =
            await suggestions.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var candidate =
                suggestions.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                var text =
                    (await candidate.InnerTextAsync()).Trim();

                if (!text.Contains(
                        station,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await candidate.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 5000
                    });

                await _page.WaitForTimeoutAsync(500);

                return;
            }
            catch
            {
            }
        }

        await input.PressAsync("ArrowDown");
        await input.PressAsync("Enter");

        await _page.WaitForTimeoutAsync(500);

        var selected =
            await input.InputValueAsync();

        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new InvalidOperationException(
                $"Unable to automatically select station '{configuredStation}'.");
        }
    }

    // ============================================================
    // JOURNEY DATE
    // ============================================================

    private async Task SelectJourneyDateAsync(
        string configuredDate)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        if (!DateTime.TryParse(
                configuredDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var journeyDate))
        {
            throw new InvalidOperationException(
                $"Invalid journey date in configuration: {configuredDate}");
        }

        journeyDate =
            journeyDate.Date;

        var dateInput =
            _page.Locator("input").Nth(8);

        await dateInput.WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15000
            });

        await dateInput.ClickAsync();

        await _page.WaitForTimeoutAsync(500);

        var calendar =
            _page.Locator(
                ".ui-datepicker, .ui-calendar");

        if (await calendar.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                "IRCTC datepicker was not found.");
        }

        var calendarElement =
            calendar.First;

        var calendarText =
            await calendarElement.InnerTextAsync();

        var expectedMonth =
            journeyDate.ToString(
                "MMMM",
                CultureInfo.InvariantCulture);

        var expectedYear =
            journeyDate.Year.ToString(
                CultureInfo.InvariantCulture);

        if (!calendarText.Contains(
                expectedMonth,
                StringComparison.OrdinalIgnoreCase) ||
            !calendarText.Contains(expectedYear))
        {
            await NavigateCalendarToMonthAsync(
                calendarElement,
                journeyDate);
        }

        await _page.WaitForTimeoutAsync(300);

        var day =
            journeyDate.Day.ToString(
                CultureInfo.InvariantCulture);

        var dayCandidates =
            calendarElement.GetByText(
                day,
                new LocatorGetByTextOptions
                {
                    Exact = true
                });

        var count =
            await dayCandidates.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var candidate =
                dayCandidates.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                await candidate.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 5000
                    });

                await _page.WaitForTimeoutAsync(500);

                return;
            }
            catch
            {
            }
        }

        throw new InvalidOperationException(
            $"Unable to select journey date {journeyDate:dd/MM/yyyy}.");
    }

    // ============================================================
    // CALENDAR NAVIGATION
    // ============================================================

    private async Task NavigateCalendarToMonthAsync(
        ILocator calendar,
        DateTime targetDate)
    {
        if (_page is null)
            return;

        for (var attempt = 0; attempt < 24; attempt++)
        {
            var text =
                await calendar.InnerTextAsync();

            var targetMonth =
                targetDate.ToString(
                    "MMMM",
                    CultureInfo.InvariantCulture);

            var targetYear =
                targetDate.Year.ToString(
                    CultureInfo.InvariantCulture);

            if (text.Contains(
                    targetMonth,
                    StringComparison.OrdinalIgnoreCase) &&
                text.Contains(targetYear))
            {
                return;
            }

            var next =
                calendar.Locator(
                    "button.ui-datepicker-next, " +
                    "a.ui-datepicker-next, " +
                    ".ui-datepicker-next");

            if (await next.CountAsync() > 0 &&
                await next.First.IsVisibleAsync())
            {
                await next.First.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true
                    });

                await _page.WaitForTimeoutAsync(200);

                continue;
            }

            var nextButton =
                calendar.Locator(
                    "button[aria-label*='Next'], " +
                    "button[title*='Next']");

            if (await nextButton.CountAsync() > 0 &&
                await nextButton.First.IsVisibleAsync())
            {
                await nextButton.First.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true
                    });

                await _page.WaitForTimeoutAsync(200);

                continue;
            }

            break;
        }
    }

    // ============================================================
    // JOURNEY CLASS
    // ============================================================

    private async Task SelectJourneyClassAsync(
        string configuredClass)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        var className =
            GetIrctcClassName(
                configuredClass);

        var labels =
            _page.Locator(
                "div.ui-dropdown-label-container " +
                "span.ui-dropdown-label");

        var count =
            await labels.CountAsync();

        ILocator? dropdown =
            null;

        for (var i = 0; i < count; i++)
        {
            var candidate =
                labels.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                var text =
                    (await candidate.InnerTextAsync()).Trim();

                if (string.Equals(
                        text,
                        "All Classes",
                        StringComparison.OrdinalIgnoreCase))
                {
                    dropdown = candidate;
                    break;
                }
            }
            catch
            {
            }
        }

        if (dropdown is null)
        {
            var inputs =
                _page.Locator(
                    "input[aria-label='All Classes']");

            count =
                await inputs.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var candidate =
                    inputs.Nth(i);

                try
                {
                    var parent =
                        candidate.Locator(
                            "xpath=ancestor::div[contains(@class,'ui-dropdown')][1]");

                    if (await parent.CountAsync() == 0)
                        continue;

                    if (!await parent.IsVisibleAsync())
                        continue;

                    dropdown = parent;

                    break;
                }
                catch
                {
                }
            }
        }

        if (dropdown is null)
        {
            throw new InvalidOperationException(
                "IRCTC class dropdown was not found.");
        }

        await dropdown.ClickAsync(
            new LocatorClickOptions
            {
                Force = true,
                Timeout = 10000
            });

        await _page.WaitForTimeoutAsync(400);

        var options =
            _page.GetByText(
                className,
                new PageGetByTextOptions
                {
                    Exact = true
                });

        count =
            await options.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var option =
                options.Nth(i);

            try
            {
                if (!await option.IsVisibleAsync())
                    continue;

                await option.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 10000
                    });

                await _page.WaitForTimeoutAsync(500);

                return;
            }
            catch
            {
            }
        }

        throw new InvalidOperationException(
            $"IRCTC class option '{className}' was not found.");
    }

    // ============================================================
    // SEARCH TRAINS
    // ============================================================

    private async Task SearchTrainsAsync()
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        Console.WriteLine();
        Console.WriteLine("============================================");
        Console.WriteLine("IRCTC: SEARCHING TRAINS");
        Console.WriteLine("============================================");

        var buttons =
            _page.GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Search Trains",
                    Exact = true
                });

        var count =
            await buttons.CountAsync();

        if (count == 0)
        {
            throw new InvalidOperationException(
                "Search Trains button was not found.");
        }

        var clicked = false;

        for (var i = 0; i < count; i++)
        {
            var button = buttons.Nth(i);

            try
            {
                if (!await button.IsVisibleAsync())
                    continue;

                await button.ScrollIntoViewIfNeededAsync();

                await button.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 10000
                    });

                clicked = true;
                break;
            }
            catch
            {
                // Try the next visible Search Trains button.
            }
        }

        if (!clicked)
        {
            throw new InvalidOperationException(
                "Search Trains button was found, but could not be clicked.");
        }

        Console.WriteLine(
            "Search Trains clicked. Waiting for train results...");

        // ------------------------------------------------------------
        // Wait for the actual train-list UI.
        //
        // Do NOT rely on a fixed 3-second delay. IRCTC can take
        // considerably longer to populate the results.
        // ------------------------------------------------------------

        var trainResultsLoaded = false;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await _page.WaitForTimeoutAsync(500);

            try
            {
                // Primary IRCTC train component.
                var trainComponents =
                    _page.Locator("app-train-avl-enq");

                var componentCount =
                    await trainComponents.CountAsync();

                if (componentCount > 0)
                {
                    for (var i = 0; i < componentCount; i++)
                    {
                        try
                        {
                            if (!await trainComponents.Nth(i).IsVisibleAsync())
                                continue;

                            var text =
                                await trainComponents.Nth(i).InnerTextAsync();

                            if (Regex.IsMatch(
                                    text,
                                    @"\b\d{4,6}\b",
                                    RegexOptions.IgnoreCase))
                            {
                                trainResultsLoaded = true;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (trainResultsLoaded)
                    break;

                // Secondary check: known train-list container.
                var trainDetails =
                    _page.Locator(".train_avl_enq_details");

                if (await trainDetails.CountAsync() > 0)
                {
                    for (var i = 0;
                         i < await trainDetails.CountAsync();
                         i++)
                    {
                        try
                        {
                            if (!await trainDetails.Nth(i).IsVisibleAsync())
                                continue;

                            var text =
                                await trainDetails.Nth(i).InnerTextAsync();

                            if (Regex.IsMatch(
                                    text,
                                    @"\b\d{4,6}\b",
                                    RegexOptions.IgnoreCase))
                            {
                                trainResultsLoaded = true;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (trainResultsLoaded)
                    break;

                // Final fallback: look for common IRCTC result text.
                var bodyText =
                    await _page
                        .Locator("body")
                        .InnerTextAsync();

                if (bodyText.Contains(
                        "Book Now",
                        StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(
                        bodyText,
                        @"\b\d{4,6}\b",
                        RegexOptions.IgnoreCase))
                {
                    trainResultsLoaded = true;
                    break;
                }

                // IMPORTANT:
                // A 510 from boardingStationEnq is not treated as a
                // workflow exception here. The request is a background
                // protected request generated by IRCTC itself.
            }
            catch
            {
                // Continue waiting for the train-list UI.
            }
        }

        if (!trainResultsLoaded)
        {
            var bodyText = string.Empty;

            try
            {
                bodyText =
                    await _page
                        .Locator("body")
                        .InnerTextAsync();
            }
            catch
            {
            }

            var debugPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "logs",
                    "irctc-search-results-timeout.txt");

            try
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(debugPath)!);

                await File.WriteAllTextAsync(
                    debugPath,
                    bodyText);
            }
            catch
            {
            }

            throw new TimeoutException(
                "IRCTC Search Trains was clicked, but the train " +
                "results did not become available within 30 seconds.\n\n" +
                $"Diagnostic:\n{debugPath}");
        }

        // ------------------------------------------------------------
        // TRAIN LIST IS NOW READY
        // ------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("============================================");
        Console.WriteLine("IRCTC: TRAIN LIST LOADED");
        Console.WriteLine("============================================");
        Console.WriteLine(
            "Train search completed successfully.");
        Console.WriteLine(
            "Any background boardingStationEnq 510 is treated as non-fatal.");
        Console.WriteLine(
            "Train selection remains MANUAL.");
        Console.WriteLine(
            "Book Now remains MANUAL.");
        Console.WriteLine("============================================");
    }

    // ============================================================
    // SELECT CONFIGURED TRAIN
    // ============================================================

    private async Task SelectConfiguredTrainAsync(
        BookingConfiguration configuration)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        var preferred =
            configuration
                .TrainPreferences
                .PreferredTrainNumbers;

        if (preferred is null ||
            preferred.Count == 0)
        {
            throw new InvalidOperationException(
                "No preferred train number is configured.");
        }

        foreach (var configuredTrain in preferred)
        {
            var trainNumber =
                configuredTrain
                    .ToString()
                    .Trim();

            if (string.IsNullOrWhiteSpace(trainNumber))
                continue;

            // ========================================================
            // FIND EXACT TRAIN
            // ========================================================

            var trainCard =
                await FindTrainCardAsync(
                    trainNumber);

            if (trainCard is null)
                continue;

            await trainCard.ScrollIntoViewIfNeededAsync();

            await _page.WaitForTimeoutAsync(500);

            // ========================================================
            // FIND CLASS
            // ========================================================

            var className =
                GetIrctcClassName(
                    configuration.Journey.Class);

            var classElement =
                await FindClassInsideTrainAsync(
                    trainCard,
                    className);

            if (classElement is null)
            {
                throw new InvalidOperationException(
                    $"Class '{className}' was not found " +
                    $"for train {trainNumber}.");
            }

            await classElement.ScrollIntoViewIfNeededAsync();

            // ========================================================
            // SELECT CLASS
            // ========================================================

            try
            {
                await classElement.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 10000
                    });
            }
            catch
            {
                // Already selected.
            }

            await _page.WaitForTimeoutAsync(1000);

            // ========================================================
            // FIND REFRESH FOR EXACT TRAIN + EXACT CLASS
            // ========================================================

            var refreshButton =
                await FindRefreshForClassAsync(
                    trainCard,
                    classElement,
                    className);

            if (refreshButton is null)
            {
                throw new InvalidOperationException(
                    $"Refresh button for train {trainNumber} " +
                    $"and class '{className}' was not found.");
            }

            await refreshButton.ScrollIntoViewIfNeededAsync();

            await refreshButton.ClickAsync(
                new LocatorClickOptions
                {
                    Force = true,
                    Timeout = 10000
                });

            // ========================================================
            // WAIT FOR AVAILABILITY
            // ========================================================

            await _page.WaitForTimeoutAsync(2000);

            // ========================================================
            // SELECT EXACT JOURNEY DATE CARD
            // ========================================================

            await SelectAvailabilityJourneyDateAsync(
                trainCard,
                configuration.Journey.JourneyDate);

            // ========================================================
            // READ AVAILABILITY + FARE
            // ========================================================

            var result =
                await ReadTrainAvailabilityAsync(
                    trainCard,
                    trainNumber,
                    className,
                    configuration.Journey.JourneyDate);

            LastAvailabilityResult =
                result;

            // ========================================================
            // OPEN BOOKING
            // ========================================================

            await OpenBookingPageAsync(
                trainCard,
                trainNumber,
                className);

            return;
        }

        var pageText =
            await _page
                .Locator("body")
                .InnerTextAsync();

        var diagnosticPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "irctc-train-results-debug.txt");

        await File.WriteAllTextAsync(
            diagnosticPath,
            pageText);

        throw new InvalidOperationException(
            "Configured preferred train was not found " +
            "in the current search results.\n\n" +
            "Diagnostic file:\n" +
            diagnosticPath);
    }

    // ============================================================
    // SELECT WHOLE AVAILABILITY DATE CARD
    //
    // Screenshot-confirmed behavior:
    //
    // BEFORE:
    // Thu, 15 Oct
    // WL39
    // Book Now disabled
    //
    // AFTER:
    // Thu, 15 Oct selected
    // WL39
    // Book Now enabled
    // CNF Probability appears
    // ============================================================

    private async Task SelectAvailabilityJourneyDateAsync(
        ILocator trainCard,
        string configuredDate)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        if (!DateTime.TryParse(
                configuredDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var journeyDate))
        {
            throw new InvalidOperationException(
                $"Invalid journey date: {configuredDate}");
        }

        journeyDate = journeyDate.Date;

        var dateLabel =
            journeyDate.ToString(
                "ddd, dd MMM",
                CultureInfo.InvariantCulture);

        // ========================================================
        // BOOK NOW
        // ========================================================

        var bookNow =
            trainCard.GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Book Now",
                    Exact = true
                });

        if (await bookNow.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                $"Book Now button was not found for train/date " +
                $"{dateLabel}.");
        }

        var bookButton = bookNow.First;

        // ========================================================
        // FIND THE DATE TEXT
        // ========================================================

        var dateTexts =
            trainCard.GetByText(
                dateLabel,
                new LocatorGetByTextOptions
                {
                    Exact = true
                });

        var dateTextCount =
            await dateTexts.CountAsync();

        if (dateTextCount == 0)
        {
            throw new InvalidOperationException(
                $"Availability date '{dateLabel}' was not found " +
                $"inside the selected train.");
        }

        // ========================================================
        // IMPORTANT FIX
        //
        // The previous version found an ancestor containing the
        // date + WL39 and clicked it.
        //
        // Your screenshot proves that locator was NOT the Angular
        // availability-selection element: WL39 was read correctly,
        // but Book Now remained disabled.
        //
        // IRCTC availability cards commonly use .pre-avl / WL
        // containers. We now explicitly find the visual date card
        // and test the result of the click.
        // ========================================================

        var candidateCards = new List<ILocator>();

        // 1. Explicit IRCTC availability containers.
        var preAvl =
            trainCard.Locator(
                ".pre-avl");

        var preAvlCount =
            await preAvl.CountAsync();

        for (var i = 0; i < preAvlCount; i++)
        {
            var candidate = preAvl.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                var text =
                    (await candidate.InnerTextAsync()).Trim();

                if (text.Contains(
                        dateLabel,
                        StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(
                        text,
                        @"\b(?:WL|RAC)\s*\d+\b|\bAVAILABLE\b",
                        RegexOptions.IgnoreCase))
                {
                    candidateCards.Add(candidate);
                }
            }
            catch
            {
            }
        }

        // 2. WL availability containers.
        var wlCards =
            trainCard.Locator(
                "td.WL, div.WL, .WL");

        var wlCount =
            await wlCards.CountAsync();

        for (var i = 0; i < wlCount; i++)
        {
            var candidate = wlCards.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                var text =
                    (await candidate.InnerTextAsync()).Trim();

                if (text.Contains(
                        dateLabel,
                        StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(
                        text,
                        @"\bWL\s*\d+\b",
                        RegexOptions.IgnoreCase))
                {
                    candidateCards.Add(candidate);
                }
            }
            catch
            {
            }
        }

        // 3. Walk from exact date text, but collect several possible
        // clickable ancestors instead of assuming the first one is
        // the correct Angular event target.
        for (var i = 0; i < dateTextCount; i++)
        {
            var dateText =
                dateTexts.Nth(i);

            try
            {
                if (!await dateText.IsVisibleAsync())
                    continue;

                var ancestors =
                    dateText.Locator(
                        "xpath=ancestor::*");

                var ancestorCount =
                    await ancestors.CountAsync();

                for (var a = 0; a < ancestorCount; a++)
                {
                    var ancestor =
                        ancestors.Nth(a);

                    try
                    {
                        if (!await ancestor.IsVisibleAsync())
                            continue;

                        var text =
                            (await ancestor.InnerTextAsync()).Trim();

                        if (!text.Contains(
                                dateLabel,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!Regex.IsMatch(
                                text,
                                @"\b(?:WL|RAC)\s*\d+\b|\bAVAILABLE\b",
                                RegexOptions.IgnoreCase))
                        {
                            continue;
                        }

                        // Don't use a container containing multiple
                        // availability dates.
                        var dateMatches =
                            Regex.Matches(
                                text,
                                @"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),\s*\d{1,2}\s+[A-Za-z]{3}\b",
                                RegexOptions.IgnoreCase);

                        if (dateMatches.Count > 1)
                            continue;

                        candidateCards.Add(ancestor);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        // Remove duplicate locators by their visible text/class
        // signature while retaining order.
        var uniqueCandidates =
            new List<ILocator>();

        var seenSignatures =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (var candidate in candidateCards)
        {
            try
            {
                var text =
                    (await candidate.InnerTextAsync()).Trim();

                var cls =
                    await candidate.GetAttributeAsync("class") ?? "";

                var signature =
                    $"{cls}|{text}";

                if (seenSignatures.Add(signature))
                    uniqueCandidates.Add(candidate);
            }
            catch
            {
            }
        }

        // ========================================================
        // TRY EACH REAL DATE-CARD TARGET
        //
        // We do not assume a click succeeded merely because
        // Playwright did not throw. We require Book Now to become
        // enabled. This is the important behavioral verification.
        // ========================================================

        foreach (var card in uniqueCandidates)
        {
            try
            {
                var text =
                    (await card.InnerTextAsync()).Trim();

                Console.WriteLine(
                    $"Trying availability card: {text}");

                await card.ScrollIntoViewIfNeededAsync();

                await card.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 10000
                    });

                await _page.WaitForTimeoutAsync(700);

                for (var attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        if (await bookButton.IsVisibleAsync() &&
                            await bookButton.IsEnabledAsync())
                        {
                            var debugPath =
                                Path.Combine(
                                    AppContext.BaseDirectory,
                                    "selected-availability-card-debug.txt");

                            await File.WriteAllTextAsync(
                                debugPath,
                                $"Requested date: {dateLabel}\n\n" +
                                $"Clicked card:\n{text}\n\n" +
                                $"Book Now enabled: YES");

                            Console.WriteLine(
                                $"SUCCESS: {dateLabel} selected; Book Now enabled.");

                            return;
                        }
                    }
                    catch
                    {
                    }

                    await _page.WaitForTimeoutAsync(400);
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        // ========================================================
        // LAST RESORT:
        // Click the exact date text itself. Angular event bubbling
        // can select the card even when the parent wrapper is not
        // the actual event target.
        // ========================================================

        for (var i = 0; i < dateTextCount; i++)
        {
            var dateText =
                dateTexts.Nth(i);

            try
            {
                if (!await dateText.IsVisibleAsync())
                    continue;

                Console.WriteLine(
                    $"Trying exact date text: {dateLabel}");

                await dateText.ScrollIntoViewIfNeededAsync();

                await dateText.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 10000
                    });

                await _page.WaitForTimeoutAsync(700);

                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        if (await bookButton.IsVisibleAsync() &&
                            await bookButton.IsEnabledAsync())
                        {
                            Console.WriteLine(
                                $"SUCCESS: exact date text selected; Book Now enabled.");

                            return;
                        }
                    }
                    catch
                    {
                    }

                    await _page.WaitForTimeoutAsync(400);
                }
            }
            catch
            {
            }
        }

        // ========================================================
        // DEBUG
        // ========================================================

        var selectedDebugPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "selected-availability-card-debug.txt");

        var trainDebugPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "book-now-disabled-debug.txt");

        var trainText =
            await trainCard.InnerTextAsync();

        await File.WriteAllTextAsync(
            selectedDebugPath,
            $"Requested date: {dateLabel}\n\n" +
            $"Candidate cards found: {uniqueCandidates.Count}\n\n" +
            $"Train card after attempts:\n{trainText}");

        await File.WriteAllTextAsync(
            trainDebugPath,
            trainText);

        throw new InvalidOperationException(
            $"IRCTC did not activate '{dateLabel}'. " +
            $"WL39 was detected, but the availability card click " +
            $"did not enable Book Now.\n\n" +
            $"Diagnostic:\n{selectedDebugPath}");
    }

    // ============================================================
    // BOOKING HANDOFF TO NORMAL CHROME
    // ============================================================

    private async Task OpenBookingPageAsync(
        ILocator trainCard,
        string trainNumber,
        string className)
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        var bookNowButtons =
            trainCard.GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Book Now",
                    Exact = true
                });

        var count =
            await bookNowButtons.CountAsync();

        if (count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one Book Now button for " +
                $"train {trainNumber}, but found {count}.");
        }

        var bookNow = bookNowButtons.First;

        if (!await bookNow.IsVisibleAsync())
        {
            throw new InvalidOperationException(
                $"Book Now button for train {trainNumber} is not visible.");
        }

        if (!await bookNow.IsEnabledAsync())
        {
            throw new InvalidOperationException(
                $"Book Now button for train {trainNumber} is still disabled.");
        }

        await bookNow.ScrollIntoViewIfNeededAsync();

        // ========================================================
        // IMPORTANT:
        //
        // We have verified that IRCTC's protected booking request
        // returns HTTP 510 in the Playwright browser session.
        // Even a MANUAL click inside that Playwright session is
        // rejected by IRCTC.
        //
        // Therefore we deliberately DO NOT click Book Now here.
        // Instead, we hand the user over to their normal Chrome
        // session, where the actual IRCTC booking flow can continue.
        // ========================================================

        var debugDirectory =
            Path.Combine(
                AppContext.BaseDirectory,
                "logs");

        Directory.CreateDirectory(debugDirectory);

        var handoffPath =
            Path.Combine(
                debugDirectory,
                "irctc-booking-handoff.txt");

        var handoff = new StringBuilder();
        handoff.AppendLine("IRCTC BOOKING HANDOFF");
        handoff.AppendLine("=====================");
        handoff.AppendLine();
        handoff.AppendLine(
            $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        handoff.AppendLine($"Train: {trainNumber}");
        handoff.AppendLine($"Class: {className}");
        handoff.AppendLine("Journey date: See booking configuration");
        handoff.AppendLine();
        handoff.AppendLine(
            "Playwright preparation completed successfully.");
        handoff.AppendLine(
            "Book Now was intentionally NOT clicked in Playwright.");
        handoff.AppendLine();
        handoff.AppendLine(
            "Next step: continue the actual booking in normal Chrome.");
        handoff.AppendLine(
            $"IRCTC URL: {TrainSearchUrl}");

        await File.WriteAllTextAsync(
            handoffPath,
            handoff.ToString());

        Console.WriteLine();
        Console.WriteLine("============================================");
        Console.WriteLine("IRCTC BOOKING READY");
        Console.WriteLine("============================================");
        Console.WriteLine($"Train : {trainNumber}");
        Console.WriteLine($"Class : {className}");
        Console.WriteLine("Book Now is ENABLED.");
        Console.WriteLine();
        Console.WriteLine(
            "Opening the normal Chrome browser for the actual booking.");
        Console.WriteLine(
            "Book Now will NOT be clicked by Playwright.");
        Console.WriteLine("============================================");

        await LaunchNormalChromeAsync(TrainSearchUrl);

        // Close the Playwright browser after handing off to Chrome.
        // The normal Chrome session is independent and remains open.
        if (_browser is not null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch
            {
            }
        }

        _browser = null;
        _playwright?.Dispose();
        _playwright = null;
        _page = null;
    }

    // ============================================================
    // OPEN NORMAL CHROME
    // ============================================================

    private static Task LaunchNormalChromeAsync(string url)
    {
        var chromePath = FindChromePath();

        if (!string.IsNullOrWhiteSpace(chromePath))
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = chromePath,
                    Arguments = $"--new-tab \"{url}\"",
                    UseShellExecute = false
                });

            return Task.CompletedTask;
        }

        // Fallback: let Windows open the user's default browser.
        Process.Start(
            new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

        return Task.CompletedTask;
    }

    private static string? FindChromePath()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Google",
                "Chrome",
                "Application",
                "chrome.exe"),

            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "Google",
                "Chrome",
                "Application",
                "chrome.exe"),

            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),
                "Google",
                "Chrome",
                "Application",
                "chrome.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // ============================================================
    // IRCTC LOGIN
    //
    // Credentials are read ONLY from Windows user environment
    // variables. They are never written to booking.json, source
    // code, diagnostics, or logs.
    //
    // Required:
    //   IRCTC_USERNAME
    //   IRCTC_PASSWORD
    //
    // SIGN IN is automated.
    // CAPTCHA / OTP remain user-controlled.
    // ============================================================

    // ============================================================
    // OPEN LOGIN DIALOG
    //
    // The login is now performed BEFORE train search.
    // We deliberately look for a visible LOGIN control on the
    // train-search page rather than relying on Book Now.
    // ============================================================

    private async Task OpenLoginDialogAsync()
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        // ========================================================
        // IMPORTANT:
        // The current IRCTC responsive website does NOT expose a
        // top-level LOGIN button on this layout.
        //
        // The actual flow shown in the supplied video is:
        //
        //   1. Click hamburger/menu icon at top-right
        //   2. Side menu opens
        //   3. Click "LOGIN / REGISTER"
        //   4. Login popup opens
        //
        // Do exactly that here.
        // ========================================================

        var existingPassword =
            _page.Locator("input[type='password']:visible");

        if (await existingPassword.CountAsync() > 0)
            return;

        // ========================================================
        // STEP 1: OPEN HAMBURGER / MENU
        // ========================================================

        var menuOpened = false;

        // First try semantic attributes commonly used by the
        // responsive IRCTC header.
        var menuSelectors = new[]
        {
            "button[aria-label*='menu' i]",
            "button[title*='menu' i]",
            "[role='button'][aria-label*='menu' i]",
            "[role='button'][title*='menu' i]",
            "button[class*='menu' i]",
            "[class*='hamburger' i]",
            "[class*='menu-icon' i]",
            "[class*='menu_icon' i]"
        };

        foreach (var selector in menuSelectors)
        {
            var candidates =
                _page.Locator(selector);

            var count =
                await candidates.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var candidate =
                    candidates.Nth(i);

                try
                {
                    if (!await candidate.IsVisibleAsync())
                        continue;

                    await candidate.ClickAsync(
                        new LocatorClickOptions
                        {
                            Force = true,
                            Timeout = 5000
                        });

                    await _page.WaitForTimeoutAsync(500);

                    if (await IsLoginRegisterMenuVisibleAsync())
                    {
                        menuOpened = true;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (menuOpened)
                break;
        }

        // ========================================================
        // STEP 1 FALLBACK:
        // Find a small, visible, textless button/role-button in
        // the top-right portion of the viewport.
        //
        // This matches the hamburger shown in the supplied video
        // without relying on a guessed CSS class.
        // ========================================================

        if (!menuOpened)
        {
            var viewport =
                _page.ViewportSize;

            if (viewport is not null)
            {
                var width = viewport.Width;

                var candidates =
                    _page.Locator(
                        "button, [role='button'], a");

                var count =
                    await candidates.CountAsync();

                for (var i = 0; i < count; i++)
                {
                    var candidate =
                        candidates.Nth(i);

                    try
                    {
                        if (!await candidate.IsVisibleAsync())
                            continue;

                        var box =
                            await candidate.BoundingBoxAsync();

                        if (box is null)
                            continue;

                        // Hamburger is in the top-right header.
                        if (box.X < width * 0.70)
                            continue;

                        if (box.Y > 180)
                            continue;

                        if (box.Width < 20 ||
                            box.Width > 100 ||
                            box.Height < 20 ||
                            box.Height > 100)
                        {
                            continue;
                        }

                        var text =
                            (await candidate.InnerTextAsync()).Trim();

                        // Don't click textual header links.
                        if (!string.IsNullOrWhiteSpace(text))
                            continue;

                        await candidate.ClickAsync(
                            new LocatorClickOptions
                            {
                                Force = true,
                                Timeout = 5000
                            });

                        await _page.WaitForTimeoutAsync(500);

                        if (await IsLoginRegisterMenuVisibleAsync())
                        {
                            menuOpened = true;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (!menuOpened)
        {
            var diagnostic =
                await GetVisibleHeaderDiagnosticsAsync();

            var debugPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "irctc-login-menu-debug.txt");

            await File.WriteAllTextAsync(
                debugPath,
                diagnostic);

            throw new InvalidOperationException(
                "IRCTC hamburger/menu could not be opened.\n\n" +
                "Diagnostic file:\n" +
                debugPath);
        }

        // ========================================================
        // STEP 2: CLICK LOGIN / REGISTER
        // ========================================================

        var loginRegister =
            _page.GetByText(
                "LOGIN / REGISTER",
                new PageGetByTextOptions
                {
                    Exact = true
                });

        var loginRegisterCount =
            await loginRegister.CountAsync();

        ILocator? loginRegisterButton = null;

        for (var i = 0; i < loginRegisterCount; i++)
        {
            var candidate =
                loginRegister.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                loginRegisterButton = candidate;
                break;
            }
            catch
            {
            }
        }

        // Some builds render it as a button/anchor whose accessible
        // name includes LOGIN / REGISTER.
        if (loginRegisterButton is null)
        {
            var roleCandidates =
                _page.GetByRole(
                    AriaRole.Button,
                    new()
                    {
                        Name = "LOGIN / REGISTER",
                        Exact = true
                    });

            var count =
                await roleCandidates.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var candidate =
                    roleCandidates.Nth(i);

                try
                {
                    if (!await candidate.IsVisibleAsync())
                        continue;

                    loginRegisterButton = candidate;
                    break;
                }
                catch
                {
                }
            }
        }

        if (loginRegisterButton is null)
        {
            var diagnostic =
                await GetVisibleHeaderDiagnosticsAsync();

            var debugPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "irctc-login-register-debug.txt");

            await File.WriteAllTextAsync(
                debugPath,
                diagnostic);

            throw new InvalidOperationException(
                "IRCTC 'LOGIN / REGISTER' menu item was not found.\n\n" +
                "Diagnostic file:\n" +
                debugPath);
        }

        await loginRegisterButton.ClickAsync(
            new LocatorClickOptions
            {
                Force = true,
                Timeout = 10000
            });

        await WaitForLoginInputsAsync();
    }

    // ============================================================
    // CHECK WHETHER THE OPEN MENU CONTAINS LOGIN / REGISTER
    // ============================================================

    private async Task<bool> IsLoginRegisterMenuVisibleAsync()
    {
        if (_page is null)
            return false;

        var loginRegister =
            _page.GetByText(
                "LOGIN / REGISTER",
                new PageGetByTextOptions
                {
                    Exact = true
                });

        var count =
            await loginRegister.CountAsync();

        for (var i = 0; i < count; i++)
        {
            try
            {
                if (await loginRegister.Nth(i).IsVisibleAsync())
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    // ============================================================
    // SAFE HEADER DIAGNOSTICS
    //
    // Does not write username/password.
    // ============================================================

    private async Task<string> GetVisibleHeaderDiagnosticsAsync()
    {
        if (_page is null)
            return string.Empty;

        var lines =
            new List<string>();

        var elements =
            _page.Locator(
                "button, [role='button'], a");

        var count =
            await elements.CountAsync();

        for (var i = 0; i < count; i++)
        {
            try
            {
                var element =
                    elements.Nth(i);

                if (!await element.IsVisibleAsync())
                    continue;

                var text =
                    (await element.InnerTextAsync()).Trim();

                var aria =
                    (await element.GetAttributeAsync("aria-label") ?? "")
                    .Trim();

                var title =
                    (await element.GetAttributeAsync("title") ?? "")
                    .Trim();

                var cls =
                    (await element.GetAttributeAsync("class") ?? "")
                    .Trim();

                var box =
                    await element.BoundingBoxAsync();

                if (box is null)
                    continue;

                if (box.Y > 220)
                    continue;

                lines.Add(
                    $"TEXT=[{text}] " +
                    $"ARIA=[{aria}] " +
                    $"TITLE=[{title}] " +
                    $"CLASS=[{cls}] " +
                    $"BOX=({box.X:0},{box.Y:0},{box.Width:0},{box.Height:0})");
            }
            catch
            {
            }
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    // ============================================================
    // WAIT FOR LOGIN INPUTS
    // ============================================================

    private async Task WaitForLoginInputsAsync()
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var passwords =
                _page.Locator("input[type='password']:visible");

            if (await passwords.CountAsync() > 0)
            {
                var visibleInputs =
                    _page.Locator("input:visible");

                if (await visibleInputs.CountAsync() >= 2)
                    return;
            }

            await _page.WaitForTimeoutAsync(250);
        }

        throw new InvalidOperationException(
            "IRCTC login dialog did not open.");
    }

    private async Task LoginToIrctcAsync()
    {
        if (_page is null)
            throw new InvalidOperationException(
                "IRCTC page is not open.");

        var username =
            Environment.GetEnvironmentVariable(
                "IRCTC_USERNAME",
                EnvironmentVariableTarget.User);

        var password =
            Environment.GetEnvironmentVariable(
                "IRCTC_PASSWORD",
                EnvironmentVariableTarget.User);

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException(
                "IRCTC_USERNAME Windows user environment variable " +
                "is not configured.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "IRCTC_PASSWORD Windows user environment variable " +
                "is not configured.");
        }

        // ========================================================
        // FIND LOGIN CONTAINER
        // ========================================================

        var loginText =
            _page.GetByText(
                "LOGIN",
                new PageGetByTextOptions
                {
                    Exact = true
                });

        ILocator? loginContainer = null;

        var loginTextCount =
            await loginText.CountAsync();

        for (var i = 0; i < loginTextCount; i++)
        {
            var candidate =
                loginText.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                var ancestors =
                    candidate.Locator(
                        "xpath=ancestor::*");

                var ancestorCount =
                    await ancestors.CountAsync();

                var smallestLength = int.MaxValue;

                for (var a = 0; a < ancestorCount; a++)
                {
                    var ancestor =
                        ancestors.Nth(a);

                    try
                    {
                        if (!await ancestor.IsVisibleAsync())
                            continue;

                        var text =
                            (await ancestor.InnerTextAsync()).Trim();

                        if (!text.Contains(
                                "LOGIN",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var inputs =
                            ancestor.Locator("input");

                        var ancestorInputCount =
                            await inputs.CountAsync();

                        if (ancestorInputCount < 2)
                            continue;

                        if (text.Length < smallestLength)
                        {
                            smallestLength = text.Length;
                            loginContainer = ancestor;
                        }
                    }
                    catch
                    {
                    }
                }

                if (loginContainer is not null)
                    break;
            }
            catch
            {
            }
        }

        if (loginContainer is null)
        {
            throw new InvalidOperationException(
                "IRCTC login popup was not found.");
        }

        // ========================================================
        // FIND USERNAME + PASSWORD
        // ========================================================

        var visibleInputs =
            loginContainer.Locator("input:visible");

        var inputCount =
            await visibleInputs.CountAsync();

        ILocator? usernameInput = null;
        ILocator? passwordInput = null;

        for (var i = 0; i < inputCount; i++)
        {
            var input =
                visibleInputs.Nth(i);

            try
            {
                var type =
                    (await input.GetAttributeAsync("type") ?? "")
                    .Trim()
                    .ToLowerInvariant();

                if (type == "password")
                {
                    passwordInput = input;
                    continue;
                }

                if (usernameInput is null &&
                    (type == "" ||
                     type == "text" ||
                     type == "email"))
                {
                    usernameInput = input;
                }
            }
            catch
            {
            }
        }

        if (usernameInput is null ||
            passwordInput is null)
        {
            throw new InvalidOperationException(
                "IRCTC username/password fields were not found.");
        }

        await usernameInput.FillAsync(username);
        await passwordInput.FillAsync(password);

        await _page.WaitForTimeoutAsync(300);

        // ========================================================
        // SIGN IN
        // ========================================================

        var signInButtons =
            loginContainer.GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "SIGN IN",
                    Exact = true
                });

        var signInCount =
            await signInButtons.CountAsync();

        if (signInCount == 0)
        {
            signInButtons =
                loginContainer.Locator("button")
                .Filter(
                    new LocatorFilterOptions
                    {
                        HasTextString = "SIGN IN"
                    });

            signInCount =
                await signInButtons.CountAsync();
        }

        ILocator? signInButton = null;

        for (var i = 0; i < signInCount; i++)
        {
            var candidate =
                signInButtons.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                signInButton = candidate;
                break;
            }
            catch
            {
            }
        }

        if (signInButton is null)
        {
            throw new InvalidOperationException(
                "IRCTC SIGN IN button was not found.");
        }

        await signInButton.ClickAsync(
            new LocatorClickOptions
            {
                Force = true,
                Timeout = 10000
            });

        Console.WriteLine(
            "IRCTC SIGN IN clicked.");

        // ========================================================
        // WAIT FOR SERVER RESPONSE.
        //
        // If CAPTCHA/OTP is presented, the user can complete it
        // manually in the same browser. We wait for the login
        // dialog to close instead of trying to automate protected
        // steps.
        // ========================================================

        for (var attempt = 0; attempt < 240; attempt++)
        {
            await _page.WaitForTimeoutAsync(500);

            // Server-side error.
            var body =
                await _page
                    .Locator("body")
                    .InnerTextAsync();

            if (body.Contains(
                    "Unable to Process Request",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "IRCTC returned 'Unable to Process Request' " +
                    "during login. The authenticated session was " +
                    "not established.");
            }

            // If the login dialog disappears, authentication has
            // progressed successfully.
            if (!await loginContainer.IsVisibleAsync())
            {
                Console.WriteLine(
                    "IRCTC login dialog closed. " +
                    "Authenticated session is ready.");

                return;
            }

            // Detect an actual CAPTCHA/OTP UI, not merely the word
            // OTP in the normal login help text.
            var captchaInputs =
                _page.Locator(
                    "input[placeholder*='CAPTCHA' i]:visible, " +
                    "input[aria-label*='CAPTCHA' i]:visible");

            var otpInputs =
                _page.Locator(
                    "input[placeholder*='OTP' i]:visible, " +
                    "input[aria-label*='OTP' i]:visible");

            if (await captchaInputs.CountAsync() > 0 ||
                await otpInputs.CountAsync() > 0)
            {
                Console.WriteLine(
                    "IRCTC is requesting CAPTCHA/OTP. " +
                    "Complete it manually in the browser; " +
                    "the application will continue after login succeeds.");
            }
        }

        throw new TimeoutException(
            "IRCTC login did not complete within 120 seconds. " +
            "Complete any CAPTCHA/OTP manually and try again.");
    }

    // ============================================================
    // FIND TRAIN CARD
    // ============================================================

    private async Task<ILocator?> FindTrainCardAsync(
        string trainNumber)
    {
        if (_page is null)
            return null;

        trainNumber = trainNumber.Trim();

        // ========================================================
        // PRIMARY: IRCTC train component
        //
        // Do NOT require "(12724)" formatting here. Different
        // Angular builds render the train number differently.
        // ========================================================

        var components =
            _page.Locator("app-train-avl-enq");

        var componentCount =
            await components.CountAsync();

        for (var i = 0; i < componentCount; i++)
        {
            var component = components.Nth(i);

            try
            {
                if (!await component.IsVisibleAsync())
                    continue;

                var text =
                    await component.InnerTextAsync();

                if (!text.Contains(
                        trainNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // If this component contains exactly the requested
                // train, it is the correct boundary.
                var trainNumbers =
                    Regex.Matches(
                            text,
                            @"\b\d{4,6}\b")
                        .Select(m => m.Value)
                        .Where(n => n.Length >= 4)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                if (trainNumbers.Count == 1 &&
                    string.Equals(
                        trainNumbers[0],
                        trainNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return component;
                }

                // If several numeric values exist because the
                // component contains fare/date/etc., locate the
                // requested train text inside this component and
                // choose the SMALLEST ancestor containing Book Now.
                var trainText =
                    component.Locator(
                        $"text=/{Regex.Escape(trainNumber)}/");

                var trainTextCount =
                    await trainText.CountAsync();

                ILocator? best = null;
                var bestLength = int.MaxValue;

                for (var t = 0; t < trainTextCount; t++)
                {
                    var target = trainText.Nth(t);

                    try
                    {
                        if (!await target.IsVisibleAsync())
                            continue;

                        var ancestors =
                            target.Locator("xpath=ancestor::*");

                        var ancestorCount =
                            await ancestors.CountAsync();

                        for (var a = 0; a < ancestorCount; a++)
                        {
                            var ancestor = ancestors.Nth(a);

                            try
                            {
                                if (!await ancestor.IsVisibleAsync())
                                    continue;

                                var ancestorText =
                                    await ancestor.InnerTextAsync();

                                if (!ancestorText.Contains(
                                        trainNumber,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var bookNow =
                                    ancestor.GetByRole(
                                        AriaRole.Button,
                                        new()
                                        {
                                            Name = "Book Now",
                                            Exact = true
                                        });

                                if (await bookNow.CountAsync() == 0)
                                    continue;

                                if (ancestorText.Length < bestLength)
                                {
                                    bestLength = ancestorText.Length;
                                    best = ancestor;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                if (best is not null)
                    return best;
            }
            catch
            {
            }
        }

        // ========================================================
        // SECONDARY: Locate the train number itself and find the
        // SMALLEST ancestor containing exactly one Book Now.
        //
        // This is deliberately different from the old implementation:
        // we inspect ALL matching ancestors and keep the smallest one.
        // That prevents a large container containing 12650 + 12724
        // from being returned as the 12724 card.
        // ========================================================

        var trainTextLocator =
            _page.Locator(
                $"text=/{Regex.Escape(trainNumber)}/");

        var targetCount =
            await trainTextLocator.CountAsync();

        ILocator? smallestCard = null;
        var smallestLength = int.MaxValue;

        for (var i = 0; i < targetCount; i++)
        {
            var target =
                trainTextLocator.Nth(i);

            try
            {
                if (!await target.IsVisibleAsync())
                    continue;

                var ancestors =
                    target.Locator("xpath=ancestor::*");

                var ancestorCount =
                    await ancestors.CountAsync();

                for (var a = 0; a < ancestorCount; a++)
                {
                    var ancestor =
                        ancestors.Nth(a);

                    try
                    {
                        if (!await ancestor.IsVisibleAsync())
                            continue;

                        var text =
                            await ancestor.InnerTextAsync();

                        if (!text.Contains(
                                trainNumber,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var bookNow =
                            ancestor.GetByRole(
                                AriaRole.Button,
                                new()
                                {
                                    Name = "Book Now",
                                    Exact = true
                                });

                        if (await bookNow.CountAsync() != 1)
                            continue;

                        if (text.Length < smallestLength)
                        {
                            smallestLength = text.Length;
                            smallestCard = ancestor;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        if (smallestCard is not null)
            return smallestCard;

        // ========================================================
        // THIRD: known IRCTC train-detail container
        // ========================================================

        var detailComponents =
            _page.Locator(".train_avl_enq_details");

        var detailCount =
            await detailComponents.CountAsync();

        for (var i = 0; i < detailCount; i++)
        {
            var component =
                detailComponents.Nth(i);

            try
            {
                if (!await component.IsVisibleAsync())
                    continue;

                var text =
                    await component.InnerTextAsync();

                if (!text.Contains(
                        trainNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return component;
            }
            catch
            {
            }
        }

        // ========================================================
        // FOURTH: train-list bordered row
        // ========================================================

        var rows =
            _page.Locator(".train-list .border-all");

        var rowCount =
            await rows.CountAsync();

        for (var i = 0; i < rowCount; i++)
        {
            var row = rows.Nth(i);

            try
            {
                if (!await row.IsVisibleAsync())
                    continue;

                var text =
                    await row.InnerTextAsync();

                if (!text.Contains(
                        trainNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return row;
            }
            catch
            {
            }
        }

        return null;
    }

    // ============================================================
    // FIND REFRESH FOR EXACT CLASS
    // ============================================================

    private async Task<ILocator?> FindRefreshForClassAsync(
        ILocator trainCard,
        ILocator classElement,
        string className)
    {
        // Start from the selected class and walk upward.
        // We keep the SMALLEST visible container that contains:
        //   1. the selected class
        //   2. Refresh
        //
        // This prevents Refresh for 12650 being selected while
        // processing 12724.

        var ancestors =
            classElement.Locator("xpath=ancestor::*");

        var ancestorCount =
            await ancestors.CountAsync();

        ILocator? bestContainer = null;
        var smallestLength = int.MaxValue;

        for (var i = 0; i < ancestorCount; i++)
        {
            var ancestor =
                ancestors.Nth(i);

            try
            {
                if (!await ancestor.IsVisibleAsync())
                    continue;

                var text =
                    (await ancestor.InnerTextAsync()).Trim();

                if (!text.Contains(
                        className,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var refreshes =
                    ancestor.GetByText(
                        "Refresh",
                        new LocatorGetByTextOptions
                        {
                            Exact = true
                        });

                var refreshCount =
                    await refreshes.CountAsync();

                if (refreshCount == 0)
                    continue;

                if (text.Length < smallestLength)
                {
                    smallestLength = text.Length;
                    bestContainer = ancestor;
                }
            }
            catch
            {
            }
        }

        if (bestContainer is not null)
        {
            var refreshes =
                bestContainer.GetByText(
                    "Refresh",
                    new LocatorGetByTextOptions
                    {
                        Exact = true
                    });

            var count =
                await refreshes.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var refresh =
                    refreshes.Nth(i);

                try
                {
                    if (!await refresh.IsVisibleAsync())
                        continue;

                    return refresh;
                }
                catch
                {
                }
            }
        }

        // Final fallback: Refresh must still be inside the exact
        // train card. Never search the whole page.
        var trainRefreshes =
            trainCard.GetByText(
                "Refresh",
                new LocatorGetByTextOptions
                {
                    Exact = true
                });

        var trainRefreshCount =
            await trainRefreshes.CountAsync();

        for (var i = 0; i < trainRefreshCount; i++)
        {
            var refresh =
                trainRefreshes.Nth(i);

            try
            {
                if (!await refresh.IsVisibleAsync())
                    continue;

                return refresh;
            }
            catch
            {
            }
        }

        return null;
    }

    // ============================================================
    // FIND CLASS INSIDE TRAIN
    // ============================================================

    private async Task<ILocator?> FindClassInsideTrainAsync(
        ILocator trainCard,
        string className)
    {
        var candidates =
            trainCard.GetByText(
                className,
                new LocatorGetByTextOptions
                {
                    Exact = true
                });

        var count =
            await candidates.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var candidate =
                candidates.Nth(i);

            try
            {
                if (!await candidate.IsVisibleAsync())
                    continue;

                return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    // ============================================================
    // READ AVAILABILITY
    // ============================================================

    private async Task<TrainAvailabilityResult>
        ReadTrainAvailabilityAsync(
            ILocator trainCard,
            string trainNumber,
            string className,
            string configuredDate)
    {
        if (!DateTime.TryParse(
                configuredDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var journeyDate))
        {
            throw new InvalidOperationException(
                $"Invalid journey date: {configuredDate}");
        }

        journeyDate = journeyDate.Date;

        if (_page is not null)
            await _page.WaitForTimeoutAsync(1000);

        var dateLabel =
            journeyDate.ToString(
                "ddd, dd MMM",
                CultureInfo.InvariantCulture);

        // ========================================================
        // CRITICAL:
        // Read WL/RAC ONLY from the exact selected train card.
        //
        // Never use _page.Locator(".WL ...") here.
        // That was the reason 12650 WL49 was being reported for
        // 12724, whose actual value is WL39.
        // ========================================================

        var availability = "Not available";

        var dateTexts =
            trainCard.GetByText(
                dateLabel,
                new LocatorGetByTextOptions
                {
                    Exact = true
                });

        var dateTextCount =
            await dateTexts.CountAsync();

        ILocator? dateCard = null;
        var smallestLength = int.MaxValue;

        for (var i = 0; i < dateTextCount; i++)
        {
            var dateText =
                dateTexts.Nth(i);

            try
            {
                if (!await dateText.IsVisibleAsync())
                    continue;

                var ancestors =
                    dateText.Locator("xpath=ancestor::*");

                var ancestorCount =
                    await ancestors.CountAsync();

                for (var a = 0; a < ancestorCount; a++)
                {
                    var ancestor =
                        ancestors.Nth(a);

                    try
                    {
                        if (!await ancestor.IsVisibleAsync())
                            continue;

                        var text =
                            (await ancestor.InnerTextAsync()).Trim();

                        if (!text.Contains(
                                dateLabel,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var match =
                            Regex.Match(
                                text,
                                @"\b(?:WL|RAC)\s*\d+\b",
                                RegexOptions.IgnoreCase);

                        var available =
                            Regex.IsMatch(
                                text,
                                @"\bAVAILABLE\b",
                                RegexOptions.IgnoreCase);

                        if (!match.Success && !available)
                            continue;

                        // Reject an ancestor containing several
                        // availability dates.
                        var dateMatches =
                            Regex.Matches(
                                text,
                                @"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),\s*\d{1,2}\s+[A-Za-z]{3}\b",
                                RegexOptions.IgnoreCase);

                        if (dateMatches.Count > 1)
                            continue;

                        if (text.Length < smallestLength)
                        {
                            smallestLength = text.Length;
                            dateCard = ancestor;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        if (dateCard is not null)
        {
            var cardText =
                (await dateCard.InnerTextAsync()).Trim();

            var match =
                Regex.Match(
                    cardText,
                    @"\b(?:WL|RAC)\s*\d+\b",
                    RegexOptions.IgnoreCase);

            if (match.Success)
            {
                availability = match.Value.Trim();
            }
            else if (cardText.Contains(
                         "AVAILABLE",
                         StringComparison.OrdinalIgnoreCase))
            {
                availability = "AVAILABLE";
            }
        }

        // ========================================================
        // Scoped fallback — STILL inside exact train card only.
        // ========================================================

        if (availability == "Not available")
        {
            var wl =
                trainCard.Locator(
                    ".WL.col-xs-12 strong");

            var count =
                await wl.CountAsync();

            for (var i = 0; i < count; i++)
            {
                try
                {
                    var element =
                        wl.Nth(i);

                    if (!await element.IsVisibleAsync())
                        continue;

                    var text =
                        (await element.InnerTextAsync()).Trim();

                    if (Regex.IsMatch(
                            text,
                            @"^(WL|RAC)\s*\d+$",
                            RegexOptions.IgnoreCase))
                    {
                        availability = text;
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        // ========================================================
        // FARE — exact train only
        // ========================================================

        var fare =
            await ExtractFareFromTrainCardAsync(
                trainCard);

        // DO NOT fall back to page-wide fare.
        // Otherwise another train's fare can be mixed in.

        var trainDebug =
            await trainCard.InnerTextAsync();

        var debugPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "selected-train-debug.txt");

        await File.WriteAllTextAsync(
            debugPath,
            $"TRAIN: {trainNumber}\n" +
            $"CLASS: {className}\n" +
            $"DATE: {dateLabel}\n" +
            $"AVAILABILITY: {availability}\n" +
            $"FARE: {fare}\n\n" +
            $"TRAIN CARD:\n{trainDebug}");

        return new TrainAvailabilityResult
        {
            TrainNumber = trainNumber,
            ClassName = className,
            JourneyDate = journeyDate.Date,
            Availability = availability,
            Fare = fare
        };
    }

    // ============================================================
    // FARE
    // ============================================================

    private static async Task<string>
        ExtractFareFromTrainCardAsync(
            ILocator trainCard)
    {
        var text =
            await trainCard.InnerTextAsync();

        if (string.IsNullOrWhiteSpace(text))
            return "Not available";

        var rupee =
            Regex.Match(
                text,
                @"₹\s*[\d,]+",
                RegexOptions.IgnoreCase);

        if (rupee.Success)
            return rupee.Value.Trim();

        var inr =
            Regex.Match(
                text,
                @"\bINR\s*[\d,]+",
                RegexOptions.IgnoreCase);

        if (inr.Success)
            return inr.Value.Trim();

        var rs =
            Regex.Match(
                text,
                @"\bRs\.?\s*[\d,]+",
                RegexOptions.IgnoreCase);

        if (rs.Success)
            return rs.Value.Trim();

        return "Not available";
    }

    // ============================================================
    // PASSENGERS
    // ============================================================

    public Task PreparePassengersAsync(
        BookingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    // ============================================================
    // PROTECTED STEP
    // ============================================================

    public Task StopAtProtectedStepAsync(
        CancellationToken cancellationToken = default)
    {
        // CAPTCHA, OTP, payment and other protected actions
        // remain user-controlled.
        return Task.CompletedTask;
    }

    // ============================================================
    // CLOSE
    // ============================================================

    public async Task CloseAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();

        _browser = null;
        _playwright = null;
        _page = null;
    }

    // ============================================================
    // CLASS MAPPING
    // ============================================================

    private static string GetIrctcClassName(
        string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "EA" => "Anubhuti Class (EA)",
            "1A" => "AC First Class (1A)",
            "EV" => "Vistadome AC (EV)",
            "EC" => "Exec. Chair Car (EC)",
            "2A" => "AC 2 Tier (2A)",
            "FC" => "First Class (FC)",
            "3A" => "AC 3 Tier (3A)",
            "3E" => "AC 3 Economy (3E)",
            "VC" => "Vistadome Chair Car (VC)",
            "CC" => "AC Chair car (CC)",
            "SL" => "Sleeper (SL)",
            "VS" => "Vistadome Non AC (VS)",
            "2S" => "Second Sitting (2S)",
            "ALL" => "All Classes",
            "ALL CLASSES" => "All Classes",
            _ => "All Classes"
        };
    }

}

// ================================================================
// AVAILABILITY RESULT
// ================================================================

public sealed class TrainAvailabilityResult
{
    public string TrainNumber { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public DateTime JourneyDate { get; set; }

    public string Availability { get; set; } = string.Empty;

    public string Fare { get; set; } = string.Empty;
}