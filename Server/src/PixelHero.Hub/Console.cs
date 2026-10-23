using PixelHero.Hub.Controllers;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Hub
{
    /// <summary>console.ts: the hub's stdin commands (/server, /player [username]).</summary>
    public sealed class Console
    {
        readonly Models models;

        public Console(Models models)
        {
            this.models = models;

            var post = Node.Post;

            // Without a running event loop (tests) there is no stdin to read.
            if (post == null) return;

            var thread = new Thread(() =>
            {
                string line;
                while ((line = System.Console.ReadLine()) != null)
                {
                    var data = line;
                    post(() => HandleData(data));
                }
            }) { IsBackground = true, Name = "stdin" };

            thread.Start();
        }

        void HandleData(string data)
        {
            var message = data.Replace("\r", "").Replace("\n", "");
            var type = message.Length > 0 ? message[0] : '\0';

            if (type != '/') return;

            var blocks = message.Substring(1).Split(' ').ToList();
            var command = blocks[0];
            blocks.RemoveAt(0);

            if (string.IsNullOrEmpty(command)) return;

            switch (command)
            {
                case "server":
                    System.Console.WriteLine(Describe(models.FindEmptyServer()));
                    return;

                case "player":
                {
                    var username = string.Join(" ", blocks);

                    if (string.IsNullOrEmpty(username))
                    {
                        Log.Warning("Malformed command - Format: /player [username]");
                        return;
                    }

                    System.Console.WriteLine(Describe(models.FindPlayer(username)));

                    break;
                }
            }
        }

        /// <summary>console.log(server): the server's information, or undefined.</summary>
        static string Describe(Model.Server server) => server == null ? "undefined" : JsJson.Stringify(server.Serialize());
    }
}
