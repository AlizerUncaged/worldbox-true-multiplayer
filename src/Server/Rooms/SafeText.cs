using System.Text;

namespace WorldboxMP.Server.Rooms
{
    /// <summary>
    /// Player-supplied display text (room names) is shown in every client's Unity UI,
    /// which renders rich-text tags. Strip tags and control characters so nobody can
    /// post a 500px name or invisible text into other players' room lists.
    /// </summary>
    public static class SafeText
    {
        public static string Clean(string s, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Trim())
            {
                if (char.IsControl(c) || c == '<' || c == '>') continue;
                sb.Append(c);
                if (sb.Length >= maxLen) break;
            }
            return sb.ToString().Trim();
        }
    }
}
