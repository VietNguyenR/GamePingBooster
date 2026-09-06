using System.Reflection;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// What the About screen shows. All of it is constant except the version, which is read from
/// this assembly rather than written out again here - the version comes from application\VERSION
/// through Directory.Build.props, and a second hand-written copy in the UI would be wrong the
/// first time somebody shipped without noticing it.
/// </summary>
public sealed class AboutViewModel
{
    public string Author => "Viet Nguyen";
    public string Email => "vietnguyen010@gmail.com";
    public string RepositoryUrl => "https://github.com/VietNguyenR/GamePingBooster";

    public string LicenceTitle => "MIT License - Copyright (c) 2026 Viet Nguyen";

    /// <summary>
    /// The MIT licence, in full.
    ///
    /// In full rather than "MIT, see LICENSE": MIT requires that the notice and this permission
    /// text travel with the software, and a user who installed a .exe has no LICENSE file in
    /// front of them. It is four sentences; there is room.
    /// </summary>
    public string LicenceText =>
        "Permission is hereby granted, free of charge, to any person obtaining a copy of this " +
        "software and associated documentation files (the \"Software\"), to deal in the Software " +
        "without restriction, including without limitation the rights to use, copy, modify, " +
        "merge, publish, distribute, sublicense, and/or sell copies of the Software, and to " +
        "permit persons to whom the Software is furnished to do so, subject to the following " +
        "conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all copies " +
        "or substantial portions of the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, " +
        "INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A " +
        "PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT " +
        "HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF " +
        "CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE " +
        "OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

    /// <summary>
    /// "Version 0.1.0". Read from the assembly, which Directory.Build.props stamped from VERSION.
    ///
    /// InformationalVersion rather than Assembly/FileVersion, because that is the one that keeps
    /// a pre-release suffix: 0.2.0-beta1 arrives here intact and as 0.2.0.0 in the others.
    /// </summary>
    public string VersionText
    {
        get
        {
            var info = typeof(AboutViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // A build with no version stamped at all is a development build, and saying so is
            // more useful than showing the 1.0.0 the SDK would otherwise invent.
            if (string.IsNullOrWhiteSpace(info) || info == "1.0.0") return "Development build";
            return $"Version {info}";
        }
    }
}
