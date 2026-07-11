using System;
using System.Collections.Generic;
using System.Linq;

namespace BedrockRender.Demo
{
    /// <summary>Parsed command-line options shared by the UI and headless render paths.</summary>
    public sealed class RenderOptions
    {
        public string SaveDir { get; set; } = "";
        public int Dimension { get; set; } = 0;          // 0=Overworld, 1=Nether, 2=End
        public ViewMode Mode { get; set; } = ViewMode.Surface;
        public int Scale { get; set; } = 4;              // pixels per block column
        public string Output { get; set; } = "world.png"; // only used with --save

        public static RenderOptions Parse(IEnumerable<string> args)
        {
            var opts = new RenderOptions();
            var list = args.ToList();
            if (list.Count > 0 && !list[0].StartsWith("--"))
                opts.SaveDir = list[0];

            for (int i = 0; i < list.Count; i++)
            {
                switch (list[i])
                {
                    case "--dim":
                    case "--dimension":
                        if (i + 1 < list.Count)
                            opts.Dimension = ParseDimension(list[++i]);
                        break;
                    case "--mode":
                        if (i + 1 < list.Count && Enum.TryParse<ViewMode>(list[++i], true, out var m))
                            opts.Mode = m;
                        break;
                    case "--scale":
                        if (i + 1 < list.Count && int.TryParse(list[++i], out var s) && s > 0)
                            opts.Scale = s;
                        break;
                    case "--save":
                        if (i + 1 < list.Count)
                            opts.Output = list[++i];
                        break;
                }
            }
            return opts;
        }

        private static int ParseDimension(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "nether": case "1": return 1;
                case "end": case "2": return 2;
                case "overworld": case "0": default: return 0;
            }
        }
    }
}
