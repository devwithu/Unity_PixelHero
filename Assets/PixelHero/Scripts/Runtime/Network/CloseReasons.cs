using System.Collections.Generic;

namespace PixelHero.Network
{
    /// <summary>
    /// Why the server closed the connection: code 1010 with a reason string (server network/connection.ts reject),
    /// shown by client/src/network/messages.ts handleCloseReason. The English texts are the original's; the UI
    /// stage looks the reason up for its translations.
    /// </summary>
    public static class CloseReasons
    {
        /// <summary>The close code the server uses when it rejects a connection for a reason.</summary>
        public const int RejectCode = 1010;

        public const string Unknown = "An unknown error has occurred, please submit a bug report.";

        public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["worldfull"] = "The servers are currently full!",
            ["error"] = "The server has responded with an error!",
            ["banned"] = "Your account has been disabled!",
            ["disabledregister"] = "Registration is currently disabled.",
            ["development"] = "The game is currently in development mode.",
            ["disallowed"] = "The server is currently not accepting connections!",
            ["maintenance"] = "PixelHero is currently under maintenance.",
            ["userexists"] = "The username you have entered already exists.",
            ["emailexists"] = "The email you have entered is not available.",
            ["invalidinput"] = "The input you have entered is invalid. Please do not use special characters.",
            ["swappedworlds"] = "You have recently swapped worlds, please wait 15 seconds.",
            ["loggedin"] = "The player is already logged in!",
            ["invalidlogin"] = "You have entered the wrong username or password.",
            ["toofast"] = "You are trying to log in too fast from the same connection.",
            ["timeout"] = "You have been disconnected for being inactive for too long.",
            ["updated"] = "The game has been updated. Please clear your browser cache.",
            ["cheating"] = "An error in client-server syncing has occurred.",
            ["lost"] = "The connection to the server has been lost.",
            ["toomany"] = "Too many devices from your IP address are connected.",
            ["ratelimit"] = "You are sending packets too fast.",
            ["invalidpassword"] = "The password you have entered is invalid."
        };

        public static string Describe(string reason) =>
            Messages.TryGetValue(reason, out var message) ? message : Unknown;
    }
}
