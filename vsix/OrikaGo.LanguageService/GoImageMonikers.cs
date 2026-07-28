using System;
using Microsoft.VisualStudio.Imaging.Interop;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Image monikers for the custom Go images registered with the Visual Studio
    /// image service through <c>OrikaGoImages.imagemanifest</c>. The GUID and IDs
    /// here must stay in sync with the &lt;Symbols&gt; section of that manifest.
    /// </summary>
    internal static class GoImageMonikers
    {
        private static readonly Guid ImagesGuid = new Guid("{7DDCABCB-7E1F-442F-8025-401F9CC88093}");

        /// <summary>Solution Explorer project-root icon for Go (.goproj) projects.</summary>
        public static ImageMoniker GoProjectNode
        {
            get { return new ImageMoniker { Guid = ImagesGuid, Id = 1 }; }
        }

        /// <summary>Solution Explorer item icon for .go source files.</summary>
        public static ImageMoniker GoFileNode
        {
            get { return new ImageMoniker { Guid = ImagesGuid, Id = 2 }; }
        }
    }
}
