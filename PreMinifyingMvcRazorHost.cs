using System.IO;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.Directives;
using Microsoft.AspNetCore.Razor;
using Microsoft.AspNetCore.Razor.CodeGenerators;
using Microsoft.AspNetCore.Razor.Parser;
using Microsoft.AspNetCore.Razor.Compilation.TagHelpers;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardRex.MinifyingMvcRazorHost
{
    public class PreMinifyingMvcRazorHost : IMvcRazorHost
    {
        private readonly MvcRazorHost _host;
        public string DefaultNamespace => _host.DefaultNamespace;
        public string MainClassNamePrefix => _host.DefaultClassName;
        private bool _outsideOfElement = true;
        private StringBuilder chunkStringBuilder = new StringBuilder();
        private StringBuilder chunkCharactersStringBuilder = new StringBuilder();
        private const string ModelExpressionProviderProperty = "ModelExpressionProvider";
        private const string ViewDataProperty = "ViewData";

        public PreMinifyingMvcRazorHost(IChunkTreeCache chunkTreeCache, ITagHelperDescriptorResolver descriptorResolver)
        {
            _host = new MvcRazorHost(chunkTreeCache, descriptorResolver);
        }

        public virtual string ModelExpressionProvider
        {
            get { return ModelExpressionProviderProperty; }
        }

        public virtual string ViewDataPropertyName
        {
            get { return ViewDataProperty; }
        }

        public GeneratorResults GenerateCode(string rootRelativePath, Stream inputStream)
        {
            StreamReader sr = new StreamReader(inputStream);
            var className = MainClassNamePrefix + ParserHelpers.SanitizeClassName(rootRelativePath);
            var engine = new RazorTemplateEngine(_host);
            return engine.GenerateCode(GenerateStreamFromString(MinifyChunk(sr.ReadToEnd())), className, DefaultNamespace, rootRelativePath);
        }
        
        private MemoryStream GenerateStreamFromString(string value)
        { 
            return new MemoryStream(Encoding.UTF8.GetBytes(value ?? ""));
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
