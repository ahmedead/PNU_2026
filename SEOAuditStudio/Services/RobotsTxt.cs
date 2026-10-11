using System.Text.RegularExpressions;

namespace SEOAuditStudio.Services
{
    /// <summary>
    /// Minimal robots.txt reader: the rules for "*" (or our own user agent), with Allow/Disallow
    /// resolved by longest match and * / $ wildcards as Google documents them.
    /// </summary>
    public sealed class RobotsTxt
    {
        private readonly List<(bool Allow, string Pattern, Regex Regex)> _rules = new();

        public List<string> Sitemaps { get; } = new();
        public int? CrawlDelaySeconds { get; private set; }

        public static RobotsTxt Empty { get; } = new();

        public static RobotsTxt Parse(string content, string agentToken = "seoauditstudio")
        {
            var groups = new List<(List<string> Agents, List<(bool, string)> Rules, int? Delay)>();
            List<string>? currentAgents = null;
            List<(bool, string)>? currentRules = null;
            int? currentDelay = null;
            bool lastWasAgent = false;
            var robots = new RobotsTxt();

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line[..hash];
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();

                switch (key)
                {
                    case "user-agent":
                        if (!lastWasAgent || currentAgents == null)
                        {
                            currentAgents = new List<string>();
                            currentRules = new List<(bool, string)>();
                            currentDelay = null;
                            groups.Add((currentAgents, currentRules, null));
                        }
                        currentAgents.Add(value.ToLowerInvariant());
                        lastWasAgent = true;
                        break;
                    case "allow":
                    case "disallow":
                        lastWasAgent = false;
                        if (currentRules != null && value.Length > 0) currentRules.Add((key == "allow", value));
                        break;
                    case "crawl-delay":
                        lastWasAgent = false;
                        if (currentAgents != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            currentDelay = (int)Math.Ceiling(d);
                            groups[^1] = (groups[^1].Agents, groups[^1].Rules, currentDelay);
                        }
                        break;
                    case "sitemap":
                        if (value.Length > 0) robots.Sitemaps.Add(value);
                        break;
                    default:
                        lastWasAgent = false;
                        break;
                }
            }

            var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && agentToken.Contains(a)));
            if (group.Agents == null) group = groups.FirstOrDefault(g => g.Agents.Contains("*"));
            if (group.Agents != null)
            {
                robots.CrawlDelaySeconds = group.Delay;
                foreach (var (allow, pattern) in group.Rules)
                    robots._rules.Add((allow, pattern, ToRegex(pattern)));
            }
            return robots;
        }

        public bool IsAllowed(Uri uri)
        {
            var path = uri.PathAndQuery;
            (bool Allow, int Length)? best = null;
            foreach (var rule in _rules)
            {
                if (!rule.Regex.IsMatch(path)) continue;
                int len = rule.Pattern.Length;
                // Longest pattern wins; on a tie Allow wins
                if (best == null || len > best.Value.Length || (len == best.Value.Length && rule.Allow))
                    best = (rule.Allow, len);
            }
            return best?.Allow ?? true;
        }

        private static Regex ToRegex(string pattern)
        {
            bool anchored = pattern.EndsWith('$');
            var body = anchored ? pattern[..^1] : pattern;
            var regex = "^" + Regex.Escape(body).Replace(@"\*", ".*") + (anchored ? "$" : "");
            return new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
    }
}
