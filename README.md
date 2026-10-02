# IRCTC Automator v1

Personal-use Windows WPF application for preparing an IRCTC booking workflow from configuration.

## Scope
- Opens the official IRCTC train-search page.
- Loads journey, train preferences, and passenger data from `config/booking.json`.
- Keeps credentials out of the JSON configuration; use Windows Credential Manager.
- Browser automation is isolated behind `IIrctcBrowserWorkflow` so IRCTC UI changes are localized.
- The workflow intentionally stops for IRCTC-protected steps such as CAPTCHA/OTP/payment and requires the user to complete those steps.

## Requirements
- Windows 10/11
- Visual Studio 2022 or .NET 10 SDK
- Playwright for .NET

## Run
1. Open `IRCTCAutomator.sln`.
2. Restore/build.
3. Configure `config/booking.json`.
4. Run the WPF app.
5. Use **Start Booking Preparation**.

Do not put an IRCTC password in `booking.json`.
