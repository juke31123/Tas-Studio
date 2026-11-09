using System;
using System.Collections.Generic;
using System.Globalization;

namespace Igtap.Tas.Core.Search
{
    public sealed class ChainAction
    {
        public FrameInput Input;
        public int Minimum, Maximum;
    }
    public static class InputChain
    {
        // N/R/L/U/D/UR/UL/DR/DL with optional +J/+Dash and bounded durations.
        public static ChainAction[] Parse(string source, SearchAbilities abilities, int horizon)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 4000) throw new ArgumentException("Enter a bounded input chain.");
            var result = new List<ChainAction>(); int minimumTotal = 0;
            foreach (string part in source.Split(';'))
            {
                var fields = part.Trim().Split(':');
                if (fields.Length != 2) throw new ArgumentException("Chain syntax: R:1-30; R+J:1-6; R:1-40");
                var range = fields[1].Trim().Split('-');
                if (range.Length < 1 || range.Length > 2 || !SearchFiles.TryInt(range[0], 1, horizon, out int min) ||
                    !SearchFiles.TryInt(range[range.Length - 1], min, horizon, out int max)) throw new ArgumentException("Each action needs a finite duration or min-max range within the horizon.");
                var tokens = fields[0].Trim().ToUpperInvariant().Split('+'); var input = new FrameInput();
                switch (tokens[0])
                {
                    case "N": break; case "R": input.X = 1; break; case "L": input.X = -1; break;
                    case "U": input.Y = 1; break; case "D": input.Y = -1; break;
                    case "UR": case "RU": input.X = input.Y = 1; break;
                    case "UL": case "LU": input.X = -1; input.Y = 1; break;
                    case "DR": case "RD": input.X = 1; input.Y = -1; break;
                    case "DL": case "LD": input.X = input.Y = -1; break;
                    default: throw new ArgumentException("Unknown chain direction: " + tokens[0]);
                }
                for (int i = 1; i < tokens.Length; i++)
                    if (tokens[i] == "J" && (abilities & SearchAbilities.Jump) != 0) input.Held |= Buttons.Jump;
                    else if (tokens[i] == "DASH" && (abilities & SearchAbilities.Dash) != 0) input.Held |= Buttons.Dash;
                    else throw new ArgumentException("Chain action is unknown or disabled: " + tokens[i]);
                result.Add(new ChainAction { Input = input, Minimum = min, Maximum = max }); minimumTotal += min;
                if (result.Count > 32 || minimumTotal > horizon) throw new ArgumentException("Chain exceeds the action/horizon limit.");
            }
            return result.ToArray();
        }
        public static long Combinations(ChainAction[] actions)
        {
            long count = 1;
            foreach (var action in actions) { int choices = action.Maximum - action.Minimum + 1; if (count > long.MaxValue / choices) return long.MaxValue; count *= choices; }
            return count;
        }
    }
}
