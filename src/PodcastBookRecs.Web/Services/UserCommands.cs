using Microsoft.AspNetCore.Identity;
using PodcastBookRecs.Core.Accounts;

namespace PodcastBookRecs.Web.Services;

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
        """;

    /// <summary>Runs one command and returns the process exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args is not [_, ("add" or "reset-password") and var command, var email])
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

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                await Console.Error.WriteLineAsync(error.Description);
            }

            return 1;
        }

        Console.WriteLine(command == "add" ? $"Created {email}." : $"Reset the password for {email} and cleared any lockout.");
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
