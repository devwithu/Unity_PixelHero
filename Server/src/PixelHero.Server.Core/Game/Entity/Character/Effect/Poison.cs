using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>character/effect/poison.ts.</summary>
    public sealed class Poison
    {
        public string name;
        public double damage;
        public double duration;
        public double rate;
        public Modules.PoisonTypes type;
        public double start;

        static JsObject _info;

        /// <summary>Modules.PoisonInfo.</summary>
        static JsObject Info => _info ??= (JsObject)JsData.Convert(PixelHero.Js.JsJson.Parse(ModuleTables.PoisonInfoJson));

        public Poison(Modules.PoisonTypes type, double? start = null)
        {
            this.type = type;
            // start = Date.now() as a default; `if (!this.start)` then replaces 0 (and NaN) too.
            this.start = start ?? Node.DateNow();
            if (this.start == 0 || double.IsNaN(this.start)) this.start = Node.DateNow();

            var info = Info.Obj(((int)type).ToString()) ??
                       throw new Common.JsTypeError("Cannot read properties of undefined (reading 'name')");
            name = info.Str("name");
            damage = info.Num("damage") ?? double.NaN;
            duration = (info.Num("duration") ?? double.NaN) * 1000; // Convert to milliseconds
            rate = (info.Num("rate") ?? double.NaN) * 1000; // Convert to milliseconds
        }

        public bool Expired()
        {
            if (duration < 0) return false;

            return Node.DateNow() - start >= duration;
        }

        public double GetRemainingTime() => duration - (duration - (Node.DateNow() - start));
    }
}
