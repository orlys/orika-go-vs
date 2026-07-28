using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Content type used to activate the Go LSP client.
    /// <para>
    /// Visual Studio 2026 ships a TextMate grammar for Go and resolves it by content
    /// type name (Starterkit\Extensions\go\syntaxes\go.json). Declaring a private
    /// content type and remapping the ".go" file extension to it therefore KILLS
    /// syntax colorization, because no grammar file matches the private name.
    /// So this extension does not remap ".go" at all: the language client attaches to
    /// the built-in content type below, leaving the grammar association untouched.
    /// </para>
    /// </summary>
    internal static class GoContentTypeDefinitions
    {
        /// <summary>
        /// Name of the built-in Go content type that carries the TextMate grammar.
        /// </summary>
        public const string ContentTypeName = "go";
    }
}
