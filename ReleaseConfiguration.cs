using System;

namespace Traymetry
{
    internal static class ReleaseConfiguration
    {
        internal const string GitHubOwner = "Alsoilia";
        internal const string GitHubRepository = "Traymetry";
        internal const string UpdateAssetName = "Traymetry.exe";
        internal const string UpdateManifestName = "Traymetry-update-manifest.txt";
        internal const string UpdateSignatureName = "Traymetry-update-manifest.sig";
        internal const string UpdateSigningKeyFingerprint =
            "57AA94063CACF527DB73CA0B307A2501EA2A9DDF6113EA50A5F412F6D6AD8100";
        internal const string UpdateSigningPublicKeyXml =
            "<RSAKeyValue><Modulus>xx+g4MNCRvoa/z1LlZFNJLN0d43ymRagz/hduGn7zfIlrXJhYLmILNGXsCODtFyg66JQAjOmdbTxuCuT6y6Q8UFr713d3roySG6yxBt2QkgPB1Mckpe80BjjVeTGB26/r1t7c9svv9IVW3RcPTaS93s/kXffOrLxecqndsTjZr1EIuqRdbGlF5pTdPe/Inl3wXQLy+n3WoDcLAqyavuABnXkYVeigfz7L098ns4EdI+T43PfjGfmYcJZuUIuTrpbQ9mXQ3NuBqQUmts7rjKTO9oGkXZgqpCC+cClNT41H+MZDo8bdhNhDy3tLtgJUojReUBz+WK5hVO0gjBAS6v5gfjdWqsW1MG4Y/z0HpoK6wrPV7PO5cbKzlFwfRxU8bOIvUiuj2JT8RMEwDw3wJieLHtmhBjg3d2fMcPeHQxNBVG2Acotk4HPNKNvVFNrJOFCkBGMdhiUcV8NZsGE6FtKzC7ivadN5HV0uP8nasHO7Rqyrn+BUIHQy5bt4BgQyXqt</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        internal static string ReleasesApiUrl
        {
            get
            {
                return "https://api.github.com/repos/" + GitHubOwner + "/" +
                    GitHubRepository + "/releases?per_page=20";
            }
        }

        internal static string ReleasesPageUrl
        {
            get
            {
                return "https://github.com/" + GitHubOwner + "/" +
                    GitHubRepository + "/releases";
            }
        }

        // This address intentionally points to one public support section.
        // It can later route to Boosty, DonationAlerts, Ko-fi or Sponsors
        // without changing the update implementation.
        internal static string SupportUrl
        {
            get
            {
                return "https://github.com/" + GitHubOwner + "/" +
                    GitHubRepository + "#support-traymetry";
            }
        }
    }
}
