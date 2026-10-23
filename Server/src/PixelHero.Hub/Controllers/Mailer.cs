using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace PixelHero.Hub.Controllers
{
    /// <summary>
    /// controllers/mailer.ts (nodemailer) with MailKit. Mail is only sent when SMTP_USER is set. SMTP_USE_SECURE means
    /// TLS from the start (port 465), otherwise STARTTLS when the server offers it, as nodemailer does.
    /// </summary>
    public sealed class Mailer
    {
        // Create the transporter if a SMTP user is provided.
        readonly bool transporter = !string.IsNullOrEmpty(Config.Current.smtpUser);

        /// <summary>Sends an email (in the background; a failure is logged).</summary>
        /// <param name="to">Who we are sending the email to.</param>
        /// <param name="subject">The subject of the email.</param>
        /// <param name="text">The text contents of the email.</param>
        public void Send(string to, string subject, string text)
        {
            // We didn't initialize the transporter, so we can't send emails.
            if (!transporter) return;

            var config = Config.Current;
            var post = Node.Post;

            Task.Run(async () =>
            {
                try
                {
                    var message = new MimeMessage();
                    message.From.Add(MailboxAddress.Parse(config.smtpUser));
                    message.To.Add(MailboxAddress.Parse(to));
                    message.Subject = subject;
                    message.Body = new TextPart("plain") { Text = text };

                    using var client = new SmtpClient();

                    await client.ConnectAsync(config.smtpHost, (int)config.smtpPort,
                        config.smtpUseSecure ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable);
                    await client.AuthenticateAsync(config.smtpUser, config.smtpPassword);
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                }
                catch (Exception error)
                {
                    // nodemailer's rejected promise is unhandled in the original; the hub logs it.
                    post(() => Log.Error($"Failed to send email to {to}! {error.Message}"));
                }
            });
        }
    }
}
