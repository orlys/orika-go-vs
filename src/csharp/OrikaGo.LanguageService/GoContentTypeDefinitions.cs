using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Declares the editor content type that activates the Go LSP client, and maps
    /// the ".go" file extension onto it.
    /// <para>
    /// Both exports are mandatory. Visual Studio's LSP client only calls
    /// <c>ILanguageClient.ActivateAsync</c> for buffers whose content type derives from
    /// "languageserver-base"; a <c>[ContentType]</c> attribute naming a content type that
    /// nothing exports resolves to nothing and the client is never instantiated.
    /// </para>
    /// <para>
    /// The base is <see cref="CodeRemoteContentDefinition.CodeRemoteContentTypeName"/>
    /// ("code-languageserver-preview"), which VS defines as deriving from
    /// "code-languageserver-base" -&gt; "languageserver-base" (the LSP activation gate) and
    /// additionally from "code-languageserver-textmate-color" / -structure / -brace /
    /// -indentation and "code-textmate-commentselection". Those TextMate bases are what keep
    /// colorization, structure guides, brace completion and comment selection working:
    /// Microsoft.VisualStudio.LanguageServices.LanguageExtension.VSCore resolves a TextMate
    /// grammar for such buffers from the document's file extension, and VS ships a Go grammar
    /// at Common7\IDE\CommonExtensions\Microsoft\TextMate\Starterkit\Extensions\go\syntaxes\go.json
    /// (scopeName "source.go", fileTypes ["go"]).
    /// </para>
    /// <para>
    /// The name is deliberately NOT "go". VS registers TextMate content types imperatively as
    /// "code++" / "code++.&lt;GrammarName&gt;" (so a plain .go buffer is typed "code++.Go"); there is
    /// no in-box content type literally named "go", and picking a distinct name avoids colliding
    /// with any future in-box registration.
    /// </para>
    /// </summary>
    internal static class GoContentTypeDefinitions
    {
        /// <summary>
        /// Name of the content type exported by this extension for Go source files.
        /// </summary>
        public const string ContentTypeName = "OrikaGo";

#pragma warning disable CS0649 // MEF assigns nothing to these fields; only their export metadata is used.
        /// <summary>
        /// The Go content type itself, derived from the LSP "code remote" content type.
        /// </summary>
        [Export(typeof(ContentTypeDefinition))]
        [Name(ContentTypeName)]
        [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition GoContentType;

        /// <summary>
        /// Maps ".go" files onto the content type above so opening one activates gopls.
        /// </summary>
        [Export(typeof(FileExtensionToContentTypeDefinition))]
        [ContentType(ContentTypeName)]
        [FileExtension(".go")]
        internal static FileExtensionToContentTypeDefinition GoFileExtension;
#pragma warning restore CS0649
    }
}
