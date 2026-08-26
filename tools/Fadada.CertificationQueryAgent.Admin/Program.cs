// Offline administration entry point for local accounts; secrets are accepted only through protected input.
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Infrastructure.Authentication;
using Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;

Environment.ExitCode = await RunAsync(args, CancellationToken.None);

static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
{
    if (arguments.Length < 3 || !string.Equals(arguments[0], "user", StringComparison.Ordinal))
    {
        WriteUsage();
        return 2;
    }

    var connectionString = Environment.GetEnvironmentVariable("FDD_STORE_CONNECTION_STRING");
    var profileValue = Environment.GetEnvironmentVariable("FDD_STORE_PROFILE");
    if (string.IsNullOrWhiteSpace(connectionString) ||
        !Enum.TryParse<SqlPersistenceProfile>(profileValue, ignoreCase: false, out var profile))
    {
        Console.Error.WriteLine("ADMIN_CONFIGURATION_INVALID");
        return 2;
    }

    try
    {
        var factory = new SqlServerConnectionFactory(new SqlServer2012Options(connectionString, profile));
        var readiness = await factory.CheckReadinessAsync(cancellationToken);
        if (!readiness.IsReady)
        {
            Console.Error.WriteLine(readiness.ErrorCode ?? "STORE_NOT_READY");
            return 3;
        }

        IAccountAdministrationService accounts = new LocalAccountService(new SqlServerUserStore(factory));
        var actor = $"local-admin:{Environment.UserName}";
        var verb = arguments[1];
        var userName = arguments[2];
        AccountAdministrationResult result;
        switch (verb)
        {
            case "create":
                var displayName = ReadOption(arguments, "--display-name");
                if (displayName is null)
                {
                    WriteUsage();
                    return 2;
                }

                result = await accounts.CreateAsync(
                    userName,
                    displayName,
                    ReadConfirmedPassword(),
                    actor,
                    cancellationToken);
                break;
            case "reset-password":
                result = await accounts.ResetPasswordAsync(
                    userName,
                    ReadConfirmedPassword(),
                    actor,
                    cancellationToken);
                break;
            case "disable":
                result = await accounts.SetActiveAsync(userName, false, actor, cancellationToken);
                break;
            case "enable":
                result = await accounts.SetActiveAsync(userName, true, actor, cancellationToken);
                break;
            default:
                WriteUsage();
                return 2;
        }

        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.ErrorCode ?? "ACCOUNT_OPERATION_FAILED");
            return 4;
        }

        Console.WriteLine("ACCOUNT_OPERATION_SUCCEEDED");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("ADMIN_CANCELLED");
        return 5;
    }
    catch
    {
        Console.Error.WriteLine("ADMIN_OPERATION_FAILED");
        return 5;
    }
}

static string? ReadOption(string[] arguments, string option)
{
    var index = Array.IndexOf(arguments, option);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string ReadConfirmedPassword()
{
    var password = ReadPassword("Password: ");
    var confirmation = ReadPassword("Confirm password: ");
    if (!string.Equals(password, confirmation, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("ACCOUNT_PASSWORD_CONFIRMATION_MISMATCH");
    }

    return password;
}

static string ReadPassword(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? string.Empty;
    }

    var buffer = new List<char>(128);
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return new string(buffer.ToArray());
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Count > 0)
            {
                buffer.RemoveAt(buffer.Count - 1);
            }
            continue;
        }

        if (!char.IsControl(key.KeyChar) && buffer.Count < 128)
        {
            buffer.Add(key.KeyChar);
        }
    }
}

static void WriteUsage() => Console.Error.WriteLine(
    "Usage: user create <username> --display-name <name> | user reset-password <username> | user disable <username> | user enable <username>");
