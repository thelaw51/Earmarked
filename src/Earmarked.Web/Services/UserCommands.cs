using Microsoft.AspNetCore.Identity;
using Earmarked.Core.Accounts;

namespace Earmarked.Web.Services;

/// <summary>
/// The <c>users</c> command line, run with <c>docker exec -it &lt;web container&gt; entrypoint.sh users ...</c>.
/// Passwords are typed at a prompt, never passed as arguments, so they stay out of shell history and <c>ps</c>.
/// </summary>
internal static class UserCommands
{
    public const string Name = "users";

    private const string Usage = """
        Usage:
          users add <email>               Create a user. Prompts for the password.
          users reset-password <email>    Set a new password and clear any lockout. Prompts for the password.
          users 2fa-enable <email>        Enrol an authenticator app. Prompts for a code from it.
          users 2fa-disable <email>       Turn off two-factor sign-in, for a lost or replaced device.
        """;

    /// <summary>Runs one command and returns the process exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args is not [_, ("add" or "reset-password" or "2fa-enable" or "2fa-disable") and var command, var email])
        {
            await Console.Error.WriteLineAsync(Usage);
            return 64;
        }

        // The UI never creates or migrates the database, and that includes this command.
        if (!await services.GetRequiredService<SchemaStatus>().IsReadyAsync(CancellationToken.None))
        {
            await Console.Error.WriteLineAsync("The database doesn't exist or isn't migrated yet. Start the Worker first.");
            return 1;
        }

        switch (command)
        {
            case "2fa-enable":
                return await EnableTwoFactorAsync(services, email);
            case "2fa-disable":
                await using (var disableScope = services.CreateAsyncScope())
                {
                    var disabled = await disableScope.ServiceProvider.GetRequiredService<UserAdmin>().DisableTwoFactorAsync(email);
                    return await ReportAsync(disabled, $"Turned off two-factor sign-in for {email}. Run 2fa-enable to set it up again.");
                }
        }

        var password = ReadNewPassword();
        if (password is null)
        {
            await Console.Error.WriteLineAsync("The passwords didn't match.");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdmin>();
        var result = command == "add"
            ? await admin.CreateAsync(email, password)
            : await admin.ResetPasswordAsync(email, password);

        return await ReportAsync(result, command == "add" ? $"Created {email}." : $"Reset the password for {email} and cleared any lockout.");
    }

    /// <summary>
    /// Shows a new key, then turns two-factor sign-in on only once a code from the app matches it. Prints the recovery
    /// codes last, since they're shown only once.
    /// </summary>
    private static async Task<int> EnableTwoFactorAsync(IServiceProvider services, string email)
    {
        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdmin>();
        var (started, key) = await admin.StartAuthenticatorSetupAsync(email);
        if (!started.Succeeded || key is null)
        {
            return await ReportAsync(started, "");
        }

        Console.WriteLine($"""
            Add this key to your authenticator app (time-based, 6 digits):

              {AuthenticatorCodes.FormatKey(key)}

            Or turn this link into a QR code, or paste it into a password manager that reads otpauth links:

              {AuthenticatorCodes.EnrolmentUri(email, key)}

            Existing sign-ins end within 5 minutes.

            """);

        // The prompt can be answered again, so a typo doesn't mean starting over with a new key.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Console.Write("Code from the app: ");
            var code = Console.ReadLine() ?? "";
            var (confirmed, recoveryCodes) = await admin.ConfirmAuthenticatorAsync(email, code);
            if (confirmed.Succeeded)
            {
                Console.WriteLine($"""

                    Two-factor sign-in is on for {email}.

                    Recovery codes. Each signs in once in place of an app code. Store them somewhere other than your phone;
                    they won't be shown again:

                    {string.Join(Environment.NewLine, recoveryCodes.Select(c => "  " + c))}
                    """);
                return 0;
            }

            await ReportAsync(confirmed, "");
            if (Console.IsInputRedirected)
            {
                break;
            }
        }

        await Console.Error.WriteLineAsync("Two-factor sign-in was not turned on. Run 2fa-enable again for a new key.");
        return 1;
    }

    /// <summary>Prints the errors, or <paramref name="success"/>, and returns the exit code.</summary>
    private static async Task<int> ReportAsync(IdentityResult result, string success)
    {
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                await Console.Error.WriteLineAsync(error.Description);
            }

            return 1;
        }

        Console.WriteLine(success);
        return 0;
    }

    /// <summary>Prompts twice without echo. Reads one line from stdin instead when input is piped.</summary>
    private static string? ReadNewPassword()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var first = ReadHidden("Password: ");
        var second = ReadHidden("Repeat password: ");
        return first == second ? first : null;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var chars = new List<char>();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }

        Console.WriteLine();
        return new string([.. chars]);
    }
}
