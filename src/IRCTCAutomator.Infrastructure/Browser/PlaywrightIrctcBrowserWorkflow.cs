using System.Globalization;
using System.Text.RegularExpressions;
using IRCTCAutomator.Core;
using IRCTCAutomator.Models;
using Microsoft.Playwright;

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

        _browser = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = false
            });

        _page = await _browser.NewPageAsync();

        await _page.GotoAsync(
            TrainSearchUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            });

        // IRCTC is Angular.
        // Do NOT wait for NetworkIdle.
        await _page.WaitForTimeoutAsync(1500);

        await SelectEnglishAsync();
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
            // Try Enter as fallback.
        }

        try
        {
            await _page.Keyboard.PressAsync("Enter");

            await _page.WaitForTimeoutAsync(1000);
        }
        catch
        {
            // Dialog may not be present.
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
        // FROM STATION
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
        // TO STATION
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
        // SEARCH TRAINS
        // ========================================================

        await SearchTrainsAsync();

        // ========================================================
        // FIND PREFERRED TRAIN
        // ========================================================

        await SelectConfiguredTrainAsync(
            configuration);
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

        // --------------------------------------------------------
        // Exact station suggestion
        // --------------------------------------------------------

        var exact =
            _page.GetByText(
                station,
                new()
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
                // Continue.
            }
        }

        // --------------------------------------------------------
        // Search autocomplete options
        // --------------------------------------------------------

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
                // Continue.
            }
        }

        // --------------------------------------------------------
        // Keyboard operation is performed by Playwright.
        // User does not need to touch the keyboard.
        // --------------------------------------------------------

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
                new()
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
                // Continue.
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

        // Find visible "All Classes" dropdown.
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
                // Continue.
            }
        }

        // Fallback.
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
                    // Continue.
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
                new()
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
                // Continue.
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

        for (var i = 0; i < count; i++)
        {
            var button =
                buttons.Nth(i);

            try
            {
                if (!await button.IsVisibleAsync())
                    continue;

                await button.ClickAsync(
                    new LocatorClickOptions
                    {
                        Force = true,
                        Timeout = 10000
                    });

                await _page.WaitForTimeoutAsync(3000);

                return;
            }
            catch
            {
                // Continue.
            }
        }

        throw new InvalidOperationException(
            "Search Trains button was not found.");
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
            configuration.TrainPreferences.PreferredTrainNumbers;

        if (preferred is null || preferred.Count == 0)
        {
            throw new InvalidOperationException(
                "No preferred train number is configured.");
        }

        foreach (var configuredTrain in preferred)
        {
            var trainNumber = configuredTrain.ToString().Trim();

            if (string.IsNullOrWhiteSpace(trainNumber))
                continue;

            // ============================================================
            // FIND THE EXACT TRAIN
            // ============================================================

            var trainCard =
                await FindTrainCardAsync(trainNumber);

            if (trainCard is null)
                continue;

            await trainCard.ScrollIntoViewIfNeededAsync();
            await _page.WaitForTimeoutAsync(500);

            // ============================================================
            // CLASS
            // ============================================================

            var className =
                GetIrctcClassName(configuration.Journey.Class);

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

            // Click the requested class.
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
                // It may already be selected.
            }

            await _page.WaitForTimeoutAsync(1000);

            // ============================================================
            // FIND REFRESH FOR THIS EXACT TRAIN + EXACT CLASS
            // ============================================================

            ILocator? refreshButton = null;

            // Find every visible "Refresh" on the page.
            var refreshes =
    _page.GetByText(
        "Refresh",
        new PageGetByTextOptions
        {
            Exact = true
        });

            var refreshCount =
                await refreshes.CountAsync();

            for (var i = 0; i < refreshCount; i++)
            {
                try
                {
                    var refresh =
                        refreshes.Nth(i);

                    if (!await refresh.IsVisibleAsync())
                        continue;

                    // ----------------------------------------------------
                    // Walk UP from this Refresh.
                    //
                    // We want an ancestor whose text contains BOTH:
                    //
                    //   12724
                    //   AC 3 Tier (3A)
                    //
                    // This prevents 12650's Refresh from being selected.
                    // ----------------------------------------------------

                    var ancestors =
                        refresh.Locator("xpath=ancestor::*");

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

                            if (string.IsNullOrWhiteSpace(text))
                                continue;

                            var hasTrain =
                                text.Contains(
                                    trainNumber,
                                    StringComparison.OrdinalIgnoreCase);

                            var hasClass =
                                text.Contains(
                                    className,
                                    StringComparison.OrdinalIgnoreCase);

                            if (!hasTrain || !hasClass)
                                continue;

                            // ------------------------------------------------
                            // We found the Refresh belonging to:
                            //
                            // TELANGANA EXP (12724)
                            // AC 3 Tier (3A)
                            // ------------------------------------------------

                            refreshButton = refresh;

                            break;
                        }
                        catch
                        {
                        }
                    }

                    if (refreshButton is not null)
                        break;
                }
                catch
                {
                }
            }

            if (refreshButton is null)
            {
                throw new InvalidOperationException(
                    $"Refresh button for train {trainNumber} " +
                    $"and class '{className}' was not found.");
            }

            // ============================================================
            // CLICK ONLY THE CORRECT REFRESH
            // ============================================================

            await refreshButton.ScrollIntoViewIfNeededAsync();

            await refreshButton.ClickAsync(
                new LocatorClickOptions
                {
                    Force = true,
                    Timeout = 10000
                });

            // ============================================================
            // WAIT FOR AVAILABILITY
            // ============================================================

            await _page.WaitForTimeoutAsync(2000);

            // ============================================================
            // READ AVAILABILITY + FARE
            // ============================================================

            var result =
                await ReadTrainAvailabilityAsync(
                    trainCard,
                    trainNumber,
                    className,
                    configuration.Journey.JourneyDate);

            LastAvailabilityResult = result;

            return;
        }

        // ================================================================
        // DIAGNOSTIC
        // ================================================================

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
    // FIND TRAIN CARD
    // ============================================================

    private async Task<ILocator?> FindTrainCardAsync(string trainNumber)
    {
        if (_page is null)
            return null;

        trainNumber = trainNumber.Trim();

        // Find the actual train heading, e.g.
        // TELANGANA EXP (12724)
        var headings = _page.Locator(
            $"text=/\\({Regex.Escape(trainNumber)}\\)/");

        var headingCount = await headings.CountAsync();

        for (var i = 0; i < headingCount; i++)
        {
            try
            {
                var heading = headings.Nth(i);

                if (!await heading.IsVisibleAsync())
                    continue;

                var headingText =
                    (await heading.InnerTextAsync()).Trim();

                if (!headingText.Contains(
                        trainNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Walk upward and find the SMALLEST useful container
                // containing:
                //   1. this train number
                //   2. Book Now
                //   3. exactly ONE Book Now button
                //
                // This prevents the parent container containing
                // multiple trains from being selected.
                var ancestors =
                    heading.Locator("xpath=ancestor::*");

                var ancestorCount =
                    await ancestors.CountAsync();

                for (var a = 0; a < ancestorCount; a++)
                {
                    var ancestor = ancestors.Nth(a);

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

                        var bookButtons =
                            ancestor.GetByRole(
                                AriaRole.Button,
                                new()
                                {
                                    Name = "Book Now",
                                    Exact = true
                                });

                        var bookCount =
                            await bookButtons.CountAsync();

                        // The correct train container should contain
                        // exactly one Book Now button.
                        if (bookCount != 1)
                            continue;

                        // Make sure this isn't a huge container
                        // containing another train number.
                        var trainMatches =
                            Regex.Matches(
                                text,
                                @"\((\d{4,6})\)");

                        var trainNumbers =
                            trainMatches
                                .Select(m => m.Groups[1].Value)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                        if (trainNumbers.Count != 1 ||
                            !string.Equals(
                                trainNumbers[0],
                                trainNumber,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        return ancestor;
                    }
                    catch
                    {
                        // Try next ancestor.
                    }
                }
            }
            catch
            {
                // Try next heading.
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
                new()
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
                // Continue.
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

        await _page.WaitForTimeoutAsync(1000);

        // ========================================================
        // AVAILABILITY
        // ========================================================

        var availability =
            "Not available";

        // Exact structure observed on IRCTC:
        //
        // <div class="WL col-xs-12">
        //     <strong>WL39</strong>
        // </div>

        var wl =
            trainCard.Locator(
                ".WL.col-xs-12 strong");

        var count =
            await wl.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var element =
                wl.Nth(i);

            try
            {
                if (!await element.IsVisibleAsync())
                    continue;

                var text =
                    (await element.InnerTextAsync()).Trim();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    availability = text;

                    break;
                }
            }
            catch
            {
                // Continue.
            }
        }

        // ========================================================
        // FALLBACK AVAILABILITY
        // ========================================================

        if (availability == "Not available")
        {
            var statusElements =
                trainCard.Locator(
                    "strong, span, div");

            count =
                await statusElements.CountAsync();

            for (var i = 0; i < count; i++)
            {
                var element =
                    statusElements.Nth(i);

                try
                {
                    if (!await element.IsVisibleAsync())
                        continue;

                    var text =
                        (await element.InnerTextAsync()).Trim();

                    if (Regex.IsMatch(
                            text,
                            @"^WL\d+$",
                            RegexOptions.IgnoreCase))
                    {
                        availability = text;

                        break;
                    }

                    if (Regex.IsMatch(
                            text,
                            @"^RAC\d+$",
                            RegexOptions.IgnoreCase))
                    {
                        availability = text;

                        break;
                    }

                    if (text.Contains(
                            "AVAILABLE",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        availability = text;

                        break;
                    }
                }
                catch
                {
                    // Continue.
                }
            }
        }

        // ========================================================
        // FARE
        // ========================================================

        var fare =
            await ExtractFareFromTrainCardAsync(
                trainCard);

        // ========================================================
        // DEBUG
        // ========================================================

        var debugText =
            await trainCard.InnerTextAsync();

        var debugPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "selected-train-debug.txt");

        await File.WriteAllTextAsync(
            debugPath,
            debugText);

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

        // Prefer ₹
        var rupee =
            Regex.Match(
                text,
                @"₹\s*[\d,]+",
                RegexOptions.IgnoreCase);

        if (rupee.Success)
            return rupee.Value.Trim();

        // INR
        var inr =
            Regex.Match(
                text,
                @"\bINR\s*[\d,]+",
                RegexOptions.IgnoreCase);

        if (inr.Success)
            return inr.Value.Trim();

        // Rs
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