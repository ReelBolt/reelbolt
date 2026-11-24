#nullable enable
using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>The footer is the last child of the real body for every swallowing shape, and mail HTML survives the round trip.</summary>
public sealed class CampaignFooterPlacerTests
{
    private const string Footer = "<hr><p>ReelBolt is operated by Acme<br>Via Roma 1<br><a href=\"{{ unsubscribe }}\">Unsubscribe</a></p>";
    private static readonly string[] Required = ["Acme", "Via Roma 1"];

    private static string Place(string html) => CampaignFooterPlacer.Place(html, Footer, Required);

    private static void FooterIsLastChildOfBody(string output)
    {
        IHtmlDocument doc = new HtmlParser().ParseDocument(output);
        IElement footer = doc.GetElementById(CampaignFooterPlacer.FooterId)!;
        footer.Should().NotBeNull();
        footer.ParentElement.Should().BeSameAs(doc.Body).And.BeAssignableTo<IHtmlBodyElement>();
        doc.Body!.LastElementChild.Should().BeSameAs(footer);
        footer.QuerySelectorAll("a").Should().Contain(a => a.GetAttribute("href") == "{{ unsubscribe }}");
    }

    [Theory]
    // the old guard's bypass shapes: all of them end with the footer as a child of body
    [InlineData("<p>hello there</p><!-- never closed")]
    [InlineData("<p>hello there</p><!--")]
    [InlineData("<p>hello there</p><style>p{color:red}")]
    [InlineData("<p>hello there</p><script>var a")]
    [InlineData("<p>hello there</p><title>x")]
    [InlineData("<p>hello there</p><textarea>")]
    [InlineData("<p>hello there</p><xmp>text")]
    [InlineData("<p>hello there</p><iframe src=\"x\">")]
    [InlineData("<p>hello there</p><noembed>text")]
    [InlineData("<p>hello there</p><noframes>text")]
    [InlineData("<p>hello there</p><a href=\"x")]
    [InlineData("<p>hello there</p><a href='x")]
    [InlineData("<p>hello there</p><div ")]
    [InlineData("<p>hello there</p></div")]
    [InlineData("<p>hello there</p><!DOCTYPE html")]
    // unclosed containers
    [InlineData("<div><div><p>hello there")]
    [InlineData("<p>hello there</p><template><div>x")]
    [InlineData("<p>hello there</p><select><option>x")]
    [InlineData("<p>hello there</p><details><summary>x")]
    [InlineData("<p>hello there</p><svg><style>")]
    [InlineData("<p>hello there</p><math><mi><style>")]
    [InlineData("<p>hello there</p><table><tr><td>x")]
    [InlineData("<p>hello there</p><div></body></html>")]
    [InlineData("<html><body><p>hello there</p></body></html><p>after</p>")]
    public void Footer_is_the_last_child_of_body_for_every_swallowing_shape(string html) => FooterIsLastChildOfBody(Place(html));

    [Theory]
    [InlineData("<html><frameset><frame src=x></frameset></html>")]
    [InlineData("<!DOCTYPE html><frameset></frameset>")]
    public void Frameset_has_no_body_and_is_refused(string html)
    {
        Action a = () => Place(html);
        a.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("html_no_body");
    }

    [Theory]
    [InlineData("<p>hello there</p><plaintext>")]
    [InlineData("<p>hello there</p><PLAINTEXT>text")]
    public void Plaintext_cannot_be_closed_so_the_verification_refuses_it(string html)
    {
        // The footer is a body child, but the serializer writes it after the open plaintext, where a parser reads it as text.
        Action a = () => Place(html);
        a.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("footer_placement_failed");
    }

    [Fact]
    public void An_open_style_in_head_keeps_the_footer_in_body_and_visible_to_the_parser()
    {
        string output = Place("<html><head><style>p{color:red}</style></head><body><p>hello there</p></body></html>");
        FooterIsLastChildOfBody(output);
        output.Should().Contain("<style>p{color:red}</style>");
    }

    [Theory]
    [InlineData("<!--[if mso]><table><tr><td><![endif]--><p>hi</p><!--[if mso]></td></tr></table><![endif]-->", "<!--[if mso]><table><tr><td><![endif]-->")]
    [InlineData("<!--[if mso]><v:rect xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:fill/></v:rect><![endif]--><p>x</p>", "<![endif]-->")]
    [InlineData("<p>Hello {{ params.FIRSTNAME }}, see <a href=\"{{ params.URL }}\">here</a></p>", "Hello {{ params.FIRSTNAME }}, see <a href=\"{{ params.URL }}\">here</a>")]
    [InlineData("<p>Hi&nbsp;there &amp; &copy; &lt;b&gt; &euro;</p>", "&nbsp;there &amp; © &lt;b&gt; €")]
    [InlineData("<center><table width=\"600\" style=\"border:0;margin:0 auto\"><tr><td style=\"padding:0\">x</td></tr></table></center>", "<center><table width=\"600\" style=\"border:0;margin:0 auto\"><tbody><tr><td style=\"padding:0\">x</td></tr></tbody></table></center>")]
    [InlineData("<P CLASS=\"a\">Upper</P><TABLE><TR><TD>x</TD></TR></TABLE>", "<p class=\"a\">Upper</p>")]
    [InlineData("<p class='single'>q</p>", "<p class=\"single\">q</p>")]
    [InlineData("<p>text<o:p></o:p></p><p class=MsoNormal>y</p>", "<o:p></o:p>")]
    [InlineData("<p>line1\r\nline2</p>", "<p>line1\nline2</p>")]
    public void Mail_html_survives_the_round_trip(string html, string expected) => Place(html).Should().Contain(expected);

    [Theory]
    // shapes of the usual email tools and Google Docs; the point is the placeholders and footer survive
    [InlineData("<!doctype html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"><head><!--[if gte mso 9]><xml><o:OfficeDocumentSettings><o:AllowPNG/><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml><![endif]--><meta charset=\"utf-8\"><style type=\"text/css\">body{margin:0}</style></head><body><mj-wrapper><div style=\"background:#fff\"><!--[if mso | IE]><table role=\"presentation\"><tr><td><![endif]--><p>Hi {{ params.NAME }}</p><!--[if mso | IE]></td></tr></table><![endif]--></div></mj-wrapper></body></html>")]
    [InlineData("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\"><html><head></head><body><table class=\"mcnTextContent\"><tr><td><a href=\"*|ARCHIVE|*\">x</a> {{ params.A }}</td></tr></table></body></html>")]
    [InlineData("<html><body id=\"body\"><table role=\"presentation\"><tr><td class=\"stp\"><img src=\"https://x.test/a.png\" width=\"1\" height=\"1\" alt=\"\"></td></tr></table><div class=\"lit\"><p>{{ params.X }}</p></div></body></html>")]
    [InlineData("<meta charset=\"utf-8\"><b style=\"font-weight:normal;\" id=\"docs-internal-guid-1\"><p dir=\"ltr\" style=\"line-height:1.38\"><span style=\"font-size:11pt\">Docs</span></p></b>")]
    public void Tool_shapes_keep_their_placeholders_and_get_the_footer(string html)
    {
        string output = Place(html);
        FooterIsLastChildOfBody(output);
        foreach (string token in new[] { "{{ params.NAME }}", "{{ params.A }}", "{{ params.X }}", "*|ARCHIVE|*" })
        {
            if (html.Contains(token, StringComparison.Ordinal)) output.Should().Contain(token);
        }

        if (html.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || html.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
            output.Should().StartWith("<!DOCTYPE");
    }

    [Fact]
    public void Placeholder_in_text_and_href_is_not_encoded()
    {
        string output = Place("<p>{{ params.a }} {{ unsubscribe }}</p><a href=\"{{ unsubscribe }}?x=1\">u</a>");
        output.Should().Contain("<p>{{ params.a }} {{ unsubscribe }}</p>").And.Contain("<a href=\"{{ unsubscribe }}?x=1\">u</a>");
    }

    [Fact]
    public void Downlevel_revealed_conditional_comments_are_rewritten_as_comments_a_known_serializer_change()
    {
        // <![if !mso]> is a bogus comment to an HTML5 parser; documented in the class comment.
        string output = Place("<![if !mso]><p>shown</p><![endif]>");
        output.Should().Contain("<p>shown</p>").And.Contain("[endif]");
    }

    [Fact]
    public void Nesting_beyond_the_limit_is_refused_fast_and_the_host_survives_200k_nested_divs()
    {
        // AngleSharp 1.9.0 alone takes minutes on this (quadratic) and caps its own tree at 512; the pre-parse scan refuses first.
        string deep = string.Concat(Enumerable.Repeat("<div>", 200_000)) + "x";
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        Action a = () => Place(deep);
        a.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("html_too_deep");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        Place(string.Concat(Enumerable.Repeat("<div>", 400)) + "ok").Should().Contain(CampaignFooterPlacer.FooterId);
    }

    [Theory]
    [InlineData("<div>")]
    [InlineData("<span>")]
    [InlineData("<b>")]
    [InlineData("<font>")]
    [InlineData("<blockquote>")]
    [InlineData("<table><tr><td>")]
    [InlineData("<div><span><b>")]
    [InlineData("<ul><li>")]
    [InlineData("<DIV CLASS=\"a>b\">")]
    [InlineData("<div/>")]
    [InlineData("<svg><g>")]
    [InlineData("<a href='x'>")]
    public void Nesting_beyond_the_limit_with_other_elements_is_refused_fast(string tag)
    {
        string deep = string.Concat(Enumerable.Repeat(tag, 100_000));
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        Action a = () => Place(deep);
        a.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("html_too_deep");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Deep_nesting_cannot_be_hidden_from_the_scan_by_comments_attributes_or_mismatched_end_tags()
    {
        string unit = "<!-- </div></div> --><div title=\"</div>\"></span></p></table>";
        Action a = () => Place(string.Concat(Enumerable.Repeat(unit, 100_000)));
        a.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("html_too_deep");
        // raw text is skipped: tags inside script and style do not nest anything
        Place("<script>" + string.Concat(Enumerable.Repeat("<div>", 50_000)) + "</script><style>" + string.Concat(Enumerable.Repeat("<div>", 50_000)) + "</style><p>ok</p>")
            .Should().Contain(CampaignFooterPlacer.FooterId);
    }

    [Fact]
    public void Flat_sloppy_mail_with_thousands_of_unclosed_paragraphs_and_cells_is_not_refused()
    {
        string html = string.Concat(Enumerable.Repeat("<p>line<br>", 5_000)) + "<table>" + string.Concat(Enumerable.Repeat("<tr><td>c", 2_000)) + "</table><ul>" + string.Concat(Enumerable.Repeat("<li>i", 2_000)) + "</ul>";
        FooterIsLastChildOfBody(Place(html));
    }

    [Fact]
    public void A_one_megabyte_flat_document_parses_within_the_budget()
    {
        string html = string.Concat(Enumerable.Repeat("<p style=\"margin:0\">hello {{ params.X }}</p>", 20_000));
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        FooterIsLastChildOfBody(Place(html));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void An_unclosed_footer_swallowing_style_in_head_and_body_leaves_the_footer_visible_content()
    {
        string output = Place("<html><head><style>p{color:red}</style></head><body><p>x</p></body></html>");
        output.Should().Contain("<div id=\"rb-campaign-footer\">").And.EndWith("</div></body></html>");
    }

    [Fact]
    public void Validation_check_uses_the_same_refusals()
    {
        Action deep = () => CampaignFooterPlacer.Check(string.Concat(Enumerable.Repeat("<div>", 100_000)));
        deep.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("html_too_deep");
        Action none = () => CampaignFooterPlacer.Check("<html><frameset></frameset></html>");
        none.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("html_no_body");
        CampaignFooterPlacer.Check("<p>fine</p>");
    }

    [Fact]
    public void An_admin_element_with_the_footer_id_is_refused_so_the_check_cannot_look_at_the_wrong_node()
    {
        Action a = () => Place("<div id=\"rb-campaign-footer\"><a href=\"{{ unsubscribe }}\">Acme Via Roma 1</a></div><p>hello</p>");
        a.Should().Throw<CampaignRefusal>().Which.Code.Should().Be("footer_placement_failed");
    }
}
