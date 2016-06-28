using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.Directives;
using Microsoft.AspNetCore.Razor;
using Microsoft.AspNetCore.Razor.Chunks;
using Microsoft.AspNetCore.Razor.CodeGenerators;
using Microsoft.AspNetCore.Razor.Parser;
using Microsoft.AspNetCore.Razor.CodeGenerators.Visitors;
using Microsoft.AspNetCore.Razor.Compilation.TagHelpers;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardRex.MinifyingMvcRazorHost
{
    public class ChunkVisitorMinifyingMvcRazorHost : IMvcRazorHost
    {
        private readonly MvcRazorHost _host;
        public static bool HasOnlySeenOpenBracket = false;
        public string DefaultNamespace => _host.DefaultNamespace;
        public string MainClassNamePrefix => _host.DefaultClassName;
        private ChunkInheritanceUtility _chunkInheritanceUtility;
        private readonly IChunkTreeCache _chunkTreeCache;
        private const string ModelExpressionProviderProperty = "ModelExpressionProvider";
        private const string ViewDataProperty = "ViewData";

        public ChunkVisitorMinifyingMvcRazorHost(IChunkTreeCache chunkTreeCache, ITagHelperDescriptorResolver descriptorResolver)
        {
            _chunkTreeCache = chunkTreeCache;
            _host = new MvcRazorHost(chunkTreeCache, descriptorResolver);
        }

        public GeneratorResults GenerateCode(string rootRelativePath, Stream inputStream)
        {
            var className = MainClassNamePrefix + ParserHelpers.SanitizeClassName(rootRelativePath);
            var engine = new CustomRazorTemplateEngine(this, _host);
            return engine.GenerateCode(inputStream, className, DefaultNamespace, rootRelativePath);
        }

        public CodeGenerator DecorateCodeGenerator(CodeGenerator incomingGenerator, CodeGeneratorContext context)
        {
            var inheritedChunkTrees = GetInheritedChunkTrees(context.SourceFile);
            ChunkInheritanceUtility.MergeInheritedChunkTrees(
                context.ChunkTreeBuilder.Root,
                inheritedChunkTrees,
                _host.DefaultModel);
            return new CustomCSharpCodeGenerator(
                context,
                _host.DefaultModel,
                _host.InjectAttribute,
                new GeneratedTagHelperAttributeContext
                {
                    ModelExpressionTypeName = _host.ModelExpressionType,
                    CreateModelExpressionMethodName = _host.CreateModelExpressionMethod,
                    ModelExpressionProviderPropertyName = ModelExpressionProviderProperty,
                    ViewDataPropertyName = ViewDataProperty
                });
        }

        public virtual string ModelExpressionProvider
        {
            get { return ModelExpressionProviderProperty; }
        }

        public virtual string ViewDataPropertyName
        {
            get { return ViewDataProperty; }
        }

        internal ChunkInheritanceUtility ChunkInheritanceUtility
        {
            get
            {
                if (_chunkInheritanceUtility == null)
                {
                    _chunkInheritanceUtility = new ChunkInheritanceUtility(_host, _chunkTreeCache, _host.DefaultInheritedChunks);
                }
                return _chunkInheritanceUtility;
            }
            set
            {
                _chunkInheritanceUtility = value;
            }
        }

        private IReadOnlyList<ChunkTree> GetInheritedChunkTrees(string sourceFileName)
        {
            var inheritedChunkTrees = _host.GetInheritedChunkTreeResults(sourceFileName)
                .Select(result => result.ChunkTree)
                .ToList();
            return inheritedChunkTrees;
        }
    }

    public class CustomRazorTemplateEngine : RazorTemplateEngine
    {
        private ChunkVisitorMinifyingMvcRazorHost _myHost;

        public CustomRazorTemplateEngine(ChunkVisitorMinifyingMvcRazorHost myHost, MvcRazorHost host) : base(host)
        {
            _myHost = myHost;
        }

        protected override CodeGenerator CreateCodeGenerator(CodeGeneratorContext context)
        {
            return _myHost.DecorateCodeGenerator(Host.CodeLanguage.CreateCodeGenerator(context), context);
        }
    }

    public class CustomCSharpCodeGenerator : MvcCSharpCodeGenerator
    {
        private GeneratedTagHelperAttributeContext _tagHelperAttributeContext;

        public CustomCSharpCodeGenerator(CodeGeneratorContext context, string defaultModel, string injectAttribute, GeneratedTagHelperAttributeContext tagHelperAttributeContext) : base(context, defaultModel, injectAttribute, tagHelperAttributeContext)
        {
            _tagHelperAttributeContext = tagHelperAttributeContext;
        }

        protected override CSharpCodeVisitor CreateCSharpCodeVisitor(CSharpCodeWriter writer, CodeGeneratorContext context)
        {
            var csharpCodeVisitor = new CustomCSharpCodeVisitor(writer, context);
            csharpCodeVisitor.TagHelperRenderer.AttributeValueCodeRenderer = new MvcTagHelperAttributeValueCodeRenderer(_tagHelperAttributeContext);
            return csharpCodeVisitor;
        }
    }

    public class CustomCSharpCodeVisitor : CSharpCodeVisitor
    {
        public CustomCSharpCodeVisitor(CSharpCodeWriter writer, CodeGeneratorContext context) : base(writer, context)
        { }

        private bool _outsideOfElement = true;
        private bool _comingOffHyperlink = false;
        private StringBuilder chunkStringBuilder = new StringBuilder();
        private StringBuilder chunkCharactersStringBuilder = new StringBuilder();
        private const int MaxStringLiteralLength = 1024;

        protected override void Visit(ParentLiteralChunk chunk)
        {
            if (Context.Host.DesignTimeMode)
            {
                // Skip generating the chunk if we're in design time or if the chunk is empty.
                return;
            }

            var text = chunk.GetText();

            if (Context.Host.EnableInstrumentation)
            {
                var start = chunk.Start.AbsoluteIndex;
                Writer.WriteStartInstrumentationContext(Context, start, text.Length, isLiteral: true);
            }

            RenderStartWriteLiteral(MinifyChunk(text));

            if (Context.Host.EnableInstrumentation)
            {
                Writer.WriteEndInstrumentationContext(Context);
            }
        }

        protected override void Visit(LiteralChunk chunk)
        {
            if (Context.Host.DesignTimeMode || string.IsNullOrEmpty(chunk.Text))
            {
                // Skip generating the chunk if we're in design time or if the chunk is empty.
                return;
            }

            if (Context.Host.EnableInstrumentation)
            {
                Writer.WriteStartInstrumentationContext(Context, chunk.Association, isLiteral: true);
            }

            RenderStartWriteLiteral(MinifyChunk(chunk.Text));

            if (Context.Host.EnableInstrumentation)
            {
                Writer.WriteEndInstrumentationContext(Context);
            }
        }

        protected override void Visit(TagHelperChunk chunk)
        {
            if (chunk.TagName == "a")
            {
                Writer.Write(@"WriteLiteral("" "");");
                TagHelperRenderer.RenderTagHelper(chunk);
                _comingOffHyperlink = true;
            }
            else
            {
                TagHelperRenderer.RenderTagHelper(chunk);
            }
        }

        private void RenderStartWriteLiteral(string text)
        {
            var charactersRendered = 0;
            // Render the string in pieces to avoid Roslyn OOM exceptions at compile time:
            // https://github.com/aspnet/External/issues/54
            while (charactersRendered < text.Length)
            {
                if (Context.ExpressionRenderingMode == ExpressionRenderingMode.WriteToOutput)
                {
                    if (!string.IsNullOrEmpty(Context.TargetWriterName))
                    {
                        Writer.WriteStartMethodInvocation(Context.Host.GeneratedClassContext.WriteLiteralToMethodName)
                        .Write(Context.TargetWriterName)
                        .WriteParameterSeparator();
                    }
                    else
                    {
                        Writer.WriteStartMethodInvocation(Context.Host.GeneratedClassContext.WriteLiteralMethodName);
                    }
                }
                string textToRender;
                if (text.Length <= MaxStringLiteralLength)
                {
                    textToRender = text;
                }
                else
                {
                    int charRemaining = text.Length - charactersRendered;
                    if (charRemaining < MaxStringLiteralLength)
                    {
                        textToRender = text.Substring(charactersRendered, charRemaining);
                    }
                    else
                    {
                        textToRender = text.Substring(charactersRendered, MaxStringLiteralLength);
                    }
                }

                Writer.WriteStringLiteral(textToRender);
                charactersRendered += textToRender.Length;

                if (Context.ExpressionRenderingMode == ExpressionRenderingMode.WriteToOutput)
                {
                    Writer.WriteEndMethodInvocation();
                }
            }
        }
 
        private string MinifyChunk(string chunkText)
        {
            chunkStringBuilder.Clear();
            chunkCharactersStringBuilder.Clear();
            foreach (char c in chunkText)
            {
                if (_outsideOfElement)
                {
                    if (!c.Equals('<'))
                    {
                        // Collect this inter-element character for processing later
                        chunkCharactersStringBuilder.Append(c);
                    }
                    else
                    {
                        // We just came to the end of an inter-element sequence of characters ... process these characters now
                        _outsideOfElement = false;
                        chunkStringBuilder.Append(MinifyCharacters(chunkCharactersStringBuilder.ToString()) + c);
                        chunkCharactersStringBuilder.Clear();
                    }
                }
                else
                {
                    // Accumulate this element character to the output chunk
                    chunkStringBuilder.Append(c);
                    if (c.Equals('>'))
                    {
                        // Time to start collecting and processing characters again
                        _outsideOfElement = true;
                    }
                }
            }
            // Minify and add any leftover characters b/c we didn't reach the end of an inter-element sequence in this chunk
            chunkStringBuilder.Append(MinifyCharacters(chunkCharactersStringBuilder.ToString()));
            // Look around here and see if we can add a space back around inline elements that are inside the chunk
            // This assumes the inline element is next to the text IN THE SAME chunk. [That might be a bad assumption!]
            if (_comingOffHyperlink)
            {
                _comingOffHyperlink = false;
                var chunkStringBuilderVal = chunkStringBuilder.ToString();
                if (chunkStringBuilderVal.Length > 0 && Regex.IsMatch(chunkStringBuilderVal.Substring(0, 1), "[A-Za-z0-9]"))
                {
                    return " " + AddSpaceBack(chunkStringBuilderVal);
                }
            }
            return AddSpaceBack(chunkStringBuilder.ToString());
        }

        private string MinifyCharacters(string inputString)
        {
            return inputString.Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim(' ');
        }

        private string AddSpaceBack(string inputString)
        {
            return Regex.Replace(inputString, "((?<=\\S)(?=(<a|<b|<big|<i|<small|<tt|<abbr|<acronym|<cite|<code|<dfn|<em|<kbd|<strong|<samp|<time|<var|<bdo|<br|<img|<map|<object|<q|<span|<sub|<sup|<button|<input|<label|<select|<textarea)))|((?<=(/a>|/b>|/big>|/i>|/small>|/tt>|/abbr>|/acronym>|/cite>|/code>|/dfn>|/em>|/kbd>|/strong>|/samp>|/time>|/var>|/bdo>|/br>|/map>|/object>|/q>|/span>|/sub>|/sup>|/button>|/input>|/label>|/select>|/textarea>))(?=[A-Za-z0-9]))", " ");
        }
    }
}
