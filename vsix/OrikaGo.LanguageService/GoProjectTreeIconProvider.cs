using System;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.VisualStudio.ProjectSystem;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// CPS tree customization for Go (.goproj) projects: supplies the Solution
    /// Explorer icon for the project root node and for .go source file nodes.
    ///
    /// This is the public, documented mechanism for external extensions
    /// (<see cref="IProjectTreePropertiesProvider"/>); the managed project system's
    /// own <c>IProjectImageProvider</c>/<c>ProjectImageKey</c> pipeline is internal
    /// to Microsoft.VisualStudio.ProjectSystem.Managed and cannot be exported
    /// against from outside that assembly.
    ///
    /// [Order(1000)]: providers run in ascending order and later providers override
    /// earlier ones. dotnet/project-system deliberately keeps its own providers at
    /// order 10-30 (see its ProjectSystem/Order.cs: "let other 1st and 3rd party
    /// components have a higher precedence than us"), and the official CPS template
    /// uses 1000 for third-party overrides, so 1000 wins over the default C#
    /// project-root icon applied by ProjectRootImageProjectTreePropertiesProvider.
    /// </summary>
    [Export(typeof(IProjectTreePropertiesProvider))]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoProjectTreeIconProvider : IProjectTreePropertiesProvider
    {
        public void CalculatePropertyValues(
            IProjectTreeCustomizablePropertyContext propertyContext,
            IProjectTreeCustomizablePropertyValues propertyValues)
        {
            if (propertyValues.Flags.Contains(ProjectTreeFlags.Common.ProjectRoot))
            {
                ProjectImageMoniker icon = GoImageMonikers.GoProjectNode.ToProjectSystemType();
                propertyValues.Icon = icon;
                propertyValues.ExpandedIcon = icon;
                return;
            }

            // .go files reach the tree as None items (Microsoft.NET.Sdk's default
            // None glob; see Orika.NET.Sdk Sdk.props), so match on the file
            // extension rather than an item type. Folders are excluded explicitly.
            if (!propertyValues.Flags.Contains(ProjectTreeFlags.Common.Folder)
                && propertyValues.Flags.Contains(ProjectTreeFlags.Common.FileSystemEntity)
                && string.Equals(Path.GetExtension(propertyContext.ItemName), ".go", StringComparison.OrdinalIgnoreCase))
            {
                propertyValues.Icon = GoImageMonikers.GoFileNode.ToProjectSystemType();
            }
        }
    }
}
