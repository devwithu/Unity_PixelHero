
using System.Text;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Hub.Controllers
{
    /// <summary>
    /// controllers/api.ts: the hub's HTTP API (Express with cors). It only listens when API_ENABLED or HUB_ENABLED is
    /// set, like the original. Sentry is not ported.
    /// </summary>
    public sealed class Api
    {
        /// <summary>The https listener's certificate and intermediates (set by Program when HUB_SSL_PORT is on).</summary>
        public static (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
            System.Security.Cryptography.X509Certificates.X509Certificate2Collection intermediates) Secure;

        readonly Models models;
        readonly Mailer mailer;
        readonly Cache cache;
        readonly JsObject mobs;

        public Api(Models models, Mailer mailer, Cache cache, JsObject mobs)
        {
            this.models = models;
            this.mailer = mailer;
            this.cache = cache;
            this.mobs = mobs;

            var apiEnabled = Config.Current.apiEnabled || Config.Current.hubEnabled;

            // API must be initialized if the hub is enabled.
            if (!apiEnabled) return;

            var app = new Express { cors = true };

            // C# hub only: the same API over https (HUB_SSL_PORT) for clients on https pages.
            if (Secure.certificate != null)
            {
                app.securePort = Config.Current.hubSslPort;
                app.certificate = Secure.certificate;
                app.intermediates = Secure.intermediates;
            }

            HandleRouter(app);

            app.Listen(Config.Current.hubPort, () =>
            {
                Log.Notice($"{Config.Current.name} hub API is now listening on {JsString.Of(Config.Current.hubPort)}.");

                if (Secure.certificate != null)
                    Log.Notice($"{Config.Current.name} hub API (https) is now listening on {JsString.Of(Config.Current.hubSslPort)}.");
            });
        }

        /// <summary>The router is where we create all the API endpoints.</summary>
        void HandleRouter(Express router)
        {
            // GET requests
            router.Get("/", HandleRoot);
            router.Get("/server", HandleServer);
            router.Get("/all", HandleAll);
            router.Get("/leaderboards", HandleLeaderboards);

            router.Post("/isOnline", HandleIsOnline);
            router.Post("/api/v1/requestReset", HandleRequestReset);
            router.Post("/api/v1/resetPassword", HandleResetPassword);

            if (!string.IsNullOrEmpty(Config.Current.stripeEndpoint))
            {
                router.Post($"/{Config.Current.stripeEndpoint}", HandleStripe);

                Log.Notice($"Stripe endpoint is enabled at /{Config.Current.stripeEndpoint}.");
            }
        }

        /// <summary>Handles the root origin of the API. This just serves as a check to see if the Hub has initialized correctly.</summary>
        void HandleRoot(ExpressRequest _, ExpressResponse response) =>
            response.Json(new JsObject("status", $"{Config.Current.name} hub is online and functional."));

        /// <summary>Hands out the first server that has space for a player to join.</summary>
        void HandleServer(ExpressRequest _, ExpressResponse response)
        {
            if (!models.HasSpace())
            {
                response.Json(new JsObject("status", "error"));
                return;
            }

            var server = models.FindEmptyServer();

            if (server == null)
            {
                response.Json(new JsObject("status", "error"));
                return;
            }

            response.Json(server.Serialize());
        }

        /// <summary>Returns all the worlds currently online (without players).</summary>
        void HandleAll(ExpressRequest _, ExpressResponse response) =>
            response.Json(models.SerializeServers().ConvertAll(server => (object)server));

        /// <summary>
        /// The leaderboards: total experience, or one skill (?skill=id), one mob's kills (?mob=key) or PvP kills
        /// (?pvp=...). The cache prevents us from having to query the database every time.
        /// </summary>
        void HandleLeaderboards(ExpressRequest request, ExpressResponse response)
        {
            var query = request.query;

            if (JsData.Truthy(query["skill"]))
            {
                var skillId = JsString.ParseInt(JsAny.Key(query["skill"]));

                // Ensure the validity of the skill id we are trying to get (Object.keys(Modules.Skills).length / 2).
                if (double.IsNaN(skillId) || skillId < 0 || skillId > Enum.GetValues<Modules.Skills>().Length)
                {
                    response.Json(new JsObject("error", "invalid"));
                    return;
                }

                // Get the skill experience from the cache.
                cache.GetSkillsExperience(skillId, data => response.Json(new JsObject("status", "success", "list", List(data))));

                return;
            }

            // Handles mob kills aggregation.
            if (JsData.Truthy(query["mob"]))
            {
                var mobKey = JsAny.Key(query["mob"]);

                // Ensure that we are not being spammed with invalid mob keys. (Port fix, ANALYSIS.md §9 #17: the
                // original's `key in mobs` also accepted Object.prototype names such as "constructor".)
                if (string.IsNullOrEmpty(mobKey) || !mobs.Has(mobKey))
                {
                    response.Json(new JsObject("error", "invalid"));
                    return;
                }

                // Get the mob kills from the cache.
                cache.GetMobKills(mobKey, data => response.Json(new JsObject("status", "success", "list", List(data))));

                return;
            }

            // Handles pvp kills aggregation.
            if (JsData.Truthy(query["pvp"]))
            {
                cache.GetPvpData(data => response.Json(new JsObject("status", "success", "list", List(data))));

                return;
            }

            cache.GetTotalExperience(data => response.Json(new JsObject(
                "status", "success",
                "list", List(data),
                "availableMobs", cache.availableMobs)));
        }

        static List<object> List(List<JsObject> data) => data?.ConvertAll(entry => (object)entry);

        /// <summary>Checks if the player is online in any of the servers (other than the asking one).</summary>
        void HandleIsOnline(ExpressRequest request, ExpressResponse response)
        {
            if (!VerifyRequest(request))
            {
                response.Json(new JsObject("error", "invalid"));
                return;
            }

            var username = JsAny.Get(request.body, "username");
            var serverId = JsAny.Get(request.body, "serverId");
            var online = false;

            // Look through all the servers and see if the player is online.
            models.ForEachServer(server =>
            {
                if (serverId is double id && server.id == id) return;

                if (username is string name && server.players.Contains(name)) online = true;
            });

            response.Json(new JsObject("status", "success", "online", online));
        }

        /// <summary>
        /// Handles validation of incoming data, creating a password reset token, and then sending it to the specified
        /// email address.
        /// </summary>
        void HandleRequestReset(ExpressRequest request, ExpressResponse response)
        {
            var email = JsAny.Get(request.body, "email");

            // Verify the email address is valid.
            if (!JsData.Truthy(email) || !Utils.IsEmail(JsAny.Key(email)))
            {
                response.Json(new JsObject("error", "invalid"));
                return;
            }

            var address = JsAny.Key(email);

            // Generate a reset token and send it to the email address.
            cache.database.CreateResetToken(address, (id, token) =>
            {
                // We just return success to prevent brute-force attacks, better for the user to not know if the email exists or not.
                if (id == null || string.IsNullOrEmpty(token))
                {
                    response.Json(new JsObject("status", "success"));
                    return;
                }

                // Send the email to the user.
                mailer.Send(
                    address,
                    "PixelHero Account Password Reset",
                    $"Hello there, you have requested a password reset for your account. Please use the following link to reset your password: {ResetLink(token, id.Id)}");

                // Send a response back to the client.
                response.Json(new JsObject("status", "success"));
            });
        }

        /// <summary>
        /// The reset page's address. The original's mail links to https://pixelhero.com/reset/; RESET_URL (C# hub only)
        /// points it at this port's own page.
        /// </summary>
        static string ResetUrl() => Config.Current.Values["resetUrl"] is string url && url.Length > 0 ? url : "https://pixelhero.com/reset/";

        /// <summary>The reset page with ?token=&id= (or &token=&id= when RESET_URL already has a query, such as ?hub=).</summary>
        static string ResetLink(string token, string id)
        {
            var url = ResetUrl();
            return $"{url}{(url.Contains('?') ? '&' : '?')}token={token}&id={id}";
        }

        /// <summary>
        /// Handles the resetting of password. Verifies that the ID, token, and password are valid and then updates
        /// the password in the database.
        /// </summary>
        void HandleResetPassword(ExpressRequest request, ExpressResponse response)
        {
            var id = JsAny.Get(request.body, "id");
            var token = JsAny.Get(request.body, "token");
            var password = JsAny.Get(request.body, "password");

            // Verify the token, id, and passwords are valid (isValidPassword reads .length, which only a string has here).
            if (!JsData.Truthy(id) || !JsData.Truthy(token) || !(password is string text && Utils.IsValidPassword(text)))
            {
                response.Json(new JsObject("error", "invalid"));
                return;
            }

            // Attempt to reset the password.
            cache.database.ResetPassword(id, token, (string)password,
                status => response.Json(new JsObject("status", status ? "success" : "invalid")));
        }

        /// <summary>
        /// This is the webhook for Stripe payment processor. The original only logs the events it receives. Express's
        /// json parser runs before the route's raw parser, so a JSON body (what Stripe sends) arrives parsed and the
        /// signature check fails, as in the original.
        /// </summary>
        void HandleStripe(ExpressRequest request, ExpressResponse response)
        {
            request.headers.TryGetValue("stripe-signature", out var signature);

            // Send an empty response if we don't have a signature.
            if (string.IsNullOrEmpty(signature))
            {
                response.Send();

                Log.Warning("Stripe signature is missing from request.");
                return;
            }

            try
            {
                // Construct an event based on the request body and signature.
                var stripeEvent = ConstructEvent(request, signature, Config.Current.stripeKeyLocal);
                var type = JsAny.Key(stripeEvent["type"]);

                // Handle events as needed.
                switch (type)
                {
                    case "payment_intent.succeeded":
                        var intentSuccess = JsAny.Get(JsAny.Get(stripeEvent, "data"), "object");

                        System.Console.WriteLine(JsJson.Stringify(intentSuccess));

                        // Relay information to the database/player here.
                        break;

                    default:
                        Log.Warning($"Unhandled Stripe event: {type}");
                        break;
                }
            }
            catch (StripeSignatureError error)
            {
                Log.Error($"Stripe webhook error: {error.Message}");
                response.Status(400).Send($"Webhook Error: {error.Message}");
                return;
            }

            response.Send();
        }

        sealed class StripeSignatureError : Exception
        {
            public StripeSignatureError(string message) : base(message) { }
        }

        /// <summary>
        /// stripe.webhooks.constructEvent(request.body, ...). The body is never the raw payload here: express.json has
        /// already parsed a JSON body (so the route's express.raw skips it), and any other body is left as {}. Stripe's
        /// library refuses an object before it looks at the signature, so every signed request fails with this error.
        /// </summary>
        static JsObject ConstructEvent(ExpressRequest request, string header, string secret) =>
            throw new StripeSignatureError(
                "Webhook payload must be provided as a string or a Buffer (https://nodejs.org/api/buffer.html) instance representing the _raw_ request body.Payload was provided as a parsed JavaScript object instead. \n" +
                "Signature verification is impossible without access to the original signed material. \n\n" +
                "Learn more about webhook signing and explore webhook integration examples for various frameworks at https://github.com/stripe/stripe-node#webhook-signing\n");

        /// <summary>Verifies the integrity of the request and if the tokens are valid.</summary>
        static bool VerifyRequest(ExpressRequest request)
        {
            if (!JsData.Truthy(request.body)) return false;

            var hubAccessToken = JsAny.Get(request.body, "hubAccessToken");
            var serverId = JsAny.Get(request.body, "serverId");

            if (!JsData.Truthy(hubAccessToken) || !JsData.Truthy(serverId)) return false;

            return hubAccessToken is string token && token == Config.Current.hubAccessToken;
        }
    }
}
