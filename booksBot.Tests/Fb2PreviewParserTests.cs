using System.Text;
using System.Xml;
using booksBot.Core.Services;

namespace booksBot.Tests;

public sealed class Fb2PreviewParserTests
{
    [Fact]
    public void Parse_IgnoresMalformedCoverButKeepsAnnotation()
    {
        const string xml = """
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
              <description><title-info>
                <annotation><p>  Хорошая   книга. </p></annotation>
                <coverpage><image l:href="#cover" /></coverpage>
              </title-info></description>
              <binary id="cover" content-type="image/jpeg">not-base64</binary>
            </FictionBook>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var preview = Fb2PreviewParser.Parse(stream);

        Assert.Equal("Хорошая книга.", preview.Annotation);
        Assert.False(preview.HasCover);
    }

    [Fact]
    public void Parse_RejectsDtd()
    {
        const string xml = "<!DOCTYPE FictionBook [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><FictionBook>&xxe;</FictionBook>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        Assert.Throws<XmlException>(() => Fb2PreviewParser.Parse(stream));
    }
}
